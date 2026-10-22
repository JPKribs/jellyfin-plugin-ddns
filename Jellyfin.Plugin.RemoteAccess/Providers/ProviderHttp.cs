using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// The HTTP client provider requests use. Providers send API keys in headers such as Cloudflare's
/// <c>X-Auth-Key</c>, and .NET only drops <c>Authorization</c> when it follows a redirect to another server, so a
/// redirect from a compromised or spoofed endpoint would hand those keys to whoever it names. This client never
/// follows a redirect to another server, and never from HTTPS to plain HTTP.
/// </summary>
public static class ProviderHttp
{
    /// <summary>The name the provider client is registered under.</summary>
    public const string ClientName = "Jellyfin.Plugin.DynamicDns.Providers";

    /// <summary>
    /// Builds the handler under the provider client: no automatic redirects, so <see cref="SameHostRedirectHandler"/>
    /// decides every one.
    /// </summary>
    /// <returns>The handler.</returns>
    public static HttpMessageHandler PrimaryHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };
}

/// <summary>
/// Hands out the provider client whatever name is asked for, so the shared HTTP helper, which asks for Jellyfin's
/// default client, gets the one that keeps credentials on their own server.
/// </summary>
public sealed class ProviderHttpClientFactory : IHttpClientFactory
{
    private readonly IHttpClientFactory _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderHttpClientFactory"/> class.
    /// </summary>
    /// <param name="inner">Jellyfin's client factory.</param>
    public ProviderHttpClientFactory(IHttpClientFactory inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => _inner.CreateClient(ProviderHttp.ClientName);
}

/// <summary>
/// Follows a redirect only to the same server, scheme and port, or from HTTP up to HTTPS on the same server, and at
/// most five times. Any other redirect is handed back unfollowed, so the provider reports it as a failure and no
/// header reaches the server it named.
/// </summary>
public sealed class SameHostRedirectHandler : DelegatingHandler
{
    private const int MaxRedirects = 5;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || hop >= MaxRedirects || current.RequestUri is not { } from)
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                return response;
            }

            var to = location.IsAbsoluteUri ? location : new Uri(from, location);
            if (!SameServer(from, to))
            {
                response.ReasonPhrase = "Redirect to another server not followed";
                return response;
            }

            // 303, and 301 or 302 after a POST, turn into a GET without a body, as browsers and HttpClient do.
            var method = current.Method;
            var keepBody = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            if (!keepBody && method != HttpMethod.Head)
            {
                method = HttpMethod.Get;
            }

            var next = new HttpRequestMessage(method, to) { Version = current.Version, VersionPolicy = current.VersionPolicy };
            foreach (var header in current.Headers)
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (keepBody && content is not null && current.Content is not null)
            {
                next.Content = new ByteArrayContent(content);
                foreach (var header in current.Content.Headers)
                {
                    next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            response.Dispose();
            if (!ReferenceEquals(current, request))
            {
                current.Dispose();
            }

            current = next;
        }
    }

    /// <summary>
    /// Whether a redirect stays on the server the request went to: the same host and port with the same scheme, or
    /// HTTP up to HTTPS on the same host. HTTPS down to HTTP is never followed.
    /// </summary>
    /// <param name="from">Where the request went.</param>
    /// <param name="to">Where the redirect points.</param>
    /// <returns>True when it may be followed.</returns>
    public static bool SameServer(Uri from, Uri to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!string.Equals(from.IdnHost, to.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (from.Scheme == to.Scheme)
        {
            return from.Port == to.Port;
        }

        return from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps;
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
