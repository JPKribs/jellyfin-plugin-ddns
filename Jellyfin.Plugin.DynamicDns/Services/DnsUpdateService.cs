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

    // Only one update pass runs at a time, so a manual run and the scheduled run never overlap.
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private readonly IPDetectionService _ipDetection;
    private readonly DNSLookupService _dnsLookup;
    private readonly StatusStoreService _statusStore;
    private readonly Dictionary<DNSProviderKind, IDNSProvider> _providers;
    private readonly SecretProtector _secrets;
    private readonly ILogger<DNSUpdateService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DNSUpdateService"/> class.
    /// </summary>
    /// <param name="ipDetection">The IP detection service.</param>
    /// <param name="dnsLookup">The DNS lookup service used to compare against live records.</param>
    /// <param name="statusStore">The store that holds runtime status outside the configuration.</param>
    /// <param name="providers">The registered DNS providers.</param>
    /// <param name="secrets">The secret protector for credentials at rest.</param>
    /// <param name="logger">The logger.</param>
    public DNSUpdateService(
        IPDetectionService ipDetection,
        DNSLookupService dnsLookup,
        StatusStoreService statusStore,
        IEnumerable<IDNSProvider> providers,
        SecretProtector secrets,
        ILogger<DNSUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _ipDetection = ipDetection;
        _dnsLookup = dnsLookup;
        _statusStore = statusStore;
        _secrets = secrets;
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

        if (plugin.SecretsPlaintext)
        {
            outcome.Warnings.Add(
                "Credential encryption is unavailable on this host, so provider credentials are stored "
                + "in plaintext. Check the server log for details.");
        }

        var status = _statusStore.Load();
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

        PersistStatus(status, plugin, enabled, statusUpdates, ip, backoffThreshold, backoffWindow);
        return outcome;
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

            ApplyOutcome(record, result, ip, backoffThreshold, backoffWindow);
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
