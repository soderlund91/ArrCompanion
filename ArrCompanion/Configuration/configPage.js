define(['emby-input', 'emby-button', 'emby-select', 'emby-checkbox'], function () {
    'use strict';

    var pluginId = 'b4e2c6a1-58d3-4f0e-9a7c-3d1f62e8b905';

    // The kinds of server, in the order the page shows them. Instances is a list so more than one
    // of a kind can come later; the page shows (and saves) the first of each.
    var KINDS = [
        { kind: 'radarr', label: 'Radarr', type: 'movie', port: 7878 },
        { kind: 'sonarr', label: 'Sonarr', type: 'show', port: 8989 }
    ];
    function kindOf(type) { return type === 'show' ? KINDS[1] : KINDS[0]; }

    var MIN_AVAILABILITY = [['announced', 'Announced'], ['inCinemas', 'In cinemas'], ['released', 'Released']];
    var SERIES_MONITOR = [['all', 'All episodes'], ['future', 'Future episodes'], ['missing', 'Missing episodes'], ['existing', 'Existing episodes'],
        ['firstSeason', 'First season'], ['latestSeason', 'Latest season'], ['pilot', 'Pilot only'], ['none', 'None']];

    var state = {
        config: null,      // the saved settings
        catalog: null,     // GET ArrCompanion/Missing
        selected: {},      // key -> true
        options: {}        // instance id (or url|key) -> { RootFolders, QualityProfiles }
    };

    // ---- Helpers ----

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function api(path, method, body) {
        var init = { method: method || 'GET', headers: { 'X-MediaBrowser-Token': window.ApiClient.accessToken() } };
        if (body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }
        return fetch(window.ApiClient.getUrl(path), init).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.status === 204 ? null : r.text().then(function (t) { return t ? JSON.parse(t) : null; });
        });
    }

    var toastTimer = null;
    function toast(msg) {
        var el = document.querySelector('.ac-toast');
        if (!el) { el = document.createElement('div'); el.className = 'ac-toast'; document.body.appendChild(el); }
        el.textContent = msg;
        el.style.display = '';
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { el.style.display = 'none'; }, 3500);
    }

    function guid() {
        return 'xxxxxxxxxxxx4xxxyxxxxxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = Math.random() * 16 | 0;
            return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
        });
    }

    function int(v) { var n = parseInt(v, 10); return isNaN(n) || n < 0 ? 0 : n; }

    function option(value, label, selected) {
        return '<option value="' + esc(value) + '"' + (selected ? ' selected' : '') + '>' + esc(label) + '</option>';
    }

    function formatDate(iso) {
        if (!iso) return '';
        var d = new Date(iso);
        return isNaN(d.getTime()) ? iso : d.toLocaleString();
    }

    function formatSize(bytes) {
        if (!bytes) return '';
        var units = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0;
        while (bytes >= 1024 && i < units.length - 1) { bytes /= 1024; i++; }
        return bytes.toFixed(i >= 3 ? 1 : 0) + ' ' + units[i];
    }

    // Light or dark from the page's own background (Emby's settings can be light while the rest is dark).
    var theme = 'dark';
    function applyTheme(view) {
        var bg = null;
        for (var el = view; el && el.nodeType === 1; el = el.parentElement) {
            var c = getComputedStyle(el).backgroundColor;
            if (c && c !== 'transparent' && c !== 'rgba(0, 0, 0, 0)') { bg = c; break; }
        }
        if (!bg) bg = getComputedStyle(document.body).backgroundColor;
        var m = (bg || '').match(/\d+/g);
        theme = m && (parseInt(m[0]) * 0.299 + parseInt(m[1]) * 0.587 + parseInt(m[2]) * 0.114) >= 128 ? 'light' : 'dark';
        view.setAttribute('data-ac-theme', theme);
    }

    // ---- Settings (the saved ones) ----

    function instances() { return (state.config && state.config.Instances) || []; }
    function instanceOf(kind) {
        return instances().filter(function (i) { return i.Kind === kind; })[0] || null;
    }
    function isSetUp(i) { return !!(i && (i.Url || '').trim() && (i.ApiKey || '').trim()); }
    function hasDefaults(i) { return !!(i && (i.RootFolderPath || '').trim() && i.QualityProfileId > 0); }
    function instanceLabel(i, k) { return (i && (i.Name || '').trim()) || k.label; }

    function loadConfig() {
        return window.ApiClient.getPluginConfiguration(pluginId).then(function (c) {
            c.Instances = c.Instances || [];
            c.Rules = c.Rules || [];
            state.config = c;
            return c;
        });
    }

    // Save: the server's current settings with this page's parts over them, so nothing else is lost.
    function saveConfig(apply) {
        return window.ApiClient.getPluginConfiguration(pluginId).then(function (server) {
            apply(server);
            return window.ApiClient.updatePluginConfiguration(pluginId, server).then(function () { return loadConfig(); });
        });
    }

    // ---- Tabs ----

    function showTab(view, name) {
        view.querySelectorAll('.ac-tab').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-tab') === name); });
        view.querySelectorAll('.ac-tab-content').forEach(function (c) { c.style.display = c.getAttribute('data-tab-content') === name ? '' : 'none'; });
        if (name === 'history') loadHistory(view);
        if (name === 'rules') renderRules(view);
        if (name === 'settings') renderServers(view);
    }

    // ---- Missing ----

    function loadMissing(view) {
        var list = view.querySelector('#acMissingList');
        if (!state.catalog) list.innerHTML = '<div class="ac-empty"><span class="ac-spin"></span></div>';
        return api('ArrCompanion/Missing').then(function (res) {
            state.catalog = res;
            var keys = {};
            (res.Rows || []).forEach(function (r) { keys[r.Key] = true; });
            Object.keys(state.selected).forEach(function (k) { if (!keys[k]) delete state.selected[k]; });
            fillListFilter(view);
            renderMissing(view);
        }).catch(function (err) {
            view.querySelector('#acMissingBanner').innerHTML = '<div class="ac-banner error">The missing titles could not be read: ' + esc(err.message) + '</div>';
            list.innerHTML = '';
        });
    }

    function fillListFilter(view) {
        var sel = view.querySelector('#acFilterList');
        var current = sel.value;
        var lists = (state.catalog && state.catalog.Lists) || [];
        sel.innerHTML = option('', 'All lists', !current) + lists.map(function (l) { return option(l, l, l === current); }).join('');
    }

    function renderBanner(view) {
        var c = state.catalog, html = '';
        if (!c) return;
        var file = c.File || {};
        if (!file.Found) {
            html += '<div class="ac-banner warn">' + esc(file.Problem || 'No missing titles file.') + '</div>';
        } else {
            html += '<div class="ac-muted" style="margin-bottom:10px;">' + (c.Rows || []).length + ' missing titles on ' + (c.Lists || []).length + ' lists'
                + (file.GeneratedUtc ? ' · list from ' + esc(formatDate(file.GeneratedUtc)) : '') + '</div>';
            var unsetKinds = KINDS.filter(function (k) {
                return !isSetUp(instanceOf(k.kind)) && (c.Rows || []).some(function (r) { return r.Type === k.type; });
            });
            if (unsetKinds.length) {
                html += '<div class="ac-banner warn">Set up ' + unsetKinds.map(function (k) { return k.label; }).join(' and ')
                    + ' under Settings to add ' + unsetKinds.map(function (k) { return k.type === 'show' ? 'shows' : 'movies'; }).join(' and ') + '.</div>';
            }
        }
        (c.Errors || []).forEach(function (e) { html += '<div class="ac-banner error">' + esc(e) + '</div>'; });
        view.querySelector('#acMissingBanner').innerHTML = html;
    }

    function bestPlace(row, list) {
        var places = (row.Places || []).filter(function (p) { return !list || p.List === list; });
        var ranked = places.filter(function (p) { return p.Rank != null; }).sort(function (a, b) { return a.Rank - b.Rank; });
        return ranked[0] || places[0] || null;
    }

    function visibleRows(view) {
        var rows = (state.catalog && state.catalog.Rows) || [];
        var q = view.querySelector('#acSearch').value.trim().toLowerCase();
        var type = view.querySelector('#acFilterType').value;
        var list = view.querySelector('#acFilterList').value;
        var top = int(view.querySelector('#acFilterTop').value);
        var status = view.querySelector('#acFilterStatus').value;
        var sort = view.querySelector('#acSort').value;

        var out = rows.filter(function (r) {
            if (q && (r.Title || '').toLowerCase().indexOf(q) < 0) return false;
            if (type && r.Type !== type) return false;
            if (status === 'missing' && r.Status !== 'missing' && r.Status !== 'noServer') return false;
            if (status && status !== 'missing' && r.Status !== status) return false;
            var places = (r.Places || []).filter(function (p) { return !list || p.List === list; });
            if (list && !places.length) return false;
            if (top && !places.some(function (p) { return p.Rank != null && p.Rank <= top; })) return false;
            return true;
        });

        function rankOf(r) { var p = bestPlace(r, list); return p && p.Rank != null ? p.Rank : 1e9; }
        out.sort(function (a, b) {
            if (sort === 'title') return (a.Title || '').localeCompare(b.Title || '');
            if (sort === 'year') return (b.Year || 0) - (a.Year || 0) || rankOf(a) - rankOf(b);
            if (sort === 'lists') return (b.Places || []).length - (a.Places || []).length || rankOf(a) - rankOf(b);
            return rankOf(a) - rankOf(b) || (b.Places || []).length - (a.Places || []).length || (a.Title || '').localeCompare(b.Title || '');
        });
        return out;
    }

    function placeChips(row, list) {
        var places = (row.Places || []).slice().sort(function (a, b) {
            if (list) { if (a.List === list && b.List !== list) return -1; if (b.List === list && a.List !== list) return 1; }
            return (a.Rank == null ? 1e9 : a.Rank) - (b.Rank == null ? 1e9 : b.Rank);
        });
        return places.map(function (p) {
            if (p.Rank == null) return '<span class="ac-chip">' + esc(p.List) + '</span>';
            return '<span class="ac-chip' + (p.Rank <= 3 ? ' top' : '') + '" title="Place ' + p.Rank + (p.Of ? ' of ' + p.Of : '') + ' on ' + esc(p.List) + '">'
                + '<span class="ac-rank">#' + p.Rank + '</span>' + esc(p.List) + (p.Of ? '<span style="opacity:.6">/' + p.Of + '</span>' : '') + '</span>';
        }).join('');
    }

    function statusPill(row) {
        var k = kindOf(row.Type);
        if (row.Status === 'inArr') return '<span class="ac-pill ok">In ' + esc(instanceLabel(instanceOf(k.kind), k)) + '</span>';
        if (row.Status === 'ignored') return '<span class="ac-pill neutral">Ignored</span>';
        if (row.Status === 'noServer') return '<span class="ac-pill warn">No ' + k.label + ' set up</span>';
        if (row.AddedBefore) return '<span class="ac-pill neutral" title="Added once by Arr Companion and since removed. Rules leave it alone.">Added before</span>';
        return '';
    }

    function renderMissing(view) {
        renderBanner(view);
        var listEl = view.querySelector('#acMissingList');
        var rows = visibleRows(view);
        var list = view.querySelector('#acFilterList').value;
        if (!rows.length) {
            var none = state.catalog && state.catalog.File && state.catalog.File.Found ? 'Nothing matches.' : 'No titles yet.';
            listEl.innerHTML = '<div class="ac-empty">' + none + '</div>';
        } else {
            listEl.innerHTML = rows.map(function (r) {
                var k = kindOf(r.Type);
                var selectable = r.Status === 'missing' || r.Status === 'ignored';
                var actions = '';
                if (r.Status === 'missing')
                    actions = '<button type="button" class="ac-btn small" data-action="add" data-key="' + esc(r.Key) + '"><i class="md-icon">add</i>' + esc(instanceLabel(instanceOf(k.kind), k)) + '</button>'
                        + '<button type="button" class="ac-btn small ghost" data-action="ignore" data-key="' + esc(r.Key) + '" title="Ignore: not shown as missing, and no rule adds it"><i class="md-icon">visibility_off</i></button>';
                else if (r.Status === 'ignored')
                    actions = '<button type="button" class="ac-btn small ghost" data-action="unignore" data-key="' + esc(r.Key) + '"><i class="md-icon">visibility</i>Unignore</button>';
                return '<div class="ac-item' + (r.Status === 'inArr' || r.Status === 'ignored' ? ' dim' : '') + '">'
                    + '<div>' + (selectable ? '<input type="checkbox" class="acSel" data-key="' + esc(r.Key) + '"' + (state.selected[r.Key] ? ' checked' : '') + ' />' : '') + '</div>'
                    + '<div style="min-width:0;">'
                    + '<div class="ac-item-title">' + esc(r.Title) + (r.Year ? '<span class="ac-year">(' + r.Year + ')</span>' : '') + '</div>'
                    + '<div class="ac-item-meta"><span class="ac-type">' + (r.Type === 'show' ? 'Show' : 'Movie') + '</span>' + placeChips(r, list) + statusPill(r) + '</div>'
                    + '</div>'
                    + '<div class="ac-item-actions">' + actions + '</div>'
                    + '</div>';
            }).join('');
        }
        updateBulk(view, rows);
    }

    function selectedKeys() { return Object.keys(state.selected).filter(function (k) { return state.selected[k]; }); }

    function updateBulk(view, rows) {
        rows = rows || visibleRows(view);
        var selectable = rows.filter(function (r) { return r.Status === 'missing' || r.Status === 'ignored'; });
        var keys = selectedKeys();
        var rowByKey = {};
        ((state.catalog && state.catalog.Rows) || []).forEach(function (r) { rowByKey[r.Key] = r; });
        var addable = keys.filter(function (k) { return rowByKey[k] && rowByKey[k].Status === 'missing'; });
        var ignorable = keys.filter(function (k) { return rowByKey[k] && rowByKey[k].Status === 'missing'; });
        view.querySelector('#acCount').textContent = rows.length + ' shown' + (keys.length ? ' · ' + keys.length + ' selected' : '');
        var all = view.querySelector('#acSelectAll');
        all.checked = selectable.length > 0 && selectable.every(function (r) { return state.selected[r.Key]; });
        all.disabled = selectable.length === 0;
        var add = view.querySelector('#acAddSelected');
        add.disabled = addable.length === 0;
        add.innerHTML = '<i class="md-icon">add</i>Add ' + (addable.length ? addable.length + ' ' : '') + 'selected';
        view.querySelector('#acIgnoreSelected').disabled = ignorable.length === 0;
    }

    function ignore(view, keys, on) {
        if (!keys.length) return Promise.resolve();
        return api('ArrCompanion/Ignore', 'POST', { Keys: keys, Ignore: on }).then(function () {
            keys.forEach(function (k) { delete state.selected[k]; });
            toast((on ? 'Ignored ' : 'No longer ignored: ') + keys.length + (keys.length === 1 ? ' title' : ' titles'));
            return loadMissing(view);
        }).catch(function (err) { toast('Failed: ' + err.message); });
    }

    // ---- The add dialog ----

    function loadOptions(instance, url, apiKey) {
        var cacheKey = url ? url + '|' + apiKey : instance.Id;
        if (state.options[cacheKey]) return Promise.resolve(state.options[cacheKey]);
        return api('ArrCompanion/Options', 'POST', { InstanceId: instance ? instance.Id : '', Url: url || '', ApiKey: apiKey || '' }).then(function (res) {
            if (res && res.Success) state.options[cacheKey] = res;
            return res;
        });
    }

    function folderOptions(res, current) {
        var folders = (res && res.RootFolders) || [];
        var html = option('', 'Pick a root folder', !current);
        if (current && !folders.some(function (f) { return f.Path === current; })) html += option(current, current, true);
        return html + folders.map(function (f) {
            return option(f.Path, f.Path + (f.FreeSpace ? ' (' + formatSize(f.FreeSpace) + ' free)' : ''), f.Path === current);
        }).join('');
    }

    function profileOptions(res, current) {
        var profiles = (res && res.QualityProfiles) || [];
        var html = option('0', 'Pick a quality profile', !current);
        if (current && !profiles.some(function (p) { return p.Id === current; })) html += option(String(current), 'Profile ' + current, true);
        return html + profiles.map(function (p) { return option(String(p.Id), p.Name, p.Id === current); }).join('');
    }

    function choiceFields(k, instance) {
        var extra = k.kind === 'radarr'
            ? '<label class="ac-field"><span>Minimum availability</span><select class="ac-select" data-f="MinimumAvailability">'
                + MIN_AVAILABILITY.map(function (o) { return option(o[0], o[1], o[0] === (instance.MinimumAvailability || 'released')); }).join('') + '</select></label>'
            : '<label class="ac-field"><span>Monitor</span><select class="ac-select" data-f="SeriesMonitor">'
                + SERIES_MONITOR.map(function (o) { return option(o[0], o[1], o[0] === (instance.SeriesMonitor || 'all')); }).join('') + '</select></label>';
        return '<div class="ac-grid">'
            + '<label class="ac-field"><span>Root folder</span><select class="ac-select" data-f="RootFolderPath"><option>Loading…</option></select></label>'
            + '<label class="ac-field"><span>Quality profile</span><select class="ac-select" data-f="QualityProfileId"><option>Loading…</option></select></label>'
            + extra
            + '</div>'
            + '<label class="ac-check"><input type="checkbox" data-f="Monitored"' + (instance.Monitored !== false ? ' checked' : '') + ' /> Monitored</label>'
            + '<label class="ac-check"><input type="checkbox" data-f="Search"' + (instance.SearchOnAdd !== false ? ' checked' : '') + ' /> Search when added</label>';
    }

    function openAddDialog(view, keys) {
        var rowByKey = {};
        ((state.catalog && state.catalog.Rows) || []).forEach(function (r) { rowByKey[r.Key] = r; });
        var rows = keys.map(function (k) { return rowByKey[k]; }).filter(function (r) { return r && r.Status === 'missing'; });
        if (!rows.length) return;
        var kinds = KINDS.filter(function (k) { return rows.some(function (r) { return r.Type === k.type; }); });

        var backdrop = document.createElement('div');
        backdrop.className = 'ac-modal-backdrop';
        backdrop.setAttribute('data-ac-theme', theme);
        var title = rows.length === 1 ? esc(rows[0].Title) + (rows[0].Year ? ' (' + rows[0].Year + ')' : '') : 'Add ' + rows.length + ' titles';
        var sub = rows.length === 1
            ? placeChips(rows[0], '')
            : kinds.map(function (k) { var n = rows.filter(function (r) { return r.Type === k.type; }).length; return n + (k.type === 'show' ? (n === 1 ? ' show' : ' shows') : (n === 1 ? ' movie' : ' movies')); }).join(', ');
        backdrop.innerHTML = '<div class="ac-modal" role="dialog" aria-modal="true">'
            + '<h3>' + title + '</h3>'
            + '<div class="ac-item-meta" style="margin-bottom:12px;">' + sub + '</div>'
            + kinds.map(function (k) {
                var instance = instanceOf(k.kind);
                return '<div class="ac-kind" data-kind="' + k.kind + '">'
                    + '<div class="ac-eyebrow">' + esc(instanceLabel(instance, k)) + '</div>'
                    + (isSetUp(instance) ? choiceFields(k, instance) : '<div class="ac-banner warn">' + k.label + ' is not set up under Settings.</div>')
                    + '</div>';
            }).join('')
            + '<div class="ac-add-results"></div>'
            + '<div class="ac-modal-actions"><button type="button" class="ac-btn ac-cancel">Cancel</button><button type="button" class="ac-btn primary ac-go"><i class="md-icon">add</i>Add</button></div>'
            + '</div>';
        document.body.appendChild(backdrop);

        var changed = false;
        function close() {
            backdrop.remove();
            document.removeEventListener('keydown', onKey);
            if (changed) loadMissing(view);
        }
        function onKey(e) { if (e.key === 'Escape') close(); }
        document.addEventListener('keydown', onKey);
        backdrop.addEventListener('click', function (e) { if (e.target === backdrop) close(); });
        backdrop.querySelector('.ac-cancel').addEventListener('click', close);

        kinds.forEach(function (k) {
            var instance = instanceOf(k.kind);
            if (!isSetUp(instance)) return;
            var box = backdrop.querySelector('.ac-kind[data-kind="' + k.kind + '"]');
            loadOptions(instance).then(function (res) {
                if (!res || !res.Success) {
                    box.insertAdjacentHTML('beforeend', '<div class="ac-banner error">' + esc((res && res.Message) || 'Could not read the options.') + '</div>');
                }
                box.querySelector('[data-f="RootFolderPath"]').innerHTML = folderOptions(res, instance.RootFolderPath || '');
                box.querySelector('[data-f="QualityProfileId"]').innerHTML = profileOptions(res, instance.QualityProfileId || 0);
            }).catch(function (err) {
                box.insertAdjacentHTML('beforeend', '<div class="ac-banner error">' + esc(err.message) + '</div>');
            });
        });

        var go = backdrop.querySelector('.ac-go');
        go.addEventListener('click', function () {
            var body = { Keys: rows.map(function (r) { return r.Key; }) };
            var missingChoice = null;
            kinds.forEach(function (k) {
                var box = backdrop.querySelector('.ac-kind[data-kind="' + k.kind + '"]');
                var instance = instanceOf(k.kind);
                if (!isSetUp(instance)) return;
                function f(name) { return box.querySelector('[data-f="' + name + '"]'); }
                var choice = {
                    InstanceId: instance.Id,
                    RootFolderPath: f('RootFolderPath').value,
                    QualityProfileId: int(f('QualityProfileId').value),
                    Monitored: f('Monitored').checked,
                    Search: f('Search').checked,
                    MinimumAvailability: f('MinimumAvailability') ? f('MinimumAvailability').value : '',
                    SeriesMonitor: f('SeriesMonitor') ? f('SeriesMonitor').value : ''
                };
                if (!choice.RootFolderPath || !choice.QualityProfileId) missingChoice = instanceLabel(instance, k);
                body[k.kind === 'radarr' ? 'Radarr' : 'Sonarr'] = choice;
            });
            if (missingChoice) { toast('Pick a root folder and a quality profile for ' + missingChoice + '.'); return; }
            go.disabled = true;
            go.innerHTML = '<span class="ac-spin"></span> Adding…';
            api('ArrCompanion/Add', 'POST', body).then(function (res) {
                changed = true;
                var results = (res && res.Results) || [];
                backdrop.querySelector('.ac-add-results').innerHTML = '<div class="ac-eyebrow">Result</div>' + results.map(function (x) {
                    var cls = x.Success ? 'ok' : x.AlreadyIn ? 'info' : 'err';
                    var label = x.Success ? 'Added' : x.AlreadyIn ? 'Already there' : 'Failed';
                    return '<div style="margin:4px 0;"><span class="ac-pill ' + cls + '">' + label + '</span> ' + esc(x.Title || x.Key)
                        + (x.Success ? '' : ' <span class="ac-muted">' + esc(x.Message) + '</span>') + '</div>';
                }).join('');
                results.forEach(function (x) { if (x.Success || x.AlreadyIn) delete state.selected[x.Key]; });
                go.style.display = 'none';
                backdrop.querySelector('.ac-cancel').textContent = 'Close';
            }).catch(function (err) {
                go.disabled = false;
                go.innerHTML = '<i class="md-icon">add</i>Add';
                toast('Failed: ' + err.message);
            });
        });
    }

    // ---- Rules ----

    function describeRule(r) {
        var what = r.MediaType === 'movie' ? 'movies' : r.MediaType === 'show' ? 'shows' : 'titles';
        var where = (r.List || '').trim() || 'any list';
        return (r.TopN > 0 ? 'Top ' + r.TopN + ' ' + what + ' of ' + where : 'All ' + what + ' on ' + where)
            + (r.MinYear > 0 ? ' from ' + r.MinYear : '') + (r.MaxPerRun > 0 ? ', at most ' + r.MaxPerRun + ' per run' : '');
    }

    function ruleCard(r) {
        var lists = ((state.catalog && state.catalog.Lists) || []).slice();
        if (r.List && lists.indexOf(r.List) < 0) lists.push(r.List);
        return '<div class="ac-rule' + (r.Enabled ? '' : ' off') + '" data-rule="' + esc(r.Id) + '">'
            + '<div class="ac-rule-head">'
            + '<input type="checkbox" data-r="Enabled" title="On"' + (r.Enabled ? ' checked' : '') + ' />'
            + '<input type="text" class="ac-input" data-r="Name" placeholder="' + esc(describeRule(r)) + '" value="' + esc(r.Name || '') + '" />'
            + '<button type="button" class="ac-btn small" data-rule-action="preview"><i class="md-icon">visibility</i>Preview</button>'
            + '<button type="button" class="ac-btn small ghost" data-rule-action="delete" title="Delete rule"><i class="md-icon">delete</i></button>'
            + '</div>'
            + '<div class="ac-rule-summary">' + esc(describeRule(r)) + '</div>'
            + '<div class="ac-rule-grid">'
            + '<label class="ac-field"><span>List</span><select class="ac-select" data-r="List">' + option('', 'Any list', !r.List)
                + lists.map(function (l) { return option(l, l, l === r.List); }).join('') + '</select></label>'
            + '<label class="ac-field"><span>Type</span><select class="ac-select" data-r="MediaType">'
                + option('', 'Movies and shows', !r.MediaType) + option('movie', 'Movies', r.MediaType === 'movie') + option('show', 'Shows', r.MediaType === 'show') + '</select></label>'
            + '<label class="ac-field"><span>Top places (0 = anywhere)</span><input type="number" min="0" class="ac-input" data-r="TopN" value="' + (r.TopN || 0) + '" /></label>'
            + '<label class="ac-field"><span>From year (0 = any)</span><input type="number" min="0" class="ac-input" data-r="MinYear" value="' + (r.MinYear || 0) + '" /></label>'
            + '<label class="ac-field"><span>At most per run (0 = no limit)</span><input type="number" min="0" class="ac-input" data-r="MaxPerRun" value="' + (r.MaxPerRun || 0) + '" /></label>'
            + '</div>'
            + '<div class="ac-preview" data-preview></div>'
            + '</div>';
    }

    // The rules as the page has them (edits not saved yet), kept on state.config.
    function readRules(view) {
        var byId = {};
        (state.config.Rules || []).forEach(function (r) { byId[r.Id] = r; });
        state.config.Rules = Array.prototype.map.call(view.querySelectorAll('.ac-rule'), function (card) {
            var r = Object.assign({}, byId[card.getAttribute('data-rule')] || {});
            function v(name) { return card.querySelector('[data-r="' + name + '"]'); }
            r.Id = card.getAttribute('data-rule');
            r.Enabled = v('Enabled').checked;
            r.Name = v('Name').value.trim();
            r.List = v('List').value;
            r.MediaType = v('MediaType').value;
            r.TopN = int(v('TopN').value);
            r.MinYear = int(v('MinYear').value);
            r.MaxPerRun = int(v('MaxPerRun').value);
            r.InstanceId = r.InstanceId || '';
            return r;
        });
        state.config.RunRulesOnListChange = view.querySelector('#acRunOnChange').checked;
        return state.config.Rules;
    }

    function renderRules(view) {
        if (!state.config) return;
        view.querySelector('#acRunOnChange').checked = state.config.RunRulesOnListChange !== false;
        var rules = state.config.Rules || [];
        view.querySelector('#acRules').innerHTML = rules.length
            ? rules.map(ruleCard).join('')
            : '<div class="ac-panel ac-muted" style="text-align:center;">No rules yet.</div>';
    }

    function picksHtml(outcome) {
        var picks = (outcome && outcome.Picks) || [];
        var html = '<div class="ac-muted">' + esc((outcome && outcome.Message) || '') + '</div>';
        if (picks.length) {
            html += '<ol>' + picks.map(function (p) {
                var result = !p.Done ? '' : p.Success ? ' <span class="ac-pill ok">Added</span>' : p.AlreadyIn ? ' <span class="ac-pill info">Already there</span>'
                    : ' <span class="ac-pill err">Failed</span> <span class="ac-muted">' + esc(p.Message) + '</span>';
                return '<li><b>' + esc(p.Title) + '</b>' + (p.Year ? ' (' + p.Year + ')' : '')
                    + ' <span class="ac-muted">' + (p.Rank != null ? '#' + p.Rank + ' on ' : 'on ') + esc(p.List) + ' → ' + esc(p.InstanceName)
                    + (p.RuleName ? ' · ' + esc(p.RuleName) : '') + '</span>' + result + '</li>';
            }).join('') + '</ol>';
        }
        ((outcome && outcome.Notes) || []).forEach(function (n) { html += '<div class="ac-banner warn" style="margin:6px 0 0;">' + esc(n) + '</div>'; });
        return html;
    }

    function saveRules(view) {
        var rules = readRules(view);
        var runOnChange = state.config.RunRulesOnListChange;
        return saveConfig(function (server) {
            server.Rules = rules;
            server.RunRulesOnListChange = runOnChange;
        }).then(function () { renderRules(view); });
    }

    function runRules(view) {
        var out = view.querySelector('#acRunResult');
        out.innerHTML = '<span class="ac-spin"></span> Saving and checking what the rules pick…';
        saveRules(view).then(function () {
            return api('ArrCompanion/Rules/Run', 'POST', { DryRun: true });
        }).then(function (preview) {
            var n = ((preview && preview.Picks) || []).length;
            if (!n) { out.innerHTML = picksHtml(preview); return null; }
            out.innerHTML = picksHtml(preview);
            if (!window.confirm('Add these ' + n + (n === 1 ? ' title' : ' titles') + ' to Sonarr / Radarr now?')) return null;
            out.innerHTML = '<span class="ac-spin"></span> Adding…';
            return api('ArrCompanion/Rules/Run', 'POST', { DryRun: false }).then(function (res) {
                out.innerHTML = picksHtml(res);
                loadMissing(view);
            });
        }).catch(function (err) { out.innerHTML = '<div class="ac-banner error">' + esc(err.message) + '</div>'; });
    }

    // ---- History ----

    function loadHistory(view) {
        var el = view.querySelector('#acHistory');
        el.innerHTML = '<div class="ac-empty"><span class="ac-spin"></span></div>';
        api('ArrCompanion/History?Count=300').then(function (entries) {
            entries = entries || [];
            if (!entries.length) { el.innerHTML = '<div class="ac-panel ac-muted" style="text-align:center;">Nothing added yet.</div>'; return; }
            el.innerHTML = '<div class="ac-panel" style="padding:0; overflow:auto;"><table class="ac-table"><thead><tr>'
                + '<th>When</th><th>Title</th><th>Place</th><th>By</th><th>Server</th><th>Result</th></tr></thead><tbody>'
                + entries.map(function (e) {
                    var cls = e.Success ? 'ok' : e.AlreadyIn ? 'info' : 'err';
                    var label = e.Success ? 'Added' : e.AlreadyIn ? 'Already there' : 'Failed';
                    return '<tr><td style="white-space:nowrap;">' + esc(formatDate(e.AtUtc)) + '</td>'
                        + '<td>' + esc(e.Title) + (e.Year ? ' <span class="ac-muted">(' + e.Year + ')</span>' : '') + '</td>'
                        + '<td>' + (e.List ? (e.Rank != null ? '#' + e.Rank + ' ' : '') + esc(e.List) : '') + '</td>'
                        + '<td>' + (e.By === 'manual' ? 'By hand' : 'Rule: ' + esc(e.By)) + '</td>'
                        + '<td>' + esc(e.InstanceName) + '</td>'
                        + '<td><span class="ac-pill ' + cls + '" title="' + esc(e.Message) + '">' + label + '</span></td></tr>';
                }).join('') + '</tbody></table></div>';
        }).catch(function (err) { el.innerHTML = '<div class="ac-banner error">' + esc(err.message) + '</div>'; });
    }

    // ---- Settings ----

    function serverPanel(k) {
        var i = instanceOf(k.kind) || { Kind: k.kind, Monitored: true, SearchOnAdd: true, SeasonFolder: true, MinimumAvailability: 'released', SeriesMonitor: 'all' };
        var extra = k.kind === 'radarr'
            ? '<label class="ac-field"><span>Minimum availability</span><select class="ac-select" data-s="MinimumAvailability">'
                + MIN_AVAILABILITY.map(function (o) { return option(o[0], o[1], o[0] === (i.MinimumAvailability || 'released')); }).join('') + '</select></label>'
            : '<label class="ac-field"><span>Monitor</span><select class="ac-select" data-s="SeriesMonitor">'
                + SERIES_MONITOR.map(function (o) { return option(o[0], o[1], o[0] === (i.SeriesMonitor || 'all')); }).join('') + '</select></label>';
        return '<div class="ac-panel" data-server="' + k.kind + '">'
            + '<div class="ac-panel-title"><i class="md-icon">' + (k.kind === 'radarr' ? 'movie' : 'tv') + '</i>' + k.label + '</div>'
            + '<div class="ac-grid">'
            + '<label class="ac-field"><span>URL</span><input type="text" class="ac-input" data-s="Url" placeholder="http://localhost:' + k.port + '" value="' + esc(i.Url || '') + '" /></label>'
            + '<label class="ac-field"><span>API key</span><input type="text" class="ac-input" data-s="ApiKey" autocomplete="off" spellcheck="false" data-lpignore="true" data-1p-ignore value="' + esc(i.ApiKey || '') + '" /></label>'
            + '</div>'
            + '<div class="ac-row"><button type="button" class="ac-btn small" data-server-action="test"><i class="md-icon">wifi_tethering</i>Test connection</button><span class="ac-muted" data-test-result></span></div>'
            + '<div class="ac-hint">The API key is under Settings › General in ' + k.label + '. It stays on the Emby server; the browser never talks to ' + k.label + '.</div>'
            + '<div class="ac-eyebrow">Defaults for new ' + (k.type === 'show' ? 'shows' : 'movies') + ' (rules always use these)</div>'
            + '<div class="ac-grid">'
            + '<label class="ac-field"><span>Root folder</span><select class="ac-select" data-s="RootFolderPath">' + folderOptions(null, i.RootFolderPath || '') + '</select></label>'
            + '<label class="ac-field"><span>Quality profile</span><select class="ac-select" data-s="QualityProfileId">' + profileOptions(null, i.QualityProfileId || 0) + '</select></label>'
            + extra
            + '</div>'
            + '<label class="ac-check"><input type="checkbox" data-s="Monitored"' + (i.Monitored !== false ? ' checked' : '') + ' /> Monitored</label>'
            + '<label class="ac-check"><input type="checkbox" data-s="SearchOnAdd"' + (i.SearchOnAdd !== false ? ' checked' : '') + ' /> Search when added</label>'
            + (k.kind === 'sonarr' ? '<label class="ac-check"><input type="checkbox" data-s="SeasonFolder"' + (i.SeasonFolder !== false ? ' checked' : '') + ' /> Season folders</label>' : '')
            + '<div class="ac-banner error" data-options-error style="display:none;"></div>'
            + '</div>';
    }

    function renderServers(view) {
        if (!state.config) return;
        view.querySelector('#acServers').innerHTML = KINDS.map(serverPanel).join('');
        view.querySelector('#acListPath').value = state.config.MissingListPath || '';
        var file = state.catalog && state.catalog.File;
        view.querySelector('#acListPathHint').innerHTML = 'Leave empty to read the file Home Screen Companion keeps. It needs Missing titles on, with "Keep the list in a file" and JSON.'
            + (file && file.Path ? '<br />Now reading: <code>' + esc(file.Path) + '</code>' + (file.Found ? '' : ' (not there yet)') : '');
        KINDS.forEach(function (k) {
            var i = instanceOf(k.kind);
            if (isSetUp(i)) refreshServerOptions(view, k, false);
        });
    }

    function panelValue(panel, name) { return panel.querySelector('[data-s="' + name + '"]'); }

    // The root folders and profiles of the URL and key the panel shows.
    function refreshServerOptions(view, k, fresh) {
        var panel = view.querySelector('[data-server="' + k.kind + '"]');
        var url = panelValue(panel, 'Url').value.trim(), key = panelValue(panel, 'ApiKey').value.trim();
        if (!url || !key) return;
        if (fresh) delete state.options[url + '|' + key];
        var err = panel.querySelector('[data-options-error]');
        loadOptions(instanceOf(k.kind), url, key).then(function (res) {
            var root = panelValue(panel, 'RootFolderPath'), profile = panelValue(panel, 'QualityProfileId');
            var currentRoot = root.value, currentProfile = int(profile.value);
            if (!res || !res.Success) {
                err.textContent = (res && res.Message) || 'Could not read the root folders and profiles.';
                err.style.display = '';
                return;
            }
            err.style.display = 'none';
            // Only one choice: picked already.
            if (!currentRoot && res.RootFolders.length === 1) currentRoot = res.RootFolders[0].Path;
            if (!currentProfile && res.QualityProfiles.length === 1) currentProfile = res.QualityProfiles[0].Id;
            root.innerHTML = folderOptions(res, currentRoot);
            profile.innerHTML = profileOptions(res, currentProfile);
        }).catch(function (e) {
            err.textContent = e.message;
            err.style.display = '';
        });
    }

    function testServer(view, k) {
        var panel = view.querySelector('[data-server="' + k.kind + '"]');
        var out = panel.querySelector('[data-test-result]');
        out.innerHTML = '<span class="ac-spin"></span>';
        api('ArrCompanion/Test', 'POST', { Url: panelValue(panel, 'Url').value.trim(), ApiKey: panelValue(panel, 'ApiKey').value.trim() }).then(function (res) {
            out.innerHTML = '<span class="ac-pill ' + (res.Success ? 'ok' : 'err') + '">' + (res.Success ? 'OK' : 'Failed') + '</span> ' + esc(res.Message);
            if (res.Success) refreshServerOptions(view, k, true);
        }).catch(function (err) {
            out.innerHTML = '<span class="ac-pill err">Failed</span> ' + esc(err.message);
        });
    }

    function saveSettings(view) {
        var panels = KINDS.map(function (k) {
            var panel = view.querySelector('[data-server="' + k.kind + '"]');
            var saved = instanceOf(k.kind);
            var i = Object.assign({}, saved || {}, { Id: (saved && saved.Id) || guid(), Kind: k.kind });
            i.Url = panelValue(panel, 'Url').value.trim();
            i.ApiKey = panelValue(panel, 'ApiKey').value.trim();
            i.RootFolderPath = panelValue(panel, 'RootFolderPath').value;
            i.QualityProfileId = int(panelValue(panel, 'QualityProfileId').value);
            i.Monitored = panelValue(panel, 'Monitored').checked;
            i.SearchOnAdd = panelValue(panel, 'SearchOnAdd').checked;
            if (k.kind === 'radarr') i.MinimumAvailability = panelValue(panel, 'MinimumAvailability').value;
            else { i.SeriesMonitor = panelValue(panel, 'SeriesMonitor').value; i.SeasonFolder = panelValue(panel, 'SeasonFolder').checked; }
            return i;
        });
        var listPath = view.querySelector('#acListPath').value.trim();
        var btn = view.querySelector('#acSaveSettings');
        btn.disabled = true;
        saveConfig(function (server) {
            // The page's instances replace theirs; any others (more of a kind, later) are kept.
            var shown = {};
            panels.forEach(function (p) { shown[p.Id] = p; });
            var kept = (server.Instances || []).filter(function (i) { return !shown[i.Id]; });
            server.Instances = panels.filter(function (p) { return p.Url || p.ApiKey || instances().some(function (i) { return i.Id === p.Id; }); }).concat(kept);
            server.MissingListPath = listPath;
        }).then(function () {
            toast('Settings saved');
            return loadMissing(view);
        }).catch(function (err) { toast('Could not save: ' + err.message); })
        .then(function () { btn.disabled = false; });
    }

    // ---- Wiring ----

    function bind(view) {
        view.querySelector('#acTabs').addEventListener('click', function (e) {
            var b = e.target.closest('.ac-tab');
            if (b) showTab(view, b.getAttribute('data-tab'));
        });

        // Missing
        ['#acSearch', '#acFilterType', '#acFilterList', '#acFilterTop', '#acFilterStatus', '#acSort'].forEach(function (sel) {
            view.querySelector(sel).addEventListener(sel === '#acSearch' ? 'input' : 'change', function () { renderMissing(view); });
        });
        view.querySelector('#acRefresh').addEventListener('click', function () { loadMissing(view); });
        view.querySelector('#acMissingList').addEventListener('change', function (e) {
            if (!e.target.classList.contains('acSel')) return;
            state.selected[e.target.getAttribute('data-key')] = e.target.checked;
            if (!e.target.checked) delete state.selected[e.target.getAttribute('data-key')];
            updateBulk(view);
        });
        view.querySelector('#acMissingList').addEventListener('click', function (e) {
            var b = e.target.closest('[data-action]');
            if (!b) return;
            var key = b.getAttribute('data-key'), action = b.getAttribute('data-action');
            if (action === 'add') openAddDialog(view, [key]);
            else if (action === 'ignore') ignore(view, [key], true);
            else if (action === 'unignore') ignore(view, [key], false);
        });
        view.querySelector('#acSelectAll').addEventListener('change', function (e) {
            visibleRows(view).forEach(function (r) {
                if (r.Status !== 'missing' && r.Status !== 'ignored') return;
                if (e.target.checked) state.selected[r.Key] = true; else delete state.selected[r.Key];
            });
            renderMissing(view);
        });
        view.querySelector('#acAddSelected').addEventListener('click', function () { openAddDialog(view, selectedKeys()); });
        view.querySelector('#acIgnoreSelected').addEventListener('click', function () {
            var rowByKey = {};
            ((state.catalog && state.catalog.Rows) || []).forEach(function (r) { rowByKey[r.Key] = r; });
            ignore(view, selectedKeys().filter(function (k) { return rowByKey[k] && rowByKey[k].Status === 'missing'; }), true);
        });

        // Rules
        view.querySelector('#acAddRule').addEventListener('click', function () {
            readRules(view);
            state.config.Rules.push({ Id: guid(), Name: '', Enabled: true, List: '', MediaType: '', TopN: 3, MinYear: 0, MaxPerRun: 0, InstanceId: '' });
            renderRules(view);
        });
        view.querySelector('#acRules').addEventListener('input', function (e) {
            var card = e.target.closest('.ac-rule');
            if (!card) return;
            readRules(view);
            var r = state.config.Rules.filter(function (x) { return x.Id === card.getAttribute('data-rule'); })[0];
            if (!r) return;
            card.querySelector('.ac-rule-summary').textContent = describeRule(r);
            card.querySelector('[data-r="Name"]').placeholder = describeRule(r);
            card.classList.toggle('off', !r.Enabled);
        });
        view.querySelector('#acRules').addEventListener('change', function (e) {
            e.target.dispatchEvent(new Event('input', { bubbles: true }));
        });
        view.querySelector('#acRules').addEventListener('click', function (e) {
            var b = e.target.closest('[data-rule-action]');
            if (!b) return;
            var card = b.closest('.ac-rule');
            var id = card.getAttribute('data-rule');
            var rules = readRules(view);
            if (b.getAttribute('data-rule-action') === 'delete') {
                state.config.Rules = rules.filter(function (r) { return r.Id !== id; });
                renderRules(view);
                return;
            }
            var rule = rules.filter(function (r) { return r.Id === id; })[0];
            var out = card.querySelector('[data-preview]');
            out.innerHTML = '<span class="ac-spin"></span>';
            api('ArrCompanion/Rules/Run', 'POST', { DryRun: true, Rule: rule }).then(function (res) {
                out.innerHTML = picksHtml(res);
            }).catch(function (err) { out.innerHTML = '<div class="ac-banner error">' + esc(err.message) + '</div>'; });
        });
        view.querySelector('#acSaveRules').addEventListener('click', function () {
            saveRules(view).then(function () { toast('Rules saved'); }).catch(function (err) { toast('Could not save: ' + err.message); });
        });
        view.querySelector('#acRunRules').addEventListener('click', function () { runRules(view); });

        // History
        view.querySelector('#acHistoryRefresh').addEventListener('click', function () { loadHistory(view); });

        // Settings
        view.querySelector('#acServers').addEventListener('click', function (e) {
            var b = e.target.closest('[data-server-action="test"]');
            if (!b) return;
            var kind = b.closest('[data-server]').getAttribute('data-server');
            testServer(view, KINDS.filter(function (k) { return k.kind === kind; })[0]);
        });
        view.querySelector('#acSaveSettings').addEventListener('click', function () { saveSettings(view); });
    }

    return function (view) {
        view.addEventListener('viewshow', function () {
            applyTheme(view);
            if (!view.dataset.acInit) {
                view.dataset.acInit = '1';
                bind(view);
            }
            loadConfig().then(function () {
                return loadMissing(view);
            }).then(function () {
                var active = view.querySelector('.ac-tab.active');
                if (active && active.getAttribute('data-tab') !== 'missing') showTab(view, active.getAttribute('data-tab'));
            });
        });
    };
});
