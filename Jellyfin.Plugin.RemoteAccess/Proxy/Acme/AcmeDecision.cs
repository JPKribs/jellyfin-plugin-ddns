using System;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Acme;

/// <summary>
/// The planner's verdict.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="NextAttemptUtc">When waiting, the earliest next attempt (UTC).</param>
public readonly record struct AcmeDecision(AcmeAction Action, DateTime? NextAttemptUtc = null);
