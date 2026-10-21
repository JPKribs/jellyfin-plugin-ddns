using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Records successful sign-ins.
/// </summary>
public sealed class SignInSucceededConsumer : IEventConsumer<AuthenticationResultEventArgs>
{
    private readonly PeopleService _people;

    /// <summary>
    /// Initializes a new instance of the <see cref="SignInSucceededConsumer"/> class.
    /// </summary>
    /// <param name="people">The People service.</param>
    public SignInSucceededConsumer(PeopleService people)
    {
        _people = people;
    }

    /// <inheritdoc />
    public Task OnEvent(AuthenticationResultEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        var user = eventArgs.User;
        var session = eventArgs.SessionInfo;
        _people.RecordSignIn(
            success: true,
            user?.Name ?? string.Empty,
            user is null ? string.Empty : user.Id.ToString("N"),
            session?.RemoteEndPoint,
            session?.DeviceId ?? string.Empty,
            session?.DeviceName ?? string.Empty,
            session?.Client ?? string.Empty);
        return Task.CompletedTask;
    }
}
