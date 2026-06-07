using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Test helper that builds an <see cref="IHttpClientFactory"/> whose clients answer with canned
/// responses, so a provider's request shaping and response parsing can be exercised without network.
/// </summary>
internal static class StubHttp
{
    /// <summary>Builds a factory whose every client replies via <paramref name="responder"/>.</summary>
    /// <param name="responder">Maps a request to a status code and body.</param>
    /// <returns>A stub HTTP client factory.</returns>
    public static IHttpClientFactory Factory(Func<HttpRequestMessage, (HttpStatusCode Code, string Body)> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new CannedHandler(responder)));
        return factory;
    }

    /// <summary>Builds a factory that returns the same status and body for every request.</summary>
    /// <param name="code">The status code to return.</param>
    /// <param name="body">The response body to return.</param>
    /// <returns>A stub HTTP client factory.</returns>
    public static IHttpClientFactory Always(HttpStatusCode code, string body)
        => Factory(_ => (code, body));

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Code, string Body)> _responder;

        public CannedHandler(Func<HttpRequestMessage, (HttpStatusCode Code, string Body)> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (code, body) = _responder(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }
}
