import { confirmDialog } from './configurationpage?name=ddns_jpkribs_shared.js';

// What every Dynamic DNS page shares, kept in one place so the pages never disagree.
export const PLUGIN_ID = '8150dce5-6153-4924-a985-a6927be95baa';

// The tab bar. Each page passes its own position here as its tabIndex.
export const TABS = [
    { href: 'configurationpage?name=ddns_dashboard', name: 'Dashboard' },
    { href: 'configurationpage?name=ddns_setup', name: 'Setup' },
    { href: 'configurationpage?name=ddns_domains', name: 'Domains' },
    { href: 'configurationpage?name=ddns_address', name: 'Address' },
    { href: 'configurationpage?name=ddns_remote', name: 'Proxy' },
    { href: 'configurationpage?name=ddns_advanced', name: 'Advanced' }
];

// Reads the problems out of a failed save, or falls back to a generic message.
export function errorText(err, fallback) {
    if (err && typeof err.json === 'function') {
        return err.json().then(function (body) {
            return body && body.Errors && body.Errors.length ? body.Errors.join(' ') : fallback;
        }).catch(function () { return fallback; });
    }
    return Promise.resolve(fallback);
}

// Puts a message in a box and shows the box, or hides it when there is no message.
export function showText(shared, id, text) {
    var el = shared.getEl(id);
    el.textContent = text || '';
    shared.setVisible(el, !!text);
}

// Trust the Proxy, as the Proxy and Setup tabs both offer it: confirm, run an optional step first such as saving
// the form, then ask Jellyfin to trust the proxy. Resolves to true once Jellyfin answered that it worked.
export function trustTheProxy(shared, statusId, prepare) {
    var message = 'Jellyfin adds this machine to its known proxies, turns on remote access and restarts once. Its ports stay the same. ' +
        'Forward only ports 80 and 443 to this server: anyone who can reach Jellyfin\'s own ports directly gets past the proxy.';
    return confirmDialog(message, { title: 'Trust the Proxy', confirmText: 'Trust' }).then(function (ok) {
        if (!ok) return false;
        return Promise.resolve(prepare ? prepare() : true).then(function (ready) {
            if (!ready) return false;
            shared.setStatus(statusId, 'Checking the proxy can reach Jellyfin...', false, { timeout: 0 });
            return shared.apiRequest('Proxy/TakeOver', 'POST').then(function (result) {
                var worked = !!(result && result.Success);
                shared.setStatus(statusId, (result && result.Message) || 'Done.', !worked, { timeout: 0 });
                return worked;
            }).catch(function () {
                shared.setStatus(statusId, 'Trusting the proxy failed.', true, { timeout: 0 });
                return false;
            });
        });
    });
}

// What to forward on the router by hand, as HTML built from the status.
export function routerInstructionHtml(shared, st) {
    var cert = st.Certificate || {};
    var lan = st.LanAddress || st.DetectedLanAddress || 'this server';
    // The internet always uses 80 and 443. Only a changed proxy port needs the router to translate.
    var target = '<strong>' + shared.escapeHtml(lan) + '</strong>';
    var external = st.HttpPort === 80 && st.HttpsPort === 443
        ? 'Forward TCP ports <strong>80</strong> and <strong>443</strong> to ' + target + '.'
        : 'Forward TCP port <strong>443</strong> to ' + target + ' port <strong>' + st.HttpsPort + '</strong>, and port <strong>80</strong> to port <strong>' + st.HttpPort + '</strong>.';
    var test = st.PublicUrl
        ? '<br>Then open <span class="jpk-mono">' + shared.escapeHtml(st.PublicUrl) + '</span> on a phone with Wi-Fi turned off.'
        : '';
    // Port 80 sends http:// links on to HTTPS, and is where Let's Encrypt checks the address.
    var httpCheck = cert.Mode === 'Automatic' && (st.Acme || {}).Method !== 'Dns';
    var port80 = '<br>Port 80 sends http:// links on to HTTPS' + (httpCheck ? ' and lets Let\'s Encrypt check your address.' : '.');
    return external + port80 + test;
}

// Whether the proxy opens the router's ports itself. Unset follows the default, which is on.
export function autoForwardingOn(settings, defaults) {
    var v = ((settings && settings.Advanced) || {}).AutoPortForwarding;
    if (v === true || v === false) return v;
    var d = defaults ? defaults.AutoPortForwarding : null;
    return d === false ? false : true;
}

// The value to store for a choice: unset when it matches the default, so it follows the default if that changes.
export function autoForwardingSetting(on, defaults) {
    var d = defaults && defaults.AutoPortForwarding === false ? false : true;
    return on === d ? null : on;
}

// What the router's automatic forwarding is doing, in a sentence or two. Empty when the server does not say.
export function routerStateText(router) {
    if (!router) return '';
    if (router.BehindCgnat) return 'Your internet provider shares one public address between many homes, so the internet cannot reach this server. ' + (router.CgnatReason || '');
    if (router.Mapped) {
        var ports = (router.Ports || []).join(' and ');
        return 'The router forwards ' + (ports ? 'ports ' + ports : 'the ports') + ' to this server' + (router.Method ? ' through ' + router.Method : '') + '.';
    }
    if (!router.Enabled) return 'Automatic port forwarding is off.';
    return 'The router did not open the ports automatically.';
}
