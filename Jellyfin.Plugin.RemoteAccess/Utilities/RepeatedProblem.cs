using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Keeps a problem that repeats, such as a database that cannot be written or detection that keeps failing,
/// from filling the log: the first time is a warning, repeats are debug lines, and the first success after
/// it says so at information.
/// </summary>
internal sealed class RepeatedProblem
{
    private int _failing;

    /// <summary>
    /// Reports the problem happening again.
    /// </summary>
    /// <param name="log">Writes the line at the level given: warning the first time, debug after.</param>
    public void Failed(Action<LogLevel> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        log(Interlocked.Exchange(ref _failing, 1) == 0 ? LogLevel.Warning : LogLevel.Debug);
    }

    /// <summary>
    /// Reports success, which ends the problem.
    /// </summary>
    /// <param name="recovered">Writes the recovery line, only when the problem was current.</param>
    public void Succeeded(Action? recovered = null)
    {
        if (Interlocked.Exchange(ref _failing, 0) == 1)
        {
            recovered?.Invoke();
        }
    }
}
