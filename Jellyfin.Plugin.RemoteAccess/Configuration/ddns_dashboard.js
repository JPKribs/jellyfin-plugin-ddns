import { createConfigPage, confirmDialog } from './configurationpage?name=ddns_jpkribs_shared.js';
import { WORLD, WORLD_VIEWBOX } from './configurationpage?name=ddns_worldmap.js';
import { TABS, errorText } from './configurationpage?name=ddns_tabs.js';

export default function (view) {
    'use strict';

    var PLUGIN_ID = '8150dce5-6153-4924-a985-a6927be95baa';
    var Shared = null;
    var data = null;
    // Which kinds the map and its table show. Any mix can be picked; all three is everything.
    var mapShown = { people: true, failed: true, blocked: true };
    // The Activity table's own filters, kept across the 30-second refresh.
    var filterType = '';
    var filterCountry = '';
    var MAP_COLORS = { people: '0, 164, 220', failed: '224, 93, 93', blocked: '155, 89, 182' };
    var TYPES = {
        User: { color: 'blue' },
        Failure: { color: 'red' },
        Blocked: { color: 'purple' },
        Seen: { color: 'gray' }
    };

    function esc(text) { return Shared.escapeHtml(text == null ? '' : String(text)); }

    // The short form, for tables where width is tight.
    function agoShort(value) {
        var t = new Date(value).getTime();
        if (isNaN(t)) return '';
        var s = Math.max(0, (Date.now() - t) / 1000);
        if (s < 120) return 'now';
        if (s < 3600) return Math.round(s / 60) + ' min ago';
        if (s < 86400) return Math.round(s / 3600) + ' hr ago';
        if (s < 86400 * 14) return Math.round(s / 86400) + ' days ago';
        return new Date(t).toLocaleDateString();
    }

    function place(item) {
        if (item.IsLocal) return 'Local';
        return item.Country || 'Unknown';
    }

    // Cities are looked up only when Location is set to City; then the table gains a column for them.
    function showsArea() {
        return !!(data && data.Settings && data.Settings.Location === 'City');
    }

    // The address without its scheme, which every row would repeat.
    function shortHost(host) {
        return (host || '').replace(/^https?:\/\//, '');
    }

    function button(label, cls, attrs, title) {
        return '<button is="emby-button" type="button" class="raised jpk-button-small ' + cls + '" ' + attrs +
            (title ? ' title="' + esc(title) + '"' : '') + '><span>' + label + '</span></button>';
    }

    // Every row has one button. A banned address can be unbanned; your network is never blocked, so its button
    // is off; a blocked address unblocks itself; an address its country's rule refuses unblocks the country;
    // anything else can be blocked.
    function action(r) {
        if (r.Banned) {
            return button('Unban', 'jpk-secondary', 'data-unban="' + esc(r.Ip) + '"');
        }

        if (r.Exempt) {
            return button('Block', 'jpk-button-destructive', 'disabled', 'Your network and trusted networks are never blocked.');
        }

        if (r.Listed) {
            return button('Unblock', 'jpk-secondary', 'data-unblock="' + esc(r.Ip) + '"');
        }

        // Blocked only by a range: unblocking one address would not help, so say where it is changed.
        if (r.Covered) {
            return button('Unblock', 'jpk-secondary', 'disabled', 'Blocked by a range under Advanced.');
        }

        if (r.CountryBlocked) {
            return button('Unblock', 'jpk-secondary', 'data-unblock-country="' + esc(r.CountryCode) + '" data-country="' + esc(r.Country) + '"', 'Allow connections from ' + r.Country);
        }

        return button('Block', 'jpk-button-destructive', 'data-block="' + esc(r.Ip) + '"');
    }

    // When a ban ends, in the same short form as the tables.
    function untilText(value) {
        if (!value) return 'Until lifted';
        var minutes = Math.max(0, (new Date(value).getTime() - Date.now()) / 60000);
        if (minutes < 60) return Math.max(1, Math.round(minutes)) + ' min';
        if (minutes < 1440 * 2) return Math.round(minutes / 60) + ' hr';
        return Math.round(minutes / 1440) + ' days';
    }

    // Addresses banned for failed sign-ins, newest first, each with a way to lift the ban.
    function renderBanned() {
        var bans = (data && data.Banned) || [];
        var box = Shared.getEl('banned');
        if (!bans.length) {
            box.innerHTML = '<div class="ddns-table-wrap ddns-empty">No banned addresses.</div>';
            return;
        }

        box.innerHTML = '<div class="ddns-table-wrap"><table class="ddns-table ddns-activity"><thead><tr>' +
            '<th>IP</th><th>Country</th><th>Failures</th><th>Banned</th><th>Ends</th><th><span class="sr-only">Action</span></th>' +
            '</tr></thead><tbody>' +
            bans.map(function (b) {
                return '<tr><td class="jpk-mono">' + esc(b.Ip) + '</td>' +
                    '<td>' + esc(b.Country || 'Unknown') + '</td>' +
                    '<td>' + esc(b.Failures) + '</td>' +
                    '<td>' + esc(agoShort(b.BannedUtc)) + '</td>' +
                    '<td>' + esc(untilText(b.UntilUtc)) + '</td>' +
                    '<td class="ddns-action">' + button('Unban', 'jpk-secondary', 'data-unban="' + esc(b.Ip) + '"') + '</td></tr>';
            }).join('') + '</tbody></table></div>';
    }

    // The country filter lists the places in the data, keeping the choice while it is still there.
    // Every country with any activity, from the full totals rather than the rows shown, plus your network.
    function fillCountryFilter() {
        var names = {};
        [data.PeopleByCountry, data.FailedByCountry, data.BlockedByCountry].forEach(function (list) {
            (list || []).forEach(function (t) { names[t.CountryCode] = t.Country || t.CountryCode; });
        });
        var codes = Object.keys(names).sort(function (a, b) { return names[a].localeCompare(names[b]); });
        var select = Shared.getEl('filterCountry');
        select.innerHTML = '<option value="">All</option><option value="local">Local</option>' + codes.map(function (c) {
            return '<option value="' + esc(c) + '">' + esc(names[c]) + '</option>';
        }).join('');
        if (filterCountry && filterCountry !== 'local' && !names[filterCountry]) {
            select.insertAdjacentHTML('beforeend', '<option value="' + esc(filterCountry) + '">' + esc(filterCountry) + '</option>');
        }
        select.value = filterCountry;
    }

    // What each address did, newest first. The server applies the filters, so a flood of one kind never hides
    // another behind the row limit.
    function renderActivity() {
        fillCountryFilter();
        var rows = (data && data.Activity) || [];
        var box = Shared.getEl('activity');
        if (!rows.length) {
            box.innerHTML = '<div class="ddns-table-wrap ddns-empty">' + (filterType || filterCountry ? 'Nothing matches these filters.' : 'Nothing recorded yet.') + '</div>';
            return;
        }

        var area = showsArea();
        box.innerHTML = '<div class="ddns-table-wrap"><table class="ddns-table ddns-activity"><thead><tr>' +
            '<th>Type</th><th>User</th><th>IP</th><th>Device</th><th>Address</th><th>Country</th>' + (area ? '<th>Area</th>' : '') + '<th>Last Seen</th>' +
            '<th><span class="sr-only">Action</span></th>' +
            '</tr></thead><tbody>' +
            rows.map(function (r) {
                var type = TYPES[r.Type] || TYPES.Seen;
                return '<tr><td><span class="jpk-badge ' + type.color + '">' + esc(r.Type) + '</span></td>' +
                    '<td>' + esc(r.User) + '</td>' +
                    '<td class="jpk-mono">' + esc(r.Ip) + '</td>' +
                    '<td>' + esc(r.Device) + '</td>' +
                    '<td>' + esc(shortHost(r.Host)) + '</td>' +
                    '<td>' + esc(place(r)) + '</td>' +
                    (area ? '<td>' + esc(r.IsLocal ? '' : r.City || '') + '</td>' : '') +
                    '<td>' + esc(agoShort(r.LastUtc)) + '</td>' +
                    '<td class="ddns-action">' + action(r) + '</td></tr>';
            }).join('') + '</tbody></table></div>';
    }

    // One row per country: users seen, failed sign-ins and blocked addresses side by side, most first, for the
    // kinds picked above the map.
    function countryRows() {
        var rows = {};
        function add(list, key) {
            (list || []).forEach(function (t) {
                var r = rows[t.CountryCode] || (rows[t.CountryCode] = { code: t.CountryCode, name: t.Country || t.CountryCode, users: 0, failures: 0, blocked: 0, last: t.LastUtc });
                r[key] = t.Count;
                if (Date.parse(t.LastUtc) > Date.parse(r.last)) {
                    r.last = t.LastUtc;
                }
            });
        }

        if (data && mapShown.people) {
            add(data.PeopleByCountry, 'users');
        }

        if (data && mapShown.failed) {
            add(data.FailedByCountry, 'failures');
        }

        if (data && mapShown.blocked) {
            add(data.BlockedByCountry, 'blocked');
        }

        return Object.keys(rows).map(function (k) { return rows[k]; }).sort(function (x, y) {
            return total(y) - total(x) || (x.name < y.name ? -1 : 1);
        });
    }

    function total(r) {
        return r.users + r.failures + r.blocked;
    }

    // A count column's heading: the word, or an icon on a narrow screen. The word stays as the tooltip and the
    // accessible name.
    function countHeading(word, icon) {
        return '<th class="ddns-num" title="' + word + '" aria-label="' + word + '"><span class="ddns-word">' + word +
            '</span><span class="ddns-icon material-icons" aria-hidden="true">' + icon + '</span></th>';
    }

    function countLabel(r) {
        return [
            r.users ? r.users + (r.users === 1 ? ' user' : ' users') : '',
            r.failures ? r.failures + (r.failures === 1 ? ' failed sign-in' : ' failed sign-ins') : '',
            r.blocked ? r.blocked + (r.blocked === 1 ? ' blocked address' : ' blocked addresses') : ''
        ].filter(function (x) { return x; }).join(', ');
    }

    // Shades each country by how much came from it. Countries with users are blue, then countries with failed
    // sign-ins are red, and countries with only blocked addresses are purple. Local connections have no
    // country, so they are not on the map.
    function renderMap() {
        var rows = countryRows();
        var byCode = {};
        var max = 0;
        rows.forEach(function (r) { byCode[r.code] = r; max = Math.max(max, total(r)); });

        var paths = Object.keys(WORLD).map(function (code) {
            var shape = WORLD[code];
            var r = byCode[code];
            if (!r) {
                return '<path d="' + shape.d + '"><title>' + esc(shape.name) + '</title></path>';
            }

            var count = total(r);
            var strength = 0.35 + 0.65 * (max > 1 ? (count - 1) / (max - 1) : 1);
            var color = r.users ? MAP_COLORS.people : r.failures ? MAP_COLORS.failed : MAP_COLORS.blocked;
            return '<path class="ddns-hit" style="fill: rgba(' + color + ', ' + strength.toFixed(2) + ')" d="' + shape.d + '">' +
                '<title>' + esc(r.name + ', ' + countLabel(r)) + '</title></path>';
        }).join('');
        Shared.getEl('worldMap').innerHTML = '<svg viewBox="' + WORLD_VIEWBOX + '" role="img" aria-label="World map">' + paths + '</svg>';

        var showUsers = mapShown.people;
        var showFailures = mapShown.failed;
        var showBlocked = mapShown.blocked;
        var legend = Shared.getEl('mapLegend');
        if (!rows.length) {
            // The empty state sits in the same frame as the table, so the layout does not jump between views.
            var picked = Object.keys(mapShown).filter(function (k) { return mapShown[k]; });
            var empty = picked.length === 3 ? 'Nothing from the internet yet.'
                : picked.length > 1 ? 'Nothing from the internet for these yet.'
                : { people: 'No users from the internet yet.', failed: 'No failed sign-ins.', blocked: 'Nothing blocked yet.' }[picked[0]];
            legend.innerHTML = '<div class="ddns-table-wrap ddns-empty">' + esc(empty) + '</div>';
        } else {
            legend.innerHTML = '<div class="ddns-table-wrap"><table class="ddns-table"><thead><tr><th>Country</th>' +
                (showUsers ? countHeading('Users', 'person') : '') +
                (showFailures ? countHeading('Failures', 'warning') : '') +
                (showBlocked ? countHeading('Blocked', 'block') : '') +
                '<th>Last</th></tr></thead><tbody>' +
                rows.map(function (r) {
                    return '<tr><td>' + esc(r.name) + '</td>' +
                        (showUsers ? '<td class="ddns-num">' + r.users + '</td>' : '') +
                        (showFailures ? '<td class="ddns-num">' + r.failures + '</td>' : '') +
                        (showBlocked ? '<td class="ddns-num">' + r.blocked + '</td>' : '') +
                        '<td>' + esc(agoShort(r.last)) + '</td></tr>';
                }).join('') + '</tbody></table></div>';
        }

        [['mapPeople', 'people'], ['mapFailed', 'failed'], ['mapBlocked', 'blocked']].forEach(function (b) {
            var button = Shared.getEl(b[0]);
            button.className = 'raised emby-button ' + (mapShown[b[1]] ? 'jpk-button-submit' : 'jpk-secondary');
            button.setAttribute('aria-pressed', mapShown[b[1]] ? 'true' : 'false');
        });
    }

    function show(id, text) {
        var el = Shared.getEl(id);
        el.textContent = text || '';
        Shared.setVisible(el, !!text);
    }

    function load() {
        var query = [];
        if (filterType) query.push('type=' + encodeURIComponent(filterType));
        if (filterCountry) query.push('place=' + encodeURIComponent(filterCountry));
        return Shared.apiRequest('People' + (query.length ? '?' + query.join('&') : ''), 'GET').then(function (d) {
            data = d || {};
            renderMap();
            renderBanned();
            renderActivity();
            var note = '';
            if (data.Settings && data.Settings.Enabled === false) note = 'History is off, so nothing new is recorded. Turn it on under Advanced.';
            else if (data.Recording === false) note = 'History is recorded while remote access is on.';
            show('peopleOff', note);
        }).catch(function () {
            Shared.getEl('activity').innerHTML = '<div class="ddns-table-wrap ddns-empty">Could not load Remote Access.</div>';
        });
    }

    function setBlocked(ip, block) {
        Shared.apiRequest('People/' + (block ? 'Block' : 'Unblock') + '?ip=' + encodeURIComponent(ip), 'POST').then(function () {
            Shared.setStatus('activityStatus', (block ? 'Blocked ' : 'Unblocked ') + ip + '.', false);
            load();
        }).catch(function (err) {
            errorText(err, 'Could not ' + (block ? 'block ' : 'unblock ') + ip + '.').then(function (message) {
                Shared.setStatus('activityStatus', message, true);
            });
        });
    }

    function unban(ip) {
        Shared.apiRequest('People/Unban?ip=' + encodeURIComponent(ip), 'POST').then(function () {
            Shared.setStatus('activityStatus', 'Unbanned ' + ip + '.', false);
            load();
        }).catch(function (err) {
            errorText(err, 'Could not unban ' + ip + '.').then(function (message) {
                Shared.setStatus('activityStatus', message, true);
            });
        });
    }

    function onActivityClick(e) {
        var lift = e.target.closest('[data-unban]');
        if (lift) {
            unban(lift.getAttribute('data-unban'));
            return;
        }

        var block = e.target.closest('[data-block]');
        if (block) {
            setBlocked(block.getAttribute('data-block'), true);
            return;
        }

        var unblock = e.target.closest('[data-unblock]');
        if (unblock) {
            setBlocked(unblock.getAttribute('data-unblock'), false);
            return;
        }

        var country = e.target.closest('[data-unblock-country]');
        if (country) unblockCountry(country.getAttribute('data-unblock-country'), country.getAttribute('data-country'));
    }

    // A country rule covers many people, so this one asks first.
    function unblockCountry(code, name) {
        confirmDialog('Connections from ' + name + ' are allowed from now on. Individual blocked addresses stay blocked.', { title: 'Unblock ' + name, confirmText: 'Unblock' }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('People/UnblockCountry?code=' + encodeURIComponent(code), 'POST').then(function () {
                Shared.setStatus('activityStatus', 'Unblocked ' + name + '.', false);
                load();
            }).catch(function (err) {
                errorText(err, 'Could not unblock ' + name + '.').then(function (message) {
                    Shared.setStatus('activityStatus', message, true);
                });
            });
        });
    }

    // Turns a kind on or off. The last one picked stays on, so the map always shows something.
    function toggleKind(kind) {
        var others = Object.keys(mapShown).some(function (k) { return k !== kind && mapShown[k]; });
        if (mapShown[kind] && !others) return;
        mapShown[kind] = !mapShown[kind];
        renderMap();
    }

    function bind() {
        Shared.getEl('activity').addEventListener('click', onActivityClick);
        Shared.getEl('banned').addEventListener('click', onActivityClick);
        Shared.getEl('filterType').addEventListener('change', function (e) { filterType = e.target.value; load(); });
        Shared.getEl('filterCountry').addEventListener('change', function (e) { filterCountry = e.target.value; load(); });
        Shared.getEl('mapPeople').addEventListener('click', function () { toggleKind('people'); });
        Shared.getEl('mapFailed').addEventListener('click', function () { toggleKind('failed'); });
        Shared.getEl('mapBlocked').addEventListener('click', function () { toggleKind('blocked'); });
    }

    Shared = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'DynamicDns',
        tabsKey: 'ddns',
        tabIndex: 0,
        tabs: TABS,
        bind: bind,
        load: function (shared, page) {
            load();
            page.createPoller(load, 30000).start(false);
        }
    }).shared;
}
