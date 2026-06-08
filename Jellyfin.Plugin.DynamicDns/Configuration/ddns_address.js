export default function (view) {
    'use strict';

    var PLUGIN_ID = '8150dce5-6153-4924-a985-a6927be95baa';
    var TABS = [
        { href: 'configurationpage?name=ddns_domains', name: 'Domains' },
        { href: 'configurationpage?name=ddns_address', name: 'Address' }
    ];

    var Shared = null;
    var setTabs = null;
    var initCollapsibles = null;
    var _sharedPromise = import('/web/configurationpage?name=ddns_jpkribs_shared.js').then(function (mod) {
        Shared = mod.createShared(view, PLUGIN_ID, 'DynamicDns');
        setTabs = mod.setTabs;
        initCollapsibles = mod.initCollapsibles;
    });

    var _bound = false;

    function el(id) { return view.querySelector('#' + id); }

    // Use the plugin's own config endpoints so credentials on other records are redacted in transit and
    // any newly entered ones are encrypted at save (see DynamicDnsController).
    function getConfig() { return Shared.apiRequest('Configuration', 'GET'); }
    function saveConfig(cfg) { return Shared.apiRequest('Configuration', 'POST', cfg); }

    function showCurrentIp(cfg) {
        var parts = [cfg.LastDetectedIPv4, cfg.LastDetectedIPv6].filter(function (x) { return x; });
        el('currentIp').value = parts.length ? parts.join('   /   ') : 'Not detected yet';
    }

    // Shows the read only detection warning when the last run could not resolve a public address for a
    // wanted family, for example when the detected address looked internal and nothing was published.
    function showDetectionWarning(message) {
        var box = el('detectionWarning');
        var text = message || '';
        box.textContent = text;
        box.style.display = text ? '' : 'none';
    }

    function load() {
        getConfig().then(function (cfg) {
            if (!cfg) { Shared.setStatus('addressStatus', 'Could not load settings.', true); return; }
            el('ipv4Url').value = cfg.IPv4DetectionUrl || '';
            el('ipv6Url').value = cfg.IPv6DetectionUrl || '';
            // Select the matching preset, falling back to Off when the stored value is not one of them.
            var force = el('forceUpdateHours');
            force.value = String(cfg.ForceUpdateHours || 0);
            if (force.selectedIndex < 0) force.value = '0';
            el('skipInternal').checked = cfg.SkipInternalAddresses !== false;
            var bf = el('backoffAfterFailures');
            bf.value = String(cfg.BackoffAfterFailures == null ? 3 : cfg.BackoffAfterFailures);
            if (bf.selectedIndex < 0) bf.value = '3';
            var bh = el('backoffHours');
            bh.value = String(cfg.BackoffHours || 24);
            if (bh.selectedIndex < 0) bh.value = '24';
            el('requestTimeout').value = cfg.RequestTimeoutSeconds || 15;
            showCurrentIp(cfg);
            showDetectionWarning(cfg.LastDetectionMessage);
        }).catch(function () {
            Shared.setStatus('addressStatus', 'Could not load settings.', true);
        });
    }

    function saveAddress() {
        getConfig().then(function (fresh) {
            if (!fresh) { throw new Error('settings unavailable'); }
            fresh.IPv4DetectionUrl = el('ipv4Url').value.trim();
            fresh.IPv6DetectionUrl = el('ipv6Url').value.trim();
            fresh.ForceUpdateHours = Math.max(0, parseInt(el('forceUpdateHours').value, 10) || 0);
            fresh.SkipInternalAddresses = el('skipInternal').checked;
            fresh.BackoffAfterFailures = Math.max(0, parseInt(el('backoffAfterFailures').value, 10) || 0);
            fresh.BackoffHours = Math.max(1, parseInt(el('backoffHours').value, 10) || 24);
            fresh.RequestTimeoutSeconds = Math.max(1, parseInt(el('requestTimeout').value, 10) || 15);
            return saveConfig(fresh);
        }).then(function (saved) {
            Shared.setStatus('addressStatus', saved ? 'Saved.' : 'Save failed.', !saved);
        }).catch(function () {
            Shared.setStatus('addressStatus', 'Save failed.', true);
        });
    }

    function runNow() {
        Shared.setStatus('runStatus', 'Updating...', false);
        Shared.apiRequest('RunNow', 'POST').then(function (outcome) {
            var list = (outcome && outcome.Records) || [];
            var ok = list.filter(function (x) { return x.Success; }).length;
            var msg = list.length
                ? 'Done: ' + ok + ' of ' + list.length + ' records OK.'
                : 'IP detected. No records configured.';
            Shared.setStatus('runStatus', msg, list.length > 0 && ok < list.length);
            return getConfig();
        }).then(function (fresh) {
            if (fresh) {
                showCurrentIp(fresh);
                showDetectionWarning(fresh.LastDetectionMessage);
            }
        }).catch(function () {
            Shared.setStatus('runStatus', 'Update failed.', true);
        });
    }

    function bind() {
        el('btnSaveAddress').addEventListener('click', saveAddress);
        el('btnRunNow').addEventListener('click', runNow);
        initCollapsibles(view);
    }

    view.addEventListener('viewshow', function () {
        _sharedPromise.then(function () {
            setTabs('ddns', 1, TABS);
            if (!_bound) { bind(); _bound = true; }
            load();
        });
    });
}
