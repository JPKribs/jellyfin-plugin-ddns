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

    // Provider list, hints, and field specs come from the backend (GET DynamicDns/Providers), so each
    // provider drives its own UI from its C# class. p[0] is the enum name and p[1] is the friendly label.
    var PROVIDERS = [];
    var PROVIDER_VALUES = [];
    var HINTS = {};
    var FIELDS = {};

    // Fallback spec showing every field for an unrecognized provider.
    var DEFAULT_FIELDS = { h: 'Hostname', l: 'Login', p: 'Password', z: 'Zone', s: true, t: true, a: ['wildcard', 'static', 'mx', 'backupmx'] };

    var config = null;
    var records = [];
    var currentIndex = -1;
    var editorEnabled = true;
    var _bound = false;

    function el(id) { return view.querySelector('#' + id); }

    // The plugin's own config endpoints redact stored credentials to a write-only sentinel on read and
    // encrypt freshly typed ones on save, so secrets never round-trip through the browser in the clear.
    function getConfig() { return Shared.apiRequest('Configuration', 'GET'); }
    function saveConfig(cfg) { return Shared.apiRequest('Configuration', 'POST', cfg); }

    // Pull the provider list, hints, and field specs from the backend once, so each provider's UI is
    // defined in one place: its C# class. The field keys map the server shape onto the short keys used here.
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
                    z: f.Zone || null, s: !!f.Server, t: !!f.Ttl, a: f.Advanced || []
                };
            });
            _providersLoaded = true;
        });
    }

    function normalizeProvider(value) {
        if (typeof value === 'number') return PROVIDER_VALUES[value] || 'Cloudflare';
        return PROVIDER_VALUES.indexOf(value) >= 0 ? value : 'Cloudflare';
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

    // Provider hint plus a link to that provider's setup doc on GitHub, named after the provider value.
    function applyHint() {
        var prov = el('recProvider').value;
        var hint = HINTS[prov] || 'Fill the fields required by this provider.';
        var html = Shared.escapeHtml(hint);
        if (prov) {
            var url = 'https://github.com/JPKribs/jellyfin-plugin-ddns/blob/master/docs/providers/' + encodeURIComponent(prov) + '.md';
            html += ' <a href="' + url + '" target="_blank" rel="noopener">Setup guide</a>';
        }
        el('providerHint').innerHTML = html;
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
        el('recUpdateIPv4').checked = r.UpdateIPv4 !== false;
        el('recUpdateIPv6').checked = !!r.UpdateIPv6;
        // Credentials are write-only: never bind the stored secret back into the form. Show a "saved"
        // placeholder when one exists. The value is kept unless the admin types a replacement.
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
        r.UpdateIPv4 = el('recUpdateIPv4').checked;
        r.UpdateIPv6 = el('recUpdateIPv6').checked;
        r.Enabled = editorEnabled;
        // Only overwrite a credential when the admin typed a replacement. A blank field keeps the stored
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
        return getConfig().then(function (fresh) {
            if (!fresh) { throw new Error('settings unavailable'); }
            fresh.Records = records;
            config = fresh;
            return saveConfig(fresh);
        }).then(function (saved) {
            renderSelect();
            Shared.setStatus('recordStatus', saved ? okMessage : errMessage, !saved);
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
        loadProviders().then(getConfig).then(function (cfg) {
            config = cfg;
            records = config.Records || [];
            renderProviderOptions();
            currentIndex = records.length ? 0 : -1;
            renderSelect();
        }).catch(function () {
            Shared.setStatus('recordStatus', 'Could not load providers or settings.', true);
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
