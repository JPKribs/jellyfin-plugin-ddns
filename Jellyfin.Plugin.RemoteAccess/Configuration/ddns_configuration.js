import { createConfigPage, confirmDialog } from './configurationpage?name=ddns_jpkribs_shared.js';
import { createHostnamePicker } from './configurationpage?name=ddns_hostnames.js';
import { PLUGIN_ID, TABS, errorText, showText, trustTheProxy, routerInstructionHtml, autoForwardingOn, autoForwardingSetting, routerStateText } from './configurationpage?name=ddns_tabs.js';

// The Configuration tab: a checklist from turning the proxy on to a test from outside. Each step holds the full
// controls for its part: the proxy switch, the DDNS records and the proxy's names, the certificate, the router,
// Trust the Proxy and the outside test. Every state is worked out from Proxy/Status and Proxy/Settings, plus the
// DDNS records for someone who only uses Dynamic DNS.
export default function (view) {
    'use strict';

    var Shared = null;

    // ===== Shared helpers =====

    function show(id, text) {
        showText(Shared, id, text);
    }

    function formatDate(value) {
        if (!value) return '';
        var d = new Date(value);
        return isNaN(d.getTime()) ? '' : d.toLocaleDateString();
    }

    function formatTime(value) {
        if (!value) return '';
        var d = new Date(value);
        return isNaN(d.getTime()) ? '' : d.toLocaleString();
    }

    function expired(value) {
        return !!value && new Date(value).getTime() <= Date.now();
    }

    // Within two weeks of expiring, and not already expired.
    function soon(value) {
        return !!value && !expired(value) && new Date(value).getTime() - Date.now() < 14 * 86400000;
    }

    // ===== DDNS records, in step 2 =====

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
    var recordsLoaded = false;
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

    function recordsDirty() {
        if (!recordsLoaded) return false;
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

    // The saved records by health, for the cards and for the address step when the proxy is off.
    function healthCounts() {
        var counts = { healthy: 0, unhealthy: 0, unpublished: 0 };
        records.filter(isSaved).forEach(function (r) { counts[classifyHealth(r)]++; });
        return counts;
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
        renderRecordStatus(r);
    }

    function renderRecordStatus(r) {
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
            // A new or renamed record is offered as a proxy address straight away.
            reloadHostnameChoices();
            if (lastStatus) render(lastStatus);
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

    function loadRecords() {
        recordsLoaded = false;
        return loadProviders().then(getConfig).then(function (cfg) {
            records = [];
            pendingSecrets = {};
            adopt((cfg && cfg.Records) || [], null);
            renderProviderOptions();
            currentIndex = records.length ? 0 : -1;
            renderSelect();
            showCurrentIp(cfg || {});
            showDetectionWarning(cfg && cfg.LastDetectionMessage);
            recordsLoaded = true;
            show('loadError', '');
        }).catch(function () {
            // Outside the editor, which stays hidden when there are no records.
            show('loadError', 'Could not load providers or settings.');
        });
    }

    // ===== Public IP and Update Now, in step 2 =====

    function showCurrentIp(cfg) {
        var parts = [cfg.LastDetectedIPv4, cfg.LastDetectedIPv6].filter(function (x) { return x; });
        Shared.getEl('currentIp').value = parts.length ? parts.join('   /   ') : 'Not detected yet';
    }

    // Warns when the last run could not resolve a public address for a wanted family, for example when the
    // detected address looked internal and nothing was published.
    function showDetectionWarning(message) {
        show('detectionWarning', message);
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
                    // The run changed each record's status. Edits not saved yet stay as they are.
                    if (recordsLoaded) {
                        syncEditor();
                        adopt(fresh.Records || [], null);
                        renderSelect();
                        if (lastStatus) render(lastStatus);
                    }
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

    // ===== Proxy settings, in steps 1 to 4 =====

    var settings = null;
    // The built-in defaults, for settings left unset. Empty until loaded, or on a server that has none.
    var defaults = {};
    var hostnames = null;
    // The last status shown, for the certificate actions and to redraw the steps.
    var lastStatus = null;
    // The self-signed root, fetched once while it is shown.
    var rootLoaded = false;
    var rootPem = '';
    // The form as last loaded or saved, to tell whether leaving would lose changes. Null until loaded.
    var baseline = null;

    var CERT_NOTES = {
        Automatic: 'Issued and renewed for you.',
        None: 'Browsers and apps need a certificate for your address before they connect.',
        PemFiles: 'From certbot, acme.sh and similar tools. Renewals apply on their own.',
        Pkcs12File: 'A .pfx or .p12 file. A renewed file applies on its own.',
        SelfSigned: 'Made on this server, and trusted by each device once its root is installed, which TVs cannot do.'
    };

    function getSettings() { return Shared.apiRequest('Proxy/Settings', 'GET'); }
    function getStatus() { return Shared.apiRequest('Proxy/Status', 'GET'); }

    // Keeps the names chosen, but offers any record added or renamed since the picker loaded.
    function reloadHostnameChoices() {
        if (!hostnames) return;
        var current = hostnames.getValue();
        hostnames.load().then(function () { hostnames.setValue(current); });
    }

    // How Let's Encrypt will check the addresses: through the DDNS provider when it can publish TXT records.
    function describeMethod(st) {
        var acme = st.Acme || {};
        var automatic = (st.Certificate || {}).Mode === 'Automatic' && (acme.Names || []).length > 0;
        var text = !automatic ? ''
            : acme.Method === 'Dns'
                ? 'Checked through ' + (acme.DnsProviders || []).join(' and ') + ' DNS, so port 80 is not needed and wildcards are covered.'
                : 'Checked over port 80, which leaves wildcards off. A Cloudflare or Duck DNS record for the address avoids both.';
        show('certMethod', text);
    }

    // What can be done with the certificate in use, shown only while its source is still the one picked above:
    // picking another source hides them before it is saved.
    function showCertActions() {
        var picked = Shared.getEl('certMode').value;
        var cert = (lastStatus && lastStatus.Certificate) || {};
        var selfSigned = picked === 'SelfSigned' && cert.Mode === 'SelfSigned' && !!cert.Loaded;
        Shared.setVisible('certRootField', selfSigned);
        Shared.setVisible('certRootExpiresRow', selfSigned);
        Shared.setVisible('certRootFingerprintRow', selfSigned);
        if (selfSigned && !rootLoaded) loadRoot();

        // Offered only once Let's Encrypt is the saved choice and the proxy can answer its checks.
        Shared.setVisible('certRequestField', picked === 'Automatic' && cert.Mode === 'Automatic'
            && !!(settings && (settings.Certificate || {}).AcceptTerms) && !!(lastStatus && lastStatus.Running));
    }

    function showCertFields() {
        var mode = Shared.getEl('certMode').value;
        Shared.getEl('certModeDescription').textContent = CERT_NOTES[mode] || '';
        Shared.setVisible('certTermsField', mode === 'Automatic');
        Shared.setVisible('certFileFields', mode === 'Pkcs12File' || mode === 'PemFiles');
        Shared.setVisible('certKeyField', mode === 'PemFiles');
        Shared.getEl('certPathDescription').textContent = mode === 'PemFiles'
            ? 'The full chain file on this server, for example fullchain.pem.'
            : 'The .pfx or .p12 file on this server.';
        showCertActions();
    }

    // Whether these settings look like they were never saved: the proxy off, no address and
    // no certificate choice made. An empty address list saved on purpose has at least one of those set.
    function neverSaved(s) {
        var cert = s.Certificate || {};
        return !s.Enabled && !(s.Hostnames || []).length
            && !cert.AcceptTerms && !cert.Path && !cert.KeyPath && !cert.Password;
    }

    // The form's current values, to tell whether leaving would lose changes.
    function formState() {
        var s = collect({});
        s.Hostnames = s.Hostnames.slice().sort();
        return JSON.stringify(s);
    }

    function proxyDirty() {
        return baseline !== null && formState() !== baseline;
    }

    function isDirty() {
        return recordsDirty() || proxyDirty();
    }

    function fill(s, suggest) {
        settings = s;
        Shared.getEl('enabled').checked = !!s.Enabled;
        // Nothing ever chosen: start from the names the DDNS records already keep pointed here. Suggested
        // names are only a starting point, so they do not count as an unsaved change.
        hostnames.setValue(suggest && neverSaved(s) ? hostnames.suggested() : s.Hostnames);
        var cert = s.Certificate || {};
        Shared.getEl('certMode').value = cert.Mode || 'Automatic';
        Shared.getEl('certPath').value = cert.Path || '';
        Shared.getEl('certKeyPath').value = cert.KeyPath || '';
        Shared.getEl('certTerms').checked = !!cert.AcceptTerms;
        Shared.getEl('autoPortForwarding').value = autoForwardingOn(s, defaults) ? 'true' : 'false';
        Shared.bindSecretField('certPassword', !!cert.Password);
        showCertFields();
        baseline = formState();
    }

    function collect(s) {
        s.Enabled = Shared.getEl('enabled').checked;
        s.Hostnames = hostnames.getValue();
        s.Certificate = s.Certificate || {};
        s.Certificate.Mode = Shared.getEl('certMode').value;
        s.Certificate.Path = Shared.getEl('certPath').value.trim();
        s.Certificate.KeyPath = Shared.getEl('certKeyPath').value.trim();
        s.Certificate.AcceptTerms = Shared.getEl('certTerms').checked;
        s.Certificate.Password = Shared.readSecretField('certPassword');
        s.Advanced = s.Advanced || {};
        s.Advanced.AutoPortForwarding = autoForwardingSetting(Shared.getEl('autoPortForwarding').value === 'true', defaults);
        return s;
    }

    // Saves onto the freshest settings so changes made on the other tabs are kept.
    function save() {
        // Let's Encrypt is only used once its terms are accepted, so the box has to be ticked to choose it.
        if (Shared.getEl('certMode').value === 'Automatic' && !Shared.getEl('certTerms').checked) {
            Shared.setStatus('saveStatus', 'Tick I accept the Let\'s Encrypt terms in step 3, or choose another certificate source.', true, { timeout: 0 });
            setStepOpen('Certificate', true);
            return Promise.resolve(false);
        }

        var button = Shared.getEl('btnSave');
        if (button.disabled) return Promise.resolve(false);
        button.disabled = true;
        Shared.setStatus('saveStatus', 'Saving...', false);
        return getSettings().then(function (fresh) {
            return Shared.apiRequest('Proxy/Settings', 'POST', collect(fresh || settings));
        }).then(function (result) {
            if (result && result.Settings) fill(result.Settings, false);
            if (result && result.Status) render(result.Status);
            Shared.setStatus('saveStatus', 'Saved.', false);
            return true;
        }).catch(function (err) {
            return errorText(err, 'Save failed.').then(function (text) {
                Shared.setStatus('saveStatus', text, true, { timeout: 0 });
                return false;
            });
        }).then(function (ok) {
            button.disabled = false;
            refresh();
            return ok;
        });
    }

    // Automatic port forwarding saves as soon as it changes, onto the freshest settings, and leaves every other
    // unsaved choice on the page alone.
    function saveAutoForwarding() {
        var select = Shared.getEl('autoPortForwarding');
        var on = select.value === 'true';
        if (select.disabled) return;
        select.disabled = true;
        Shared.setStatus('routerStatus', 'Saving...', false);
        getSettings().then(function (fresh) {
            fresh = fresh || settings;
            fresh.Advanced = fresh.Advanced || {};
            fresh.Advanced.AutoPortForwarding = autoForwardingSetting(on, defaults);
            return Shared.apiRequest('Proxy/Settings', 'POST', fresh);
        }).then(function (result) {
            if (result && result.Settings) settings = result.Settings;
            // The saved choice is now the baseline for this control, so it is not an unsaved change.
            if (baseline !== null) {
                var base = JSON.parse(baseline);
                base.Advanced = base.Advanced || {};
                base.Advanced.AutoPortForwarding = autoForwardingSetting(on, defaults);
                baseline = JSON.stringify(base);
            }
            if (result && result.Status) render(result.Status);
            Shared.setStatus('routerStatus', 'Saved.', false);
        }).catch(function (err) {
            return errorText(err, 'Save failed.').then(function (text) {
                Shared.setStatus('routerStatus', text, true, { timeout: 0 });
            });
        }).then(function () {
            select.disabled = false;
            refresh();
        });
    }

    function retryRouter() {
        var button = Shared.getEl('btnRouterRetry');
        if (button.disabled) return;
        button.disabled = true;
        Shared.setStatus('routerStatus', 'Asking the router...', false, { timeout: 0 });
        Shared.apiRequest('Proxy/Router/Refresh', 'POST').then(function (r) {
            var text = !r ? 'Done.'
                : r.Mapped ? 'The router opened the ports.'
                    : r.BehindCgnat ? 'Your internet provider shares your public address, so the router cannot open the ports.'
                        : r.Error || 'The router did not open the ports.';
            Shared.setStatus('routerStatus', text, !(r && r.Mapped), { timeout: 0 });
        }).catch(function () {
            Shared.setStatus('routerStatus', 'The router could not be asked.', true, { timeout: 0 });
        }).then(function () {
            button.disabled = false;
            refresh();
        });
    }

    // The form is saved first, so Jellyfin is trusted with the settings shown here.
    function trust() {
        var button = Shared.getEl('btnTrust');
        if (button.disabled) return;
        button.disabled = true;
        trustTheProxy(Shared, 'trustStatus', save).then(function () {
            button.disabled = false;
            refresh();
        });
    }

    function restore() {
        var button = Shared.getEl('btnRestore');
        if (button.disabled) return;
        confirmDialog('Jellyfin\'s network settings go back to how they were, and internet visitors through the proxy are turned away. Jellyfin restarts once.', { title: 'Undo', confirmText: 'Undo', destructive: true }).then(function (ok) {
            if (!ok) return;
            button.disabled = true;
            Shared.apiRequest('Proxy/Restore', 'POST').then(function (result) {
                Shared.setStatus('trustStatus', (result && result.Message) || 'Restored.', !(result && result.Success), { timeout: 0 });
            }).catch(function () {
                Shared.setStatus('trustStatus', 'Restore failed.', true, { timeout: 0 });
            }).then(function () {
                button.disabled = false;
                refresh();
            });
        });
    }

    function loadRoot() {
        rootLoaded = true;
        Shared.apiRequest('Proxy/Certificate/Root', 'GET').then(function (root) {
            rootPem = (root && root.Pem) || '';
            Shared.getEl('certRootExpires').textContent = root && root.NotAfterUtc ? formatDate(root.NotAfterUtc) : '';

            // Four lines of eight pairs, as phones and Keychain Access group them, so it can be read off and compared.
            var pairs = ((root && root.Fingerprint) || '').split(':');
            var lines = [];
            for (var i = 0; i < pairs.length; i += 8) lines.push(pairs.slice(i, i + 8).join(' '));
            Shared.getEl('certRootFingerprint').textContent = lines.join('\n');
        }).catch(function () {
            rootLoaded = false;
        });
    }

    // Saved as a .crt file, which Windows, macOS, iOS and Android all open as a certificate to install.
    function downloadRoot() {
        if (!rootPem) return;
        var blob = new Blob([rootPem], { type: 'application/x-x509-ca-cert' });
        var url = URL.createObjectURL(blob);
        var link = document.createElement('a');
        link.href = url;
        link.download = 'remote-access-root.crt';
        link.style.display = 'none';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        setTimeout(function () { URL.revokeObjectURL(url); }, 0);
    }

    function requestCertificate() {
        var button = Shared.getEl('btnRequestCert');
        if (button.disabled) return;
        confirmDialog('Let\'s Encrypt is asked for a new certificate now, even if the current one is fine. The current one keeps working until the new one is ready.', { title: 'Request a New Certificate', confirmText: 'Request' }).then(function (ok) {
            if (!ok) return;
            button.disabled = true;
            Shared.setStatus('certRequestStatus', 'Requesting...', false, { timeout: 0 });
            Shared.apiRequest('Proxy/Certificate/Request', 'POST').then(function (result) {
                Shared.setStatus('certRequestStatus', (result && result.Message) || 'Done.', !(result && result.Ok), { timeout: 0 });
                if (result && result.Status) render(result.Status);
            }).catch(function () {
                Shared.setStatus('certRequestStatus', 'The request failed.', true, { timeout: 0 });
            }).then(function () {
                button.disabled = false;
            });
        });
    }

    // ===== The steps =====

    // The steps in order. The key names the step's elements: step, head, body, icon, state and text followed by the key.
    var STEPS = [
        { key: 'Power', title: 'Proxy on' },
        { key: 'Address', title: 'Address' },
        { key: 'Certificate', title: 'Certificate' },
        { key: 'Router', title: 'Router' },
        { key: 'Trust', title: 'Trust the Proxy' },
        { key: 'Reach', title: 'Reachable from outside' }
    ];

    var BADGES = { green: 'green', amber: 'orange', red: 'red', gray: 'gray' };

    // The last test from outside run from this page, kept until the status carries a newer one.
    var lastReach = null;
    // Steps are folded or opened once per visit, so the poller never folds a step someone is working in.
    var arranged = false;

    function step(state, word, text, idle) {
        return { state: state, word: word, text: text || '', idle: !!idle };
    }

    // A step that only matters once the proxy is on, while it is off. It waits quietly and starts folded.
    function idleStep() {
        return step('gray', 'Proxy off', 'Used once the proxy is on.', true);
    }

    // The names chosen for the proxy, or the one in its public address when the settings list none.
    function chosenNames(st) {
        var names = (settings && settings.Hostnames) || [];
        if (names.length) return names;
        var m = /^https?:\/\/([^/:]+)/i.exec(st.PublicUrl || '');
        return m ? [m[1]] : [];
    }

    // The newer of the test result kept here and the one in the status.
    function currentReach(st) {
        var fromStatus = st.Reachability || null;
        if (!lastReach) return fromStatus;
        if (!fromStatus || !fromStatus.CheckedUtc) return lastReach;
        return new Date(fromStatus.CheckedUtc).getTime() >= new Date(lastReach.CheckedUtc || 0).getTime() ? fromStatus : lastReach;
    }

    function assessPower(st) {
        if (st.Running) return step('green', 'Running', 'The proxy is running and answers on ports ' + st.HttpPort + ' and ' + st.HttpsPort + '.');
        if (st.Enabled) return step('red', 'Not running', st.Error || 'The proxy is turned on but is not running.');
        return step('gray', 'Off', 'Turn the proxy on for a secure way in to Jellyfin from the internet. The DDNS records in step 2 work without it.');
    }

    // The name endings only a home network knows, the same list the server uses. Phones outside cannot open them.
    var PRIVATE_ENDINGS = ['lan', 'local', 'localdomain', 'localhost', 'home', 'internal', 'intranet', 'corp', 'private', 'test', 'invalid', 'example'];

    function isPublicName(name) {
        var labels = String(name || '').replace(/^\*\./, '').toLowerCase().split('.');
        if (labels.length < 2 || /^[0-9.]+$/.test(name) || name.indexOf(':') >= 0) return false;
        return PRIVATE_ENDINGS.indexOf(labels[labels.length - 1]) < 0 && !/\.home\.arpa$/.test(name);
    }

    // With the proxy off, the address step is only about the DDNS records.
    function assessRecords() {
        if (!recordsLoaded) return step('gray', 'Checking', 'Loading the DDNS records.');
        var c = healthCounts();
        var total = c.healthy + c.unhealthy + c.unpublished;
        if (!total) return step('gray', 'No records', 'Add a DDNS record below to keep a name pointed at this server.');
        if (c.unhealthy) return step('red', 'Failing', c.unhealthy + ' of ' + total + ' records failed to update in the last day. Check their status below.');
        if (c.unpublished) return step('gray', 'Not updated yet', 'Some records have not been sent yet. Select Update Now to send them.');
        return step('green', 'Up to date', 'Every record points at this server.');
    }

    function assessAddress(st) {
        if (!st.Enabled) return assessRecords();
        var names = chosenNames(st);
        if (!names.length) return step('amber', 'Not set', 'Choose the name people type to reach Jellyfin. Without one, no certificate can be issued.');
        if (!names.some(isPublicName)) return step('amber', 'Home only', names.join(', ') + ' only works on your home network. Choose a public name so people can reach Jellyfin from outside.');
        var n = st.Name;
        // An older server does not look the name up, so a chosen name is as far as this can check.
        if (!n) return step('green', 'Chosen', 'People reach Jellyfin at ' + names.join(', ') + '.');
        var host = n.Hostname || names[0];
        if (n.Error) return step('red', 'Not found', host + ' could not be looked up. ' + n.Error);
        if (n.Resolves) return step('green', 'Ready', host + ' points to this server.');
        if (!(n.Addresses || []).length) return step('amber', 'Not pointed here', host + ' does not point anywhere yet. A new record can take a few minutes to appear.');
        return step('amber', 'Not pointed here', host + ' points somewhere other than this server. Check its DDNS record below.');
    }

    function assessCertificate(st) {
        if (!st.Enabled) return idleStep();
        var cert = st.Certificate || {};
        var acme = st.Acme || {};
        var chosen = (settings && settings.Certificate) || {};
        var mode = cert.Mode || chosen.Mode || 'Automatic';
        if (mode === 'Automatic' && !chosen.AcceptTerms) {
            return step('amber', 'Terms', 'Let\'s Encrypt gives your address a free certificate that every device trusts. Accept its terms below and save to request one.');
        }
        if (cert.Loaded) {
            if ((cert.UncoveredHostnames || []).length) return step('red', 'Wrong names', 'The certificate does not cover ' + cert.UncoveredHostnames.join(', ') + '.');
            if (expired(cert.NotAfterUtc)) return step('red', 'Expired', 'The certificate expired on ' + formatDate(cert.NotAfterUtc) + '. Browsers and apps refuse it until it is renewed.');
            if (cert.Mode === 'SelfSigned') return step('amber', 'Self-signed', 'Only devices that install its root trust a self-signed certificate. Let\'s Encrypt gives one that every device trusts.');
            if (cert.Error) return step('amber', 'Check file', 'The certificate file changed and could not be loaded, so the previous one is still served.');
            if (soon(cert.NotAfterUtc)) return step('amber', 'Expires soon', 'The certificate expires on ' + formatDate(cert.NotAfterUtc) + '. Renew it soon.');
            var covers = (cert.Names || []).join(', ');
            return step('green', 'Ready', (covers ? 'Covers ' + covers : 'A certificate is in use') + (cert.NotAfterUtc ? ' until ' + formatDate(cert.NotAfterUtc) : '') + '.');
        }
        if (!st.Running) return step('gray', 'Not checked', 'The certificate is checked once the proxy is running.');
        if (mode === 'None') return step('amber', 'Not set', 'Without a certificate, browsers and apps cannot connect securely.');
        if (mode !== 'Automatic') return step('red', 'Cannot load', 'The certificate could not be loaded. ' + (cert.Error || ''));
        if (acme.Issuing) return step('amber', 'Issuing', 'Let\'s Encrypt is checking your address.');
        if (acme.LastError) return step('red', 'Failed', 'Let\'s Encrypt did not issue a certificate. ' + acme.LastError);
        if (!(acme.Names || []).length) return step('amber', 'Waiting', 'Let\'s Encrypt needs an address that is not a wildcard.');
        return step('amber', 'Requested', acme.Method === 'Dns'
            ? 'Let\'s Encrypt checks your address through ' + (acme.DnsProviders || []).join(' and ') + '.'
            : 'Let\'s Encrypt checks your address over port 80, so forward it on the router.');
    }

    function assessRouter(st) {
        if (!st.Enabled) return idleStep();
        var r = st.Router;
        var reach = currentReach(st);
        var reached = !!(reach && reach.Reachable === true);
        if (r && r.BehindCgnat) return step('red', 'Shared address', routerStateText(r));
        if (r && r.Mapped) return step('green', 'Forwarded', routerStateText(r));
        // Ports forwarded by hand cannot be seen from here, but a test from outside that got through proves them.
        if (reached) return step('green', 'Forwarded', 'The test from outside reached this server, so the ports are open.');
        if (!r) return step('gray', 'Not checked', 'This server cannot check the router. Forward the ports by hand as below.');
        return step('amber', 'Forward by hand', r.Enabled
            ? 'The router did not open the ports automatically, so forward them by hand as below.'
            : 'Automatic port forwarding is off, so forward the ports by hand as below.');
    }

    function assessTrust(st) {
        // Undo stays at hand while Jellyfin is still set up for the proxy, even with the proxy off.
        if (!st.Enabled && (!st.TakeoverPhase || st.TakeoverPhase === 'Unmanaged')) return idleStep();
        if (st.TakeoverPhase === 'Managed' && st.JellyfinTrustsProxy === false) return step('red', 'Not trusted', 'Jellyfin no longer trusts the proxy. Its network settings were changed since, so trust the proxy again to fix this.');
        if (st.TakeoverPhase === 'Managed') return step('green', 'Trusted', 'Jellyfin trusts the proxy, sees each visitor\'s real address and keeps its own ports, ' + st.JellyfinHttpPort + (st.JellyfinHttpsPort ? ' and ' + st.JellyfinHttpsPort : '') + '.');
        if (st.TakeoverPhase === 'PendingRestart') return step('amber', 'Restarting', 'Jellyfin is restarting to trust the proxy.');
        if (st.JellyfinTrustsProxy) return step('green', 'Trusted', 'Jellyfin already trusts the proxy through its own network settings.');
        if (st.TakeoverError) return step('red', 'Failed', 'Jellyfin does not trust the proxy yet, so internet visitors are turned away.');
        return step('amber', 'Not yet', 'Until Jellyfin trusts the proxy, internet visitors are turned away.');
    }

    function assessReach(st) {
        if (!st.Enabled) return idleStep();
        var reach = currentReach(st);
        if (!reach || (reach.Reachable == null && !reach.Error)) return step('gray', 'Not tested', 'Check that the internet can reach your address.');
        var when = formatTime(reach.CheckedUtc);
        var nodes = reach.Nodes || [];
        var ok = nodes.filter(function (x) { return x.Ok; }).length;
        var tally = nodes.length ? ok + ' of ' + nodes.length + ' places' : '';
        if (reach.Reachable === true) return step('green', 'Reachable', 'Reached from ' + (tally || 'outside your network') + (when ? ' on ' + when : '') + '.');
        if (reach.Reachable === false) return step('red', 'Not reachable', reach.Error || 'Nothing outside your network could reach your address' + (when ? ' on ' + when : '') + '.');
        return step('amber', 'Test failed', 'The test could not run. ' + reach.Error);
    }

    // Steps fold with the shared collapsible sections. This opens or folds one from code.
    function setStepOpen(key, open) {
        var head = Shared.getEl('step' + key).querySelector('.jpk-collapsible-header');
        head.classList.toggle('collapsed', !open);
        head.setAttribute('aria-expanded', open ? 'true' : 'false');
        Shared.getEl('body' + key).classList.toggle('collapsed', !open);
    }

    // On the first render of a visit, open every step that is not done and fold the rest away.
    function arrange(results) {
        if (arranged) return;
        arranged = true;
        STEPS.forEach(function (s, i) { setStepOpen(s.key, results[i].state !== 'green' && !results[i].idle); });
    }

    function renderStep(key, result) {
        var li = Shared.getEl('step' + key);
        li.setAttribute('data-state', result.state);
        var badge = Shared.getEl('state' + key);
        badge.className = 'jpk-badge ' + BADGES[result.state];
        badge.textContent = result.word;
        Shared.getEl('text' + key).textContent = result.text;
    }

    // The summary above the steps: how many are done, a bar segment per step, and the next one to look at.
    function renderSummary(st, results) {
        var segments = Shared.getEl('summaryBar').children;
        var done = 0;
        var first = -1;
        results.forEach(function (r, i) {
            segments[i].setAttribute('data-state', st.Enabled ? r.state : 'gray');
            if (r.state === 'green') done++;
            else if (first < 0) first = i;
        });
        // Someone using only Dynamic DNS is not asked to finish the proxy steps.
        if (!st.Enabled) {
            summary('The proxy is off', 'gray', 'DDNS only', 'The DDNS records in step 2 still keep your address pointed at this server.', -1);
            return -1;
        }
        if (first < 0) {
            summary('Ready to share', 'green', 'All set', st.PublicUrl
                ? 'Jellyfin can be reached from outside your network at ' + st.PublicUrl + '.'
                : 'Jellyfin can be reached from outside your network.', -1);
            return first;
        }
        var r = results[first];
        summary(done + ' of ' + results.length + ' steps done', BADGES[r.state],
            r.state === 'gray' ? 'Not done' : 'Needs attention',
            STEPS[first].title + ': ' + r.text, first);
        return first;
    }

    function summary(title, color, word, text, step) {
        Shared.getEl('summaryTitle').textContent = title;
        var badge = Shared.getEl('summaryBadge');
        badge.className = 'jpk-badge ' + color;
        badge.textContent = word;
        Shared.getEl('summaryText').textContent = text;
        var open = Shared.getEl('summaryOpen');
        open.textContent = step >= 0 ? 'Open step ' + (step + 1) : '';
        open.setAttribute('data-step', step >= 0 ? STEPS[step].key : '');
        Shared.setVisible('summaryOpen', step >= 0);
    }

    function renderAddressDetails(st, result) {
        var n = st.Name;
        var mismatch = !!st.Enabled && !!n && result.state !== 'green' && !!n.Hostname && !n.Error;
        Shared.setVisible('nameDetails', mismatch);
        if (mismatch) {
            Shared.getEl('nameHostname').textContent = n.Hostname;
            Shared.getEl('nameAddresses').textContent = (n.Addresses || []).length ? n.Addresses.join(', ') : 'Nothing yet';
            Shared.getEl('namePublic').textContent = n.PublicAddress || '';
            Shared.setVisible('namePublicRow', !!n.PublicAddress);
        }
    }

    function renderCertificate(st) {
        describeMethod(st);
        var cert = st.Certificate || {};
        Shared.setVisible('certStatus', !!cert.Loaded);
        Shared.getEl('certNames').textContent = (cert.Names || []).join(', ');
        Shared.getEl('certExpires').textContent = formatDate(cert.NotAfterUtc);
        // The issuer's common name alone. The rest of the distinguished name is noise here.
        var issuer = cert.Issuer || '';
        var cn = /(?:^|,\s*)CN=([^,]+)/.exec(issuer);
        Shared.getEl('certIssuer').textContent = cn ? cn[1].trim() : issuer;
        // The step's own text already names most problems. A file that failed to reload is the one it does not explain.
        show('certProblem', cert.Loaded && cert.Error ? 'The certificate could not be loaded. ' + cert.Error : '');
        showCertActions();
    }

    function renderRouter(st, result) {
        var r = st.Router;
        show('routerError', r && !r.Mapped && !r.BehindCgnat ? r.Error : '');
        var manual = result.state === 'amber' || result.state === 'gray';
        Shared.setVisible('routerManual', manual);
        if (manual) {
            // The address the router itself reported is the one it would forward to.
            var target = Object.assign({}, st, { LanAddress: (r && r.LanAddress) || st.LanAddress || st.DetectedLanAddress });
            Shared.getEl('routerInstruction').innerHTML = routerInstructionHtml(Shared, target);
        }
    }

    function renderTrust(st, result) {
        show('trustError', result.state === 'green' ? '' : st.TakeoverError);
        show('jellyfinExposed', st.JellyfinExposedAt
            ? 'This server also has the public address ' + st.JellyfinExposedAt + ', where Jellyfin\'s own port ' + st.JellyfinHttpPort +
              ' answers without the proxy\'s protection. Block Jellyfin\'s ports for the internet in this machine\'s firewall.'
            : '');
        var managed = st.TakeoverPhase === 'Managed' || st.TakeoverPhase === 'PendingRestart';
        Shared.setVisible('btnRestore', managed);
        // Trusting again keeps the original snapshot, so Undo still puts back what was there first.
        Shared.setVisible('btnTrust', !managed || (st.TakeoverPhase === 'Managed' && st.JellyfinTrustsProxy === false));
    }

    function renderReachNodes(st) {
        var reach = currentReach(st);
        var nodes = (st.Enabled && reach && reach.Nodes) || [];
        var box = Shared.getEl('reachNodes');
        Shared.setVisible(box, nodes.length > 0);
        box.innerHTML = nodes.map(function (x) {
            var result = x.Ok ? 'Reached' : (x.Error || 'Not reached');
            return '<div class="jpk-record-row"><span class="jpk-record-label">' + Shared.escapeHtml(x.Location || 'Unknown') + '</span>' +
                '<span class="' + (x.Ok ? 'jpk-status-ok' : 'jpk-status-bad') + '">' + Shared.escapeHtml(result) + '</span></div>';
        }).join('');
    }

    function render(st) {
        if (!st || !settings) return;
        lastStatus = st;
        var results = [assessPower(st), assessAddress(st), assessCertificate(st), assessRouter(st), assessTrust(st), assessReach(st)];
        renderSummary(st, results);
        STEPS.forEach(function (s, i) { renderStep(s.key, results[i]); });
        arrange(results);

        show('folderProblem', st.FolderProblem);
        show('everyoneLocal', st.Enabled && st.EveryoneLooksLocal
            ? 'Most visitors in the last hour came from one local address, so something in front of the proxy, such as rootless Docker or a tunnel, may be hiding their real addresses. Bans, the block list and country rules cannot work until those addresses reach the proxy.'
            : '');
        show('countryNote', st.Enabled && st.CountryRule ? st.CountryRule + ' Change it under Advanced → Blocking.' : '');

        renderAddressDetails(st, results[1]);
        renderCertificate(st);
        renderRouter(st, results[3]);
        renderTrust(st, results[4]);
        renderReachNodes(st);
    }

    function testReach() {
        var button = Shared.getEl('btnReach');
        if (button.disabled) return;
        button.disabled = true;
        Shared.setStatus('reachStatus', 'Testing from outside, which can take 20 seconds...', false, { timeout: 0 });
        Shared.apiRequest('Proxy/Reachability', 'POST').then(function (result) {
            if (result) lastReach = result;
            Shared.setStatus('reachStatus', '', false);
            if (lastStatus) render(lastStatus);
        }).catch(function () {
            Shared.setStatus('reachStatus', 'The test could not run.', true, { timeout: 0 });
        }).then(function () {
            button.disabled = false;
            refresh();
        });
    }

    // ===== Loading and polling =====

    // Reads both, since the address and certificate steps depend on the saved settings too. The form keeps
    // whatever is typed, so the poller never undoes a choice that is not saved yet.
    function refresh() {
        if (document.hidden) return Promise.resolve();
        return Promise.all([getSettings(), getStatus()]).then(function (results) {
            if (results[0]) settings = results[0];
            render(results[1]);
        }).catch(function () { /* the poller tries again */ });
    }

    function load() {
        baseline = null;
        arranged = false;
        // An older server without the defaults still loads, with each setting following its usual default.
        var defaultsLoaded = Shared.apiRequest('Proxy/Defaults', 'GET').catch(function () { return null; });
        // The records load on their own, so a proxy problem never hides them.
        var recordsDone = loadRecords();
        Promise.all([getSettings(), getStatus(), hostnames.load(), defaultsLoaded]).then(function (results) {
            defaults = (results[3] && results[3].Values) || {};
            // The DDNS names are loaded with the rest, so the suggested addresses are known before filling.
            if (results[0]) fill(results[0], true);
            // Wait for the records, so the address step is not judged before they are known.
            return recordsDone.then(function () { render(results[1]); });
        }).catch(function () {
            summary('Status unavailable', 'red', 'Error', 'Could not load the proxy status.', -1);
        });
    }

    function bind() {
        hostnames = createHostnamePicker(Shared, 'hostnamePicker', 'hostnames');
        Shared.getEl('summaryOpen').addEventListener('click', function () {
            var key = this.getAttribute('data-step');
            if (!key) return;
            setStepOpen(key, true);
            Shared.getEl('step' + key).scrollIntoView({ behavior: 'smooth', block: 'start' });
        });

        // Step 2: records and the public IP.
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
        Shared.getEl('btnRunNow').addEventListener('click', runNow);

        // Steps 3 to 6.
        Shared.getEl('certMode').addEventListener('change', showCertFields);
        Shared.getEl('btnDownloadRoot').addEventListener('click', downloadRoot);
        Shared.getEl('btnRequestCert').addEventListener('click', requestCertificate);
        Shared.getEl('autoPortForwarding').addEventListener('change', saveAutoForwarding);
        Shared.getEl('btnRouterRetry').addEventListener('click', retryRouter);
        Shared.getEl('btnTrust').addEventListener('click', trust);
        Shared.getEl('btnRestore').addEventListener('click', restore);
        Shared.getEl('btnReach').addEventListener('click', testReach);

        Shared.getEl('btnSave').addEventListener('click', save);
        Shared.initCollapsibles();
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 1,
        tabs: TABS,
        bind: bind,
        isDirty: isDirty,
        load: function (shared, page) {
            load();
            page.createPoller(refresh, 10000).start(false);
        }
    }).shared;
}
