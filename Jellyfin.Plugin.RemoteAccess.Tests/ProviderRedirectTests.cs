using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Providers;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Provider requests carry API keys in headers that .NET keeps across a redirect. A redirect to another server is
/// never followed, so a key only ever reaches the server it was entered for.
/// </summary>
public class ProviderRedirectTests
{
    [Fact]
    public async Task RedirectToAnotherServer_IsNotFollowed()
    {
        var seen = new List<HttpRequestMessage>();
        using var client = Client(seen, req => req.RequestUri!.Host == "api.cloudflare.com"
            ? Redirect(HttpStatusCode.Found, "https://attacker.example/steal")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.cloudflare.com/client/v4/zones");
        request.Headers.Add("X-Auth-Key", "secret-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(seen);
        Assert.DoesNotContain(seen, r => r.RequestUri!.Host == "attacker.example");
    }

    [Fact]
    public async Task RedirectOnTheSameServer_IsFollowedWithItsHeaders()
    {
        var seen = new List<HttpRequestMessage>();
        using var client = Client(seen, req => req.RequestUri!.AbsolutePath == "/old"
            ? Redirect(HttpStatusCode.MovedPermanently, "/new")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/old");
        request.Headers.Add("X-Api-Key", "secret-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, seen.Count);
        Assert.Equal("https://api.example.com/new", seen[1].RequestUri!.AbsoluteUri);
        Assert.Equal("secret-key", seen[1].Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task TemporaryRedirect_KeepsTheMethodAndBody()
    {
        var seen = new List<(HttpMethod Method, string Body)>();
        using var client = new HttpClient(new SameHostRedirectHandler { InnerHandler = new Responder(async req =>
        {
            seen.Add((req.Method, req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync()));
            return req.RequestUri!.AbsolutePath == "/v1"
                ? Redirect(HttpStatusCode.TemporaryRedirect, "https://api.example.com/v2")
                : new HttpResponseMessage(HttpStatusCode.OK);
        }) });

        using var response = await client.PostAsync(new Uri("https://api.example.com/v1"), new StringContent("{\"ip\":\"203.0.113.7\"}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { (HttpMethod.Post, "{\"ip\":\"203.0.113.7\"}"), (HttpMethod.Post, "{\"ip\":\"203.0.113.7\"}") }, seen);
    }

    [Theory]
    [InlineData("https://api.example.com/a", "https://api.example.com/b", true)]
    [InlineData("https://api.example.com/a", "https://API.example.com/b", true)]
    [InlineData("http://api.example.com/a", "https://api.example.com/b", true)]
    [InlineData("https://api.example.com/a", "http://api.example.com/b", false)]
    [InlineData("https://api.example.com/a", "https://api.example.com:8443/b", false)]
    [InlineData("https://api.example.com/a", "https://evil.example.com/b", false)]
    [InlineData("https://api.example.com/a", "https://api.example.com.evil.net/b", false)]
    public void SameServer_OnlyAllowsTheSameHost(string from, string to, bool expected)
        => Assert.Equal(expected, SameHostRedirectHandler.SameServer(new Uri(from), new Uri(to)));

    [Fact]
    public async Task TooManyRedirects_StopsAndHandsBackTheRedirect()
    {
        var seen = new List<HttpRequestMessage>();
        using var client = Client(seen, _ => Redirect(HttpStatusCode.Found, "/again"));

        using var response = await client.GetAsync(new Uri("https://api.example.com/start"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void Registration_GivesProvidersAClientThatNeverRedirectsOnItsOwn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());
        using var provider = services.BuildServiceProvider();

        using var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ProviderHttp.ClientName);
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current);
        }

        Assert.Contains(chain, h => h is SameHostRedirectHandler);
        var primary = Assert.IsType<SocketsHttpHandler>(chain[^1]);
        Assert.False(primary.AllowAutoRedirect);
    }

    private static HttpClient Client(List<HttpRequestMessage> seen, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new SameHostRedirectHandler { InnerHandler = new Responder(req =>
        {
            seen.Add(req);
            return Task.FromResult(respond(req));
        }) });

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class Responder : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public Responder(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request);
    }
}
