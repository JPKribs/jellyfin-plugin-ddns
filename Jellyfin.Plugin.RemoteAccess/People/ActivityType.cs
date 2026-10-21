namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// What an address did, for the Remote Access table.
/// </summary>
public enum ActivityType
{
    /// <summary>A user signed in or had a session from it.</summary>
    User,

    /// <summary>A sign-in from it failed.</summary>
    Failure,

    /// <summary>The block list or the country rules refused it.</summary>
    Blocked,

    /// <summary>It reached the page from the internet but never tried to sign in.</summary>
    Seen
}
