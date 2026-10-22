import { createConfigPage, confirmDialog, createChipSelect } from './configurationpage?name=ddns_jpkribs_shared.js';
import { PLUGIN_ID, TABS, errorText, showText, loadProviders, primaryRecord, recordNames, otherAddresses, addressLines, saveProxyHostnames } from './configurationpage?name=ddns_tabs.js';
import { COUNTRY_CODES, WORLD } from './configurationpage?name=ddns_worldmap.js';

export default function (view) {
    'use strict';

    var Shared = null;
    var defaults = null;
    // Both default sets, standard and low-memory, and which one this server uses.
    var defaultSets = null;
    // The values as last loaded or saved, so a save only writes what was changed on this page.
    var loaded = {};
    var settingsLoaded = false;
    // Until the history settings load, saving would overwrite them with the form's defaults.
    var historyLoaded = false;
    // The history settings as last loaded or saved, to tell whether leaving would lose changes.
    var historyBaseline = '';
    // What the server said about restarting Jellyfin after the last save, if anything.
    var restartNote = '';
    var countries = null;
    var serverCountry = '';
    // The proxy settings as last loaded or saved, for the certificate request.
    var proxySettings = null;
    // The Dynamic DNS fine-tuning, saved through the DDNS configuration rather than the proxy settings. The form
    // as last loaded or saved, to tell whether it changed. Null until loaded.
    var ddnsBaseline = null;
    var DDNS_IDS = ['ipv4Url', 'ipv6Url', 'forceUpdateHours', 'skipInternal', 'backoffAfterFailures', 'backoffHours', 'requestTimeout'];

    // The one DDNS record's finer settings, edited here and saved into the same record Configuration shows.
    // The record as last loaded or saved, or null when there is none.
    var record = null;
    // Records an older setup saved beyond the one shown. They are dropped on the next save.
    var extraCount = 0;
    var providers = null;
    var recordBaseline = null;
    var RECORD_FIELDS = [
        ['recEnabled', 'Enabled', 'on'], ['recUpdateIPv4', 'UpdateIPv4', 'on'], ['recUpdateIPv6', 'UpdateIPv6', 'check'],
        ['recServer', 'Server', 'text'], ['recTtl', 'Ttl', 'ttl'], ['recProxied', 'Proxied', 'check'],
        ['recWildcard', 'Wildcard', 'check'], ['recStatic', 'Static', 'check'], ['recMx', 'Mx', 'text'],
        ['recBackupMx', 'BackupMx', 'check']
    ];

    // The proxy's other addresses, its stored names that are not the record's, as last loaded or saved. Null until
    // loaded.
    var hostnamesBaseline = null;

    var MODE_NOTES = {
        ServerCountry: 'Only this server\'s country and any below can connect.',
        Block: 'The countries below are refused.',
        AllowOnly: 'Only the countries below can connect.',
        Off: 'Every country can connect.'
    };

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
        var html = Shared.escapeHtml(MODE_NOTES[effective] || '');
        // The server's country stands out in green, or the sentence turns yellow while it is still unknown.
        if (effective === 'ServerCountry') {
            html += serverCountry
                ? ' This server is in <strong class="ddns-country-known">' + Shared.escapeHtml(serverCountry) + '</strong>.'
                : ' <strong class="ddns-country-unknown">Until this server\'s country is known, everyone can connect.</strong>';
        }
        Shared.getEl('countryModeDescription').innerHTML = html;
        Shared.setVisible('countryPickerField', effective !== 'Off');
    }

    function refreshCountryStatus() {
        return Shared.apiRequest('Proxy/Status', 'GET').then(function (st) {
            if (!st) return;
            showCertRequest(st, proxySettings);
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

    function loadHistory() {
        return Shared.apiRequest('People', 'GET').then(function (d) {
            fillHistory(d && d.Settings);
            showLocationStatus(d && d.Location);
            historyLoaded = !!(d && d.Settings);
            historyBaseline = JSON.stringify(readHistory());
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

    function ddnsState() {
        return JSON.stringify(DDNS_IDS.map(function (id) {
            var el = Shared.getEl(id);
            return el.type === 'checkbox' ? el.checked : el.value.trim();
        }));
    }

    function ddnsDirty() {
        return ddnsBaseline !== null && ddnsState() !== ddnsBaseline;
    }

    // Uses the plugin's own config endpoints, so credentials on the records are redacted in transit and kept as
    // they are on save.
    function loadDdns() {
        ddnsBaseline = null;
        recordBaseline = null;
        var providersLoaded = providers ? Promise.resolve(providers)
            : loadProviders(Shared).then(function (p) { providers = p; return p; }).catch(function () { return null; });
        return Promise.all([Shared.apiRequest('Configuration', 'GET'), providersLoaded]).then(function (results) {
            var cfg = results[0];
            if (!cfg) { throw new Error('settings unavailable'); }
            fillRecord(primaryRecord(cfg.Records), (cfg.Records || []).length);
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
            ddnsBaseline = ddnsState();
            showText(Shared, 'ddnsLoadError', '');
        }).catch(function () {
            showText(Shared, 'ddnsLoadError', 'Could not load the Dynamic DNS settings.');
        });
    }

    // ===== The DDNS record =====

    function recordContainer(id) {
        var el = Shared.getEl(id);
        return el.closest('.inputContainer') || el.closest('.checkboxContainer');
    }

    // Hides what the record's provider does not use.
    function applyRecordFields() {
        if (!record || !providers) return;
        var spec = providers.specFor(providers.normalize(record.Provider));
        var adv = spec.a || [];
        var visible = {
            recUpdateIPv6: spec.v6 !== false, recServer: !!spec.s, recTtl: !!spec.t,
            recWildcard: adv.indexOf('wildcard') >= 0, recStatic: adv.indexOf('static') >= 0,
            recMx: adv.indexOf('mx') >= 0, recBackupMx: adv.indexOf('backupmx') >= 0
        };
        Object.keys(visible).forEach(function (id) {
            var box = recordContainer(id);
            if (box) box.classList.toggle('hidden', !visible[id]);
        });
    }

    function recordState() {
        return JSON.stringify(RECORD_FIELDS.map(function (f) {
            var el = Shared.getEl(f[0]);
            return el.type === 'checkbox' ? el.checked : el.value.trim();
        }));
    }

    function recordDirty() {
        return recordBaseline !== null && !!record && recordState() !== recordBaseline;
    }

    function fillRecord(r, total) {
        record = r || null;
        extraCount = Math.max(0, (total || 0) - (r ? 1 : 0));
        Shared.setVisible('recordFields', !!r);
        Shared.setVisible('recordEmpty', !r);
        var name = r ? (r.Hostname || r.Name || '') : '';
        Shared.getEl('recordIntro').textContent = r
            ? 'The finer settings of the DDNS record' + (name ? ' for ' + name : '') + '. Its provider, hostname and credentials are in step 2 on Configuration.'
            : '';
        showText(Shared, 'extraRecords', !extraCount ? ''
            : extraCount === 1 ? 'This server has 1 more DDNS record saved. It is removed on the next save, and only the record shown here is kept.'
                : 'This server has ' + extraCount + ' more DDNS records saved. They are removed on the next save, and only the record shown here is kept.');
        if (r) {
            RECORD_FIELDS.forEach(function (f) {
                var el = Shared.getEl(f[0]);
                var v = r[f[1]];
                if (f[2] === 'on') el.checked = v !== false;
                else if (f[2] === 'check') el.checked = !!v;
                else if (f[2] === 'ttl') el.value = v || 1;
                else el.value = v || '';
            });
            applyRecordFields();
        }
        recordBaseline = recordState();
    }

    // Copies this page's record settings onto the server's copy of the record.
    function mergeRecord(base) {
        var r = JSON.parse(JSON.stringify(base));
        RECORD_FIELDS.forEach(function (f) {
            var el = Shared.getEl(f[0]);
            if (f[2] === 'on' || f[2] === 'check') r[f[1]] = el.checked;
            else if (f[2] === 'ttl') r[f[1]] = Math.max(1, parseInt(el.value, 10) || 1);
            else r[f[1]] = el.value.trim();
        });
        return r;
    }

    // The names the record covers now, which a save replaces in the proxy's addresses.
    function namesNow() {
        return record ? recordNames(Shared, record.Id).catch(function () { return []; }) : Promise.resolve([]);
    }

    function hostnamesDirty() {
        return hostnamesBaseline !== null && JSON.stringify(addressLines(Shared.getEl('hostnames').value)) !== hostnamesBaseline;
    }

    function fillHostnames(hostnames, names) {
        Shared.getEl('hostnames').value = otherAddresses(hostnames, names || []).join('\n');
        hostnamesBaseline = JSON.stringify(addressLines(Shared.getEl('hostnames').value));
    }

    // Removes the record, and its names from the proxy's addresses. The other addresses stay.
    function removeRecord() {
        var button = Shared.getEl('btnRemoveRecord');
        if (button.disabled || !record) return;
        var label = record.Hostname || record.Name || 'the DDNS record';
        confirmDialog('Remove ' + label + '? This server stops keeping it pointed here, and the proxy stops answering on it. Other addresses stay. This cannot be undone.', { title: 'Remove Record', confirmText: 'Remove', destructive: true }).then(function (ok) {
            if (!ok) return;
            button.disabled = true;
            Shared.setStatus('recordStatus', 'Removing...', false, { timeout: 0 });
            var before = [];
            var removed = false;
            namesNow().then(function (names) {
                before = names;
                return Shared.apiRequest('Configuration', 'GET');
            }).then(function (fresh) {
                if (!fresh) { throw new Error('settings unavailable'); }
                fresh.Records = [];
                return Shared.apiRequest('Configuration', 'POST', fresh);
            }).then(function (result) {
                if (!result) { throw new Error('no reply'); }
                removed = true;
                fillRecord(null, 0);
                return saveProxyHostnames(Shared, before);
            }).then(function (result) {
                if (result && result.Settings) {
                    proxySettings = result.Settings;
                    fillHostnames(result.Settings.Hostnames, []);
                }
                Shared.setStatus('recordStatus', 'Removed.', false);
            }).catch(function (err) {
                return errorText(err, removed ? 'The proxy\'s addresses were not updated.' : 'Remove failed.').then(function (text) {
                    Shared.setStatus('recordStatus', removed ? 'The record was removed, but the proxy\'s addresses were not updated. ' + text : text, true, { timeout: 0 });
                });
            }).then(function () {
                button.disabled = false;
            });
        });
    }

    // ===== Request a New Certificate =====

    // Offered while the proxy runs with Let's Encrypt as the saved source and its terms accepted.
    var certRequestable = false;

    function showCertRequest(st, settings) {
        var cert = (st && st.Certificate) || {};
        certRequestable = cert.Mode === 'Automatic' && !!(settings && (settings.Certificate || {}).AcceptTerms) && !!(st && st.Running);
        Shared.getEl('btnRequestCert').disabled = !certRequestable;
    }

    function requestCertificate() {
        var button = Shared.getEl('btnRequestCert');
        if (button.disabled || !certRequestable) return;
        confirmDialog('Let\'s Encrypt is asked for a new certificate now, even if the current one is fine. The current one keeps working until the new one is ready.', { title: 'Request a New Certificate', confirmText: 'Request' }).then(function (ok) {
            if (!ok) return;
            button.disabled = true;
            Shared.setStatus('certRequestStatus', 'Requesting...', false, { timeout: 0 });
            Shared.apiRequest('Proxy/Certificate/Request', 'POST').then(function (result) {
                Shared.setStatus('certRequestStatus', (result && result.Message) || 'Done.', !(result && result.Ok), { timeout: 0 });
            }).catch(function () {
                Shared.setStatus('certRequestStatus', 'The request failed.', true, { timeout: 0 });
            }).then(function () {
                button.disabled = !certRequestable;
            });
        });
    }

    // Saves the Dynamic DNS fields and the record onto the freshest DDNS configuration, only when something
    // changed or extra records are left to drop. The record is kept as the only one. Resolves to an error
    // message, or empty when it worked or there was nothing to save.
    function saveDdns() {
        if (!ddnsDirty() && !recordDirty() && !extraCount) return Promise.resolve('');
        var posted = ddnsState();
        var id = record ? record.Id : null;
        return Shared.apiRequest('Configuration', 'GET').then(function (fresh) {
            if (!fresh) { throw new Error('settings unavailable'); }
            fresh.IPv4DetectionUrl = Shared.getEl('ipv4Url').value.trim();
            fresh.IPv6DetectionUrl = Shared.getEl('ipv6Url').value.trim();
            fresh.ForceUpdateHours = Math.max(0, parseInt(Shared.getEl('forceUpdateHours').value, 10) || 0);
            fresh.SkipInternalAddresses = Shared.getEl('skipInternal').checked;
            fresh.BackoffAfterFailures = Math.max(0, parseInt(Shared.getEl('backoffAfterFailures').value, 10) || 0);
            fresh.BackoffHours = Math.max(1, parseInt(Shared.getEl('backoffHours').value, 10) || 24);
            fresh.RequestTimeoutSeconds = Math.max(1, parseInt(Shared.getEl('requestTimeout').value, 10) || 15);
            var list = fresh.Records || [];
            var base = id ? list.find(function (x) { return x.Id === id; }) : null;
            var keep = base ? mergeRecord(base) : primaryRecord(list);
            fresh.Records = keep ? [keep] : [];
            return Shared.apiRequest('Configuration', 'POST', fresh);
        }).then(function (result) {
            if (!result) { throw new Error('no reply'); }
            ddnsBaseline = posted;
            fillRecord(primaryRecord(result.Records), (result.Records || []).length);
            return '';
        }).catch(function (err) {
            return errorText(err, 'Save failed.');
        });
    }

    // Whether leaving now would lose changes, against the values last loaded or saved.
    function isDirty() {
        return (settingsLoaded && JSON.stringify(read()) !== JSON.stringify(loaded))
            || (historyLoaded && JSON.stringify(readHistory()) !== historyBaseline)
            || ddnsDirty() || recordDirty() || hostnamesDirty();
    }

    function load() {
        settingsLoaded = false;
        historyLoaded = false;
        loadHistory();
        hostnamesBaseline = null;
        var ddnsDone = loadDdns();
        var defaultsLoaded = defaultSets ? Promise.resolve(defaultSets) : Shared.apiRequest('Proxy/Defaults', 'GET');
        Promise.all([defaultsLoaded, Shared.apiRequest('Proxy/Settings', 'GET')]).then(function (results) {
            defaultSets = results[0] || {};
            defaults = defaultSets.Values || {};
            showDefaults();
            fill(results[1] && results[1].Advanced);
            loaded = read();
            settingsLoaded = true;
            proxySettings = results[1];
            refreshCountryStatus();
            // The other addresses are the stored ones that are not the record's, so the record has to be known.
            return ddnsDone.then(namesNow).then(function (names) {
                fillHostnames((results[1] && results[1].Hostnames) || [], names);
            });
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
        var button = Shared.getEl('btnSaveAdvanced');
        if (button.disabled) return;
        button.disabled = true;
        Shared.setStatus('advancedStatus', 'Saving...', false);
        var ddnsChanged = ddnsDirty() || recordDirty() || extraCount > 0;
        var before = [];
        var ddnsError = '';
        // The DDNS settings and the record save first, so the proxy's addresses follow the record's names as now
        // saved. Each save goes to its own endpoint, so one failing never stops the other, and both are reported.
        namesNow().then(function (names) {
            before = names;
            return saveDdns();
        }).then(function (error) {
            ddnsError = error;
            return saveProxy(before);
        }).then(function (proxy) {
            if (!ddnsError) {
                var done = proxy.ok || !ddnsChanged ? proxy.text
                    : 'The proxy settings were not saved. ' + proxy.text + ' The Dynamic DNS settings were saved.';
                Shared.setStatus('advancedStatus', done, proxy.bad, proxy.keep ? { timeout: 0 } : undefined);
                return;
            }
            var text = (proxy.ok ? proxy.text : 'The proxy settings were not saved. ' + proxy.text) +
                ' The Dynamic DNS settings were not saved. ' + ddnsError;
            Shared.setStatus('advancedStatus', text, true, { timeout: 0 });
        }).then(function () {
            button.disabled = false;
        });
    }

    // Saves the proxy settings onto the freshest ones, then the history settings. The proxy answers on the
    // record's names and the other addresses. Resolves to how it went.
    function saveProxy(before) {
        var others = hostnamesDirty() ? addressLines(Shared.getEl('hostnames').value) : null;
        return saveProxyHostnames(Shared, before, others, function (fresh) {
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
            return fresh;
        }).then(function (result) {
            if (result && result.Settings) {
                proxySettings = result.Settings;
                fill(result.Settings.Advanced);
                loaded = read();
            }
            restartNote = (result && result.Restart) || '';
            return namesNow().then(function (names) {
                if (result && result.Settings) fillHostnames(result.Settings.Hostnames, names);
                return historyLoaded ? Shared.apiRequest('People/Settings', 'POST', readHistory()) : null;
            });
        }).then(function (history) {
            if (history) fillHistory(history);
            // History settings that never loaded were not sent, so the page does not claim to have saved them.
            var skipped = !historyLoaded;
            loadHistory();
            var text = 'Saved.' + (skipped ? ' The history settings did not load, so they were not saved.' : '') + (restartNote ? ' ' + restartNote : '');
            return { ok: true, bad: skipped, keep: skipped || !!restartNote, text: text };
        }).catch(function (err) {
            return errorText(err, 'Save failed.').then(function (text) {
                return { ok: false, bad: true, keep: true, text: text };
            });
        });
    }

    // Lists you built up yourself are not tuning, so a reset keeps them.
    var KEPT_ON_RESET = ['TrustedNetworks', 'BlockedAddresses', 'CountryMode', 'Countries'];

    function reset() {
        confirmDialog('Every limit and security setting goes back to its default, keeping trusted networks, the block list, country rules, other addresses, history and Dynamic DNS. Nothing changes until you save.', { title: 'Reset to Defaults', confirmText: 'Reset' }).then(function (ok) {
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
        Shared.getEl('btnRemoveRecord').addEventListener('click', removeRecord);
        Shared.getEl('btnRequestCert').addEventListener('click', requestCertificate);
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
