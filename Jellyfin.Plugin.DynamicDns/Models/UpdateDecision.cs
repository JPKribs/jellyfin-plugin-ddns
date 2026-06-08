namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>The decision on whether a record needs to be pushed to its provider this run.</summary>
public enum UpdateDecision
{
    /// <summary>Push the record now.</summary>
    Update,

    /// <summary>Skip: no usable address for the record's enabled families.</summary>
    SkipNoAddress,

    /// <summary>Skip: the address is unchanged since the last successful update.</summary>
    SkipUnchanged
}
