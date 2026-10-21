using System;
using System.Threading;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The current runtime snapshot, shared between the plugin and the proxy host. Swapping the reference is
/// how settings reach a running proxy without a restart.
/// </summary>
public sealed class ProxyRuntimeHolder
{
    private ProxyRuntime _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyRuntimeHolder"/> class.
    /// </summary>
    /// <param name="initial">The first snapshot.</param>
    public ProxyRuntimeHolder(ProxyRuntime initial)
    {
        _current = initial;
    }

    /// <summary>Raised after a new snapshot is set, so open connections can be checked against new rules.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets or sets the current snapshot.</summary>
    public ProxyRuntime Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            Volatile.Write(ref _current, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
