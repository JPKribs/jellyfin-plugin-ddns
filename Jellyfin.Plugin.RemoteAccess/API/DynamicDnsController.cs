using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DynamicDns.Api;

/// <summary>
/// Administrative endpoints backing the Dynamic DNS dashboard page.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("DynamicDns")]
[Produces("application/json")]
public class DynamicDNSController : ControllerBase
{
    // Stand-in returned to the browser for a stored credential and recognized on save as "unchanged".
    // It is deliberately not a value a real credential could take, so a saved record round-trips without
    // the credential ever leaving the server, and a typed replacement is distinguishable from it.
    private const string SecretSentinel = "__JPK_DDNS_SECRET_KEPT__";

    // Posted in a credential field to remove the stored value. A blank field keeps the stored value, so
    // clearing needs its own marker rather than an empty string.
    private const string SecretClearSentinel = "__JPK_DDNS_SECRET_CLEAR__";

    private readonly DNSUpdateService _updateService;
    private readonly StatusStoreService _statusStore;
    private readonly SecretProtector _secrets;
    private readonly ActivityLogger _activity;
    private readonly IEnumerable<IDNSProvider> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicDNSController"/> class.
    /// </summary>
    /// <param name="updateService">The update service.</param>
    /// <param name="statusStore">The runtime status store.</param>
    /// <param name="secrets">The secret protector for credentials at rest.</param>
    /// <param name="activity">The activity log writer.</param>
    /// <param name="providers">The registered DNS providers, used to drive the provider dropdown and fields.</param>
    public DynamicDNSController(DNSUpdateService updateService, StatusStoreService statusStore, SecretProtector secrets, ActivityLogger activity, IEnumerable<IDNSProvider> providers)
    {
        _updateService = updateService;
        _statusStore = statusStore;
        _secrets = secrets;
        _activity = activity;
        _providers = providers;
    }

