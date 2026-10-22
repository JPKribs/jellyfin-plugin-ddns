import { createConfigPage, confirmDialog } from './configurationpage?name=ddns_jpkribs_shared.js';
import { createHostnamePicker } from './configurationpage?name=ddns_hostnames.js';
import { PLUGIN_ID, TABS, errorText, showText } from './configurationpage?name=ddns_tabs.js';

export default function (view) {
    'use strict';

    // What to do next for each section that is not finished, in the order they matter.
    var NEXT = {
        power: 'Turn on remote access.',
        address: 'Choose your address.',
        certificate: 'Add a certificate.',
        jellyfin: 'Let Jellyfin trust the proxy.'
    };

    var Shared = null;
    var settings = null;
    var hostnames = null;
    var arranged = false;
    // The last status shown, for the certificate actions.
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

    // Tints a status card by its state and shows the state's word.
    function setState(id, badge) {
        Shared.getEl('card' + id).className = 'jpk-card ' + badge[0];
        Shared.getEl('state' + id).textContent = badge[1];
    }

    function show(id, text) {
        showText(Shared, id, text);
    }

    function formatDate(value) {
        if (!value) return '';
        var d = new Date(value);
        return isNaN(d.getTime()) ? '' : d.toLocaleDateString();
    }

    function expired(value) {
        return !!value && new Date(value).getTime() <= Date.now();
    }

    // Within two weeks of expiring, and not already expired.
    function soon(value) {
        return !!value && !expired(value) && new Date(value).getTime() - Date.now() < 14 * 86400000;
    }

    // Works out each section's state from the status, so the badges and the summary agree.
    function assess(st) {
        var cert = st.Certificate || {};
        var parts = {};

        if (!st.Enabled) parts.power = { done: false, badge: ['gray', 'Off'] };
        else if (st.Running) parts.power = { done: true, badge: ['green', 'Running'] };
        else parts.power = { done: false, badge: ['red', 'Not running'], why: st.Error || 'The proxy is not running.' };

        parts.address = (settings.Hostnames || []).length
            ? { done: true, badge: ['green', 'Ready'] }
            : { done: false, badge: ['gray', 'Not set'], why: 'Without one, no certificate can be issued.' };

        var acme = st.Acme || {};
        // A stopped proxy loads no certificate, so only the chosen mode can be described.
        if (!st.Running && cert.Mode && cert.Mode !== 'None' && !cert.Loaded) parts.certificate = { done: false, badge: ['gray', 'Not checked'], why: 'It is checked once the proxy is running.' };
        else if (cert.Mode === 'Automatic' && !cert.Loaded) parts.certificate = automaticState(st, acme);
        else if (!cert.Mode || cert.Mode === 'None') parts.certificate = { done: false, badge: ['gray', 'Not set'], why: 'Without one, browsers and apps cannot connect securely.' };
        else if (!cert.Loaded) parts.certificate = { done: false, badge: ['red', 'Cannot load'], why: 'The current one could not be loaded.' };
        else if ((cert.UncoveredHostnames || []).length) parts.certificate = { done: false, badge: ['red', 'Wrong names'], why: 'The current one does not cover your address.' };
        else if (cert.Error) parts.certificate = { done: false, badge: ['orange', 'Check file'], why: 'The certificate file changed and could not be loaded, so the previous one is still served.' };
        else if (cert.Mode === 'SelfSigned') parts.certificate = { done: true, badge: ['blue', 'Self-signed'] };
        else if (expired(cert.NotAfterUtc)) parts.certificate = { done: false, badge: ['red', 'Expired'], why: 'The current one expired on ' + formatDate(cert.NotAfterUtc) + '.' };
        else if (soon(cert.NotAfterUtc)) parts.certificate = { done: true, badge: ['orange', 'Expires soon'] };
        else parts.certificate = { done: true, badge: ['green', 'Ready'] };

        parts.router = { done: true };

        // What Jellyfin actually trusts decides, so a setting changed outside this page shows up here.
        if (st.TakeoverPhase === 'Managed' && st.JellyfinTrustsProxy === false) parts.jellyfin = { done: false, why: 'Jellyfin no longer trusts the proxy. Its network settings were changed since.' };
        else if (st.TakeoverPhase === 'Managed' || (st.TakeoverPhase !== 'PendingRestart' && st.JellyfinTrustsProxy)) parts.jellyfin = { done: true };
        else if (st.TakeoverPhase === 'PendingRestart') parts.jellyfin = { done: false, why: 'Jellyfin is restarting.' };
        else parts.jellyfin = { done: false, why: 'Until then, internet visitors are turned away.' };

        return parts;
    }

    function setOpen(key, open) {
        var section = view.querySelector('[data-section="' + key + '"]');
        var header = section.querySelector('.jpk-collapsible-header');
        header.classList.toggle('collapsed', !open);
        header.setAttribute('aria-expanded', open ? 'true' : 'false');
        section.querySelector('.jpk-collapsible-content').classList.toggle('collapsed', !open);
        return section;
    }

    // On the first load only, open the sections that need something and fold the finished ones away.
    function arrange(parts) {
        if (arranged) return;
        arranged = true;
        ['address', 'certificate', 'router', 'jellyfin'].forEach(function (key) { setOpen(key, !parts[key].done); });
    }

    // A status card opens its section and brings it into view.
    function openFromCard(e) {
        var card = e.target.closest('[data-open]');
        if (card) setOpen(card.getAttribute('data-open'), true).scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    // Before Let's Encrypt has issued anything: why not yet, and what it needs.
    function automaticState(st, acme) {
        if (!(settings.Certificate || {}).AcceptTerms) return { done: false, badge: ['gray', 'Terms'], why: 'Accept the Let\'s Encrypt terms in the Certificate section.' };
        if (!(acme.Names || []).length) return { done: false, badge: ['gray', 'Waiting'], why: 'Let\'s Encrypt needs an address that is not a wildcard.' };
        if (acme.Issuing) return { done: false, badge: ['purple', 'Issuing'], why: 'Let\'s Encrypt is checking your address.' };
        if (acme.LastError) return { done: false, badge: ['red', 'Failed'], why: acme.LastError };
        if (!st.Running) return { done: false, badge: ['gray', 'Waiting'], why: 'It is requested once the proxy is running.' };
        return { done: false, badge: ['purple', 'Requested'], why: acme.Method === 'Dns'
            ? 'Let\'s Encrypt checks your address through ' + (acme.DnsProviders || []).join(' and ') + '.'
            : 'Forward port 80 so Let\'s Encrypt can check your address.' };
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

    function renderSummary(st, parts) {
        var box = Shared.getEl('summary');
        var next = ['power', 'address', 'certificate', 'jellyfin'].filter(function (k) { return !parts[k].done; })[0];
        if (!st.Enabled) {
            box.className = 'jpk-info';
            box.textContent = 'Remote access is off.';
        } else if (next) {
            box.className = 'jpk-warn';
            box.innerHTML = '<strong>Recommendation</strong><br>' + Shared.escapeHtml(NEXT[next] + (parts[next].why ? ' ' + parts[next].why : ''));
        } else {
            box.className = 'jpk-success';
            box.textContent = 'Remote access is ready' + (st.PublicUrl ? ' at ' + st.PublicUrl : '') + '.';
        }
    }

    function renderStatus(st) {
        if (!st || !settings) return;
        var parts = assess(st);
        setState('Power', parts.power.badge);
        setState('Address', parts.address.badge);
        setState('Certificate', parts.certificate.badge);
        renderSummary(st, parts);
        arrange(parts);
        describeMethod(st);

        show('powerError', st.Enabled && !st.Running ? st.Error : '');
        show('folderProblem', st.FolderProblem);
        show('everyoneLocal', st.Enabled && st.EveryoneLooksLocal
            ? 'Most visitors in the last hour came from one local address, so something in front of the proxy, such as rootless Docker or a tunnel, may be hiding their real addresses. Bans, the block list and country rules cannot work until those addresses reach the proxy.'
            : '');
        show('countryNote', st.Enabled && st.CountryRule ? st.CountryRule + ' Change it under Advanced → Blocking.' : '');

        var cert = st.Certificate || {};
        Shared.setVisible('certStatus', !!cert.Loaded);
        Shared.getEl('certNames').textContent = (cert.Names || []).join(', ');
        Shared.getEl('certExpires').textContent = formatDate(cert.NotAfterUtc);
        // The issuer's common name alone; the rest of the distinguished name is noise here.
        var issuer = cert.Issuer || '';
        var cn = /(?:^|,\s*)CN=([^,]+)/.exec(issuer);
        Shared.getEl('certIssuer').textContent = cn ? cn[1].trim() : issuer;
        var problem = '';
        var acmeError = cert.Mode === 'Automatic' && st.Acme && !st.Acme.Issuing && st.Acme.LastError;
        if (acmeError) problem = 'Let\'s Encrypt did not issue a certificate. ' + st.Acme.LastError;
        else if (cert.Error) problem = 'The certificate could not be loaded. ' + cert.Error;
        else if ((cert.UncoveredHostnames || []).length) problem = 'This certificate does not cover ' + cert.UncoveredHostnames.join(', ') + '. Use a certificate issued for your address.';
        else if (cert.Loaded && expired(cert.NotAfterUtc)) problem = 'This certificate expired on ' + formatDate(cert.NotAfterUtc) + '. Browsers and apps refuse it until it is renewed.';
        else if (cert.Loaded && soon(cert.NotAfterUtc)) problem = 'This certificate expires ' + formatDate(cert.NotAfterUtc) + '. Renew it soon.';
        show('certProblem', problem);

        lastStatus = st;
        showCertActions();

        var lan = st.LanAddress || st.DetectedLanAddress || 'this server';
        // The internet always uses 80 and 443; only a changed proxy port needs the router to translate.
        var target = '<strong>' + Shared.escapeHtml(lan) + '</strong>';
        var external = st.HttpPort === 80 && st.HttpsPort === 443
            ? 'Forward TCP ports <strong>80</strong> and <strong>443</strong> to ' + target + '.'
            : 'Forward TCP port <strong>443</strong> to ' + target + ' port <strong>' + st.HttpsPort + '</strong>, and port <strong>80</strong> to port <strong>' + st.HttpPort + '</strong>.';
        var test = st.PublicUrl
            ? '<br>Then open <span class="jpk-mono">' + Shared.escapeHtml(st.PublicUrl) + '</span> on a phone with Wi-Fi turned off.'
            : '';
        // Port 80 sends http:// links on to HTTPS, and is where Let's Encrypt checks the address.
        var httpCheck = cert.Mode === 'Automatic' && (st.Acme || {}).Method !== 'Dns';
        var port80 = '<br>Port 80 sends http:// links on to HTTPS' + (httpCheck ? ' and lets Let\'s Encrypt check your address.' : '.');
        Shared.getEl('routerInstruction').innerHTML = external + port80 + test;

        var phases = {
            Unmanaged: 'Jellyfin does not trust the proxy yet, so internet visitors are turned away.',
            PendingRestart: 'Jellyfin is restarting to trust the proxy.',
            Managed: 'Jellyfin trusts the proxy and keeps its own ports, ' + st.JellyfinHttpPort + (st.JellyfinHttpsPort ? ' and ' + st.JellyfinHttpsPort : '') + '.'
        };
        var phaseText = phases[st.TakeoverPhase] || '';
        if (st.TakeoverPhase === 'Managed' && st.JellyfinTrustsProxy === false) phaseText = parts.jellyfin.why + ' Trust the proxy again to fix this.';
        else if (st.TakeoverPhase === 'Unmanaged' && st.JellyfinTrustsProxy) phaseText = 'Jellyfin already trusts the proxy through its own network settings.';
        Shared.getEl('jellyfinState').textContent = phaseText;
        show('jellyfinError', st.TakeoverError);
        show('jellyfinExposed', st.JellyfinExposedAt
            ? 'This server also has the public address ' + st.JellyfinExposedAt + ', where Jellyfin\'s own port ' + st.JellyfinHttpPort +
              ' answers without the proxy\'s protection. Block Jellyfin\'s ports for the internet in this machine\'s firewall.'
            : '');
        var managed = st.TakeoverPhase === 'Managed' || st.TakeoverPhase === 'PendingRestart';
        Shared.setVisible('btnRestore', managed);
        // Trusting again keeps the original snapshot, so Undo still puts back what was there first.
        Shared.setVisible('btnMove', !managed || (st.TakeoverPhase === 'Managed' && st.JellyfinTrustsProxy === false));
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

    // Whether these settings look like they were never saved: the proxy off, no address, no services and
    // no certificate choice made. An empty address list saved on purpose has at least one of those set.
    function neverSaved(s) {
        var cert = s.Certificate || {};
        return !s.Enabled && !(s.Hostnames || []).length && !(s.Routes || []).length
            && !cert.AcceptTerms && !cert.Path && !cert.KeyPath && !cert.Password;
    }

    // The form's current values, to tell whether leaving would lose changes.
    function formState() {
        var s = collect({});
        s.Hostnames = s.Hostnames.slice().sort();
        return JSON.stringify(s);
    }

    function isDirty() {
        return baseline !== null && formState() !== baseline;
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
        return s;
    }

    function refreshStatus() {
        return getStatus().then(renderStatus).catch(function () { /* the poller tries again */ });
    }

    function load() {
        baseline = null;
        Promise.all([getSettings(), getStatus(), hostnames.load()]).then(function (results) {
            // The DDNS names are loaded with the rest, so the suggested addresses are known before filling.
            if (results[0]) fill(results[0], true);
            renderStatus(results[1]);
        }).catch(function () {
            Shared.setStatus('saveStatus', 'Could not load settings.', true);
        });
    }

    // Saves onto the freshest settings so changes made on the other tabs are kept.
    function save() {
        // Let's Encrypt is only used once its terms are accepted, so the box has to be ticked to choose it.
        if (Shared.getEl('certMode').value === 'Automatic' && !Shared.getEl('certTerms').checked) {
            Shared.setStatus('saveStatus', 'Tick I accept the Let\'s Encrypt terms, or choose another certificate source.', true, { timeout: 0 });
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
            if (result && result.Status) renderStatus(result.Status);
            Shared.setStatus('saveStatus', 'Saved.', false);
            return true;
        }).catch(function (err) {
            return errorText(err, 'Save failed.').then(function (text) {
                Shared.setStatus('saveStatus', text, true, { timeout: 0 });
                return false;
            });
        }).then(function (ok) {
            button.disabled = false;
            return ok;
        });
    }

    function move() {
        var message = 'Jellyfin adds this machine to its known proxies, turns on remote access and restarts once. Its ports stay the same. ' +
            'Forward only ports 80 and 443 to this server: anyone who can reach Jellyfin\'s own ports directly gets past the proxy.';
        confirmDialog(message, { title: 'Trust the Proxy', confirmText: 'Trust' }).then(function (ok) {
            if (!ok) return;
            save().then(function (saved) {
                if (!saved) return;
                Shared.setStatus('moveStatus', 'Checking the proxy can reach Jellyfin...', false, { timeout: 0 });
                Shared.apiRequest('Proxy/TakeOver', 'POST').then(function (result) {
                    Shared.setStatus('moveStatus', (result && result.Message) || 'Done.', !(result && result.Success), { timeout: 0 });
                    refreshStatus();
                }).catch(function () {
                    Shared.setStatus('moveStatus', 'Trusting the proxy failed.', true, { timeout: 0 });
                });
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
        confirmDialog('Let\'s Encrypt is asked for a new certificate now, even if the current one is fine. The current one keeps working until the new one is ready.', { title: 'Request a New Certificate', confirmText: 'Request' }).then(function (ok) {
            if (!ok) return;
            Shared.setStatus('certRequestStatus', 'Requesting...', false, { timeout: 0 });
            Shared.apiRequest('Proxy/Certificate/Request', 'POST').then(function (result) {
                Shared.setStatus('certRequestStatus', (result && result.Message) || 'Done.', !(result && result.Ok), { timeout: 0 });
                if (result && result.Status) renderStatus(result.Status);
            }).catch(function () {
                Shared.setStatus('certRequestStatus', 'The request failed.', true, { timeout: 0 });
            });
        });
    }

    function restore() {
        confirmDialog('Jellyfin\'s network settings go back to how they were, and internet visitors through the proxy are turned away. Jellyfin restarts once.', { title: 'Undo', confirmText: 'Undo', destructive: true }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('Proxy/Restore', 'POST').then(function (result) {
                Shared.setStatus('moveStatus', (result && result.Message) || 'Restored.', !(result && result.Success), { timeout: 0 });
                refreshStatus();
            }).catch(function () {
                Shared.setStatus('moveStatus', 'Restore failed.', true, { timeout: 0 });
            });
        });
    }

    function bind() {
        hostnames = createHostnamePicker(Shared, 'hostnamePicker', 'hostnames');
        Shared.getEl('certMode').addEventListener('change', showCertFields);
        Shared.getEl('btnSave').addEventListener('click', save);
        Shared.getEl('btnMove').addEventListener('click', move);
        Shared.getEl('btnRestore').addEventListener('click', restore);
        Shared.getEl('btnRequestCert').addEventListener('click', requestCertificate);
        Shared.getEl('btnDownloadRoot').addEventListener('click', downloadRoot);
        view.querySelector('.ddns-status').addEventListener('click', openFromCard);
        Shared.initCollapsibles();
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 3,
        tabs: TABS,
        bind: bind,
        isDirty: isDirty,
        load: function (shared, page) {
            load();
            page.createPoller(refreshStatus, 10000).start(false);
        }
    }).shared;
}
