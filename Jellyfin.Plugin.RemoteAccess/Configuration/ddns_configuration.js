import { createConfigPage, confirmDialog } from './configurationpage?name=ddns_jpkribs_shared.js';
import { PLUGIN_ID, TABS, errorText, showText, trustTheProxy, routerInstructionHtml, routerStateText, loadProviders, primaryRecord, readDdnsSecret, recordNames, saveProxyHostnames } from './configurationpage?name=ddns_tabs.js';

// The Configuration tab: a checklist from turning the proxy on to a test from outside. Each step holds only what
// most people need: the proxy switch, the one DDNS record whose name is the proxy's address, the certificate, the
// router, Trust the Proxy and the outside test. The finer settings are under Advanced. Every state is worked out
// from Proxy/Status and Proxy/Settings, plus the DDNS record for someone who only uses Dynamic DNS.
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

    // ===== The DDNS record, in step 2 =====

    // A server keeps one record, whose names are the proxy's address. Only the fields every provider needs are
    // here: the provider, the hostname and the credentials it asks for. The rest of the record is under Advanced.

    // The provider list and the fields each provider uses, filled once by loadProviders.
    var providers = null;
    // The record as the server last sent it, or null when there is none. Its credentials hold the server's kept
    // marker or empty, never a value.
    var record = null;
    // Records an older setup saved beyond the one shown. They are dropped on the next save.
    var extraCount = 0;
    var recordsLoaded = false;
    // The detected public IP, from the DDNS configuration.
    var detected = {};
    // The record fields as last loaded or saved, to tell whether they changed. Null until loaded.
    var recordBaseline = null;

    var SECRET_IDS = ['recLogin', 'recPassword'];
    var CLEAR_BUTTONS = { recLogin: 'btnClearLogin', recPassword: 'btnClearPassword' };

    // The plugin's own config endpoints send a kept marker in place of a stored credential and encrypt a typed one
    // on save, so secrets never reach the browser.
    function getConfig() { return Shared.apiRequest('Configuration', 'GET'); }
    function saveConfig(cfg) { return Shared.apiRequest('Configuration', 'POST', cfg); }

    function ensureProviders() {
        if (providers) return Promise.resolve(providers);
        return loadProviders(Shared).then(function (p) { providers = p; return p; });
    }

    function specFor(provider) {
        return providers.specFor(provider);
    }

    function newRecord() {
        return {
            Id: Shared.generateGuid(), Name: '', Provider: 'Cloudflare', Hostname: '', Enabled: true,
            UpdateIPv4: true, UpdateIPv6: true,
            Login: '', Password: '', Zone: '', Server: '', Ttl: 1,
            Proxied: false, Wildcard: false, Mx: '', BackupMx: false, Static: false
        };
    }

    function renderProviderOptions() {
        Shared.getEl('recProvider').innerHTML = providers.options.map(function (p) {
            return '<option value="' + Shared.escapeHtml(p[0]) + '">' + Shared.escapeHtml(p[1]) + '</option>';
        }).join('');
    }

    // Provider hint plus a link to that provider's setup doc on GitHub, named after the provider value.
    function applyHint() {
        var prov = Shared.getEl('recProvider').value;
        var hint = providers.hints[prov] || 'Fill the fields required by this provider.';
        var html = Shared.escapeHtml(hint);
        if (prov) {
            var url = 'https://github.com/JPKribs/jellyfin-plugin-remoteaccess/blob/master/docs/providers/' + encodeURIComponent(prov) + '.md';
            html += ' <a href="' + url + '" target="_blank" rel="noopener">Setup guide</a>';
        }
        Shared.getEl('providerHint').innerHTML = html;
    }

    // Shows a field with the provider's own label for it, or hides it when the provider does not use it.
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

    // Providers whose hostname field holds a record id rather than a name, the same list the server uses.
    var ID_PROVIDERS = ['DnsMadeEasy', 'Sitelutions'];

    // Whether the record's hostname can be the proxy's address. A provider without a hostname field, or one keyed
    // by a record id, needs the name under Other addresses instead.
    function namesTheProxy(provider) {
        return !!specFor(provider).h && ID_PROVIDERS.indexOf(provider) < 0;
    }

    function onProviderChange() {
        applyHint();
        var provider = Shared.getEl('recProvider').value;
        var spec = specFor(provider);
        Shared.getEl('recHostnameDescription').textContent = namesTheProxy(provider)
            ? 'The name people use to reach Jellyfin.'
            : 'Not a name. Add your name under Advanced > Addresses.';
        setField('recHostname', spec.h);
        setField('recLogin', spec.l);
        setField('recPassword', spec.p);
        setField('recZone', spec.z);
    }

    function bindSecret(id, stored) {
        Shared.bindSecretField(id, stored);
        Shared.setVisible(CLEAR_BUTTONS[id], stored);
    }

    // Marks a stored credential for removal on the next save. Typing a new value still replaces it.
    function clearSecret(id) {
        Shared.clearSecretField(id);
        Shared.getEl(id).placeholder = 'Cleared when saved';
        Shared.setVisible(CLEAR_BUTTONS[id], false);
    }

    // The record fields as typed, to tell whether they changed.
    function recordState() {
        return JSON.stringify({
            provider: Shared.getEl('recProvider').value,
            hostname: Shared.getEl('recHostname').value.trim(),
            zone: Shared.getEl('recZone').value.trim(),
            secrets: SECRET_IDS.map(function (id) { var el = Shared.getEl(id); return [el.value, !!el.dataset.jpkSecretCleared]; })
        });
    }

    // Whether anything was typed for a record that does not exist yet.
    function hasRecordInput() {
        return ['recHostname', 'recZone'].concat(SECRET_IDS).some(function (id) { return !!Shared.getEl(id).value.trim(); });
    }

    function recordDirty() {
        if (!recordsLoaded || recordBaseline === null) return false;
        if (!record && !hasRecordInput()) return false;
        return recordState() !== recordBaseline;
    }

    // Changed fields are saved, and so are extra records, which the save drops.
    function recordNeedsSave() {
        return recordDirty() || (recordsLoaded && extraCount > 0);
    }

    function fillRecord(r) {
        record = r || null;
        Shared.getEl('recProvider').value = providers.normalize(r ? r.Provider : 'Cloudflare');
        Shared.getEl('recHostname').value = (r && r.Hostname) || '';
        Shared.getEl('recZone').value = (r && r.Zone) || '';
        bindSecret('recLogin', !!(r && r.Login));
        bindSecret('recPassword', !!(r && r.Password));
        onProviderChange();
        renderRecordStatus();
        recordBaseline = recordState();
    }

    function showExtras() {
        show('extraRecords', !extraCount ? ''
            : extraCount === 1 ? 'This server has 1 more DDNS record saved. It is removed on the next save, and only the record below is kept.'
                : 'This server has ' + extraCount + ' more DDNS records saved. They are removed on the next save, and only the record below is kept.');
    }

    function renderRecordStatus() {
        var parts = [detected.LastDetectedIPv4, detected.LastDetectedIPv6].filter(function (x) { return x; });
        Shared.getEl('stIp').textContent = parts.length ? parts.join(' / ') : 'Not detected yet';
        show('detectionWarning', detected.LastDetectionMessage);

        var r = record;
        var checked = r && r.LastCheckedUtc ? new Date(r.LastCheckedUtc) : null;
        Shared.getEl('stChecked').textContent = !checked ? 'Not yet' : isNaN(checked.getTime()) ? String(r.LastCheckedUtc) : checked.toLocaleString();

        var result = Shared.getEl('stResult');
        var text;
        var tone = '';
        if (!r) text = 'No record yet';
        else if (r.Enabled === false) text = 'Paused';
        else if (!r.LastCheckedUtc) text = 'Not sent yet';
        else {
            text = r.LastStatus || (r.LastSuccess ? 'OK' : 'Failed');
            tone = r.LastSuccess ? ' jpk-ok' : ' jpk-bad';
        }
        result.textContent = text;
        result.className = 'jpk-mono' + tone;
    }

    // The record to save: the server's copy with the fields from this page. A record never saved starts from the
    // defaults. Its name follows the hostname unless it was given one of its own.
    function buildRecord(base) {
        var r = base ? JSON.parse(JSON.stringify(base)) : newRecord();
        var oldHost = r.Hostname || '';
        r.Provider = Shared.getEl('recProvider').value;
        // A provider that works the host out from the account hides the field, and stores no hostname.
        r.Hostname = specFor(r.Provider).h ? Shared.getEl('recHostname').value.trim() : '';
        r.Zone = Shared.getEl('recZone').value.trim();
        if (!r.Name || r.Name === oldHost) r.Name = r.Hostname;
        r.Login = readDdnsSecret(Shared, 'recLogin');
        r.Password = readDdnsSecret(Shared, 'recPassword');
        return r;
    }

    // A problem found on the page, shown as it is rather than as a server reply.
    function pageError(text) {
        var err = new Error(text);
        err.pageText = text;
        return err;
    }

    // Saves the record as the only one, onto the freshest configuration, so the detection settings saved under
    // Advanced are kept. Resolves once saved, or at once when nothing changed.
    function saveRecord() {
        if (!recordNeedsSave()) return Promise.resolve();
        var provider = Shared.getEl('recProvider').value;
        if (specFor(provider).h && !Shared.getEl('recHostname').value.trim() && (record || hasRecordInput())) {
            setStepOpen('Address', true);
            return Promise.reject(pageError('Enter the hostname in step 2.'));
        }

        var id = record ? record.Id : null;
        return getConfig().then(function (fresh) {
            if (!fresh) { throw new Error('settings unavailable'); }
            var base = id ? (fresh.Records || []).find(function (x) { return x.Id === id; }) : null;
            // No record and nothing typed: the save only drops the extra records.
            fresh.Records = !record && !hasRecordInput() ? [] : [buildRecord(base || (record ? record : null))];
            return saveConfig(fresh);
        }).then(function (result) {
            if (!result || !result.Records) { throw new Error('no reply'); }
            detected = result;
            extraCount = Math.max(0, result.Records.length - 1);
            fillRecord(primaryRecord(result.Records));
            showExtras();
        });
    }

    function loadRecords() {
        recordsLoaded = false;
        recordBaseline = null;
        return ensureProviders().then(getConfig).then(function (cfg) {
            cfg = cfg || {};
            var list = cfg.Records || [];
            detected = cfg;
            renderProviderOptions();
            extraCount = Math.max(0, list.length - 1);
            fillRecord(primaryRecord(list));
            showExtras();
            recordsLoaded = true;
            show('loadError', '');
        }).catch(function () {
            show('loadError', 'Could not load providers or settings.');
        });
    }

    // ===== Update Now, in step 2 =====

    // Says how the run went. With no result for the record, the configuration tells apart having no record, a
    // paused record and a run that left the record as it was.
    function runMessage(outcome, cfg) {
        var list = (outcome && outcome.Records) || [];
        var warnings = (outcome && outcome.Warnings) || [];
        var failed = list.some(function (x) { return !x.Success; });
        var text;
        if (list.length) {
            text = failed ? 'Finished. The record did not update.' : 'Finished. The record is up to date.';
        } else if (warnings.length) {
            text = '';
        } else {
            var r = cfg ? primaryRecord(cfg.Records) : record;
            text = !r ? 'IP detected. No record is set up yet.'
                : r.Enabled === false ? 'IP detected. The record is paused under Advanced.'
                    : 'Finished. The record needed no update.';
        }

        return {
            text: [text].concat(warnings).filter(function (x) { return x; }).join(' '),
            bad: warnings.length > 0 || failed
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
                if (fresh && recordsLoaded) {
                    detected = fresh;
                    // The run changed the record's status. Edits not saved yet stay as they are.
                    var latest = primaryRecord(fresh.Records);
                    extraCount = Math.max(0, (fresh.Records || []).length - 1);
                    if (recordDirty()) {
                        if (latest && record && latest.Id === record.Id) record = latest;
                        renderRecordStatus();
                    } else {
                        fillRecord(latest);
                    }
                    showExtras();
                    if (lastStatus) render(lastStatus);
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

    // ===== Proxy settings, in steps 1 to 3 =====

    var settings = null;
    // The last status shown, for the certificate actions and to redraw the steps.
    var lastStatus = null;
    // The self-signed root, fetched once while it is shown.
    var rootLoaded = false;
    var rootPem = '';
    // The form as last loaded or saved, to tell whether leaving would lose changes. Null until loaded.
    var baseline = null;

    var CERT_NOTES = {
        Automatic: 'Issued and renewed for you.',
        None: 'Browsers need a certificate before they connect.',
        PemFiles: 'From certbot, acme.sh and similar tools. Renewals apply on their own.',
        Pkcs12File: 'A .pfx or .p12 file. A renewed file applies on its own.',
        SelfSigned: 'Trusted only by devices that install its root, which TVs cannot.'
    };

    function getSettings() { return Shared.apiRequest('Proxy/Settings', 'GET'); }
    function getStatus() { return Shared.apiRequest('Proxy/Status', 'GET'); }

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

    // The form's current values, to tell whether leaving would lose changes.
    function formState() {
        return JSON.stringify(collect({}));
    }

    function proxyDirty() {
        return baseline !== null && formState() !== baseline;
    }

    function isDirty() {
        return recordDirty() || proxyDirty();
    }

    function fill(s) {
        settings = s;
        Shared.getEl('enabled').checked = !!s.Enabled;
        var cert = s.Certificate || {};
        Shared.getEl('certMode').value = cert.Mode || 'Automatic';
        Shared.getEl('certPath').value = cert.Path || '';
        Shared.getEl('certKeyPath').value = cert.KeyPath || '';
        Shared.getEl('certTerms').checked = !!cert.AcceptTerms;
        Shared.bindSecretField('certPassword', !!cert.Password);
        showCertFields();
        baseline = formState();
    }

    // The proxy's addresses are not on this page: the save sets them from the record's names and the other
    // addresses kept under Advanced.
    function collect(s) {
        s.Enabled = Shared.getEl('enabled').checked;
        s.Certificate = s.Certificate || {};
        s.Certificate.Mode = Shared.getEl('certMode').value;
        s.Certificate.Path = Shared.getEl('certPath').value.trim();
        s.Certificate.KeyPath = Shared.getEl('certKeyPath').value.trim();
        s.Certificate.AcceptTerms = Shared.getEl('certTerms').checked;
        s.Certificate.Password = Shared.readSecretField('certPassword');
        return s;
    }

    // The names the record covers now, which the save replaces in the proxy's addresses.
    function namesNow() {
        return record ? recordNames(Shared, record.Id).catch(function () { return []; }) : Promise.resolve([]);
    }

    function setSaving(on) {
        ['btnSave', 'btnRunNow'].forEach(function (id) { Shared.getEl(id).disabled = on; });
    }

    // Saves every step: the record first, when it changed, then the proxy settings onto the freshest ones so
    // changes made on the other tabs are kept. The proxy answers on the record's names and the other addresses.
    function save() {
        // Let's Encrypt is only used once its terms are accepted, so with the proxy on the box has to be ticked to
        // choose it.
        if (Shared.getEl('enabled').checked && Shared.getEl('certMode').value === 'Automatic' && !Shared.getEl('certTerms').checked) {
            Shared.setStatus('saveStatus', 'Tick I accept the Let\'s Encrypt terms in step 3, or choose another certificate source.', true, { timeout: 0 });
            setStepOpen('Certificate', true);
            return Promise.resolve(false);
        }

        var button = Shared.getEl('btnSave');
        if (button.disabled) return Promise.resolve(false);
        setSaving(true);
        Shared.setStatus('saveStatus', 'Saving...', false);
        var recordSaved = false;
        var changedRecord = recordNeedsSave();
        var before = [];
        return namesNow().then(function (names) {
            before = names;
            return saveRecord();
        }).then(function () {
            recordSaved = true;
            return saveProxyHostnames(Shared, before, null, function (fresh) { return collect(fresh || settings); });
        }).then(function (result) {
            if (result && result.Settings) fill(result.Settings);
            if (result && result.Status) render(result.Status);
            Shared.setStatus('saveStatus', 'Saved.', false);
            return true;
        }).catch(function (err) {
            var reply = err && err.pageText ? Promise.resolve(err.pageText) : errorText(err, 'Save failed.');
            return reply.then(function (text) {
                var prefix = !recordSaved ? (changedRecord ? 'The record was not saved. ' : '')
                    : changedRecord ? 'The record was saved, but the proxy settings were not. ' : '';
                Shared.setStatus('saveStatus', prefix + text, true, { timeout: 0 });
                return false;
            });
        }).then(function (ok) {
            setSaving(false);
            refresh();
            return ok;
        });
    }

    function retryRouter() {
        var button = Shared.getEl('btnRouterRetry');
        if (button.disabled) return;
        button.disabled = true;
        Shared.setStatus('routerStatus', 'Asking the router...', false, { timeout: 0 });
        Shared.apiRequest('Proxy/Router/Refresh', 'POST').then(function (r) {
            var text = !r ? 'Done.'
                : r.Mapped ? routerStateText(r)
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
        if (st.Running) return step('green', 'Running', 'Running on ports ' + st.HttpPort + ' and ' + st.HttpsPort + '.');
        if (st.Enabled) return step('red', 'Not running', st.Error || 'The proxy is turned on but is not running.');
        return step('gray', 'Off', 'Turn it on to reach Jellyfin securely from the internet. Step 2 works without it.');
    }

    // The name endings only a home network knows, the same list the server uses. Phones outside cannot open them.
    var PRIVATE_ENDINGS = ['lan', 'local', 'localdomain', 'localhost', 'home', 'internal', 'intranet', 'corp', 'private', 'test', 'invalid', 'example'];

    function isPublicName(name) {
        var labels = String(name || '').replace(/^\*\./, '').toLowerCase().split('.');
        if (labels.length < 2 || /^[0-9.]+$/.test(name) || name.indexOf(':') >= 0) return false;
        return PRIVATE_ENDINGS.indexOf(labels[labels.length - 1]) < 0 && !/\.home\.arpa$/.test(name);
    }

    // With the proxy off, the address step is only about the DDNS record.
    function assessRecord() {
        if (!recordsLoaded) return step('gray', 'Checking', 'Loading the DDNS record.');
        var r = record;
        if (!r) return step('gray', 'No record', 'Fill in the record below.');
        if (r.Enabled === false) return step('gray', 'Paused', 'Updates are paused under Advanced > Dynamic DNS.');
        if (!r.LastCheckedUtc) return step('gray', 'Not updated yet', 'Select Update Now to send it.');
        var checked = new Date(r.LastCheckedUtc).getTime();
        var recent = !isNaN(checked) && (Date.now() - checked) < 86400000;
        if (!r.LastSuccess || !recent) return step('red', 'Failing', 'Not updated in the last day. See the result below.');
        return step('green', 'Up to date', 'The record points at this server.');
    }

    function assessAddress(st) {
        if (!st.Enabled) return assessRecord();
        var names = chosenNames(st);
        if (!names.length) {
            var where = record && providers && !namesTheProxy(providers.normalize(record.Provider))
                ? 'Add your name under Advanced > Addresses.'
                : 'Enter your hostname below and save.';
            return step('amber', 'Not set', where);
        }
        if (!names.some(isPublicName)) return step('amber', 'Home only', names.join(', ') + ' only works at home. Use a public name.');
        var n = st.Name;
        // An older server does not look the name up, so a chosen name is as far as this can check.
        if (!n) return step('green', 'Chosen', 'People reach Jellyfin at ' + names.join(', ') + '.');
        var host = n.Hostname || names[0];
        if (n.Error) return step('red', 'Not found', host + ' could not be looked up. ' + n.Error);
        if (n.Resolves) return step('green', 'Ready', host + ' points to this server.');
        if (!(n.Addresses || []).length) return step('amber', 'Not pointed here', host + ' does not point anywhere yet. New records can take a few minutes.');
        return step('amber', 'Not pointed here', host + ' points somewhere else. Check the record below.');
    }

    function assessCertificate(st) {
        if (!st.Enabled) return idleStep();
        var cert = st.Certificate || {};
        var acme = st.Acme || {};
        var chosen = (settings && settings.Certificate) || {};
        var mode = cert.Mode || chosen.Mode || 'Automatic';
        if (mode === 'Automatic' && !chosen.AcceptTerms) {
            return step('amber', 'Terms', 'Accept the terms below and save to get a free certificate.');
        }
        if (cert.Loaded) {
            if ((cert.UncoveredHostnames || []).length) return step('red', 'Wrong names', 'The certificate does not cover ' + cert.UncoveredHostnames.join(', ') + '.');
            if (expired(cert.NotAfterUtc)) return step('red', 'Expired', 'The certificate expired on ' + formatDate(cert.NotAfterUtc) + '. Browsers refuse it until it renews.');
            if (cert.Mode === 'SelfSigned') return step('amber', 'Self-signed', 'Only devices that install its root trust it. Let\'s Encrypt works everywhere.');
            if (cert.Error) return step('amber', 'Check file', 'The new file could not be loaded, so the old one is still used.');
            if (soon(cert.NotAfterUtc)) return step('amber', 'Expires soon', 'The certificate expires on ' + formatDate(cert.NotAfterUtc) + '.');
            var covers = (cert.Names || []).join(', ');
            return step('green', 'Ready', (covers ? 'Covers ' + covers : 'A certificate is in use') + (cert.NotAfterUtc ? ' until ' + formatDate(cert.NotAfterUtc) : '') + '.');
        }
        if (!st.Running) return step('gray', 'Not checked', 'Checked once the proxy runs.');
        if (mode === 'None') return step('amber', 'Not set', 'Browsers cannot connect securely without one.');
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
        if (reached) return step('green', 'Forwarded', 'The outside test reached this server.');
        if (!r) return step('gray', 'Not checked', 'Forward the ports by hand as below.');
        return step('amber', 'Forward by hand', r.Enabled
            ? 'The router did not open the ports. Forward them by hand as below.'
            : 'Automatic port forwarding is off. Forward the ports by hand as below.');
    }

    function assessTrust(st) {
        // Undo stays at hand while Jellyfin is still set up for the proxy, even with the proxy off.
        if (!st.Enabled && (!st.TakeoverPhase || st.TakeoverPhase === 'Unmanaged')) return idleStep();
        if (st.TakeoverPhase === 'Managed' && st.JellyfinTrustsProxy === false) return step('red', 'Not trusted', 'Jellyfin\'s network settings changed. Trust the proxy again.');
        if (st.TakeoverPhase === 'Managed') return step('green', 'Trusted', 'Jellyfin sees each visitor\'s real address.');
        if (st.TakeoverPhase === 'PendingRestart') return step('amber', 'Restarting', 'Jellyfin is restarting to trust the proxy.');
        if (st.JellyfinTrustsProxy) return step('green', 'Trusted', 'Jellyfin trusts the proxy through its own settings.');
        if (st.TakeoverError) return step('red', 'Failed', 'Trusting failed, so internet visitors are turned away.');
        return step('amber', 'Not yet', 'Internet visitors are turned away until then.');
    }

    function assessReach(st) {
        if (!st.Enabled) return idleStep();
        var reach = currentReach(st);
        if (!reach || (reach.Reachable == null && !reach.Error)) return step('gray', 'Not tested', 'Not tested yet.');
        var when = formatTime(reach.CheckedUtc);
        var nodes = reach.Nodes || [];
        var ok = nodes.filter(function (x) { return x.Ok; }).length;
        var tally = nodes.length ? ok + ' of ' + nodes.length + ' places' : '';
        if (reach.Reachable === true) return step('green', 'Reachable', 'Reached from ' + (tally || 'outside your network') + '.');
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
            summary('The proxy is off', 'gray', 'DDNS only', 'The DDNS record in step 2 still keeps your address pointed at this server.', -1);
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

    // The test's result as a titled card: when it ran, then each place it was tried from and how that went.
    function renderReachNodes(st) {
        var reach = currentReach(st);
        var nodes = (st.Enabled && reach && reach.Nodes) || [];
        var box = Shared.getEl('reachNodes');
        Shared.setVisible(box, nodes.length > 0);
        if (!nodes.length) { box.innerHTML = ''; return; }
        var when = formatTime(reach.CheckedUtc);
        var title = '<div class="jpk-record-row"><strong>Outside check</strong>' +
            '<span class="jpk-record-label">' + Shared.escapeHtml(when) + '</span></div>';
        box.innerHTML = title + nodes.map(function (x) {
            var badge = '<span class="jpk-badge ' + (x.Ok ? 'green' : 'red') + '">' + (x.Ok ? 'Reached' : 'Failed') + '</span>';
            var error = !x.Ok && x.Error ? '<span class="jpk-record-label">' + Shared.escapeHtml(x.Error) + '</span> ' : '';
            return '<div class="jpk-record-row"><span>' + Shared.escapeHtml(x.Location || 'Unknown') + '</span>' +
                '<span>' + error + badge + '</span></div>';
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
        renderCountryNote(st);

        renderAddressDetails(st, results[1]);
        renderCertificate(st);
        renderRouter(st, results[3]);
        renderTrust(st, results[4]);
        renderReachNodes(st);
    }

    function countryName(code) {
        try {
            return new Intl.DisplayNames([navigator.language || 'en'], { type: 'region' }).of(code) || code;
        } catch (e) {
            return code;
        }
    }

    // Names countries in bold, green for the ones let in and red for the ones refused.
    function countryList(names, css) {
        var bold = names.map(function (n) { return '<strong class="' + css + '">' + Shared.escapeHtml(n) + '</strong>'; });
        return bold.length < 2 ? bold.join('') : bold.slice(0, -1).join(', ') + ' and ' + bold[bold.length - 1];
    }

    // Says which countries can connect, so it is clear access is limited, with the way to change it.
    function renderCountryNote(st) {
        var box = Shared.getEl('countryNote');
        var adv = (settings && settings.Advanced) || {};
        var mode = adv.CountryMode || 'ServerCountry';
        var chosen = (adv.Countries || []).map(countryName);
        var html = '';
        if (st.Enabled && mode === 'ServerCountry') {
            html = st.ServerCountry
                ? 'Only visitors from ' + countryList([st.ServerCountry].concat(chosen), 'ddns-country-known') + ' can connect from the internet.'
                : '<strong class="ddns-country-unknown">This server\'s country is not known yet</strong>, so everyone can connect until it is. Then only visitors from its country' + (chosen.length ? ' and ' + countryList(chosen, 'ddns-country-known') : '') + ' can.';
        } else if (st.Enabled && mode === 'AllowOnly' && chosen.length) {
            html = 'Only visitors from ' + countryList(chosen, 'ddns-country-known') + ' can connect from the internet.';
        } else if (st.Enabled && mode === 'Block' && chosen.length) {
            html = 'Visitors from ' + countryList(chosen, 'ddns-country-blocked') + ' are refused.';
        }
        Shared.setVisible('countryNote', !!html);
        if (html) box.innerHTML = html + ' Change this in <a href="#/configurationpage?name=ddns_advanced">Advanced &gt; Blocking</a>.';
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
        // The record loads on its own, so a proxy problem never hides it.
        var recordsDone = loadRecords();
        Promise.all([getSettings(), getStatus()]).then(function (results) {
            if (results[0]) fill(results[0]);
            // Wait for the record, so the address step is not judged before it is known.
            return recordsDone.then(function () { render(results[1]); });
        }).catch(function () {
            summary('Status unavailable', 'red', 'Error', 'Could not load the proxy status.', -1);
        });
    }

    function bind() {
        Shared.getEl('summaryOpen').addEventListener('click', function () {
            var key = this.getAttribute('data-step');
            if (!key) return;
            setStepOpen(key, true);
            Shared.getEl('step' + key).scrollIntoView({ behavior: 'smooth', block: 'start' });
        });

        // Step 2: the record and Update Now.
        Shared.getEl('recProvider').addEventListener('change', onProviderChange);
        Shared.getEl('btnClearLogin').addEventListener('click', function () { clearSecret('recLogin'); });
        Shared.getEl('btnClearPassword').addEventListener('click', function () { clearSecret('recPassword'); });
        Shared.getEl('btnRunNow').addEventListener('click', runNow);

        // Steps 3 to 6.
        Shared.getEl('certMode').addEventListener('change', showCertFields);
        Shared.getEl('btnDownloadRoot').addEventListener('click', downloadRoot);
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
