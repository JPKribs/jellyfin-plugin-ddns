import { createConfigPage, confirmDialog } from './configurationpage?name=ddns_jpkribs_shared.js';
import { createHostnamePicker } from './configurationpage?name=ddns_hostnames.js';
import { PLUGIN_ID, TABS, errorText, showText } from './configurationpage?name=ddns_tabs.js';

export default function (view) {
    'use strict';

    var Shared = null;
    var routes = [];
    var current = -1;
    var hostnames = null;
    var globals = {};

    // Where each override's global value comes from, and how to say it in the field's description.
    var INHERITED = {
        MaxRequestBodyMbWan: 'mb',
        MaxRequestBodyMbLan: 'mb',
        BackendActivityTimeoutSeconds: 'seconds',
        HstsEnabled: 'onoff',
        HstsMaxAgeDays: 'days',
        SameOriginFramesOnly: 'frames'
    };

    function overrideFields() {
        return Array.prototype.slice.call(view.querySelectorAll('[data-ov]'));
    }

    function globalValueText(key) {
        var kind = INHERITED[key];
        var value = globals[key];
        if (value == null) return '';
        switch (kind) {
            case 'mb': return value === 0 ? 'unlimited' : value + ' MB';
            case 'seconds': return value + ' seconds';
            case 'days': return value + ' days';
            case 'onoff': return value ? 'on' : 'off';
            case 'frames': return value ? 'same site only' : 'any site';
            default: return String(value);
        }
    }

    // Each override's description ends with the global value it falls back to.
    function showInherited() {
        Array.prototype.slice.call(view.querySelectorAll('[data-inherit-text]')).forEach(function (el) {
            var text = globalValueText(el.dataset.inheritText);
            el.textContent = text ? 'Follows the Advanced tab, currently ' + text + '.' : '';
        });
        overrideFields().forEach(function (el) {
            if (el.tagName !== 'SELECT' && INHERITED[el.dataset.ov]) el.placeholder = 'Default';
        });
    }

    function readOverrides() {
        var o = {};
        overrideFields().forEach(function (el) {
            var key = el.dataset.ov;
            if (el.dataset.ovKind === 'bool') {
                o[key] = el.value === '' ? null : el.value === 'true';
            } else {
                var n = parseInt(el.value, 10);
                o[key] = el.value.trim() === '' || isNaN(n) ? null : n;
            }
        });
        return o;
    }

    function fillOverrides(o) {
        o = o || {};
        overrideFields().forEach(function (el) {
            var value = o[el.dataset.ov];
            el.value = value == null ? '' : String(value);
        });
    }

    function getSettings() { return Shared.apiRequest('Proxy/Settings', 'GET'); }

    function newRoute() {
        return {
            Id: Shared.generateGuid(), Name: '', Enabled: true, Hostnames: [], Target: '',
            Exposure: 'LanAndWan', PreserveHost: false, BackendTls: 'Verify', PinnedThumbprint: '',
            Overrides: {}
        };
    }

    // Keep the open route's edits in the list before switching, saving or deleting.
    function storeEditor() {
        if (current < 0 || current >= routes.length) return;
        var r = routes[current];
        r.Name = Shared.getEl('routeName').value.trim();
        r.Enabled = Shared.getEl('routeEnabled').checked;
        r.Hostnames = hostnames.getValue();
        r.Target = Shared.getEl('routeTarget').value.trim();
        r.Exposure = Shared.getEl('routeExposure').value;
        r.PreserveHost = Shared.getEl('routePreserveHost').checked;
        r.BackendTls = Shared.getEl('routeTls').value;
        r.PinnedThumbprint = Shared.getEl('routePin').value.trim();
        r.Overrides = readOverrides();
    }

    function showPin() {
        Shared.setVisible('routePinField', Shared.getEl('routeTls').value === 'Pinned');
    }

    function loadEditor() {
        var r = routes[current];
        Shared.getEl('routeName').value = r.Name || '';
        Shared.getEl('routeEnabled').checked = r.Enabled !== false;
        hostnames.setValue(r.Hostnames);
        Shared.getEl('routeTarget').value = r.Target || '';
        Shared.getEl('routeExposure').value = r.Exposure || 'LanAndWan';
        Shared.getEl('routePreserveHost').checked = !!r.PreserveHost;
        Shared.getEl('routeTls').value = r.BackendTls || 'Verify';
        Shared.getEl('routePin').value = r.PinnedThumbprint || '';
        fillOverrides(r.Overrides);
        showPin();
    }

    // A disabled route says so below the dropdown rather than in the option text.
    function showRouteState() {
        var r = routes[current];
        showText(Shared, 'routeState', r && r.Enabled === false ? 'Off, so it cannot be reached.' : '');
    }

    function render() {
        var select = Shared.getEl('selectRoute');
        select.innerHTML = routes.map(function (r, i) {
            var label = r.Name || (r.Hostnames || [])[0] || 'New service';
            return '<option value="' + i + '">' + Shared.escapeHtml(label) + '</option>';
        }).join('');

        var has = routes.length > 0;
        Shared.setVisible('emptyState', !has);
        Shared.setVisible('routeEditor', has);
        Shared.setVisible('btnDeleteRoute', has);
        if (has) {
            if (current < 0 || current >= routes.length) current = 0;
            select.value = String(current);
            loadEditor();
            showRouteState();
        } else {
            current = -1;
        }
    }

    function load() {
        Promise.all([getSettings(), hostnames.load(), Shared.apiRequest('Proxy/Defaults', 'GET')]).then(function (results) {
            var s = results[0];
            // What a route falls back to: the Advanced tab's value, or the built-in default when that is empty too.
            var advanced = (s && s.Advanced) || {};
            var defaults = (results[2] && results[2].Values) || {};
            globals = {};
            Object.keys(INHERITED).forEach(function (k) { globals[k] = advanced[k] != null ? advanced[k] : defaults[k]; });
            showInherited();
            routes = (s && s.Routes) || [];
            current = routes.length ? 0 : -1;
            render();
        }).catch(function () {
            Shared.setStatus('routesStatus', 'Could not load services.', true);
        });
    }

    function save() {
        storeEditor();
        Shared.setStatus('routesStatus', 'Saving...', false);
        getSettings().then(function (fresh) {
            fresh.Routes = routes;
            return Shared.apiRequest('Proxy/Settings', 'POST', fresh);
        }).then(function (result) {
            routes = (result && result.Settings && result.Settings.Routes) || routes;
            render();
            Shared.setStatus('routesStatus', 'Saved.', false);
        }).catch(function (err) {
            errorText(err, 'Save failed.').then(function (text) {
                Shared.setStatus('routesStatus', text, true, { timeout: 0 });
            });
        });
    }

    function bind() {
        hostnames = createHostnamePicker(Shared, 'routeHostnamePicker', 'routeHostnames');
        Shared.getEl('selectRoute').addEventListener('change', function (e) {
            storeEditor();
            current = parseInt(e.target.value, 10);
            render();
        });
        Shared.getEl('btnNewRoute').addEventListener('click', function () {
            storeEditor();
            routes.push(newRoute());
            current = routes.length - 1;
            render();
            Shared.getEl('routeName').focus();
        });
        Shared.getEl('btnDeleteRoute').addEventListener('click', function () {
            if (current < 0) return;
            var r = routes[current];
            confirmDialog('Delete ' + (r.Name || (r.Hostnames || [])[0] || 'this service') + '? It stops being reachable when you save.', { title: 'Delete service', confirmText: 'Delete', destructive: true }).then(function (ok) {
                if (!ok) return;
                routes.splice(current, 1);
                current = Math.min(current, routes.length - 1);
                render();
            });
        });
        Shared.getEl('routeTls').addEventListener('change', showPin);
        Shared.getEl('routeEnabled').addEventListener('change', function () {
            if (current >= 0) routes[current].Enabled = Shared.getEl('routeEnabled').checked;
            showRouteState();
        });
        Shared.getEl('btnSaveRoutes').addEventListener('click', save);
        Shared.initCollapsibles();
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 4,
        tabs: TABS,
        bind: bind,
        load: load
    }).shared;
}
