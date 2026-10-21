// The tab bar shared by every Dynamic DNS page, kept in one place so the pages never disagree.
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
