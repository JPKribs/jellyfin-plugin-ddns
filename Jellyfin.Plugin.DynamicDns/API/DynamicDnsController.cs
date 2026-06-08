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

    private readonly DNSUpdateService _updateService;
    private readonly StatusStoreService _statusStore;
    private readonly SecretProtector _secrets;
    private readonly IEnumerable<IDNSProvider> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicDNSController"/> class.
    /// </summary>
    /// <param name="updateService">The update service.</param>
    /// <param name="statusStore">The runtime status store.</param>
    /// <param name="secrets">The secret protector for credentials at rest.</param>
    /// <param name="providers">The registered DNS providers, used to drive the provider dropdown and fields.</param>
    public DynamicDNSController(DNSUpdateService updateService, StatusStoreService statusStore, SecretProtector secrets, IEnumerable<IDNSProvider> providers)
    {
        _updateService = updateService;
        _statusStore = statusStore;
        _secrets = secrets;
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

        plugin.MutateConfiguration(current =>
        {
            current.Records = RecordMerge.Apply(current.Records, incoming.Records, ResolveSecret, () => Guid.NewGuid().ToString("N"));
            current.IPv4DetectionUrl = incoming.IPv4DetectionUrl ?? string.Empty;
            current.IPv6DetectionUrl = incoming.IPv6DetectionUrl ?? string.Empty;
            current.ForceUpdateHours = Math.Max(0, incoming.ForceUpdateHours);
            current.SkipInternalAddresses = incoming.SkipInternalAddresses;
            current.BackoffAfterFailures = Math.Max(0, incoming.BackoffAfterFailures);
            current.BackoffHours = Math.Max(1, incoming.BackoffHours);
            current.RequestTimeoutSeconds = Math.Max(1, incoming.RequestTimeoutSeconds);

            return true;
        });

        var status = _statusStore.Load();
        return Ok(plugin.ReadConfiguration(c => Redact(c, status)));
    }

    /// <summary>
    /// Runs an immediate update pass over every enabled record and returns the per-record outcome.
    /// Behaves like the scheduled run: a record whose IP is unchanged since its last successful update
    /// is skipped rather than re-pushed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregate update outcome.</returns>
    [HttpPost("RunNow")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DNSUpdateOutcome>> RunNow(CancellationToken cancellationToken)
    {
        var outcome = await _updateService.RunAsync(cancellationToken).ConfigureAwait(false);
        return Ok(outcome);
    }

    private string ResolveSecret(string? submitted, string? stored)
    {
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
