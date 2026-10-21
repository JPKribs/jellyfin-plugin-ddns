using System;
using System.Security.Claims;

namespace Jellyfin.Plugin.DynamicDns.Api;

/// <summary>
/// The Jellyfin user behind a request, so an activity entry for an administrator's change says who made it.
/// </summary>
public static class ActivityUser
{
    // The claim Jellyfin's authentication puts the user's id in.
    private const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>
    /// The id of the signed-in user.
    /// </summary>
    /// <param name="user">The request's user.</param>
    /// <returns>The id, or empty when the request carries none, such as one made with an API key.</returns>
    public static Guid Of(ClaimsPrincipal? user)
        => Guid.TryParse(user?.FindFirst(UserIdClaim)?.Value, out var id) ? id : Guid.Empty;
}
