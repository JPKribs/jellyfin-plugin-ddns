using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// A refused sign-in the proxy saw, waiting for Jellyfin's own failure event.
/// </summary>
/// <param name="Ip">The client IP.</param>
/// <param name="Url">The address used.</param>
/// <param name="At">When (UTC).</param>
public sealed record PendingFailure(string Ip, string Url, DateTime At);
