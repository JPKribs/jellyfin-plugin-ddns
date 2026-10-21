import { createConfigPage, confirmDialog, createChipSelect } from './configurationpage?name=ddns_jpkribs_shared.js';
import { TABS, errorText } from './configurationpage?name=ddns_tabs.js';
import { COUNTRY_CODES, WORLD } from './configurationpage?name=ddns_worldmap.js';

export default function (view) {
    'use strict';

    var PLUGIN_ID = '8150dce5-6153-4924-a985-a6927be95baa';
    var Shared = null;
    var defaults = null;
    // Both default sets, standard and low-memory, and which one this server uses.
    var defaultSets = null;
    // The values as last loaded or saved, so a save only writes what was changed on this page.
    var loaded = {};
    var settingsLoaded = false;
    // What the server said about restarting Jellyfin after the last save, if anything.
    var restartNote = '';
    var countries = null;

    var MODE_NOTES = {
        ServerCountry: 'Only this server\'s country and any below can connect.',
        Block: 'The countries below are refused.',
        AllowOnly: 'Only the countries below can connect.',
        Off: 'Every country can connect.'
    };
    var serverCountry = '';

    var LOCATION_NOTES = {
        Country: 'Looked up on this server. Nothing is sent anywhere.',
        City: 'A larger download, about 130 MB. Cities are approximate.',
        Off: 'No locations, unless a country rule needs them.'
    };
    var HISTORY_DEFAULTS = { Enabled: true, HistoryDays: 90, Location: 'Country' };

    // Country names in the browser's language, falling back to the map's English name.
    function countryName(code) {
        try {
            var names = new Intl.DisplayNames([navigator.language || 'en'], { type: 'region' });
            return names.of(code) || (WORLD[code] && WORLD[code].name) || code;
        } catch (e) {
            return (WORLD[code] && WORLD[code].name) || code;
        }
    }

    function countryOptions() {
        return COUNTRY_CODES.map(function (code) { return { value: code, label: countryName(code) }; })
            .sort(function (a, b) { return a.label.localeCompare(b.label); });
    }

    function showCountryMode() {
        var mode = Shared.getEl('advCountryMode').value;
        var effective = mode || (defaults && defaults.CountryMode) || 'ServerCountry';
        var text = MODE_NOTES[effective] || '';
        if (effective === 'ServerCountry') {
            text += serverCountry ? ' This server is in ' + serverCountry + '.' : ' Until this server\'s country is known, everyone can connect.';
        }
        Shared.getEl('countryModeDescription').textContent = text;
        Shared.setVisible('countryPickerField', effective !== 'Off');
    }

    function refreshCountryStatus() {
        return Shared.apiRequest('Proxy/Status', 'GET').then(function (st) {
            if (!st) return;
            serverCountry = st.ServerCountry || '';
            showCountryMode();
            var refused = st.CountryRefused || {};
            var parts = Object.keys(refused).sort(function (a, b) { return refused[b] - refused[a]; }).map(function (code) {
                return (code ? countryName(code) : 'Unknown') + ' ' + refused[code];
            });
            var text = parts.length
                ? 'Refused since Jellyfin started: ' + parts.join(', ') + '.'
                : 'Nothing has been refused since Jellyfin started.';
            if (!st.CountryDatabaseLoaded) text += ' The location database is not loaded yet. It downloads when country rules are saved.';
            Shared.getEl('countryStatus').textContent = text;
        }).catch(function () { /* status is optional */ });
    }

    function fields() {
        return Array.prototype.slice.call(view.querySelectorAll('[data-adv]'));
    }

    // A value in the field's own unit.
    function valueText(v, unit) {
        switch (unit) {
            case 'onoff': return v ? 'on' : 'off';
            case 'allowblock': return v ? 'block' : 'allow';
            case 'seconds': return v + ' seconds';
            case 'days': return v + ' days';
            case 'kb': return v + ' KB';
            case 'mb': return v === 0 ? 'unlimited' : v + ' MB';
            case 'count': return v === 0 ? 'unlimited' : String(v);
            case 'count-off':
            case 'bps': return v === 0 ? 'off' : String(v);
            case 'minutes': return v + ' minutes';
            default: return String(v);
        }
    }

    // Settings whose default depends on the server's memory. Left empty, they show Auto and follow it.
    function isAuto(key) {
        return !!(defaultSets && defaultSets.Standard && defaultSets.LowMemory)
            && JSON.stringify(defaultSets.Standard[key]) !== JSON.stringify(defaultSets.LowMemory[key]);
    }

    // Says what the default is, and for an Auto setting, what Auto picks and why.
    function defaultText(key, unit) {
        var v = defaults ? defaults[key] : null;
        if (v == null) return '';
        if (!isAuto(key)) return 'The default is ' + valueText(v, unit) + '.';
        var standard = valueText(defaultSets.Standard[key], unit);
        var low = valueText(defaultSets.LowMemory[key], unit);
        var gb = Math.round((defaultSets.LowMemoryBelowMb || 3072) / 1024) + ' GB';
        return defaultSets.IsLowMemory
            ? 'Leave empty for Auto, which uses ' + low + ' here, since this server has under ' + gb + ' of memory, and ' + standard + ' on larger servers.'
            : 'Leave empty for Auto, which uses ' + standard + ' here and ' + low + ' on servers with under ' + gb + ' of memory.';
    }

    function showDefaults() {
        Array.prototype.slice.call(view.querySelectorAll('[data-default]')).forEach(function (el) {
            el.textContent = defaultText(el.dataset.default, el.dataset.unit);
        });
        fields().forEach(function (el) {
            if (isAuto(el.dataset.adv)) el.setAttribute('placeholder', 'Auto');
        });
    }

    // A setting left unset shows the default it runs with, except an Auto one, which stays empty.
    function fill(advanced) {
        advanced = advanced || {};
        if (countries) countries.setValue(advanced.Countries || []);
        fields().forEach(function (el) {
            var v = advanced[el.dataset.adv];
            if (v == null && defaults && !isAuto(el.dataset.adv)) v = defaults[el.dataset.adv];
            if (el.dataset.kind === 'lines') el.value = (v || []).join('\n');
            else el.value = v == null ? '' : String(v);
        });
        showCountryMode();
    }

    function read() {
        var advanced = {};
        fields().forEach(function (el) {
            var key = el.dataset.adv;
            var raw = el.value.trim();
            switch (el.dataset.kind) {
                case 'bool': advanced[key] = raw === '' ? null : raw === 'true'; break;
                case 'int': advanced[key] = raw === '' || isNaN(parseInt(raw, 10)) ? null : parseInt(raw, 10); break;
                case 'lines': advanced[key] = raw.split(/\r?\n/).map(function (x) { return x.trim(); }).filter(function (x) { return x; }); break;
                case 'enum': advanced[key] = raw === '' ? null : raw; break;
                default: advanced[key] = raw;
            }
        });
        advanced.Countries = countries ? countries.getValue() : [];
        return advanced;
    }

    function showLocationNote() {
        Shared.getEl('locationDescription').textContent = LOCATION_NOTES[Shared.getEl('locationDetail').value] || '';
    }

    function fillHistory(settings) {
        var s = settings || HISTORY_DEFAULTS;
        Shared.getEl('historyEnabled').value = s.Enabled === false ? 'false' : 'true';
        Shared.getEl('historyDays').value = s.HistoryDays || HISTORY_DEFAULTS.HistoryDays;
        Shared.getEl('locationDetail').value = s.Location || HISTORY_DEFAULTS.Location;
        showLocationNote();
    }

    function readHistory() {
        return {
            Enabled: Shared.getEl('historyEnabled').value === 'true',
            HistoryDays: Math.max(1, parseInt(Shared.getEl('historyDays').value, 10) || HISTORY_DEFAULTS.HistoryDays),
            Location: Shared.getEl('locationDetail').value
        };
    }

    function showLocationStatus(loc) {
        loc = loc || {};
        var status;
        if (loc.Detail === 'Off') status = 'Location lookups are off.';
        else if (loc.Downloading) status = 'Downloading the location database...';
        else if (loc.Loaded) status = 'The location database is ready. It was built ' + new Date(loc.BuiltUtc).toLocaleDateString() + ' and is refreshed weekly by the Refresh Locations scheduled task.';
        else if (loc.Error) status = loc.Error;
        else status = 'The location database has not been downloaded yet.';
        Shared.getEl('locationStatus').textContent = status;
    }

    // Until the history settings load, saving would overwrite them with the form's defaults.
    var historyLoaded = false;

    function loadHistory() {
        return Shared.apiRequest('People', 'GET').then(function (d) {
            fillHistory(d && d.Settings);
            showLocationStatus(d && d.Location);
            historyLoaded = !!(d && d.Settings);
        }).catch(function () { /* the rest of the page still works */ });
    }

    function updateLocation() {
        Shared.setStatus('historyStatus', 'Downloading...', false, { timeout: 0 });
        Shared.apiRequest('People/Location/Update', 'POST').then(function (st) {
            Shared.setStatus('historyStatus', st && st.Loaded ? 'The location database is up to date.' : (st && st.Error) || 'The update did not finish.', !(st && st.Loaded));
            showLocationStatus(st);
        }).catch(function () {
            Shared.setStatus('historyStatus', 'The update failed.', true);
        });
    }

    function clearHistory() {
        confirmDialog('Every recorded visit, sign-in and refusal is deleted, and the block list is kept. This cannot be undone.', { title: 'Clear History', confirmText: 'Clear', destructive: true }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('People/History', 'DELETE').then(function () {
                Shared.setStatus('historyStatus', 'History cleared.', false);
            }).catch(function () {
                Shared.setStatus('historyStatus', 'Clearing failed.', true);
            });
        });
    }

    function load() {
        loadHistory();
        var defaultsLoaded = defaultSets ? Promise.resolve(defaultSets) : Shared.apiRequest('Proxy/Defaults', 'GET');
        Promise.all([defaultsLoaded, Shared.apiRequest('Proxy/Settings', 'GET')]).then(function (results) {
            defaultSets = results[0] || {};
            defaults = defaultSets.Values || {};
            showDefaults();
            fill(results[1] && results[1].Advanced);
            loaded = read();
            settingsLoaded = true;
            refreshCountryStatus();
        }).catch(function () {
            Shared.setStatus('advancedStatus', 'Could not load settings.', true);
        });
    }

    function save() {
        // Without the saved values to compare against, every field would look changed and be written empty.
        if (!settingsLoaded) {
            Shared.setStatus('advancedStatus', 'The settings did not load. Reload the page before saving.', true, { timeout: 0 });
            return;
        }

        // Turning this on can cut off whoever is using Jellyfin's own address, this page included, so it is confirmed.
        var current = read();
        var effective = function (v) { return v == null ? !!(defaults && defaults.JellyfinLoopbackOnly) : v; };
        if (effective(current.JellyfinLoopbackOnly) && !effective(loaded.JellyfinLoopbackOnly)) {
            confirmDialog('Everyone, at home too, will reach Jellyfin only through your Remote Access address, so apps and casting that use the server\'s own address stop working. Use Undo before disabling the plugin, and see Recovery in the documentation if you lose access.', { title: 'Only Through the Proxy', confirmText: 'Turn On' }).then(function (ok) {
                if (ok) doSave();
            });
            return;
        }

        doSave();
    }

    function doSave() {
        Shared.setStatus('advancedStatus', 'Saving...', false);
        Shared.apiRequest('Proxy/Settings', 'GET').then(function (fresh) {
            // Settings this page does not show, such as the ports, are kept as they are.
            // Only fields changed here are written. The block list and countries can also change from the
            // Dashboard while this page is open, and those changes must survive a save made here.
            var current = read();
            fresh.Advanced = fresh.Advanced || {};
            Object.keys(current).forEach(function (key) {
                if (JSON.stringify(current[key]) === JSON.stringify(loaded[key])) return;
                // A value set back to the default is stored as unset, so it follows the default if that changes.
                // An Auto setting keeps a typed value, since the default it matches may not apply later.
                var isDefault = !Array.isArray(current[key]) && !isAuto(key) && defaults && current[key] === defaults[key];
                fresh.Advanced[key] = isDefault ? null : current[key];
            });
            return Shared.apiRequest('Proxy/Settings', 'POST', fresh);
        }).then(function (result) {
            if (result && result.Settings) {
                fill(result.Settings.Advanced);
                loaded = read();
            }
            restartNote = (result && result.Restart) || '';
            return historyLoaded ? Shared.apiRequest('People/Settings', 'POST', readHistory()) : null;
        }).then(function (history) {
            if (history) fillHistory(history);
            loadHistory();
            Shared.setStatus('advancedStatus', restartNote ? 'Saved. ' + restartNote : 'Saved.', false, restartNote ? { timeout: 0 } : undefined);
        }).catch(function (err) {
            errorText(err, 'Save failed.').then(function (text) {
                Shared.setStatus('advancedStatus', text, true, { timeout: 0 });
            });
        });
    }

    // Lists you built up yourself are not tuning, so a reset keeps them.
    var KEPT_ON_RESET = ['TrustedNetworks', 'BlockedAddresses', 'CountryMode', 'Countries'];

    function reset() {
        confirmDialog('Every limit and security setting goes back to its default, keeping trusted networks, the block list, country rules and history. Nothing changes until you save.', { title: 'Reset to Defaults', confirmText: 'Reset' }).then(function (ok) {
            if (!ok) return;
            var current = read();
            var kept = {};
            KEPT_ON_RESET.forEach(function (key) { kept[key] = current[key]; });
            fill(kept);
        });
    }

    function restartProxy() {
        Shared.setStatus('restartStatus', 'Restarting...', false);
        Shared.apiRequest('Proxy/Restart', 'POST').then(function (st) {
            var running = st && st.Running;
            Shared.setStatus('restartStatus', running ? 'The proxy is running.' : (st && st.Error) || 'The proxy is not running.', !running);
        }).catch(function () {
            Shared.setStatus('restartStatus', 'Restart failed.', true);
        });
    }

    function bind() {
        countries = createChipSelect({ options: countryOptions(), placeholder: 'Add a country' });
        Shared.getEl('countryPicker').appendChild(countries.element);
        Shared.getEl('advCountryMode').addEventListener('change', showCountryMode);
        Shared.getEl('btnSaveAdvanced').addEventListener('click', save);
        Shared.getEl('btnResetAdvanced').addEventListener('click', reset);
        Shared.getEl('btnRestartProxy').addEventListener('click', restartProxy);
        Shared.getEl('locationDetail').addEventListener('change', showLocationNote);
        Shared.getEl('btnUpdateLocation').addEventListener('click', updateLocation);
        Shared.getEl('btnDeleteAll').addEventListener('click', clearHistory);
        Shared.initCollapsibles();
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 5,
        tabs: TABS,
        bind: bind,
        load: load
    }).shared;
}
