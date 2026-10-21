namespace Jellyfin.Plugin.DynamicDns.Proxy.Network;

/// <summary>
/// The outcome of applying or restoring the port takeover.
/// </summary>
/// <param name="Success">Whether it went ahead.</param>
/// <param name="Message">What happened, or why not.</param>
/// <param name="Restarting">Whether Jellyfin is about to restart.</param>
public sealed record TakeoverResult(bool Success, string Message, bool Restarting);
