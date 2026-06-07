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

    function showCurrentIp(cfg) {
        var parts = [cfg.LastDetectedIPv4, cfg.LastDetectedIPv6].filter(function (x) { return x; });
        el('currentIp').value = parts.length ? parts.join('   /   ') : 'Not detected yet';
    }

    function load() {
        Shared.getConfig().then(function (cfg) {
            el('enableIPv4').checked = cfg.EnableIPv4 !== false;
            el('enableIPv6').checked = !!cfg.EnableIPv6;
            el('ipv4Url').value = cfg.IPv4DetectionUrl || '';
            el('ipv6Url').value = cfg.IPv6DetectionUrl || '';
            showCurrentIp(cfg);
        });
    }

    function saveAddress() {
        Shared.getConfig().then(function (fresh) {
            fresh.EnableIPv4 = el('enableIPv4').checked;
            fresh.EnableIPv6 = el('enableIPv6').checked;
            fresh.IPv4DetectionUrl = el('ipv4Url').value.trim();
            fresh.IPv6DetectionUrl = el('ipv6Url').value.trim();
            return Shared.saveConfig(fresh);
        }).then(function () {
            Shared.setStatus('addressStatus', 'Saved.', false);
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
            return Shared.getConfig();
        }).then(function (fresh) {
            if (fresh) showCurrentIp(fresh);
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
