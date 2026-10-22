import { createConfigPage } from './configurationpage?name=ddns_jpkribs_shared.js';
import { createHostnamePicker } from './configurationpage?name=ddns_hostnames.js';
import { PLUGIN_ID, TABS, errorText, showText, trustTheProxy, routerInstructionHtml, autoForwardingOn, autoForwardingSetting, routerStateText } from './configurationpage?name=ddns_tabs.js';

// The Setup tab: a checklist from turning the proxy on to a test from outside, each step with its state and the
// action that finishes it. Every state is worked out from Proxy/Status and Proxy/Settings, so it matches the other tabs.
export default function (view) {
    'use strict';

    // The steps in order. The key names the step's elements: step, icon, state and text followed by the key.
    var STEPS = [
        { key: 'Power', title: 'Proxy on' },
        { key: 'Address', title: 'Address' },
        { key: 'Certificate', title: 'Certificate' },
        { key: 'Router', title: 'Router' },
        { key: 'Trust', title: 'Trust the Proxy' },
        { key: 'Reach', title: 'Reachable from outside' }
    ];

    var ICONS = { green: '✓', amber: '!', red: '✕', gray: '○' };

    var Shared = null;
    var settings = null;
    var lastStatus = null;
    // The built-in defaults, for settings left unset. Empty on a server that has none.
    var defaults = {};
    var hostnames = null;
    // The last test from outside run from this page, kept until the status carries a newer one.
    var lastReach = null;
    // Whether the address picker was opened for a step that is already done.
    var changingAddress = false;

    function getSettings() { return Shared.apiRequest('Proxy/Settings', 'GET'); }
    function getStatus() { return Shared.apiRequest('Proxy/Status', 'GET'); }

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

    function soon(value) {
        return !!value && !expired(value) && new Date(value).getTime() - Date.now() < 14 * 86400000;
    }

    function step(state, word, text) {
        return { state: state, word: word, text: text || '' };
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
        return step('amber', 'Off', 'Turn the proxy on so the internet has a secure way in to Jellyfin.');
    }

    // The name endings only a home network knows, the same list the server uses. Phones outside cannot open them.
    var PRIVATE_ENDINGS = ['lan', 'local', 'localdomain', 'localhost', 'home', 'internal', 'intranet', 'corp', 'private', 'test', 'invalid', 'example'];

    function isPublicName(name) {
        var labels = String(name || '').replace(/^\*\./, '').toLowerCase().split('.');
        if (labels.length < 2 || /^[0-9.]+$/.test(name) || name.indexOf(':') >= 0) return false;
        return PRIVATE_ENDINGS.indexOf(labels[labels.length - 1]) < 0 && !/\.home\.arpa$/.test(name);
    }

    function assessAddress(st) {
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
        return step('amber', 'Not pointed here', host + ' points somewhere other than this server. Check its record on the Domains tab.');
    }

    function assessCertificate(st) {
        var cert = st.Certificate || {};
        var acme = st.Acme || {};
        var chosen = (settings && settings.Certificate) || {};
        var mode = cert.Mode || chosen.Mode || 'Automatic';
        if (mode === 'Automatic' && !chosen.AcceptTerms) {
            return step('amber', 'Terms', 'Let\'s Encrypt gives your address a free certificate that every device trusts. Accept its terms to request one.');
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
        if (st.TakeoverPhase === 'Managed' && st.JellyfinTrustsProxy === false) return step('red', 'Not trusted', 'Jellyfin no longer trusts the proxy. Its network settings were changed since.');
        if (st.TakeoverPhase === 'Managed') return step('green', 'Trusted', 'Jellyfin trusts the proxy and sees each visitor\'s real address.');
        if (st.TakeoverPhase === 'PendingRestart') return step('amber', 'Restarting', 'Jellyfin is restarting to trust the proxy.');
        if (st.JellyfinTrustsProxy) return step('green', 'Trusted', 'Jellyfin already trusts the proxy through its own network settings.');
        if (st.TakeoverError) return step('red', 'Failed', 'Jellyfin does not trust the proxy yet, so internet visitors are turned away.');
        return step('amber', 'Not yet', 'Until Jellyfin trusts the proxy, internet visitors are turned away.');
    }

    function assessReach(st) {
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

    function renderStep(key, result, later) {
        var li = Shared.getEl('step' + key);
        li.setAttribute('data-state', result.state);
        li.classList.toggle('ddns-step-later', later);
        Shared.getEl('icon' + key).textContent = ICONS[result.state];
        Shared.getEl('state' + key).textContent = result.word;
        Shared.getEl('text' + key).textContent = result.text;
    }

    function renderSummary(st, results) {
        var box = Shared.getEl('summary');
        var first = -1;
        for (var i = 0; i < results.length; i++) {
            if (results[i].state !== 'green') { first = i; break; }
        }
        if (first < 0) {
            box.className = 'jpk-success';
            box.textContent = st.PublicUrl
                ? 'Fully accessible from outside your network at ' + st.PublicUrl + '.'
                : 'Fully accessible from outside your network.';
            return first;
        }
        var r = results[first];
        box.className = r.state === 'red' ? 'jpk-error' : r.state === 'amber' ? 'jpk-warn' : 'jpk-info';
        box.textContent = 'Step ' + (first + 1) + ', ' + STEPS[first].title + ', ' + (r.state === 'gray' ? 'is not done yet.' : 'needs attention.');
        return first;
    }

    function renderAddressDetails(st, result) {
        var n = st.Name;
        var mismatch = !!n && result.state !== 'green' && !!n.Hostname && !n.Error;
        Shared.setVisible('nameDetails', mismatch);
        if (mismatch) {
            Shared.getEl('nameHostname').textContent = n.Hostname;
            Shared.getEl('nameAddresses').textContent = (n.Addresses || []).length ? n.Addresses.join(', ') : 'Nothing yet';
            Shared.getEl('namePublic').textContent = n.PublicAddress || '';
            Shared.setVisible('namePublicRow', !!n.PublicAddress);
        }
        var done = result.state === 'green';
        Shared.setVisible('addressChangeRow', done && !changingAddress);
        Shared.setVisible('addressPick', !done || changingAddress);
        Shared.getEl('btnChangeAddress').setAttribute('aria-expanded', changingAddress ? 'true' : 'false');
    }

    function renderCertificateActions(st, result) {
        var chosen = (settings && settings.Certificate) || {};
        var mode = (st.Certificate || {}).Mode || chosen.Mode || 'Automatic';
        var terms = mode === 'Automatic' && !chosen.AcceptTerms;
        Shared.setVisible('termsAction', terms);
        Shared.setVisible('certLink', !terms && result.state !== 'green');
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
        Shared.setVisible('trustAction', result.state !== 'green' && st.TakeoverPhase !== 'PendingRestart');
    }

    function renderReachNodes(st) {
        var reach = currentReach(st);
        var nodes = (reach && reach.Nodes) || [];
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
        var first = renderSummary(st, results);
        STEPS.forEach(function (s, i) { renderStep(s.key, results[i], first >= 0 && i > first); });

        Shared.setVisible('powerAction', !st.Enabled);
        renderAddressDetails(st, results[1]);
        renderCertificateActions(st, results[2]);
        renderRouter(st, results[3]);
        renderTrust(st, results[4]);
        renderReachNodes(st);
    }

    // The form controls follow the saved settings only on load and after a save made here, so the poller never
    // undoes a choice that is not saved yet.
    function fill(s) {
        settings = s;
        var names = s.Hostnames || [];
        hostnames.setValue(names.length ? names : hostnames.suggested());
        Shared.getEl('certTerms').checked = !!(s.Certificate || {}).AcceptTerms;
        Shared.getEl('autoPortForwarding').value = autoForwardingOn(s, defaults) ? 'true' : 'false';
    }

    // Reads both, since the address and certificate steps depend on the settings too.
    function refresh() {
        if (document.hidden) return Promise.resolve();
        return Promise.all([getSettings(), getStatus()]).then(function (results) {
            if (results[0]) settings = results[0];
            render(results[1]);
        }).catch(function () { /* the poller tries again */ });
    }

    function load() {
        var defaultsLoaded = Shared.apiRequest('Proxy/Defaults', 'GET').catch(function () { return null; });
        Promise.all([getSettings(), getStatus(), hostnames.load(), defaultsLoaded]).then(function (results) {
            defaults = (results[3] && results[3].Values) || {};
            if (results[0]) fill(results[0]);
            render(results[1]);
        }).catch(function () {
            var box = Shared.getEl('summary');
            box.className = 'jpk-error';
            box.textContent = 'Could not load the proxy status.';
        });
    }

    // Saves one change onto the freshest settings, so changes made on the other tabs are kept.
    function saveWith(statusId, button, change) {
        if (button && button.disabled) return Promise.resolve(false);
        if (button) button.disabled = true;
        Shared.setStatus(statusId, 'Saving...', false);
        return getSettings().then(function (fresh) {
            fresh = fresh || settings;
            change(fresh);
            return Shared.apiRequest('Proxy/Settings', 'POST', fresh);
        }).then(function (result) {
            if (result && result.Settings) fill(result.Settings);
            if (result && result.Status) render(result.Status);
            Shared.setStatus(statusId, 'Saved.', false);
            return true;
        }).catch(function (err) {
            return errorText(err, 'Save failed.').then(function (text) {
                Shared.setStatus(statusId, text, true, { timeout: 0 });
                return false;
            });
        }).then(function (ok) {
            if (button) button.disabled = false;
            refresh();
            return ok;
        });
    }

    function turnOn() {
        saveWith('powerStatus', Shared.getEl('btnTurnOn'), function (s) { s.Enabled = true; });
    }

    function saveAddress() {
        var names = hostnames.getValue();
        if (!names.length) {
            Shared.setStatus('addressStatus', 'Choose or type at least one name.', true);
            return;
        }
        saveWith('addressStatus', Shared.getEl('btnSaveAddress'), function (s) { s.Hostnames = names; }).then(function (ok) {
            if (ok) {
                changingAddress = false;
                if (lastStatus) render(lastStatus);
            }
        });
    }

    function changeAddress() {
        changingAddress = true;
        if (lastStatus) render(lastStatus);
    }

    function saveTerms() {
        if (!Shared.getEl('certTerms').checked) {
            Shared.setStatus('certStatus', 'Tick I accept the Let\'s Encrypt terms first.', true);
            return;
        }
        saveWith('certStatus', Shared.getEl('btnSaveTerms'), function (s) {
            s.Certificate = s.Certificate || {};
            s.Certificate.Mode = s.Certificate.Mode || 'Automatic';
            s.Certificate.AcceptTerms = true;
        });
    }

    function saveAutoForwarding() {
        var on = Shared.getEl('autoPortForwarding').value === 'true';
        saveWith('routerStatus', null, function (s) {
            s.Advanced = s.Advanced || {};
            s.Advanced.AutoPortForwarding = autoForwardingSetting(on, defaults);
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

    function trust() {
        trustTheProxy(Shared, 'trustStatus').then(refresh);
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

    function bind() {
        hostnames = createHostnamePicker(Shared, 'hostnamePicker', 'hostnames');
        Shared.getEl('btnTurnOn').addEventListener('click', turnOn);
        Shared.getEl('btnSaveAddress').addEventListener('click', saveAddress);
        Shared.getEl('btnChangeAddress').addEventListener('click', changeAddress);
        Shared.getEl('btnSaveTerms').addEventListener('click', saveTerms);
        Shared.getEl('autoPortForwarding').addEventListener('change', saveAutoForwarding);
        Shared.getEl('btnRouterRetry').addEventListener('click', retryRouter);
        Shared.getEl('btnTrust').addEventListener('click', trust);
        Shared.getEl('btnReach').addEventListener('click', testReach);
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 1,
        tabs: TABS,
        bind: bind,
        load: function (shared, page) {
            changingAddress = false;
            load();
            page.createPoller(refresh, 10000).start(false);
        }
    }).shared;
}
