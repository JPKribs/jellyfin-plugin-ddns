export default function (view) {
    'use strict';

    var PLUGIN_ID = '8150dce5-6153-4924-a985-a6927be95baa';
    var TABS = [
        { href: 'configurationpage?name=ddns_domains', name: 'Domains' },
        { href: 'configurationpage?name=ddns_address', name: 'Address' }
    ];

    var Shared = null;
    var setTabs = null;
    var initCollapsibles = null;
    var _sharedPromise = import('/web/configurationpage?name=ddns_jpkribs_shared.js').then(function (mod) {
        Shared = mod.createShared(view, PLUGIN_ID, 'DynamicDns');
        setTabs = mod.setTabs;
        initCollapsibles = mod.initCollapsibles;
    });

    // p[0] is the C# enum name and p[1] is the friendly label.
    var PROVIDERS = [
        ['Hosting1984', '1984 Hosting'], ['ChangeIp', 'ChangeIP'], ['Cloudflare', 'Cloudflare'],
        ['CloudNs', 'ClouDNS'], ['DdnsFm', 'DDNS.fm'], ['Ddnss', 'DDNSS.de'], ['DigitalOcean', 'DigitalOcean'],
        ['Dinahosting', 'Dinahosting'], ['Directnic', 'Directnic'], ['DnsExit2', 'DNSExit'],
        ['DnsMadeEasy', 'DNS Made Easy'], ['Domeneshop', 'Domeneshop'], ['DonDominio', 'DonDominio'],
        ['DslReports1', 'DSLReports'], ['DuckDns', 'DuckDNS'], ['Dynadot', 'Dynadot'],
        ['DynDns1', 'DynDNS v1 legacy'], ['DynDns2', 'DynDNS v2 generic'], ['Dynu', 'Dynu'],
        ['EasyDns', 'easyDNS'], ['Enom', 'eNom'],
        ['FreeDns', 'FreeDNS afraid.org'], ['FreeMyIp', 'freemyip.com'], ['Gandi', 'Gandi LiveDNS'],
        ['GoDaddy', 'GoDaddy'], ['HeNet', 'Hurricane Electric'], ['Hetzner', 'Hetzner DNS'],
        ['Infomaniak', 'Infomaniak'], ['Inwx', 'INWX'], ['Ionos', 'IONOS'], ['KeySystems', 'Key Systems'],
        ['MythicDyn', 'Mythic Beasts'], ['Namecheap', 'Namecheap'], ['Nfsn', 'NearlyFreeSpeech.NET'],
        ['Njalla', 'Njalla'], ['NoIp', 'No IP'], ['Ns1', 'NS1'],
        ['Ovh', 'OVH'], ['Porkbun', 'Porkbun'], ['RegfishDe', 'regfish.de'], ['SimplyCom', 'Simply.com'],
        ['Sitelutions', 'Sitelutions'], ['Spaceship', 'Spaceship'], ['Yandex', 'Yandex'], ['ZoneEdit1', 'ZoneEdit']
    ];
    var PROVIDER_VALUES = PROVIDERS.map(function (p) { return p[0]; });

    var HINTS = {
        Hosting1984: 'Password is your 1984 API key. Hostname is the domain. Login and Zone are unused.',
        ChangeIp: 'Login and Password are your ChangeIP account credentials.',
        Cloudflare: 'Set Login to the word token for a scoped API token, or to your account email for a global key. Password is the token or global API key. Zone is the zone name such as example.com.',
        CloudNs: 'Password is the full ClouDNS DynURL, which is the secret update URL. Hostname, Login and Zone are not used.',
        DdnsFm: 'Hostname is your domain. Password is your DDNS.fm key.',
        Ddnss: 'Hostname is your host. Password is your DDNSS.de key.',
        DigitalOcean: 'Password is a DigitalOcean API token. Zone is the apex domain. Hostname is the record name.',
        Dinahosting: 'Login and Password are your Dinahosting credentials. Hostname is host.domain.',
        Directnic: 'Login is your IPv4 gateway URL and Password is your IPv6 gateway URL. Paste the full per record gateway URLs.',
        DnsExit2: 'Password is your DNSExit API key. Zone is the domain and defaults to the hostname. Hostname is the record.',
        DnsMadeEasy: 'Login is your account email. Password is the dynamic DNS record password. Hostname is the numeric record ID.',
        Domeneshop: 'Login and Password are your Domeneshop API token and secret. Hostname is the host.',
        DonDominio: 'Login is the user. Password is your DonDominio API key. Hostname is the host.',
        DslReports1: 'Login and Password are your DSLReports credentials.',
        DuckDns: 'Hostname is your DuckDNS labels. Password is your account token. Login and Zone are unused.',
        Dynadot: 'Password is your DDNS password. Zone is optional and splits the hostname into domain and subdomain.',
        DynDns1: 'Legacy DynDNS v1. Login and Password are your credentials.',
        DynDns2: 'Login and Password are your account credentials. Server defaults to members.dyndns.org. Override it for a compatible service.',
        Dynu: 'Login and Password are your Dynu credentials. Set Zone for a custom domain to split the hostname into zone and alias.',
        EasyDns: 'Login and Password are your easyDNS API credentials.',
        Enom: 'Login is the base domain. Password is the domain password. Hostname is the host.',
        FreeDns: 'Login and Password are your afraid.org credentials, used to derive the update token.',
        FreeMyIp: 'Hostname is your domain. Password is the freemyip token.',
        Gandi: 'Password is a Gandi API key. Or set Login to the word token with a personal access token. Zone is the domain.',
        GoDaddy: 'Login is the API key. Password is the API secret. Zone is the domain.',
        HeNet: 'Hostname is the record. Password is the per record DDNS key. Login is unused.',
        Hetzner: 'Password is a Hetzner API token. Zone is the DNS zone.',
        Infomaniak: 'Login and Password are your Infomaniak credentials.',
        Inwx: 'Login and Password are your INWX DynDNS account. The host is derived from the account.',
        Ionos: 'Password is your IONOS API key in the form prefix.secret. Login is unused.',
        KeySystems: 'Hostname is the host. Password is your Key Systems password.',
        MythicDyn: 'Login and Password are your Mythic Beasts dynamic DNS credentials.',
        Namecheap: 'Login is the domain. Password is the domain DDNS password. Hostname is host.domain.',
        Nfsn: 'Login is the member login. Password is the API key. Zone is the DNS zone.',
        Njalla: 'Hostname is the host. Password is your Njalla dynamic DNS key.',
        NoIp: 'Login and Password are your No IP credentials. A DDNS key is recommended over your account password.',
        Ns1: 'Login is your NS1 API key. Zone is optional and is inferred from the hostname if blank.',
        Ovh: 'Login and Password are your OVH DynHost credentials.',
        Porkbun: 'Login is the apikey. Password is the secretapikey. Zone is an optional root domain.',
        RegfishDe: 'Password is your regfish update token.',
        SimplyCom: 'Login and Password are your Simply.com credentials. Zone is optional.',
        Sitelutions: 'Hostname is the numeric record ID. Login is the account email. Password is the account password.',
        Spaceship: 'Login is the API key. Password is the API secret. Zone is the domain.',
        Yandex: 'Login is the domain. Password is your PDD token. Hostname is the FQDN.',
        ZoneEdit1: 'Login and Password are your ZoneEdit username and dynamic DNS token. Zone is optional.'
    };

    // Per provider field spec. h hostname, l login, p password, z zone, s server, t ttl, a advanced flags.
    // A missing key hides that field. Labels and visibility come from each provider's UpdateAsync.
    var FIELDS = {
        Hosting1984: { h: 'Domain', p: 'API key', s: true },
        ChangeIp: { h: 'Hostname', l: 'Username', p: 'Password', s: true },
        Cloudflare: { h: 'Record name', l: 'Auth email or token', p: 'API token or key', z: 'Zone name', s: true },
        CloudNs: { p: 'Update URL' },
        DdnsFm: { h: 'Domain', p: 'Key', s: true },
        Ddnss: { h: 'Host', p: 'Key', s: true },
        DigitalOcean: { h: 'Record name', p: 'API token', z: 'Apex domain', s: true },
        Dinahosting: { h: 'Host and domain', l: 'Username', p: 'Password', s: true },
        Directnic: { l: 'IPv4 gateway URL', p: 'IPv6 gateway URL', s: true },
        DnsExit2: { h: 'Record name', p: 'API key', z: 'Domain', t: true, s: true },
        DnsMadeEasy: { h: 'Record ID', l: 'Account email', p: 'Record password', s: true },
        Domeneshop: { h: 'Host', l: 'API token', p: 'API secret', s: true },
        DonDominio: { h: 'Host', l: 'User', p: 'API key', s: true },
        DslReports1: { h: 'Host', l: 'Username', p: 'Password', s: true, a: ['static'] },
        DuckDns: { h: 'Labels', p: 'Token', s: true },
        DynDns1: { h: 'Hostname', l: 'Username', p: 'Password', s: true, a: ['wildcard', 'static', 'mx', 'backupmx'] },
        DynDns2: { h: 'Hostname', l: 'Username', p: 'Password', s: true, a: ['wildcard', 'mx', 'backupmx'] },
        Dynadot: { h: 'Hostname', p: 'DDNS password', z: 'Domain', t: true, s: true },
        Dynu: { h: 'Hostname', l: 'Username', p: 'Password', z: 'Custom domain', s: true },
        EasyDns: { h: 'Hostname', l: 'Username', p: 'API token', s: true, a: ['wildcard', 'mx', 'backupmx'] },
        Enom: { h: 'Host', l: 'Base domain', p: 'Domain password', s: true },
        FreeDns: { h: 'Hostname', l: 'Username', p: 'Password', s: true },
        FreeMyIp: { h: 'Domain', p: 'Token', s: true },
        Gandi: { h: 'Record name', l: 'Auth keyword', p: 'API key or token', z: 'Domain', t: true, s: true },
        GoDaddy: { h: 'Record name', l: 'API key', p: 'API secret', z: 'Domain', t: true, s: true },
        HeNet: { h: 'Record', p: 'DDNS key', s: true },
        Hetzner: { h: 'Record name', p: 'API token', z: 'DNS zone', t: true, s: true },
        Infomaniak: { h: 'Hostname', l: 'Username', p: 'Password', s: true },
        Inwx: { l: 'Username', p: 'Password', s: true },
        Ionos: { h: 'Hostname', p: 'API key', t: true, s: true },
        KeySystems: { h: 'Host', p: 'Password', s: true },
        MythicDyn: { h: 'Hostname', l: 'Username', p: 'Password', s: true },
        Namecheap: { h: 'Host and domain', l: 'Domain', p: 'DDNS password', s: true },
        Nfsn: { h: 'Hostname', l: 'Member login', p: 'API key', z: 'DNS zone', t: true, s: true },
        Njalla: { h: 'Host', p: 'DDNS key', s: true },
        NoIp: { h: 'Hostname', l: 'Username', p: 'Password or DDNS key', s: true },
        Ns1: { h: 'Record name', l: 'API key', z: 'Zone', t: true, s: true },
        Ovh: { h: 'Hostname', l: 'Username', p: 'Password', s: true },
        Porkbun: { h: 'Hostname', l: 'API key', p: 'Secret API key', z: 'Root domain', s: true },
        RegfishDe: { h: 'Hostname', p: 'Update token', s: true },
        SimplyCom: { h: 'Hostname', l: 'Account name', p: 'API key', z: 'Zone', s: true },
        Sitelutions: { h: 'Record ID', l: 'Account email', p: 'Account password', t: true, s: true },
        Spaceship: { h: 'Hostname', l: 'API key', p: 'API secret', z: 'Domain', t: true, s: true },
        Yandex: { h: 'FQDN', l: 'Domain', p: 'PDD token', s: true },
        ZoneEdit1: { h: 'Hostname', l: 'Username', p: 'DDNS token', z: 'Zone', s: true }
    };

    // Fallback spec showing every field for an unrecognized provider.
    var DEFAULT_FIELDS = { h: 'Hostname', l: 'Login', p: 'Password', z: 'Zone', s: true, t: true, a: ['wildcard', 'static', 'mx', 'backupmx'] };

    var config = null;
    var records = [];
    var currentIndex = -1;
    var editorEnabled = true;
    var _bound = false;

    function el(id) { return view.querySelector('#' + id); }

    function normalizeProvider(value) {
        if (typeof value === 'number') return PROVIDER_VALUES[value] || 'Cloudflare';
        return PROVIDER_VALUES.indexOf(value) >= 0 ? value : 'Cloudflare';
    }

    function newRecord() {
        return {
            Id: '', Name: '', Provider: 'Cloudflare', Hostname: '', Enabled: true,
            UpdateIPv4: true, UpdateIPv6: true,
            Login: '', Password: '', Zone: '', Server: '', Ttl: 1,
            Proxied: false, Wildcard: false, Mx: '', BackupMx: false, Static: false,
            LastIPv4: '', LastIPv6: '', LastStatus: '', LastSuccess: false, LastCheckedUtc: null
        };
    }

    function renderProviderOptions() {
        el('recProvider').innerHTML = PROVIDERS.map(function (p) {
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
        el('recordHealth').innerHTML =
            '<div class="jpk-card green"><span class="jpk-card-count">' + counts.healthy + '</span><span class="jpk-card-label">Healthy</span></div>' +
            '<div class="jpk-card red" title="Failed to update within the last 24 hours"><span class="jpk-card-count">' + counts.unhealthy + '</span><span class="jpk-card-label">Unhealthy</span></div>' +
            '<div class="jpk-card gray"><span class="jpk-card-count">' + counts.unpublished + '</span><span class="jpk-card-label">Unpublished</span></div>';
    }

    function renderSelect() {
        var select = el('selectRecord');
        select.innerHTML = records.map(function (r, i) {
            var label = (r.Name || r.Hostname || 'new record') + (r.Enabled === false ? ', disabled' : '');
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
        }
    }

    function setEnableVisual(enabled) {
        var btn = el('btnEnable');
        btn.classList.toggle('jpk-button-submit', enabled);
        btn.classList.toggle('jpk-secondary', !enabled);
        el('enableLabel').textContent = enabled ? 'Enabled' : 'Disabled';
        btn.querySelector('.material-icons').textContent = enabled ? 'play_arrow' : 'pause';
    }

    function applyHint() {
        el('providerHint').textContent = HINTS[el('recProvider').value] || 'Fill the fields required by this provider.';
    }

    function setField(id, label) {
        var container = el(id).closest('.inputContainer');
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
        var container = el(id).closest('.checkboxContainer');
        if (container) container.classList.toggle('hidden', !visible);
    }

    // Credential inputs are write-only: clear the field and, when a secret is already stored, show a
    // placeholder so the admin knows it is set without the value ever being sent to the browser DOM.
    function setSecretField(id, stored) {
        var input = el(id);
        input.value = '';
        input.placeholder = stored ? '••••••••' : '';
    }

    function applyFields(provider) {
        var spec = FIELDS[provider] || DEFAULT_FIELDS;
        var adv = spec.a || [];
        setField('recHostname', spec.h);
        setField('recLogin', spec.l);
        setField('recPassword', spec.p);
        setField('recZone', spec.z);
        setField('recServer', spec.s === true ? 'Server override' : (spec.s || null));
        setField('recTtl', spec.t ? 'TTL in seconds. 1 means automatic when supported' : null);
        setCheck('recProxied', adv.indexOf('proxied') >= 0);
        setCheck('recWildcard', adv.indexOf('wildcard') >= 0);
        setCheck('recStatic', adv.indexOf('static') >= 0);
        setField('recMx', adv.indexOf('mx') >= 0 ? 'MX host for the dyndns family' : null);
        setCheck('recBackupMx', adv.indexOf('backupmx') >= 0);
        var section = el('advancedContent').closest('.jpk-collapsible-section');
        if (section) section.classList.toggle('hidden', adv.length === 0);
    }

    function onProviderChange() {
        applyHint();
        applyFields(el('recProvider').value);
    }

    function loadEditor() {
        var r = records[currentIndex];
        if (!r) return;

        el('recName').value = r.Name || '';
        el('recProvider').value = normalizeProvider(r.Provider);
        el('recHostname').value = r.Hostname || '';
        // Credentials are write-only: never bind the stored secret back into the form. Show a "saved"
        // placeholder when one exists; the value is kept unless the admin types a replacement.
        setSecretField('recLogin', r.Login);
        setSecretField('recPassword', r.Password);
        el('recZone').value = r.Zone || '';
        el('recServer').value = r.Server || '';
        el('recTtl').value = r.Ttl || 1;
        el('recProxied').checked = !!r.Proxied;
        el('recWildcard').checked = !!r.Wildcard;
        el('recStatic').checked = !!r.Static;
        el('recMx').value = r.Mx || '';
        el('recBackupMx').checked = !!r.BackupMx;

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
        var action = el('stAction');
        var act = r.LastAction || '';
        action.textContent = act || '—';
        var actClass = 'jpk-mono';
        if (act === 'Updated') actClass += ' jpk-ok';
        else if (act === 'Failed' || act === 'No address') actClass += ' jpk-bad';
        action.className = actClass; // 'Unchanged' (and legacy empty) stay neutral

        var last = el('stLast');
        last.textContent = r.LastStatus || (r.LastSuccess ? 'OK' : 'Unknown');
        last.className = 'jpk-mono ' + (r.LastSuccess ? 'jpk-ok' : 'jpk-bad');

        var checked = new Date(r.LastCheckedUtc);
        el('stChecked').textContent = isNaN(checked.getTime()) ? String(r.LastCheckedUtc) : checked.toLocaleString();
        el('stIps').textContent = (r.LastIPv4 || 'none') + ' / ' + (r.LastIPv6 || 'none');
    }

    function readEditorInto(r) {
        r.Name = el('recName').value.trim();
        r.Provider = el('recProvider').value;
        r.Hostname = el('recHostname').value.trim();
        r.Enabled = editorEnabled;
        // Only overwrite a credential when the admin typed a replacement; a blank field keeps the stored
        // (encrypted) value rather than wiping it.
        var login = el('recLogin').value.trim();
        if (login) r.Login = login;
        var password = el('recPassword').value;
        if (password) r.Password = password;
        r.Zone = el('recZone').value.trim();
        r.Server = el('recServer').value.trim();
        r.Ttl = parseInt(el('recTtl').value, 10) || 1;
        r.Proxied = el('recProxied').checked;
        r.Wildcard = el('recWildcard').checked;
        r.Static = el('recStatic').checked;
        r.Mx = el('recMx').value.trim();
        r.BackupMx = el('recBackupMx').checked;
    }

    function persistRecords(okMessage, errMessage) {
        return Shared.getConfig().then(function (fresh) {
            fresh.Records = records;
            config = fresh;
            return Shared.saveConfig(fresh);
        }).then(function () {
            renderSelect();
            Shared.setStatus('recordStatus', okMessage, false);
        }).catch(function () {
            Shared.setStatus('recordStatus', errMessage, true);
        });
    }

    function saveRecord() {
        var r = records[currentIndex];
        if (!r) return;
        readEditorInto(r);
        if (!r.Hostname) {
            Shared.setStatus('recordStatus', 'A hostname is required.', true);
            return;
        }

        persistRecords('Saved.', 'Save failed.');
    }

    function addRecord() {
        records.push(newRecord());
        currentIndex = records.length - 1;
        renderSelect();
        el('recName').focus();
    }

    function deleteRecord() {
        var r = records[currentIndex];
        if (!r) return;
        var label = r.Name || r.Hostname || 'this record';
        if (!window.confirm('Delete ' + label + '? This cannot be undone.')) return;

        records.splice(currentIndex, 1);
        currentIndex = -1;
        persistRecords('Deleted.', 'Delete failed.');
    }

    function bind() {
        el('selectRecord').addEventListener('change', function () {
            currentIndex = parseInt(this.value, 10);
            loadEditor();
        });
        el('recProvider').addEventListener('change', onProviderChange);
        el('btnNewRecord').addEventListener('click', addRecord);
        el('btnDeleteRecord').addEventListener('click', deleteRecord);
        el('btnSaveRecord').addEventListener('click', saveRecord);
        el('btnEnable').addEventListener('click', function () {
            editorEnabled = !editorEnabled;
            setEnableVisual(editorEnabled);
        });
        initCollapsibles(view);
    }

    function load() {
        Shared.getConfig().then(function (cfg) {
            config = cfg;
            records = config.Records || [];
            renderProviderOptions();
            currentIndex = records.length ? 0 : -1;
            renderSelect();
        });
    }

    view.addEventListener('viewshow', function () {
        _sharedPromise.then(function () {
            setTabs('ddns', 0, TABS);
            if (!_bound) { bind(); _bound = true; }
            load();
        });
    });
}
