import { createConfigPage } from './configurationpage?name=ddns_jpkribs_shared.js';
import { PLUGIN_ID, TABS, errorText, showText } from './configurationpage?name=ddns_tabs.js';

export default function (view) {
    'use strict';

    var Shared = null;
    // The form as last loaded or saved, to tell whether leaving would lose changes. Null until loaded.
    var baseline = null;

    var FORM_IDS = ['ipv4Url', 'ipv6Url', 'forceUpdateHours', 'skipInternal', 'backoffAfterFailures', 'backoffHours', 'requestTimeout'];

    function formState() {
        return JSON.stringify(FORM_IDS.map(function (id) {
            var el = Shared.getEl(id);
            return el.type === 'checkbox' ? el.checked : el.value.trim();
        }));
    }

    function isDirty() {
        return baseline !== null && formState() !== baseline;
    }

    // Use the plugin's own config endpoints so credentials on other records are redacted in transit and
    // any newly entered ones are encrypted at save (see DynamicDnsController).
    function getConfig() { return Shared.apiRequest('Configuration', 'GET'); }
    function saveConfig(cfg) { return Shared.apiRequest('Configuration', 'POST', cfg); }

    function showCurrentIp(cfg) {
        var parts = [cfg.LastDetectedIPv4, cfg.LastDetectedIPv6].filter(function (x) { return x; });
        Shared.getEl('currentIp').value = parts.length ? parts.join('   /   ') : 'Not detected yet';
    }

    // Warns when the last run could not resolve a public address for a wanted family, for example when the
    // detected address looked internal and nothing was published.
    function showDetectionWarning(message) {
        showText(Shared, 'detectionWarning', message);
    }

    function load() {
        baseline = null;
        getConfig().then(function (cfg) {
            if (!cfg) { Shared.setStatus('addressStatus', 'Could not load settings.', true); return; }
            Shared.getEl('ipv4Url').value = cfg.IPv4DetectionUrl || '';
            Shared.getEl('ipv6Url').value = cfg.IPv6DetectionUrl || '';
            // Select the matching preset, falling back to Off when the stored value is not one of them.
            var force = Shared.getEl('forceUpdateHours');
            force.value = String(cfg.ForceUpdateHours || 0);
            if (force.selectedIndex < 0) force.value = '0';
            Shared.getEl('skipInternal').checked = cfg.SkipInternalAddresses !== false;
            var bf = Shared.getEl('backoffAfterFailures');
            bf.value = String(cfg.BackoffAfterFailures == null ? 3 : cfg.BackoffAfterFailures);
            if (bf.selectedIndex < 0) bf.value = '3';
            var bh = Shared.getEl('backoffHours');
            bh.value = String(cfg.BackoffHours || 24);
            if (bh.selectedIndex < 0) bh.value = '24';
            Shared.getEl('requestTimeout').value = cfg.RequestTimeoutSeconds || 15;
            showCurrentIp(cfg);
            showDetectionWarning(cfg.LastDetectionMessage);
            baseline = formState();
        }).catch(function () {
            Shared.setStatus('addressStatus', 'Could not load settings.', true);
        });
    }

    function saveAddress() {
        var button = Shared.getEl('btnSaveAddress');
        if (button.disabled) return;
        button.disabled = true;
        var posted = formState();
        getConfig().then(function (fresh) {
            if (!fresh) { throw new Error('settings unavailable'); }
            fresh.IPv4DetectionUrl = Shared.getEl('ipv4Url').value.trim();
            fresh.IPv6DetectionUrl = Shared.getEl('ipv6Url').value.trim();
            fresh.ForceUpdateHours = Math.max(0, parseInt(Shared.getEl('forceUpdateHours').value, 10) || 0);
            fresh.SkipInternalAddresses = Shared.getEl('skipInternal').checked;
            fresh.BackoffAfterFailures = Math.max(0, parseInt(Shared.getEl('backoffAfterFailures').value, 10) || 0);
            fresh.BackoffHours = Math.max(1, parseInt(Shared.getEl('backoffHours').value, 10) || 24);
            fresh.RequestTimeoutSeconds = Math.max(1, parseInt(Shared.getEl('requestTimeout').value, 10) || 15);
            return saveConfig(fresh);
        }).then(function (saved) {
            if (!saved) { throw new Error('no reply'); }
            baseline = posted;
            Shared.setStatus('addressStatus', 'Saved.', false);
        }).catch(function (err) {
            return errorText(err, 'Save failed.').then(function (text) {
                Shared.setStatus('addressStatus', text, true, { timeout: 0 });
            });
        }).then(function () {
            button.disabled = false;
        });
    }

    // Says how the run went. With no record results, the configuration tells apart having no records,
    // having none enabled, and a run that left every record as it was.
    function runMessage(outcome, cfg) {
        var list = (outcome && outcome.Records) || [];
        var warnings = (outcome && outcome.Warnings) || [];
        var ok = list.filter(function (x) { return x.Success; }).length;
        var text;
        if (list.length) {
            text = 'Finished. ' + ok + ' of ' + list.length + ' records succeeded.';
        } else if (warnings.length) {
            text = '';
        } else if (!cfg) {
            text = 'Finished. No records were updated.';
        } else {
            var all = cfg.Records || [];
            var enabled = all.filter(function (r) { return r.Enabled !== false; }).length;
            text = !all.length ? 'IP detected. No records configured.'
                : !enabled ? 'IP detected. No records are enabled.'
                : 'Finished. No records were updated.';
        }

        return {
            text: [text].concat(warnings).filter(function (x) { return x; }).join(' '),
            bad: warnings.length > 0 || ok < list.length
        };
    }

    function runNow() {
        var button = Shared.getEl('btnRunNow');
        if (button.disabled) return;
        button.disabled = true;
        Shared.setStatus('runStatus', 'Updating...', false, { timeout: 0 });
        Shared.apiRequest('RunNow', 'POST').then(function (outcome) {
            return getConfig().catch(function () { return null; }).then(function (fresh) {
                var result = runMessage(outcome, fresh);
                Shared.setStatus('runStatus', result.text, result.bad, result.bad ? { timeout: 0 } : undefined);
                if (fresh) {
                    showCurrentIp(fresh);
                    showDetectionWarning(fresh.LastDetectionMessage);
                }
            });
        }).catch(function (err) {
            return errorText(err, 'Update failed.').then(function (text) {
                Shared.setStatus('runStatus', text, true, { timeout: 0 });
            });
        }).then(function () {
            button.disabled = false;
        });
    }

    function bind() {
        Shared.getEl('btnSaveAddress').addEventListener('click', saveAddress);
        Shared.getEl('btnRunNow').addEventListener('click', runNow);
        Shared.initCollapsibles();
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 2,
        tabs: TABS,
        bind: bind,
        load: load,
        isDirty: isDirty
    }).shared;
}
