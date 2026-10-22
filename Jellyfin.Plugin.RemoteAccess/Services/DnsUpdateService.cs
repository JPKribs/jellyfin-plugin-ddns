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

    // Longest provider reply kept in a record's status and the activity log.
    private const int MaxMessageLength = 300;

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
        // Backstop only. Every save path encrypts (the dashboard endpoint and the OnConfigurationSaving
        // hook on the generic endpoint), but a config written before those hooks existed, or edited
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

        var timeout = config.RequestTimeout();

        var enabled = config.Records.Where(r => r.Enabled).ToList();

        // Single-address protocols cannot push an AAAA record, so IPv6 is cleared on those records up
        // front (these are the run's snapshot clones, never the stored configuration). Detection, the
        // update decision, and the status bookkeeping then all agree no IPv6 push is pending, instead of
        // endlessly re-pushing because an address the protocol cannot deliver never matches DNS.
        foreach (var record in enabled.Where(r =>
            r.UpdateIPv6 && _providers.TryGetValue(r.Provider, out var p) && !p.SupportsIPv6))
        {
            record.UpdateIPv6 = false;
        }

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

        // Status is persisted even when the run is cancelled or fails part way, so a push that already
        // landed is never forgotten and re-sent (or its failure miscounted) on the next run.
        try
        {
            await UpdateRecordsAsync(enabled, status, ip, timeout, forceInterval, backoffThreshold, statusUpdates, outcome, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            LogOutcomes(enabled, outcome, ip);
            PersistStatus(status, plugin, enabled, statusUpdates, ip, backoffThreshold, backoffWindow);
        }

        return outcome;
    }

    private async Task UpdateRecordsAsync(
        List<DNSRecord> enabled,
        StatusData status,
        DetectedIP ip,
        TimeSpan timeout,
        TimeSpan forceInterval,
        int backoffThreshold,
        Dictionary<string, RecordOutcome> statusUpdates,
        DNSUpdateOutcome outcome,
        CancellationToken cancellationToken)
    {
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

            // A paused record is still retried once its address moves, so a pause never holds back a new IP.
            var now = DateTime.UtcNow;
            if (RecordBackoff.IsBackingOff(record, backoffThreshold, now) && !RecordBackoff.AddressMoved(record, ip))
            {
                result.Skipped = true;
                result.Success = false;
                result.Action = BackingOffAction;
                result.Message = PauseSummary(record, backoffThreshold) + " Next attempt after "
                    + record.BackoffUntilUtc!.Value.ToString("u", CultureInfo.InvariantCulture)
                    + " or when the public IP changes.";
                statusUpdates[record.Id] = result;
                outcome.Records.Add(result);
                continue;
            }

            // Proxied records compare against the last pushed IP, so they need no DNS lookup. Neither does a
            // record whose hostname field holds no resolvable name (a record id, several DuckDNS labels).
            var name = record.Proxied ? null : RecordTarget.ResolvableName(record);
            var dns = name is null
                ? DNSResolution.NotChecked
                : await _dnsLookup.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
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
                result.Message = "Stored credentials could not be decrypted. Please re-enter them in step 2, Address, on the Configuration tab.";
                statusUpdates[record.Id] = result;
                outcome.Records.Add(result);
                continue;
            }

            // A family DNS already serves is left out while the other one is pushed, where the provider sends
            // each family in its own request. A forced refresh needs neither, so it still sends both.
            var (needV4, needV6) = UpdatePolicy.FamiliesNeedingPush(record, ip, dns, now);
            var holdV4 = record.WantsIPv4(ip) && !needV4 && needV6;
            var holdV6 = record.WantsIPv6(ip) && !needV6 && needV4;

            var (providerResult, transient, heldV4, heldV6) = await PushAsync(provider, record, login, password, ip, timeout, holdV4, holdV6, cancellationToken).ConfigureAwait(false);
            result.Success = providerResult.Success;
            result.Action = providerResult.Success ? "Updated" : "Failed";

            // Provider replies are echoed into the status and activity log, so strip control characters
            // (no forged log lines) and cap the length (no flooding from an error page).
            result.Message = string.IsNullOrEmpty(providerResult.Message)
                ? string.Empty
                : FormatUtilities.SanitizeForLog(providerResult.Message, MaxMessageLength);
            result.IPv4Applied = providerResult.IPv4Applied;
            result.IPv6Applied = providerResult.IPv6Applied;
            result.IPv4Held = heldV4;
            result.IPv6Held = heldV6;
            result.Transient = !providerResult.Success && transient;
            result.DnsAlreadyServes = !providerResult.Success && ServesWanted(record, ip, dns);
            statusUpdates[record.Id] = result;
            outcome.Records.Add(result);
        }
    }

    // Describes why a record is paused: definitive failures earn the long pause, temporary ones a short one.
    private static string PauseSummary(DNSRecord record, int threshold)
        => record.ConsecutiveFailures >= threshold
            ? "Paused after " + record.ConsecutiveFailures + " consecutive failures."
            : "Paused after " + record.TransientFailures + " consecutive temporary failures. These are timeouts, network errors or provider server errors.";

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

            var record = enabled.Find(r => string.Equals(r.Id, result.Id, StringComparison.Ordinal));

            // The status still holds the run before this one, so a record that was failing is seen recovering.
            if (result.Success && record is { ConsecutiveFailures: > 0 })
            {
                _logger.LogInformation("Dynamic DNS update for {Host} works again after {Count} failed attempts", result.Hostname, record.ConsecutiveFailures);
                _activity.Log(
                    "Dynamic DNS update for " + result.Hostname + " works again",
                    "DynamicDNS.RecordRecovered",
                    $"It succeeded after {record.ConsecutiveFailures} failed attempts.");
            }

            if (result.Success && record is not null && Unchanged(record, ip))
            {
                // A periodic refresh of the same address is routine: the server log has it, the activity log
                // keeps to changes.
                _logger.LogDebug("Dynamic DNS refreshed {Host}, address unchanged", result.Hostname);
            }
            else if (result.Success)
            {
                var summary = NewAddressSummary(record, ip);
                _logger.LogInformation("Dynamic DNS updated {Host}{Summary}", result.Hostname, summary);
                _activity.Log("Dynamic DNS updated " + result.Hostname + summary, "DynamicDNS.RecordUpdated", result.Message);
            }
            else if (result.DnsAlreadyServes)
            {
                // DNS already points at this server, as after a failed forced refresh: nobody is cut off yet.
                _logger.LogWarning("Dynamic DNS update failed for {Host}, though DNS already serves the current address: {Message}", result.Hostname, result.Message);
                _activity.Log(
                    "Dynamic DNS update failed for " + result.Hostname,
                    "DynamicDNS.RecordFailed",
                    result.Message + " DNS already serves the current address, so nothing is cut off yet.",
                    LogLevel.Warning);
            }
            else
            {
                // The record now points at an old address, so remote clients may not reach the server: an error.
                _logger.LogError("Dynamic DNS update failed for {Host}: {Message}", result.Hostname, result.Message);
                _activity.Log(
                    "Dynamic DNS update failed for " + result.Hostname,
                    "DynamicDNS.RecordFailed",
                    result.Message,
                    LogLevel.Error);
            }
        }
    }

    // Whether a push only sent the addresses the record already had, as a forced refresh does.
    private static bool Unchanged(DNSRecord record, DetectedIP ip)
    {
        var v4 = record.WantsIPv4(ip);
        var v6 = record.WantsIPv6(ip);
        var sameV4 = !v4 || string.Equals(record.LastIPv4, ip.IPv4, StringComparison.Ordinal);
        var sameV6 = !v6 || string.Equals(record.LastIPv6, ip.IPv6, StringComparison.Ordinal);
        return (v4 || v6) && sameV4 && sameV6;
    }

    // Whether DNS already serves every detected address the record keeps, so a failed push cuts nobody off.
    private static bool ServesWanted(DNSRecord record, DetectedIP ip, DNSResolution dns)
    {
        var v4 = record.WantsIPv4(ip);
        var v6 = record.WantsIPv6(ip);
        var servesV4 = !v4 || dns.Serves(ip.IPv4);
        var servesV6 = !v6 || dns.Serves(ip.IPv6);
        return dns.Succeeded && (v4 || v6) && servesV4 && servesV6;
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

    private async Task<(DNSUpdateResult Result, bool Transient, bool HeldV4, bool HeldV6)> PushAsync(
        IDNSProvider provider,
        DNSRecord record,
        string login,
        string password,
        DetectedIP ip,
        TimeSpan timeout,
        bool holdV4,
        bool holdV6,
        CancellationToken cancellationToken)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reqCts.CancelAfter(timeout);

        // Watch the provider's HTTP responses so a failure can be classed as temporary or definitive. The
        // attempt also tells a per-family provider which family is already current.
        var attempt = ProviderAttempt.Begin(holdV4, holdV6, cancellationToken);

        try
        {
            // Hand the provider a transient copy with the decrypted credentials. The config keeps the
            // encrypted values.
            var live = record.WithSecrets(login, password);
            var result = await provider.UpdateAsync(live, ip, reqCts.Token).ConfigureAwait(false);
            return (result, attempt.FailureIsTransient, attempt.IPv4Held, attempt.IPv6Held);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per request timeout fired rather than an outer cancellation.
            return (DNSUpdateResult.Fail("Request timed out after " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s."), true, attempt.IPv4Held, attempt.IPv6Held);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error updating {Host}", record.Hostname);
            return (DNSUpdateResult.Fail("Unexpected error: " + ex.Message), false, attempt.IPv4Held, attempt.IPv6Held);
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
        // Records are read fresh rather than from the run's snapshot, so a record added mid-run is not
        // pruned and one edited mid-run to target something else does not inherit this run's status.
        var current = plugin.ReadConfiguration(c => c.Records
            .Where(r => !string.IsNullOrEmpty(r.Id))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Clone(), StringComparer.Ordinal));

        // The status this run produced per record: a value to write, or null to remove it.
        var written = new Dictionary<string, RecordStatus?>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        foreach (var record in enabled)
        {
            if (!statusUpdates.TryGetValue(record.Id, out var result))
            {
                continue;
            }

            if (current.TryGetValue(record.Id, out var edited) && !RecordTarget.SameTarget(record, edited))
            {
                written[record.Id] = null;
                continue;
            }

            // A record crossing into backoff this run gets one warning entry. While it stays paused the
            // pause is still in force, so the streak produces no further entries until it recovers. An
            // expired pause left behind by a past streak does not count.
            var wasBackingOff = record.BackoffUntilUtc is { } previousUntil && previousUntil > now;
            ApplyOutcome(record, result, ip, backoffThreshold, backoffWindow);
            if (!wasBackingOff && record.BackoffUntilUtc is not null)
            {
                _activity.Log(
                    "Dynamic DNS paused updates for " + record.Hostname,
                    "DynamicDNS.RecordBackoff",
                    PauseSummary(record, backoffThreshold) + " The next attempt is after "
                    + record.BackoffUntilUtc.Value.ToString("u", CultureInfo.InvariantCulture)
                    + " or when the public IP changes.",
                    LogLevel.Warning);
            }

            written[record.Id] = RecordStatus.FromRecord(record);
        }

        // Merge into the status as it is on disk now rather than writing back the copy loaded when the run
        // started, so a reset the dashboard made mid-run (for a record this run did not touch) survives.
        _statusStore.Update(fresh =>
        {
            fresh.DetectedIPv4 = status.DetectedIPv4;
            fresh.DetectedIPv6 = status.DetectedIPv6;
            fresh.DetectionMessage = status.DetectionMessage;
            fresh.ConsecutiveDetectionFailures = status.ConsecutiveDetectionFailures;
            fresh.DetectionUnhealthyLogged = status.DetectionUnhealthyLogged;
            fresh.PlaintextWarningLogged = status.PlaintextWarningLogged;

            foreach (var (id, value) in written)
            {
                if (value is null)
                {
                    fresh.Records.Remove(id);
                }
                else
                {
                    fresh.Records[id] = value;
                }
            }

            // Drop status for records that no longer exist so the store does not grow without bound.
            foreach (var key in fresh.Records.Keys.Where(k => !current.ContainsKey(k)).ToList())
            {
                fresh.Records.Remove(key);
            }
        });
    }

    // Internal (not private) so the outcome bookkeeping matrix is testable without a full run.
    internal static void ApplyOutcome(DNSRecord record, RecordOutcome result, DetectedIP ip, int backoffThreshold, TimeSpan backoffWindow)
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

        // A family left out of the push because DNS already served it was not attempted, so it neither
        // landed nor failed this run.
        var v4Sent = record.WantsIPv4(ip) && !result.IPv4Held;
        var v6Sent = record.WantsIPv6(ip) && !result.IPv6Held;
        var v4Applied = result.IPv4Applied ?? (result.Success && v4Sent);
        var v6Applied = result.IPv6Applied ?? (result.Success && v6Sent);

        // A partial success (one family pushed, the other rejected) is still a failed attempt for backoff.
        // Otherwise a family the provider keeps refusing would be retried, and logged, on every run forever.
        // The family that landed is still recorded below, and later pushes leave it out where they can.
        RecordBackoff.ApplyAttempt(record, result.Success, result.Transient, backoffThreshold, backoffWindow, now);

        // Remember what each failed family tried to push, so a pause is lifted early only once an address
        // moves. A family that landed, or was not sent, keeps none. A success clears both.
        record.FailedIPv4 = !result.Success && v4Sent && !v4Applied ? ip.IPv4! : string.Empty;
        record.FailedIPv6 = !result.Success && v6Sent && !v6Applied ? ip.IPv6! : string.Empty;

        // Record only the addresses that actually landed, per family, so a family the provider rejected
        // (or cannot carry) is never marked as pushed and silently skipped forever after.
        if (v4Applied)
        {
            record.LastIPv4 = ip.IPv4!;
        }

        if (v6Applied)
        {
            record.LastIPv6 = ip.IPv6!;
        }

        if (result.Success)
        {
            // Every enabled family succeeded, so reset the force update clock.
            record.LastUpdateUtc = now;
        }
    }
}
