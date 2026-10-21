using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Records failed sign-ins Jellyfin reports. Jellyfin only reports them for usernames that exist; the proxy
/// catches the rest.
/// </summary>
public sealed class SignInFailedConsumer : IEventConsumer<AuthenticationRequestEventArgs>
{
    private readonly PeopleService _people;

    /// <summary>
    /// Initializes a new instance of the <see cref="SignInFailedConsumer"/> class.
    /// </summary>
    /// <param name="people">The People service.</param>
    public SignInFailedConsumer(PeopleService people)
    {
        _people = people;
    }

    /// <inheritdoc />
    public Task OnEvent(AuthenticationRequestEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        _people.RecordSignIn(
            success: false,
            eventArgs.Username ?? string.Empty,
            eventArgs.UserId is { } id && !id.Equals(Guid.Empty) ? id.ToString("N") : string.Empty,
            eventArgs.RemoteEndPoint,
            eventArgs.DeviceId ?? string.Empty,
            eventArgs.DeviceName ?? string.Empty,
            eventArgs.App ?? string.Empty);
        return Task.CompletedTask;
    }
}
