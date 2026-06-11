using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Services;

/// <summary>
/// Detects the public IP and pushes each enabled record to its provider, recording status.
/// </summary>
public sealed class DNSUpdateService : IDisposable
{
    // Action label written when a record is paused by the failure backoff policy.
    private const string BackingOffAction = "Backing off";

    // Consecutive failed detections before the activity log gets an unhealthy warning. Four runs is
    // about an hour on the default fifteen minute schedule, long enough to skip transient blips.
    private const int UnhealthyDetectionRuns = 4;

    // Only one update pass runs at a time, so a manual run and the scheduled run never overlap.
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private readonly IPDetectionService _ipDetection;
    private readonly DNSLookupService _dnsLookup;
    private readonly StatusStoreService _statusStore;
    private readonly Dictionary<DNSProviderKind, IDNSProvider> _providers;
    private readonly SecretProtector _secrets;
    private readonly ActivityLogger _activity;
    private readonly ILogger<DNSUpdateService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DNSUpdateService"/> class.
    /// </summary>
    /// <param name="ipDetection">The IP detection service.</param>
    /// <param name="dnsLookup">The DNS lookup service used to compare against live records.</param>
    /// <param name="statusStore">The store that holds runtime status outside the configuration.</param>
    /// <param name="providers">The registered DNS providers.</param>
    /// <param name="secrets">The secret protector for credentials at rest.</param>
    /// <param name="activity">The activity log writer.</param>
    /// <param name="logger">The logger.</param>
    public DNSUpdateService(
        IPDetectionService ipDetection,
        DNSLookupService dnsLookup,
        StatusStoreService statusStore,
        IEnumerable<IDNSProvider> providers,
        SecretProtector secrets,
        ActivityLogger activity,
        ILogger<DNSUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _ipDetection = ipDetection;
        _dnsLookup = dnsLookup;
        _statusStore = statusStore;
        _secrets = secrets;
        _activity = activity;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.Kind);
    }

    /// <summary>Releases the run lock.</summary>
    public void Dispose() => _runLock.Dispose();

    /// <summary>
    /// Runs an update pass over every enabled record. A record whose IP already matches is skipped. Only
    /// one pass runs at a time, so a second caller is told the run is busy rather than overlapping.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregate outcome.</returns>
    public async Task<DNSUpdateOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var outcome = new DNSUpdateOutcome();
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return outcome;
        }

        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            outcome.Warnings.Add("An update is already running, so this run was skipped.");
            return outcome;
        }

        try
        {
            return await RunCoreAsync(plugin, outcome, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<DNSUpdateOutcome> RunCoreAsync(Plugin plugin, DNSUpdateOutcome outcome, CancellationToken cancellationToken)
    {
        // Backstop only. Every save path encrypts (the dashboard endpoint and the UpdateConfiguration
        // override on the generic endpoint), but a config written before those hooks existed, or edited
        // directly on disk, is still caught here so a plaintext secret is never left bare.
        plugin.MutateConfiguration(c => CredentialEncryption.ProtectAll(c.Records, _secrets));

        // Snapshot the configuration under the lock so a save during the run cannot swap or mutate what
        // this pass iterates. Status written during the run lands on the clones, never on live config.
        var config = plugin.ReadConfiguration(Snapshot);

        var status = _statusStore.Load();

        if (plugin.SecretsPlaintext)
        {
            outcome.Warnings.Add(
                "Credential encryption is unavailable on this host, so provider credentials are stored "
                + "in plaintext. Check the server log for details.");

            // One activity log warning per encryption outage, not one per run.
            if (!status.PlaintextWarningLogged)
            {
                status.PlaintextWarningLogged = true;
                _activity.Log(
                    "Dynamic DNS is storing credentials in plaintext",
                    "DynamicDNS.PlaintextCredentials",
                    "Credential encryption is unavailable on this host. Check the server log for details.",
                    LogLevel.Warning);
            }
        }
        else
        {
            // Encryption is working again, so a future outage warns anew.
            status.PlaintextWarningLogged = false;
        }
        var timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds > 0 ? config.RequestTimeoutSeconds : 15);

        var enabled = config.Records.Where(r => r.Enabled).ToList();

        // The families to detect follow the records. IPv4 is also detected when there are no records yet,
        // so the dashboard can still show the current address.
        var needV4 = enabled.Count == 0 || enabled.Any(r => r.UpdateIPv4);
        var needV6 = enabled.Any(r => r.UpdateIPv6);

        var ip = await _ipDetection.DetectAsync(config, needV4, needV6, cancellationToken).ConfigureAwait(false);
        outcome.DetectedIPv4 = ip.IPv4;
        outcome.DetectedIPv6 = ip.IPv6;

        if (needV4 && ip.IPv4 is null && !string.IsNullOrEmpty(ip.IPv4Note))
        {
            outcome.Warnings.Add(ip.IPv4Note);
        }

        if (needV6 && ip.IPv6 is null && !string.IsNullOrEmpty(ip.IPv6Note))
        {
            outcome.Warnings.Add(ip.IPv6Note);
        }

        status.DetectedIPv4 = ip.IPv4 ?? string.Empty;
        status.DetectedIPv6 = ip.IPv6 ?? string.Empty;
        status.DetectionMessage = string.Join(" ", outcome.Warnings);

        // Detection health only matters once records exist, since with none configured there is
        // nothing the missing address would have updated.
        if (enabled.Count > 0)
        {
            UpdateDetectionHealth(status, (needV4 && ip.IPv4 is null) || (needV6 && ip.IPv6 is null), outcome);
        }

        var forceInterval = config.ForceUpdateHours > 0 ? TimeSpan.FromHours(config.ForceUpdateHours) : TimeSpan.Zero;
        var backoffThreshold = config.BackoffAfterFailures;
        var backoffWindow = TimeSpan.FromHours(config.BackoffHours > 0 ? config.BackoffHours : 24);

        var statusUpdates = new Dictionary<string, RecordOutcome>(StringComparer.Ordinal);

        foreach (var record in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Load this record's runtime status from the store so the decision sees the real history.
            (status.Records.GetValueOrDefault(record.Id) ?? new RecordStatus()).ApplyTo(record);

            var result = new RecordOutcome
            {
                Id = record.Id,
                Name = record.DisplayName(),
                Hostname = record.Hostname
            };

            var now = DateTime.UtcNow;
            if (BackoffPolicy.IsBackingOff(record, backoffThreshold, now))
            {
                result.Skipped = true;
                result.Success = false;
                result.Action = BackingOffAction;
                result.Message = "Paused after " + record.ConsecutiveFailures
                    + " consecutive failures. Next attempt after "
                    + record.BackoffUntilUtc!.Value.ToString("u", CultureInfo.InvariantCulture) + ".";
                statusUpdates[record.Id] = result;
                outcome.Records.Add(result);
                continue;
            }

            // Proxied records compare against the last pushed IP, so they need no DNS lookup.
            var dns = record.Proxied
                ? DNSResolution.Failed
                : await _dnsLookup.ResolveAsync(record.Hostname, cancellationToken).ConfigureAwait(false);
            var decision = UpdatePolicy.Decide(record, ip, dns, forceInterval, now);
            if (decision.IsSkip())
            {
                var (action, message, succeeded) = decision.SkipOutcome();
                result.Skipped = true;
                result.Success = succeeded;
                result.Action = action;
                result.Message = message;
                statusUpdates[record.Id] = result;
                outcome.Records.Add(result);
                continue;
            }

            if (!_providers.TryGetValue(record.Provider, out var provider))
            {
                result.Success = false;
                result.Action = "Failed";
                result.Message = "No provider registered for " + record.Provider + ".";
                statusUpdates[record.Id] = result;
                outcome.Records.Add(result);
                continue;
            }

            // Decrypt the stored credentials. An encrypted value that comes back empty means the key could
            // not decrypt it, so surface that clearly rather than letting the provider report a vague error.
            var login = _secrets.Unprotect(record.Login);
            var password = _secrets.Unprotect(record.Password);
            if ((SecretProtector.IsProtected(record.Login) && login.Length == 0)
                || (SecretProtector.IsProtected(record.Password) && password.Length == 0))
            {
                result.Success = false;
                result.Action = "Failed";
                result.Message = "Stored credentials could not be decrypted. Please re-enter them on the Domains tab.";
                statusUpdates[record.Id] = result;
                outcome.Records.Add(result);
                continue;
            }

            var providerResult = await PushAsync(provider, record, login, password, ip, timeout, cancellationToken).ConfigureAwait(false);
            result.Success = providerResult.Success;
            result.Action = providerResult.Success ? "Updated" : "Failed";
            result.Message = providerResult.Message;
            statusUpdates[record.Id] = result;
            outcome.Records.Add(result);
        }

        LogOutcomes(enabled, outcome, ip);
        PersistStatus(status, plugin, enabled, statusUpdates, ip, backoffThreshold, backoffWindow);
        return outcome;
    }

    /// <summary>
    /// Tracks detection health across runs so a persistent inability to find a public IP surfaces once
    /// in the activity log rather than on every run, with a recovery entry when detection returns.
    /// </summary>
    private void UpdateDetectionHealth(StatusData status, bool detectionFailed, DNSUpdateOutcome outcome)
    {
        if (detectionFailed)
        {
            status.ConsecutiveDetectionFailures++;
            if (status.ConsecutiveDetectionFailures >= UnhealthyDetectionRuns && !status.DetectionUnhealthyLogged)
            {
                status.DetectionUnhealthyLogged = true;
                _activity.Log(
                    "Dynamic DNS has been unable to detect a public IP",
                    "DynamicDNS.DetectionUnhealthy",
                    "Detection has failed for " + status.ConsecutiveDetectionFailures
                    + " runs in a row. " + string.Join(" ", outcome.Warnings),
                    LogLevel.Warning);
            }

            return;
        }

        if (status.DetectionUnhealthyLogged)
        {
            _activity.Log(
                "Dynamic DNS public IP detection recovered",
                "DynamicDNS.DetectionRecovered",
                "Detection succeeded after " + status.ConsecutiveDetectionFailures + " failed runs.");
        }

        status.ConsecutiveDetectionFailures = 0;
        status.DetectionUnhealthyLogged = false;
    }

    /// <summary>
    /// Writes activity log entries for the records that actually contacted their provider. Skips stay
    /// silent, a successful push is informational, and a failed push is an error. Backoff caps how many
    /// error entries one broken record can produce before it pauses.
    /// </summary>
    private void LogOutcomes(List<DNSRecord> enabled, DNSUpdateOutcome outcome, DetectedIP ip)
    {
        foreach (var result in outcome.Records)
        {
            if (result.Skipped)
            {
                continue;
            }

            if (result.Success)
            {
                var record = enabled.Find(r => string.Equals(r.Id, result.Id, StringComparison.Ordinal));
                _activity.Log(
                    "Dynamic DNS updated " + result.Hostname + NewAddressSummary(record, ip),
                    "DynamicDNS.RecordUpdated",
                    result.Message);
            }
            else
            {
                _activity.Log(
                    "Dynamic DNS update failed for " + result.Hostname,
                    "DynamicDNS.RecordFailed",
                    result.Message,
                    LogLevel.Error);
            }
        }
    }

    private static string NewAddressSummary(DNSRecord? record, DetectedIP ip)
    {
        if (record is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(2);
        if (record.WantsIPv4(ip))
        {
            parts.Add(ip.IPv4!);
        }

        if (record.WantsIPv6(ip))
        {
            parts.Add(ip.IPv6!);
        }

        return parts.Count == 0 ? string.Empty : " to " + string.Join(" and ", parts);
    }

    private static PluginConfiguration Snapshot(PluginConfiguration config) => new()
    {
        Records = config.Records.ConvertAll(r => r.Clone()),
        IPv4DetectionUrl = config.IPv4DetectionUrl,
        IPv6DetectionUrl = config.IPv6DetectionUrl,
        ForceUpdateHours = config.ForceUpdateHours,
        SkipInternalAddresses = config.SkipInternalAddresses,
        BackoffAfterFailures = config.BackoffAfterFailures,
        BackoffHours = config.BackoffHours,
        RequestTimeoutSeconds = config.RequestTimeoutSeconds
    };

    private async Task<DNSUpdateResult> PushAsync(
        IDNSProvider provider,
        DNSRecord record,
        string login,
        string password,
        DetectedIP ip,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reqCts.CancelAfter(timeout);

        try
        {
            // Hand the provider a transient copy with the decrypted credentials. The config keeps the
            // encrypted values.
            var live = record.WithSecrets(login, password);
            return await provider.UpdateAsync(live, ip, reqCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per request timeout fired rather than an outer cancellation.
            return DNSUpdateResult.Fail("Request timed out after " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error updating {Host}", record.Hostname);
            return DNSUpdateResult.Fail("Unexpected error: " + ex.Message);
        }
    }

    private void PersistStatus(
        StatusData status,
        Plugin plugin,
        List<DNSRecord> enabled,
        Dictionary<string, RecordOutcome> statusUpdates,
        DetectedIP ip,
        int backoffThreshold,
        TimeSpan backoffWindow)
    {
        foreach (var record in enabled)
        {
            if (!statusUpdates.TryGetValue(record.Id, out var result))
            {
                continue;
            }

            // A record crossing into backoff this run gets one warning entry. While it stays paused the
            // timestamp is already set, so the streak produces no further entries until it recovers.
            var wasBackingOff = record.BackoffUntilUtc is not null;
            ApplyOutcome(record, result, ip, backoffThreshold, backoffWindow);
            if (!wasBackingOff && record.BackoffUntilUtc is not null)
            {
                _activity.Log(
                    "Dynamic DNS paused updates for " + record.Hostname,
                    "DynamicDNS.RecordBackoff",
                    "Paused after " + record.ConsecutiveFailures + " consecutive failures. The next attempt is after "
                    + record.BackoffUntilUtc.Value.ToString("u", CultureInfo.InvariantCulture) + ".",
                    LogLevel.Warning);
            }

            status.Records[record.Id] = RecordStatus.FromRecord(record);
        }

        // Drop status for records that no longer exist so the store does not grow without bound. The ids
        // are read fresh rather than from the run's snapshot so a record added mid-run is not pruned.
        var validIds = plugin.ReadConfiguration(c => new HashSet<string>(c.Records.Select(r => r.Id), StringComparer.Ordinal));
        foreach (var key in status.Records.Keys.Where(k => !validIds.Contains(k)).ToList())
        {
            status.Records.Remove(key);
        }

        _statusStore.Save(status);
    }

    private static void ApplyOutcome(DNSRecord record, RecordOutcome result, DetectedIP ip, int backoffThreshold, TimeSpan backoffWindow)
    {
        var now = DateTime.UtcNow;
        record.LastStatus = result.Message;
        record.LastAction = result.Action;
        record.LastSuccess = result.Success;
        record.LastCheckedUtc = now;

        if (string.Equals(result.Action, BackingOffAction, StringComparison.Ordinal))
        {
            // Still paused. Leave the failure count and backoff time exactly as they are.
            return;
        }

        if (result.Skipped)
        {
            // A skip that did not contact the provider (no address or unchanged) leaves the counters alone.
            return;
        }

        BackoffPolicy.ApplyAttempt(record, result.Success, backoffThreshold, backoffWindow, now);

        if (result.Success)
        {
            // A real push succeeded, so reset the force update clock and record the addresses.
            record.LastUpdateUtc = now;

            if (record.WantsIPv4(ip))
            {
                record.LastIPv4 = ip.IPv4!;
            }

            if (record.WantsIPv6(ip))
            {
                record.LastIPv6 = ip.IPv6!;
            }
        }
    }
}
