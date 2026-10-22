import { createConfigPage, confirmDialog } from './configurationpage?name=ddns_jpkribs_shared.js';
import { PLUGIN_ID, TABS, errorText, showText } from './configurationpage?name=ddns_tabs.js';

export default function (view) {
    'use strict';

    var Shared = null;

    // Provider list, hints, and field specs, filled by loadProviders. In PROVIDERS, p[0] is the enum name and
    // p[1] is the friendly label.
    var PROVIDERS = [];
    var PROVIDER_VALUES = [];
    var HINTS = {};
    var FIELDS = {};

    // Fallback spec showing every field for an unrecognized provider.
    var DEFAULT_FIELDS = { h: 'Hostname', l: 'Login', p: 'Password', z: 'Zone', s: true, t: true, v6: true, a: ['wildcard', 'static', 'mx', 'backupmx'] };

    // The records on the page: the saved ones, each possibly with edits not saved yet, followed by new
    // records that were never saved. A record's credentials always hold what the server last sent, its
    // redacted marker or empty, never a typed value.
    var records = [];
    // The records as the server last sent them, by id. A record missing here was never saved.
    var saved = {};
    var loaded = false;
    var busy = false;
    var currentIndex = -1;
    var editorEnabled = true;

    // Posted in a credential field to remove the stored value. A blank field keeps it, so clearing is an
    // explicit action.
    var CLEAR_SECRET = '__JPK_DDNS_SECRET_CLEAR__';
    var CLEAR_BUTTONS = { recLogin: 'btnClearLogin', recPassword: 'btnClearPassword' };
    var SECRET_FIELDS = { recLogin: 'Login', recPassword: 'Password' };
    // Credential edits not saved yet, by record id and input id: { typed, cleared }. Kept apart from the
    // record so a typed value is never mistaken for the saved one when the record is shown again.
    var pendingSecrets = {};
    // Whether the stored value of the field now shown is marked for removal. Reset whenever the editor
    // loads a record.
    var clearedSecrets = {};

    // The settings that can be edited, compared to tell whether a record has unsaved edits.
    var EDITABLE = ['Name', 'Provider', 'Hostname', 'Enabled', 'UpdateIPv4', 'UpdateIPv6', 'Zone', 'Server', 'Ttl',
        'Proxied', 'Wildcard', 'Static', 'Mx', 'BackupMx'];
    // The switches among them, and the two that are on unless set off.
    var SWITCHES = ['Enabled', 'UpdateIPv4', 'UpdateIPv6', 'Proxied', 'Wildcard', 'Static', 'BackupMx'];
    var ON_BY_DEFAULT = ['Enabled', 'UpdateIPv4'];

    // The plugin's own config endpoints redact stored credentials to a write-only sentinel on read and
    // encrypt freshly typed ones on save, so secrets never round-trip through the browser in the clear.
    function getConfig() { return Shared.apiRequest('Configuration', 'GET'); }
    function saveConfig(cfg) { return Shared.apiRequest('Configuration', 'POST', cfg); }

    // Pulls the provider list, hints, and field specs from the backend (GET DynamicDns/Providers) once, so each
    // provider's UI is defined in one place: its C# class. The field keys map the server shape onto the short
    // keys used here.
    var _providersLoaded = false;
    function loadProviders() {
        if (_providersLoaded) { return Promise.resolve(); }
        return Shared.apiRequest('Providers', 'GET').then(function (list) {
            list = list || [];
            PROVIDERS = list.map(function (p) { return [p.value, p.label]; });
            PROVIDER_VALUES = PROVIDERS.map(function (p) { return p[0]; });
            HINTS = {};
            FIELDS = {};
            list.forEach(function (p) {
                HINTS[p.value] = p.hint || '';
                var f = p.fields || {};
                FIELDS[p.value] = {
                    h: f.Hostname || null, l: f.Login || null, p: f.Password || null,
                    z: f.Zone || null, s: !!f.Server, t: !!f.Ttl, v6: f.IPv6 !== false, a: f.Advanced || []
                };
            });
            _providersLoaded = true;
        });
    }

    function normalizeProvider(value) {
        if (typeof value === 'number') return PROVIDER_VALUES[value] || 'Cloudflare';
        return PROVIDER_VALUES.indexOf(value) >= 0 ? value : 'Cloudflare';
    }

    function specFor(provider) {
        return FIELDS[provider] || DEFAULT_FIELDS;
    }

    function newRecord() {
        return {
            Id: Shared.generateGuid(), Name: '', Provider: 'Cloudflare', Hostname: '', Enabled: true,
            UpdateIPv4: true, UpdateIPv6: true,
            Login: '', Password: '', Zone: '', Server: '', Ttl: 1,
            Proxied: false, Wildcard: false, Mx: '', BackupMx: false, Static: false,
            LastIPv4: '', LastIPv6: '', LastStatus: '', LastSuccess: false, LastCheckedUtc: null
        };
    }

    function isSaved(r) {
        return !!(r && saved[r.Id]);
    }

    // The editable settings in one canonical form, so a value read back from the form compares equal to
    // the same value as the server sent it.
    function editable(r) {
        var out = {};
        EDITABLE.forEach(function (key) {
            var v = r[key];
            if (key === 'Provider') v = normalizeProvider(v);
            else if (key === 'Ttl') v = parseInt(v, 10) || 1;
            else if (ON_BY_DEFAULT.indexOf(key) >= 0) v = v !== false;
            else if (SWITCHES.indexOf(key) >= 0) v = !!v;
            else v = (v == null ? '' : String(v)).trim();
            out[key] = v;
        });
        return JSON.stringify(out);
    }

    function hasPendingSecret(r) {
        var p = pendingSecrets[r.Id];
        return !!p && Object.keys(p).some(function (id) { return p[id].typed || p[id].cleared; });
    }

    // A record never saved always counts, since leaving the page loses it.
    function isRecordDirty(r) {
        if (!isSaved(r)) return true;
        return hasPendingSecret(r) || editable(r) !== editable(saved[r.Id]);
    }

    function isDirty() {
        if (!loaded) return false;
        syncEditor();
        return records.some(isRecordDirty);
    }

    function renderProviderOptions() {
        Shared.getEl('recProvider').innerHTML = PROVIDERS.map(function (p) {
            return '<option value="' + p[0] + '">' + Shared.escapeHtml(p[1]) + '</option>';
        }).join('');
    }

    function classifyHealth(r) {
        if (!r.LastCheckedUtc) return 'unpublished';
        var checked = new Date(r.LastCheckedUtc).getTime();
        var within24 = !isNaN(checked) && (Date.now() - checked) < 86400000;
        return (r.LastSuccess && within24) ? 'healthy' : 'unhealthy';
    }

    function renderHealth() {
        var counts = { healthy: 0, unhealthy: 0, unpublished: 0 };
        records.forEach(function (r) { counts[classifyHealth(r)]++; });
        Shared.getEl('recordHealth').innerHTML =
            '<div class="jpk-card green"><span class="jpk-card-count">' + counts.healthy + '</span><span class="jpk-card-label">Healthy</span></div>' +
            '<div class="jpk-card red" title="Failed to update within the last 24 hours"><span class="jpk-card-count">' + counts.unhealthy + '</span><span class="jpk-card-label">Unhealthy</span></div>' +
            '<div class="jpk-card gray"><span class="jpk-card-count">' + counts.unpublished + '</span><span class="jpk-card-label">Unpublished</span></div>';
    }

    function renderSelect() {
        var select = Shared.getEl('selectRecord');
        select.innerHTML = records.map(function (r, i) {
            var label = r.Name || r.Hostname || 'New record';
            return '<option value="' + i + '">' + Shared.escapeHtml(label) + '</option>';
        }).join('');

        var hasRecords = records.length > 0;
        Shared.setVisible('emptyState', !hasRecords);
        Shared.setVisible('recordEditor', hasRecords);
        Shared.setVisible('btnDeleteRecord', hasRecords);
        Shared.setVisible('btnEnable', hasRecords);
        renderHealth();

        if (hasRecords) {
            if (currentIndex < 0 || currentIndex >= records.length) currentIndex = 0;
            select.value = String(currentIndex);
            loadEditor();
        } else {
            currentIndex = -1;
        }
    }

    function setEnableVisual(enabled) {
        var btn = Shared.getEl('btnEnable');
        btn.classList.toggle('jpk-button-submit', enabled);
        btn.classList.toggle('jpk-secondary', !enabled);
        Shared.getEl('enableLabel').textContent = enabled ? 'Enabled' : 'Disabled';
        btn.querySelector('.material-icons').textContent = enabled ? 'play_arrow' : 'pause';
    }

    // Provider hint plus a link to that provider's setup doc on GitHub, named after the provider value.
    function applyHint() {
        var prov = Shared.getEl('recProvider').value;
        var hint = HINTS[prov] || 'Fill the fields required by this provider.';
        var html = Shared.escapeHtml(hint);
        if (prov) {
            var url = 'https://github.com/JPKribs/jellyfin-plugin-remoteaccess/blob/master/docs/providers/' + encodeURIComponent(prov) + '.md';
            html += ' <a href="' + url + '" target="_blank" rel="noopener">Setup guide</a>';
        }
        Shared.getEl('providerHint').innerHTML = html;
    }

    function setField(id, label) {
        var container = Shared.getEl(id).closest('.inputContainer');
        if (!container) return;
        if (label) {
            var lbl = container.querySelector('label');
            if (lbl) lbl.textContent = label;
            container.classList.remove('hidden');
        } else {
            container.classList.add('hidden');
        }
    }

    function setCheck(id, visible) {
        var container = Shared.getEl(id).closest('.checkboxContainer');
        if (container) container.classList.toggle('hidden', !visible);
    }

    // Credential inputs are write-only: clear the field and, when a secret is already stored, show a
    // placeholder so the admin knows it is set without the value ever being sent to the browser DOM.
    function setSecretField(id, stored) {
        var input = Shared.getEl(id);
        input.value = '';
        input.placeholder = stored ? '••••••••' : '';
        clearedSecrets[id] = false;
        Shared.setVisible(CLEAR_BUTTONS[id], !!stored);
    }

    // Marks a stored credential for removal on the next save. Typing a new value still replaces it.
    function clearSecret(id) {
        var input = Shared.getEl(id);
        input.value = '';
        input.placeholder = 'Cleared when saved';
        clearedSecrets[id] = true;
        Shared.setVisible(CLEAR_BUTTONS[id], false);
    }

    function applyFields(provider) {
        var spec = specFor(provider);
        var adv = spec.a || [];
        setField('recHostname', spec.h);
        setField('recLogin', spec.l);
        setField('recPassword', spec.p);
        setField('recZone', spec.z);
        setField('recServer', spec.s === true ? 'Server override' : (spec.s || null));
        setField('recTtl', spec.t ? 'TTL' : null);
        // Single-address protocols cannot push an AAAA record, so the IPv6 toggle is hidden for them.
        setCheck('recUpdateIPv6', spec.v6 !== false);
        setCheck('recWildcard', adv.indexOf('wildcard') >= 0);
        setCheck('recStatic', adv.indexOf('static') >= 0);
        setField('recMx', adv.indexOf('mx') >= 0 ? 'MX host for the dyndns family' : null);
        setCheck('recBackupMx', adv.indexOf('backupmx') >= 0);
        var section = Shared.getEl('advancedContent').closest('.jpk-collapsible-section');
        if (section) section.classList.toggle('hidden', adv.length === 0);
    }

    function onProviderChange() {
        applyHint();
        applyFields(Shared.getEl('recProvider').value);
    }

    function loadEditor() {
        var r = records[currentIndex];
        if (!r) return;

        Shared.getEl('recName').value = r.Name || '';
        Shared.getEl('recProvider').value = normalizeProvider(r.Provider);
        Shared.getEl('recHostname').value = r.Hostname || '';
        Shared.getEl('recUpdateIPv4').checked = r.UpdateIPv4 !== false;
        Shared.getEl('recUpdateIPv6').checked = !!r.UpdateIPv6;
        // Credentials are write-only: never bind the stored secret back into the form. Show a "saved"
        // placeholder when one exists, then put back any replacement typed or clear asked for earlier.
        Object.keys(SECRET_FIELDS).forEach(function (id) {
            setSecretField(id, r[SECRET_FIELDS[id]]);
            var p = (pendingSecrets[r.Id] || {})[id];
            if (p && p.cleared) clearSecret(id);
            if (p && p.typed) Shared.getEl(id).value = p.typed;
        });
        Shared.getEl('recZone').value = r.Zone || '';
        Shared.getEl('recServer').value = r.Server || '';
        Shared.getEl('recTtl').value = r.Ttl || 1;
        Shared.getEl('recProxied').checked = !!r.Proxied;
        Shared.getEl('recWildcard').checked = !!r.Wildcard;
        Shared.getEl('recStatic').checked = !!r.Static;
        Shared.getEl('recMx').value = r.Mx || '';
        Shared.getEl('recBackupMx').checked = !!r.BackupMx;

        editorEnabled = r.Enabled !== false;
        setEnableVisual(editorEnabled);

        onProviderChange();
        renderStatus(r);
    }

    function renderStatus(r) {
        var hasStatus = !!r.LastCheckedUtc;
        Shared.setVisible('recStatusCard', hasStatus);
        if (!hasStatus) return;

        // Denote whether the last run pushed a change, made no change, or failed.
        var action = Shared.getEl('stAction');
        var act = r.LastAction || '';
        action.textContent = act || '—';
        var actClass = 'jpk-mono';
        if (act === 'Updated') actClass += ' jpk-ok';
        else if (act === 'Failed' || act === 'No address') actClass += ' jpk-bad';
        action.className = actClass; // 'Unchanged' (and legacy empty) stay neutral

        var last = Shared.getEl('stLast');
        last.textContent = r.LastStatus || (r.LastSuccess ? 'OK' : 'Unknown');
        last.className = 'jpk-mono ' + (r.LastSuccess ? 'jpk-ok' : 'jpk-bad');

        var checked = new Date(r.LastCheckedUtc);
        Shared.getEl('stChecked').textContent = isNaN(checked.getTime()) ? String(r.LastCheckedUtc) : checked.toLocaleString();
        Shared.getEl('stIps').textContent = (r.LastIPv4 || 'none') + ' / ' + (r.LastIPv6 || 'none');
    }

    // Copies the form into the record shown, and its credential edits into pendingSecrets.
    function readEditorInto(r) {
        r.Name = Shared.getEl('recName').value.trim();
        r.Provider = Shared.getEl('recProvider').value;
        // A provider that works the host out from the account hides the field, and stores no hostname.
        r.Hostname = specFor(r.Provider).h ? Shared.getEl('recHostname').value.trim() : '';
        r.UpdateIPv4 = Shared.getEl('recUpdateIPv4').checked;
        r.UpdateIPv6 = Shared.getEl('recUpdateIPv6').checked;
        r.Enabled = editorEnabled;
        r.Zone = Shared.getEl('recZone').value.trim();
        r.Server = Shared.getEl('recServer').value.trim();
        r.Ttl = parseInt(Shared.getEl('recTtl').value, 10) || 1;
        r.Proxied = Shared.getEl('recProxied').checked;
        r.Wildcard = Shared.getEl('recWildcard').checked;
        r.Static = Shared.getEl('recStatic').checked;
        r.Mx = Shared.getEl('recMx').value.trim();
        r.BackupMx = Shared.getEl('recBackupMx').checked;
        pendingSecrets[r.Id] = {
            recLogin: { typed: Shared.getEl('recLogin').value.trim(), cleared: !!clearedSecrets.recLogin },
            recPassword: { typed: Shared.getEl('recPassword').value, cleared: !!clearedSecrets.recPassword }
        };
    }

    function syncEditor() {
        if (records[currentIndex]) readEditorInto(records[currentIndex]);
    }

    // What to send for a credential: a typed replacement, the clear marker, or the value the server sent,
    // which is its kept marker for a stored secret and empty otherwise.
    function secretToSend(r, id) {
        var p = (pendingSecrets[r.Id] || {})[id] || {};
        if (p.typed) return p.typed;
        if (p.cleared) return CLEAR_SECRET;
        return r[SECRET_FIELDS[id]] || '';
    }

    function toPost(r) {
        var copy = JSON.parse(JSON.stringify(r));
        copy.Login = secretToSend(r, 'recLogin');
        copy.Password = secretToSend(r, 'recPassword');
        return copy;
    }

    // Takes in the records the server now holds. The record just saved or deleted, and any record without
    // unsaved edits, is replaced by the server's copy, so a record changed or added elsewhere shows as it
    // is. A record with unsaved edits keeps them, with the server's status. New records never saved stay.
    function adopt(serverRecords, settledId) {
        var local = {};
        records.forEach(function (r) { local[r.Id] = r; });
        var drafts = records.filter(function (r) { return !isSaved(r) && r.Id !== settledId; });

        var next = (serverRecords || []).map(function (s) {
            var mine = local[s.Id];
            if (!mine || s.Id === settledId || !isRecordDirty(mine)) return s;
            Object.keys(s).forEach(function (key) {
                if (EDITABLE.indexOf(key) < 0 && key !== 'Login' && key !== 'Password') mine[key] = s[key];
            });
            mine.Login = s.Login;
            mine.Password = s.Password;
            return mine;
        });

        saved = {};
        (serverRecords || []).forEach(function (s) { saved[s.Id] = JSON.parse(JSON.stringify(s)); });
        if (settledId) delete pendingSecrets[settledId];
        records = next.concat(drafts);
    }

    function setBusy(on) {
        busy = on;
        ['btnSaveRecord', 'btnDeleteRecord'].forEach(function (id) { Shared.getEl(id).disabled = on; });
    }

    // Reads the freshest settings, changes only the one record, and saves. Every other record goes back
    // exactly as the server holds it.
    function persist(change, settledId, okMessage, fallback) {
        setBusy(true);
        return getConfig().then(function (fresh) {
            if (!fresh) { throw new Error('settings unavailable'); }
            fresh.Records = change(fresh.Records || []);
            return saveConfig(fresh);
        }).then(function (result) {
            if (!result || !result.Records) { throw new Error('no reply'); }
            adopt(result.Records, settledId);
            currentIndex = records.findIndex(function (x) { return x.Id === settledId; });
            renderSelect();
            Shared.setStatus('recordStatus', okMessage, false);
        }).catch(function (err) {
            return errorText(err, fallback).then(function (text) {
                Shared.setStatus('recordStatus', text, true, { timeout: 0 });
            });
        }).then(function () {
            setBusy(false);
        });
    }

    function saveRecord() {
        var r = records[currentIndex];
        if (!r || busy) return;
        readEditorInto(r);
        if (specFor(r.Provider).h && !r.Hostname) {
            Shared.setStatus('recordStatus', 'A hostname is required.', true);
            return;
        }

        var posted = toPost(r);
        persist(function (list) {
            var index = list.findIndex(function (x) { return x.Id === r.Id; });
            if (index >= 0) list[index] = posted;
            else list.push(posted);
            return list;
        }, r.Id, 'Saved.', 'Save failed.');
    }

    function addRecord() {
        syncEditor();
        records.push(newRecord());
        currentIndex = records.length - 1;
        renderSelect();
        Shared.getEl('recName').focus();
    }

    function deleteRecord() {
        var r = records[currentIndex];
        if (!r || busy) return;
        syncEditor();
        var label = r.Name || r.Hostname || 'this record';
        confirmDialog('Delete ' + label + '? This cannot be undone.', { confirmText: 'Delete', destructive: true }).then(function (ok) {
            // The record list can change while the dialog is open, so drop the one that was asked about.
            var index = records.indexOf(r);
            if (!ok || index < 0) return;

            // A record never saved only lives on this page.
            if (!isSaved(r)) {
                records.splice(index, 1);
                delete pendingSecrets[r.Id];
                currentIndex = Math.min(index, records.length - 1);
                renderSelect();
                return;
            }

            persist(function (list) {
                return list.filter(function (x) { return x.Id !== r.Id; });
            }, r.Id, 'Deleted.', 'Delete failed.');
        });
    }

    function bind() {
        Shared.getEl('selectRecord').addEventListener('change', function () {
            // Edits not yet saved stay with the record they were made on, rather than being lost on switching.
            syncEditor();
            currentIndex = parseInt(this.value, 10);
            loadEditor();
        });
        Shared.getEl('recProvider').addEventListener('change', onProviderChange);
        Shared.getEl('btnNewRecord').addEventListener('click', addRecord);
        Shared.getEl('btnDeleteRecord').addEventListener('click', deleteRecord);
        Shared.getEl('btnSaveRecord').addEventListener('click', saveRecord);
        Shared.getEl('btnClearLogin').addEventListener('click', function () { clearSecret('recLogin'); });
        Shared.getEl('btnClearPassword').addEventListener('click', function () { clearSecret('recPassword'); });
        Shared.getEl('btnEnable').addEventListener('click', function () {
            editorEnabled = !editorEnabled;
            setEnableVisual(editorEnabled);
        });
        Shared.initCollapsibles();
    }

    function load() {
        loaded = false;
        loadProviders().then(getConfig).then(function (cfg) {
            records = [];
            pendingSecrets = {};
            adopt((cfg && cfg.Records) || [], null);
            renderProviderOptions();
            currentIndex = records.length ? 0 : -1;
            renderSelect();
            loaded = true;
        }).catch(function () {
            // Outside the editor, which stays hidden when there are no records.
            showText(Shared, 'loadError', 'Could not load providers or settings.');
        });
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 1,
        tabs: TABS,
        bind: bind,
        load: load,
        isDirty: isDirty
    }).shared;
}