    /// <summary>
    /// Returns every provider with its friendly label, hint, and fields, so the dashboard builds the
    /// provider dropdown and the per provider inputs from one source rather than a hand kept table.
    /// </summary>
    /// <returns>The providers sorted by label.</returns>
    [HttpGet("Providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetProviders()
    {
        var list = _providers
            .OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .Select(p => new
            {
                value = p.Kind.ToString(),
                label = p.Label,
                hint = p.Hint,
                fields = p.Fields
            });

        return Ok(list);
    }

    /// <summary>
    /// Returns the plugin configuration for the dashboard with stored credentials replaced by a
    /// write-only sentinel, so secrets (encrypted or not) never reach the browser. Runtime status is
    /// merged in from the status store.
    /// </summary>
    /// <returns>The redacted configuration.</returns>
    [HttpGet("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> GetConfiguration()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var status = _statusStore.Load();
        return Ok(plugin.ReadConfiguration(c => Redact(c, status)));
    }

    /// <summary>
    /// Saves the configuration from the dashboard. Newly entered credentials are encrypted in place so
    /// they are never persisted in the clear. An unchanged credential (the sentinel) keeps its stored
    /// value, records are matched to the existing ones by id, and a duplicate id is given a fresh one.
    /// </summary>
    /// <param name="incoming">The configuration submitted by the dashboard.</param>
    /// <returns>The redacted configuration as persisted.</returns>
    [HttpPost("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> SaveConfiguration([FromBody] PluginConfiguration incoming)
    {
        if (incoming is null)
        {
            return BadRequest();
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        // Checked before anything is changed: XML serialization fails partway on an unknown provider and
        // leaves a truncated configuration file behind. Encrypted values are never sent by the dashboard,
        // which shows a placeholder instead, so one arriving here was copied from somewhere else.
        incoming.Records ??= new();
        if (XmlText.FindInvalid(incoming) is { } bad)
        {
            return BadRequest(new { Errors = new List<string> { $"{bad} holds a character that cannot be saved." } });
        }

        if (incoming.Records.Any(r => r is null || !Enum.IsDefined(r.Provider)))
        {
            return BadRequest(new { Errors = new List<string> { "A record has an unknown provider." } });
        }

        if (incoming.Records.Any(r => string.IsNullOrWhiteSpace(r.Hostname)))
        {
            return BadRequest(new { Errors = new List<string> { "Every record needs a hostname." } });
        }

        if (incoming.Records.Any(r => SecretProtector.IsProtected(r.Login) || SecretProtector.IsProtected(r.Password)))
        {
            return BadRequest(new { Errors = new List<string> { "Encrypted values cannot be entered as credentials." } });
        }

        // The merge replaces the record list and never mutates the stored record objects, so the prior
        // list captured here still holds the pre save values for the audit diff after the mutation.
        List<DNSRecord>? before = null;
        List<DNSRecord>? after = null;
        var settingsChanged = false;
        plugin.MutateConfiguration(current =>
        {
            before = current.Records;
            settingsChanged = current.IPv4DetectionUrl != (incoming.IPv4DetectionUrl ?? string.Empty)
                || current.IPv6DetectionUrl != (incoming.IPv6DetectionUrl ?? string.Empty)
                || current.ForceUpdateHours != Math.Max(0, incoming.ForceUpdateHours)
                || current.SkipInternalAddresses != incoming.SkipInternalAddresses
                || current.BackoffAfterFailures != Math.Max(0, incoming.BackoffAfterFailures)
                || current.BackoffHours != Math.Max(1, incoming.BackoffHours)
                || current.RequestTimeoutSeconds != Math.Clamp(incoming.RequestTimeoutSeconds, 1, 300);
            current.Records = RecordMerge.Apply(current.Records, incoming.Records, ResolveSecret, () => Guid.NewGuid().ToString("N"));
            after = current.Records;
            current.IPv4DetectionUrl = incoming.IPv4DetectionUrl ?? string.Empty;
            current.IPv6DetectionUrl = incoming.IPv6DetectionUrl ?? string.Empty;
            current.ForceUpdateHours = Math.Max(0, incoming.ForceUpdateHours);
            current.SkipInternalAddresses = incoming.SkipInternalAddresses;
            current.BackoffAfterFailures = Math.Max(0, incoming.BackoffAfterFailures);
            current.BackoffHours = Math.Max(1, incoming.BackoffHours);
            current.RequestTimeoutSeconds = Math.Clamp(incoming.RequestTimeoutSeconds, 1, 300);

            return true;
        });

        var userId = ActivityUser.Of(User);
        ConfigurationAudit.LogRecordChanges(_activity, before, after, userId);
        if (settingsChanged)
        {
            _activity.Log("Dynamic DNS settings were changed", "DynamicDNS.SettingsChanged", "Detection, refresh or backoff settings were changed.", userId: userId);
        }

        // Runtime status is keyed by record id, so a record edited to point somewhere else (another
        // provider, host, zone, server, or account) would otherwise inherit the old target's backoff and
        // last pushed addresses. Start those records over.
        _statusStore.RemoveRecords(RetargetedIds(before, after));

        var status = _statusStore.Load();
        return Ok(plugin.ReadConfiguration(c => Redact(c, status)));
    }

    /// <summary>
    /// Runs an immediate update pass over every enabled record and returns the per-record outcome.
    /// Behaves like the scheduled run: a record whose IP is unchanged since its last successful update
    /// is skipped rather than re-pushed.
    /// </summary>
    /// <remarks>
    /// The run is deliberately not tied to the request's abort token. Navigating away mid run would
    /// otherwise cancel it after some records were already pushed, and those pushes would be forgotten.
    /// </remarks>
    /// <returns>The aggregate update outcome.</returns>
    [HttpPost("RunNow")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DNSUpdateOutcome>> RunNow()
    {
        var outcome = await _updateService.RunAsync(CancellationToken.None).ConfigureAwait(false);
        return Ok(outcome);
    }

    // Ids of records kept across a save whose target or credentials changed.
    internal static IEnumerable<string> RetargetedIds(IReadOnlyList<DNSRecord>? before, IReadOnlyList<DNSRecord>? after)
    {
        if (before is null || after is null)
        {
            return Array.Empty<string>();
        }

        var prior = before
            .Where(r => !string.IsNullOrEmpty(r.Id))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return after
            .Where(r => prior.TryGetValue(r.Id, out var old) && !RecordTarget.SameTarget(old, r))
            .Select(r => r.Id)
            .ToList();
    }

    internal string ResolveSecret(string? submitted, string? stored)
    {
        // An explicit clear removes the stored value.
        if (string.Equals(submitted, SecretClearSentinel, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // Sentinel or blank means no replacement was typed, so keep whatever is already stored.
        if (string.IsNullOrEmpty(submitted) || string.Equals(submitted, SecretSentinel, StringComparison.Ordinal))
        {
            return stored ?? string.Empty;
        }

        // A real value was typed: encrypt it now so it is never written to disk in the clear.
        return _secrets.Protect(submitted);
    }

    private static PluginConfiguration Redact(PluginConfiguration config, StatusData status)
    {
        return new PluginConfiguration
        {
            IPv4DetectionUrl = config.IPv4DetectionUrl,
            IPv6DetectionUrl = config.IPv6DetectionUrl,
            ForceUpdateHours = config.ForceUpdateHours,
            SkipInternalAddresses = config.SkipInternalAddresses,
            BackoffAfterFailures = config.BackoffAfterFailures,
            BackoffHours = config.BackoffHours,
            RequestTimeoutSeconds = config.RequestTimeoutSeconds,
            LastDetectedIPv4 = status.DetectedIPv4,
            LastDetectedIPv6 = status.DetectedIPv6,
            LastDetectionMessage = status.DetectionMessage,
            Records = config.Records.Select(r => RedactRecord(r, status.Records.GetValueOrDefault(r.Id))).ToList()
        };
    }

    private static DNSRecord RedactRecord(DNSRecord record, RecordStatus? status)
    {
        // WithSecrets clones the record, so the live configuration is never mutated by redaction.
        var copy = record.WithSecrets(
            string.IsNullOrEmpty(record.Login) ? string.Empty : SecretSentinel,
            string.IsNullOrEmpty(record.Password) ? string.Empty : SecretSentinel);

        // Populate the runtime status from the store for the dashboard to display.
        (status ?? new RecordStatus()).ApplyTo(copy);
        return copy;
    }
}
