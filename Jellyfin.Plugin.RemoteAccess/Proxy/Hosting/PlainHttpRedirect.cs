using System;
using System.Linq;
using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Catches plain HTTP sent to the HTTPS port, which a browser otherwise reports as an empty response. A TLS
/// connection always opens with a handshake record (first byte 0x16); anything else is plain HTTP. Local
/// clients get a redirect to the same address over HTTPS. Internet clients are dropped, like every other
/// plain-HTTP request from the internet.
/// </summary>
public static class PlainHttpRedirect
{
    private const byte TlsHandshake = 0x16;
    private const int MaxHeaderBytes = 8192;
    private static readonly TimeSpan _readTimeout = TimeSpan.FromSeconds(5);

    // Matches the TLS handshake timeout, which only starts once this check hands the connection on.
    private static readonly TimeSpan _firstByteTimeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] _headerEnd = "\r\n\r\n"u8.ToArray();

    /// <summary>
    /// Kestrel connection middleware for the HTTPS listener, placed before TLS.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="next">TLS and then HTTP.</param>
    /// <param name="holder">The runtime holder, for the LAN test.</param>
    /// <returns>A task that completes when the connection is done.</returns>
    public static async Task OnConnectionAsync(ConnectionContext connection, ConnectionDelegate next, ProxyRuntimeHolder holder)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(holder);

        var input = connection.Transport.Input;
        ReadResult first;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed))
        {
            timeout.CancelAfter(_firstByteTimeout);
            try
            {
                first = await input.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Connected and sent nothing: close it rather than hold it open forever.
                connection.Abort();
                return;
            }
        }

        if (first.Buffer.IsEmpty)
        {
            input.AdvanceTo(first.Buffer.Start);
            return;
        }

        // Leave the bytes unread, so TLS sees the handshake from its first byte.
        var isTls = first.Buffer.FirstSpan[0] == TlsHandshake;
        input.AdvanceTo(first.Buffer.Start);
        if (isTls)
        {
            await next(connection).ConfigureAwait(false);
            return;
        }

        var remote = (connection.RemoteEndPoint as IPEndPoint)?.Address;
        if (!holder.Current.Classifier.IsLocal(remote))
        {
            connection.Abort();
            return;
        }

        var head = await ReadHeadAsync(connection).ConfigureAwait(false);
        var local = connection.LocalEndPoint as IPEndPoint;
        var location = RedirectLocation(head, local?.Address, local?.Port ?? 443);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 308 Permanent Redirect\r\n"
            + "Location: " + location + "\r\n"
            + "Content-Length: 0\r\n"
            + "Connection: close\r\n\r\n");
        try
        {
            await connection.Transport.Output.WriteAsync(response, connection.ConnectionClosed).ConfigureAwait(false);
            await connection.Transport.Output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionResetException or InvalidOperationException)
        {
            // The client went away before the redirect was sent. Nothing is left to answer.
            connection.Abort();
        }
    }

    /// <summary>
    /// Builds the HTTPS address for a plain-HTTP request head: the same host and path on the HTTPS port.
    /// Anything unusable in the request falls back to the server's own address and the root path.
    /// </summary>
    /// <param name="head">The request line and headers, as text.</param>
    /// <param name="fallbackHost">The address the connection arrived on, used when no usable Host was sent.</param>
    /// <param name="httpsPort">The HTTPS port.</param>
    /// <returns>The absolute HTTPS URL.</returns>
    public static string RedirectLocation(string head, IPAddress? fallbackHost, int httpsPort)
    {
        var lines = (head ?? string.Empty).Split("\r\n");
        var target = "/";
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length >= 2 && requestLine[1].StartsWith('/') && IsSafe(requestLine[1]))
        {
            target = requestLine[1];
        }

        string? host = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                host = HostWithoutPort(line[5..].Trim());
                break;
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            var ip = fallbackHost is null || IPAddress.Any.Equals(fallbackHost) || IPAddress.IPv6Any.Equals(fallbackHost)
                ? IPAddress.Loopback
                : NetworkClassifier.Normalize(fallbackHost);
            host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString();
        }

        var port = httpsPort == 443 ? string.Empty : ":" + httpsPort.ToString(CultureInfo.InvariantCulture);
        return "https://" + host + port + target;
    }

    private static async Task<string> ReadHeadAsync(ConnectionContext connection)
    {
        var input = connection.Transport.Input;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        timeout.CancelAfter(_readTimeout);
        try
        {
            while (true)
            {
                var result = await input.ReadAsync(timeout.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                var end = Find(buffer, _headerEnd);
                if (end >= 0 || buffer.Length >= MaxHeaderBytes || result.IsCompleted)
                {
                    var length = end >= 0 ? end : Math.Min(buffer.Length, MaxHeaderBytes);
                    var text = Encoding.ASCII.GetString(buffer.Slice(0, length));
                    input.AdvanceTo(buffer.End);
                    return text;
                }

                input.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static long Find(ReadOnlySequence<byte> buffer, byte[] value)
    {
        var reader = new SequenceReader<byte>(buffer);
        return reader.TryReadTo(out ReadOnlySequence<byte> before, value, advancePastDelimiter: false) ? before.Length : -1;
    }

    // Only plain host characters, so nothing a client sends can add headers to the response.
    private static string? HostWithoutPort(string value)
    {
        if (value.Length == 0 || value.Length > 255)
        {
            return null;
        }

        string host;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            host = value[..(close + 1)];

            // A scope id after '%' is free text to the parser, so only hex digits, colons and dots get through;
            // nothing a client sends can add a line to the response.
            var inner = host.Trim('[', ']');
            return inner.All(c => Uri.IsHexDigit(c) || c is ':' or '.') && IPAddress.TryParse(inner, out _) ? host : null;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        host = colon >= 0 ? value[..colon] : value;
        foreach (var c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                return null;
            }
        }

        return host.Length > 0 ? host : null;
    }

    private static bool IsSafe(string target)
    {
        foreach (var c in target)
        {
            if (c <= ' ' || c >= 0x7F)
            {
                return false;
            }
        }

        return target.Length <= 4096;
    }
}
