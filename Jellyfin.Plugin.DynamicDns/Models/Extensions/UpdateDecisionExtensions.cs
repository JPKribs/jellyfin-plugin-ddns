using System;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// Presentation for an <see cref="UpdateDecision"/>, so callers ask the decision how to render itself
/// rather than switching on it. This keeps the skip labels and messages in one place beside the enum.
/// </summary>
public static class UpdateDecisionExtensions
{
    /// <summary>Returns whether the decision is a skip rather than a push.</summary>
    /// <param name="decision">The update decision.</param>
    /// <returns>True for any skip, false for <see cref="UpdateDecision.Update"/>.</returns>
    public static bool IsSkip(this UpdateDecision decision) => decision != UpdateDecision.Update;

    /// <summary>
    /// Maps a skip decision to the action label, user message, and success flag shown for it. A
    /// no address skip is not a success, an unchanged skip is, since the record already serves the IP.
    /// </summary>
    /// <param name="decision">The skip decision.</param>
    /// <returns>The action, message, and whether it counts as a success.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the decision is not a skip.</exception>
    public static (string Action, string Message, bool Succeeded) SkipOutcome(this UpdateDecision decision) => decision switch
    {
        UpdateDecision.SkipNoAddress => ("No address", "No IP available for the enabled record type(s).", false),
        UpdateDecision.SkipUnchanged => ("IP Unchanged", "Update skipped.", true),
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Update is not a skip decision.")
    };
}
