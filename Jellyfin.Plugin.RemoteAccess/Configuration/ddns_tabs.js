// What every Dynamic DNS page shares, kept in one place so the pages never disagree.
export const PLUGIN_ID = '8150dce5-6153-4924-a985-a6927be95baa';

// The tab bar.
export const TABS = [
    { href: 'configurationpage?name=ddns_dashboard', name: 'Dashboard' },
    { href: 'configurationpage?name=ddns_domains', name: 'Domains' },
    { href: 'configurationpage?name=ddns_address', name: 'Address' },
    { href: 'configurationpage?name=ddns_remote', name: 'Proxy' },
    { href: 'configurationpage?name=ddns_services', name: 'Services' },
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
