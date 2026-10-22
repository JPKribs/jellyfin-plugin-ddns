using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// Checks that the public name people use resolves to this server. The dashboard polls status, so an answer is
/// kept for a minute and the lookup is bounded.
/// </summary>
public sealed class NameChecker : IDisposable
{
    private static readonly TimeSpan _keep = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _lookupLimit = TimeSpan.FromSeconds(3);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<CancellationToken, Task<(string V4, string V6)>> _publicAddresses;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NameStatus _last = new();
    private DateTime _checkedUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="NameChecker"/> class.
    /// </summary>
    /// <param name="resolve">Looks a name up, normally the system resolver.</param>
    /// <param name="publicAddresses">Returns this server's public IPv4 and IPv6 addresses, each empty while unknown.</param>
    public NameChecker(Func<string, CancellationToken, Task<IPAddress[]>> resolve, Func<CancellationToken, Task<(string V4, string V6)>> publicAddresses)
    {
        _resolve = resolve;
        _publicAddresses = publicAddresses;
    }

    /// <summary>
    /// Returns the last answer for the name without looking anything up.
    /// </summary>
    /// <param name="hostname">The name, empty when there is none.</param>
    /// <returns>The answer, or an empty one for a name not checked yet.</returns>
    public NameStatus Cached(string hostname)
    {
        var last = Volatile.Read(ref _last);
        return string.Equals(last.Hostname, hostname, StringComparison.OrdinalIgnoreCase) ? last.Copy() : new NameStatus { Hostname = hostname };
    }

    /// <summary>
    /// Checks the name, or returns the answer from less than a minute ago.
    /// </summary>
    /// <param name="hostname">The name, empty when there is none.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The answer.</returns>
    public async Task<NameStatus> GetAsync(string hostname, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(hostname))
        {
            return new NameStatus();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var last = Volatile.Read(ref _last);
            if (string.Equals(last.Hostname, hostname, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _checkedUtc < _keep)
            {
                return last.Copy();
            }

            var status = await CheckAsync(hostname, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _last, status);
            _checkedUtc = DateTime.UtcNow;
            return status.Copy();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Compares what a name resolved to with this server's public addresses.
    /// </summary>
    /// <param name="hostname">The name.</param>
    /// <param name="resolved">What it resolved to.</param>
    /// <param name="publicIPv4">This server's public IPv4 address, empty while unknown.</param>
    /// <param name="publicIPv6">This server's public IPv6 address, empty while unknown.</param>
    /// <returns>The answer.</returns>
    public static NameStatus Compare(string hostname, IEnumerable<IPAddress> resolved, string publicIPv4, string publicIPv6)
    {
        var addresses = (resolved ?? Enumerable.Empty<IPAddress>())
            .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Distinct()
            .ToList();
        var mine = new[] { publicIPv4, publicIPv6 }
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .Where(a => a is not null)
            .Cast<IPAddress>()
            .ToList();
        var status = new NameStatus
        {
            Hostname = hostname,
            Addresses = addresses.Select(a => a.ToString()).ToList(),
            PublicAddress = mine.FirstOrDefault()?.ToString() ?? string.Empty
        };

        if (addresses.Count == 0)
        {
            status.Error = $"{hostname} does not resolve to any address yet. Check its DNS record.";
            return status;
        }

        var match = addresses.FirstOrDefault(a => mine.Any(m => m.Equals(a)));
        if (match is not null)
        {
            status.Resolves = true;
            status.PublicAddress = match.ToString();
            return status;
        }

        status.Error = mine.Count == 0
            ? "This server's public address is not known yet, so the name cannot be compared with it."
            : $"{hostname} points to {string.Join(" and ", status.Addresses)}, not to this server at {status.PublicAddress}. Check its DNS record.";
        return status;
    }

    private async Task<NameStatus> CheckAsync(string hostname, CancellationToken cancellationToken)
    {
        var (v4, v6) = await _publicAddresses(cancellationToken).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_lookupLimit);
        try
        {
            var resolved = await _resolve(hostname, limit.Token).ConfigureAwait(false);
            return Compare(hostname, resolved, v4, v6);
        }
        catch (SocketException)
        {
            return Compare(hostname, Array.Empty<IPAddress>(), v4, v6);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NameStatus { Hostname = hostname, PublicAddress = v4.Length > 0 ? v4 : v6, Error = $"{hostname} could not be looked up in time." };
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
