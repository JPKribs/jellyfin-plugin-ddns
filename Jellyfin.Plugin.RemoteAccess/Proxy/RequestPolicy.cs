using System;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Decides what happens to a request before it is routed. Pure, so every rule is unit-tested.
/// </summary>
public static class RequestPolicy
{
    private const string ChallengePrefix = "/.well-known/acme-challenge";

    /// <summary>
    /// Decides what to do with a request.
    /// </summary>
    /// <param name="runtime">The proxy runtime.</param>
    /// <param name="facts">The request.</param>
    /// <param name="challenges">Pending HTTP-01 challenges.</param>
    /// <returns>The decision.</returns>
    public static RequestDecision Decide(ProxyRuntime runtime, RequestFacts facts, Http01ChallengeStore challenges)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(challenges);

        // Certificate checks are answered for any client: Let's Encrypt can arrive from an address the LAN test
        // calls local, such as a router or container that rewrites the source of forwarded connections.
        if (!facts.IsHttps && TryChallenge(runtime, facts, challenges) is { } challenge)
        {
            return challenge;
        }

        // Plain HTTP from the internet: only redirects to HTTPS are answered.
        if (!facts.Local && !facts.IsHttps)
        {
            return DecideWanHttp(runtime, facts);
        }

        // The internet must ask for a configured name. The bare IP or a scanner's guess gets nothing.
        if (!facts.Local && !runtime.IsConfiguredHost(facts.Host))
        {
            return new RequestDecision(RequestAction.Drop);
        }

        var route = runtime.MatchRoute(facts.Host);
        if (route is not null)
        {
            return route.Exposure == RouteExposure.LanOnly && !facts.Local
                ? new RequestDecision(RequestAction.Drop)
                : new RequestDecision(RequestAction.Forward, route);
        }

        // Without forwarding headers honoured, Jellyfin would treat this client as local: no remote
        // access rules, no remote bitrate limit. Refuse until Jellyfin trusts the proxy.
        if (!facts.Local && !runtime.JellyfinTrustsForwarding)
        {
            return new RequestDecision(RequestAction.Drop);
        }

        if (!facts.Local && PathRules.IsStartup(PathRules.StripBase(facts.Path, runtime.BaseUrl)))
        {
            return new RequestDecision(RequestAction.Forbidden);
        }

        return new RequestDecision(RequestAction.Forward);
    }

    /// <summary>
    /// Builds the HTTPS URL for a request. The internet always reaches HTTPS on port 443.
    /// </summary>
    /// <param name="host">The configured host the request asked for.</param>
    /// <param name="path">The path.</param>
    /// <param name="query">The query string.</param>
    /// <returns>The absolute HTTPS URL.</returns>
    public static string HttpsUrl(string host, PathString path, QueryString query)
        => "https://" + host + path.ToUriComponent() + query.ToUriComponent();

    // A pending HTTP-01 answer for a configured name. Anything else under the challenge path is not one.
    private static RequestDecision? TryChallenge(ProxyRuntime runtime, RequestFacts facts, Http01ChallengeStore challenges)
    {
        if (!runtime.IsConfiguredHost(facts.Host) || !facts.Path.StartsWithSegments(ChallengePrefix, StringComparison.Ordinal, out var rest))
        {
            return null;
        }

        var token = (rest.Value ?? string.Empty).TrimStart('/');
        return token.Length > 0 && !token.Contains('/', StringComparison.Ordinal) && challenges.TryGet(token, out var answer)
            ? new RequestDecision(RequestAction.Challenge, Value: answer)
            : null;
    }

    // Port 80 on the router lands here. A configured name is sent on to HTTPS, so http://
    // links still work; anything else, such as the bare IP, is dropped.
    private static RequestDecision DecideWanHttp(ProxyRuntime runtime, RequestFacts facts)
    {
        if (!runtime.IsConfiguredHost(facts.Host) || facts.Path.StartsWithSegments(ChallengePrefix, StringComparison.Ordinal))
        {
            return new RequestDecision(RequestAction.Drop);
        }

        // Until a certificate is loaded there is no HTTPS to send anyone to.
        if (runtime.Listener.HttpsPort is null)
        {
            return new RequestDecision(RequestAction.Drop);
        }

        // Redirect only to the configured name the client asked for, never to an arbitrary Host.
        return new RequestDecision(RequestAction.Redirect, Value: HttpsUrl(facts.Host.ToLowerInvariant(), facts.Path, facts.Query));
    }
}
