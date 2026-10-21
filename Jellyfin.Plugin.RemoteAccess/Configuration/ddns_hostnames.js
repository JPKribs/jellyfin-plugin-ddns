import { createCheckboxList } from './configurationpage?name=ddns_jpkribs_shared.js';

// A hostname picker shared by the Proxy and Services tabs: a checklist of the names the DDNS records keep
// pointed at this server, plus a box for any other names. Both save into the same hostname list, so a
// name checked here and a name typed below are treated alike by the proxy.
export function createHostnamePicker(shared, containerId, customId) {
    'use strict';

    var choices = [];
    var list = createCheckboxList({
        search: false,
        idKey: 'Hostname',
        fetchItems: function () { return choices; },
        emptyMessage: 'No DDNS records yet. Add one on the Domains tab, or type hostnames below.',
        renderRow: function (h) {
            return { label: h.Hostname, sublabel: h.Enabled ? 'From ' + h.Record : 'From ' + h.Record + ', which is disabled' };
        }
    });
    shared.getEl(containerId).appendChild(list.element);

    function known(name) {
        return choices.some(function (c) { return c.Hostname === name; });
    }

    function customLines() {
        return shared.getEl(customId).value.split(/\r?\n/)
            .map(function (x) { return x.trim().toLowerCase().replace(/\.$/, ''); })
            .filter(function (x) { return x; });
    }

    return {
        // Loads the DDNS choices. Call before setValue so stored names split correctly.
        load: function () {
            return shared.apiRequest('Proxy/DdnsHostnames', 'GET').then(function (items) {
                choices = items || [];
                return list.refresh ? list.refresh() : null;
            }).catch(function () {
                choices = [];
            });
        },

        // Ticks stored names that match a DDNS record and puts the rest in the custom box.
        setValue: function (hostnames) {
            var names = (hostnames || []).map(function (x) { return String(x).toLowerCase(); });
            list.setValue(names.filter(known));
            shared.getEl(customId).value = names.filter(function (n) { return !known(n); }).join('\n');
        },

        // The names the enabled DDNS records keep pointed at this server, the sensible first choice.
        suggested: function () {
            return choices.filter(function (c) { return c.Enabled; }).map(function (c) { return c.Hostname; });
        },

        // The ticked DDNS names followed by the custom ones, without duplicates.
        getValue: function () {
            var picked = list.getValue();
            return picked.concat(customLines().filter(function (n) { return picked.indexOf(n) < 0; }))
                .filter(function (n, i, all) { return all.indexOf(n) === i; });
        }
    };
}
