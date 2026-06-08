using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// The result of a per-record update, surfaced to the dashboard.
/// </summary>
public sealed class RecordOutcome
{
    /// <summary>Gets or sets the record identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the record's friendly name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the hostname acted on.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the update succeeded (or was skipped as unchanged).</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a value indicating whether the record was skipped without contacting the provider.</summary>
    public bool Skipped { get; set; }

    /// <summary>Gets or sets the outcome message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the run outcome: <c>Updated</c>, <c>Unchanged</c>, <c>No address</c>, or <c>Failed</c>.</summary>
    public string Action { get; set; } = string.Empty;
}

/// <summary>
/// The aggregate result of an update run, surfaced to the dashboard.
/// </summary>
public sealed class DNSUpdateOutcome
{
    /// <summary>Gets or sets the detected public IPv4 address, if any.</summary>
    public string? DetectedIPv4 { get; set; }

    /// <summary>Gets or sets the detected public IPv6 address, if any.</summary>
    public string? DetectedIPv6 { get; set; }

    /// <summary>Gets the per-record outcomes.</summary>
    public IList<RecordOutcome> Records { get; } = new List<RecordOutcome>();

    /// <summary>
    /// Gets the run level warnings, such as a wanted IP family resolving only to an internal address so
    /// nothing could be published. Surfaced to the dashboard after an immediate run.
    /// </summary>
    public IList<string> Warnings { get; } = new List<string>();
}
