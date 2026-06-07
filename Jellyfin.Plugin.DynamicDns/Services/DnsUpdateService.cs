using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Services;

/// <summary>
/// Detects the public IP and pushes each enabled record to its provider, recording status.
/// </summary>
public sealed class DnsUpdateService
{
    private readonly IpDetectionService _ipDetection;
    private readonly Dictionary<DnsProviderKind, IDnsProvider> _providers;
    private readonly SecretProtector _secrets;
    private readonly ILogger<DnsUpdateService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DnsUpdateService"/> class.
    /// </summary>
    /// <param name="ipDetection">The IP detection service.</param>
    /// <param name="providers">The registered DNS providers.</param>
    /// <param name="secrets">The secret protector for credentials at rest.</param>
    /// <param name="logger">The logger.</param>
    public DnsUpdateService(
        IpDetectionService ipDetection,
        IEnumerable<IDnsProvider> providers,
        SecretProtector secrets,
        ILogger<DnsUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _ipDetection = ipDetection;
        _secrets = secrets;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.Kind);
    }

    /// <summary>
    /// Runs an update pass over every enabled record. Records whose IP is unchanged since their last
    /// successful update are skipped.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregate outcome.</returns>
    public async Task<DnsUpdateOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var outcome = new DnsUpdateOutcome();
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return outcome;
        }

        var config = plugin.ReadConfiguration(c => c);

        // IP families are controlled globally; keep each record's flags in sync so providers honor them.
        // The same pass migrates any plaintext credentials to encrypted-at-rest (e.g. a secret just
        // entered in the UI, which arrives as plaintext) so they are never persisted in the clear again.
        plugin.MutateConfiguration(c =>
        {
            var changed = false;
            foreach (var r in c.Records)
            {
                if (r.UpdateIPv4 != c.EnableIPv4)
                {
                    r.UpdateIPv4 = c.EnableIPv4;
                    changed = true;
                }

                if (r.UpdateIPv6 != c.EnableIPv6)
                {
                    r.UpdateIPv6 = c.EnableIPv6;
                    changed = true;
                }

                var login = _secrets.Protect(r.Login);
                if (!string.Equals(login, r.Login, StringComparison.Ordinal))
                {
                    r.Login = login;
                    changed = true;
                }

                var password = _secrets.Protect(r.Password);
                if (!string.Equals(password, r.Password, StringComparison.Ordinal))
                {
                    r.Password = password;
                    changed = true;
                }
            }

            return changed;
        });

        // Always detect the public IP — even with no records — so the dashboard can show it.
        var ip = await _ipDetection.DetectAsync(config, config.EnableIPv4, config.EnableIPv6, cancellationToken).ConfigureAwait(false);
        outcome.DetectedIPv4 = ip.IPv4;
        outcome.DetectedIPv6 = ip.IPv6;

        // Record the detected public IP so the dashboard can show it read-only (written only on change).
        plugin.MutateConfiguration(c =>
        {
            var v4 = ip.IPv4 ?? string.Empty;
            var v6 = ip.IPv6 ?? string.Empty;
            if (string.Equals(c.LastDetectedIPv4, v4, StringComparison.Ordinal)
                && string.Equals(c.LastDetectedIPv6, v6, StringComparison.Ordinal))
            {
                return false;
            }

            c.LastDetectedIPv4 = v4;
            c.LastDetectedIPv6 = v6;
            return true;
        });

        var enabled = config.Records.Where(r => r.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return outcome;
        }

        var statusUpdates = new Dictionary<string, RecordOutcome>(StringComparer.Ordinal);

        foreach (var record in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new RecordOutcome
            {
                Id = record.Id,
                Name = string.IsNullOrWhiteSpace(record.Name) ? record.Hostname : record.Name,
                Hostname = record.Hostname
            };

            var decision = UpdatePolicy.Decide(record, ip);
            if (decision == UpdateDecision.SkipNoAddress)
            {
                result.Skipped = true;
                result.Success = false;
                result.Action = "No address";
                result.Message = "No IP available for the enabled record type(s).";
                outcome.Records.Add(result);
                statusUpdates[record.Id] = result;
                continue;
            }

            if (decision == UpdateDecision.SkipUnchanged)
            {
                result.Skipped = true;
                result.Success = true;
                result.Action = "IP Unchanged";
                result.Message = "Update skipped.";
                outcome.Records.Add(result);
                statusUpdates[record.Id] = result;
                continue;
            }

            if (!_providers.TryGetValue(record.Provider, out var provider))
            {
                result.Success = false;
                result.Action = "Failed";
                result.Message = "No provider registered for " + record.Provider + ".";
                outcome.Records.Add(result);
                statusUpdates[record.Id] = result;
                continue;
            }

            DnsUpdateResult providerResult;
            try
            {
                // Hand the provider a transient copy with decrypted credentials; the config keeps the
                // encrypted values.
                var live = record.WithSecrets(_secrets.Unprotect(record.Login), _secrets.Unprotect(record.Password));
                providerResult = await provider.UpdateAsync(live, ip, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected error updating {Host}", record.Hostname);
                providerResult = DnsUpdateResult.Fail("Unexpected error: " + ex.Message);
            }

            result.Success = providerResult.Success;
            result.Action = providerResult.Success ? "Updated" : "Failed";
            result.Message = providerResult.Message;

            outcome.Records.Add(result);

            statusUpdates[record.Id] = result;
        }

        PersistStatus(plugin, ip, statusUpdates);
        return outcome;
    }

    private static void PersistStatus(
        Plugin plugin,
        DetectedIp ip,
        Dictionary<string, RecordOutcome> statusUpdates)
    {
        if (statusUpdates.Count == 0)
        {
            return;
        }

        plugin.MutateConfiguration(config =>
        {
            var wrote = false;
            foreach (var record in config.Records)
            {
                if (!statusUpdates.TryGetValue(record.Id, out var result))
                {
                    continue;
                }

                record.LastStatus = result.Message;
                record.LastAction = result.Action;
                record.LastSuccess = result.Success;
                record.LastCheckedUtc = DateTime.UtcNow;

                if (result.Success && !result.Skipped)
                {
                    if (record.UpdateIPv4 && ip.IPv4 is not null)
                    {
                        record.LastIPv4 = ip.IPv4;
                    }

                    if (record.UpdateIPv6 && ip.IPv6 is not null)
                    {
                        record.LastIPv6 = ip.IPv6;
                    }
                }

                wrote = true;
            }

            return wrote;
        });
    }
}
