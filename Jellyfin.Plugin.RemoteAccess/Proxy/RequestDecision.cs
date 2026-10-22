namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The outcome of <see cref="RequestPolicy.Decide"/>.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="Value">The redirect URL or challenge answer.</param>
public readonly record struct RequestDecision(RequestAction Action, string Value = "");
