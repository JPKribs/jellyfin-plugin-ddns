import { confirmDialog, SECRET_KEPT } from './configurationpage?name=ddns_jpkribs_shared.js';

// What every Dynamic DNS page shares, kept in one place so the pages never disagree.
export const PLUGIN_ID = '8150dce5-6153-4924-a985-a6927be95baa';

// The tab bar. Each page passes its own position here as its tabIndex.
export const TABS = [
    { href: 'configurationpage?name=ddns_dashboard', name: 'Dashboard' },
    { href: 'configurationpage?name=ddns_configuration', name: 'Configuration' },
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

// Trust the Proxy, as step 5 of the Configuration tab offers it: confirm, run an optional step first such as saving
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

// ===== The DDNS record =====

// A server keeps one DDNS record. When an older setup saved several, the one shown and kept is the first enabled
// record, else the first. The rest are dropped on the next save.
export function primaryRecord(records) {
    var list = records || [];
    return list.find(function (r) { return r && r.Enabled !== false; }) || list[0] || null;
}

// Every field a provider can use, for a provider this page does not know.
var DEFAULT_FIELDS = { h: 'Hostname', l: 'Login', p: 'Password', z: 'Zone', s: true, t: true, v6: true, a: ['wildcard', 'static', 'mx', 'backupmx'] };

// The provider list, hints and the fields each provider uses, from GET DynamicDns/Providers, so each provider's
// fields are defined once, in its C# class. Resolves to { options, hints, specFor, normalize }.
export function loadProviders(shared) {
    return shared.apiRequest('Providers', 'GET').then(function (list) {
        list = list || [];
        var values = list.map(function (p) { return p.value; });
        var hints = {};
        var fields = {};
        list.forEach(function (p) {
            hints[p.value] = p.hint || '';
            var f = p.fields || {};
            fields[p.value] = {
                h: f.Hostname || null, l: f.Login || null, p: f.Password || null,
                z: f.Zone || null, s: !!f.Server, t: !!f.Ttl, v6: f.IPv6 !== false, a: f.Advanced || []
            };
        });
        return {
            options: list.map(function (p) { return [p.value, p.label]; }),
            hints: hints,
            specFor: function (provider) { return fields[provider] || DEFAULT_FIELDS; },
            normalize: function (value) {
                if (typeof value === 'number') return values[value] || 'Cloudflare';
                return values.indexOf(value) >= 0 ? value : 'Cloudflare';
            }
        };
    });
}

// The DDNS endpoints keep their own credential markers. A bound secret field reads the shared kept marker while
// untouched and empty once cleared, so both are turned into the markers the DDNS endpoints know.
var DDNS_KEPT = '__JPK_DDNS_SECRET_KEPT__';
var DDNS_CLEAR = '__JPK_DDNS_SECRET_CLEAR__';

export function readDdnsSecret(shared, id) {
    var el = shared.getEl(id);
    var value = shared.readSecretField(id);
    if (value === SECRET_KEPT) return DDNS_KEPT;
    if (!value && el.dataset.jpkSecretCleared) return DDNS_CLEAR;
    return value;
}

// ===== The proxy's addresses =====

// The proxy answers on the names the DDNS record covers followed by any other addresses, so Configuration and
// Advanced build the list the same way and never undo each other.

function cleanName(name) {
    return String(name || '').trim().toLowerCase().replace(/\.$/, '');
}

// Addresses typed one per line, cleaned the way the server stores them.
export function addressLines(text) {
    return String(text || '').split(/\r?\n/).map(cleanName).filter(function (x) { return x; });
}

// The names the record covers, its own name and the wildcard under it when it updates one, as the server works
// them out. With no record id, the names of every record.
export function recordNames(shared, recordId) {
    return shared.apiRequest('Proxy/DdnsHostnames', 'GET').then(function (items) {
        return (items || []).filter(function (h) { return !recordId || h.RecordId === recordId; })
            .map(function (h) { return cleanName(h.Hostname); });
    });
}

// The stored addresses that are not the record's names.
export function otherAddresses(hostnames, names) {
    return (hostnames || []).map(cleanName).filter(function (n) { return n && names.indexOf(n) < 0; });
}

// The record's names followed by the other addresses, without duplicates.
export function proxyHostnames(names, others) {
    return (names || []).concat(others || []).map(cleanName)
        .filter(function (n, i, all) { return n && all.indexOf(n) === i; });
}

// Saves the proxy settings with its addresses: the names the saved record covers now, followed by the other
// addresses. Without others given, they are the stored addresses that were not the record's names before the
// change. A mutate callback may change the rest of the freshly read settings first. Resolves to the server's reply.
export function saveProxyHostnames(shared, namesBefore, others, mutate) {
    return Promise.all([recordNames(shared), shared.apiRequest('Proxy/Settings', 'GET')]).then(function (results) {
        var fresh = results[1];
        if (!fresh) { throw new Error('settings unavailable'); }
        var kept = others || otherAddresses(fresh.Hostnames, namesBefore || []);
        if (mutate) fresh = mutate(fresh) || fresh;
        fresh.Hostnames = proxyHostnames(results[0], kept);
        return shared.apiRequest('Proxy/Settings', 'POST', fresh);
    });
}
