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
        options: {},       // instance id (or url|key) -> { RootFolders, QualityProfiles }
        posters: {},       // key -> poster url ('' = none)
        posterAsked: {},   // key -> true while asked
        noTmdbKey: false,  // no way to look posters up: they are not asked for
        type: '',          // Movies / Shows chip: '' | 'movie' | 'show'
        showInArr: false,  // also the titles Sonarr / Radarr already have
        hiddenView: false, // Show hidden: only the hidden titles
        view: 'list',      // 'list' | 'posters'
        loads: 0,          // GET ArrCompanion/Missing asked so far; an older answer is dropped
        folds: {},         // Settings panels the user opened or closed: data-fold -> open
        languages: null    // GET ArrCompanion/Languages: [{ Code, Name }]
    };

    function storedView() { try { return localStorage.getItem('arrCompanionView') || 'list'; } catch (e) { return 'list'; } }
    function storeView(v) { try { localStorage.setItem('arrCompanionView', v); } catch (e) { } }
    function storedShowInArr() { try { return localStorage.getItem('arrCompanionShowInArr') === '1'; } catch (e) { return false; } }
    function storeShowInArr(on) { try { localStorage.setItem('arrCompanionShowInArr', on ? '1' : '0'); } catch (e) { } }

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
        // The footer bar starts beside Emby's drawer when it is docked.
        var drawer = document.querySelector('.mainDrawer');
        var drawerRight = drawer ? drawer.getBoundingClientRect().right : 0;
        view.style.setProperty('--ac-footer-left', (drawerRight > 0 && drawerRight < window.innerWidth * 0.5 ? drawerRight : 0) + 'px');
    }

    // Footer: the installed version, and a newer release on GitHub (nothing shown when GitHub can't be read).
    function loadFooter(view) {
        window.ApiClient.getInstalledPlugins().then(function (plugins) {
            var me = (plugins || []).filter(function (p) { return (p.Id || '').toLowerCase() === pluginId; })[0];
            var ver = (me && me.Version) || '';
            if (!ver) return;
            view.querySelector('#acFooterVersion').innerHTML = '<a href="https://github.com/soderlund91/ArrCompanion/releases/tag/v' + esc(ver) + '" target="_blank" style="color:inherit;text-decoration:none;">v' + esc(ver) + '</a>';
            fetch('https://api.github.com/repos/soderlund91/ArrCompanion/releases/latest')
                .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
                .then(function (release) {
                    var latest = (release.tag_name || '').replace(/^v/i, '');
                    if (!latest) return;
                    var a = latest.split('.').map(Number), b = ver.split('.').map(Number), newer = false;
                    for (var i = 0; i < Math.max(a.length, b.length); i++) {
                        if ((a[i] || 0) > (b[i] || 0)) { newer = true; break; }
                        if ((a[i] || 0) < (b[i] || 0)) break;
                    }
                    if (!newer) return;
                    view.querySelector('#acFooterUpdateSep').style.display = '';
                    view.querySelector('#acFooterUpdateInfo').innerHTML = '<a class="footer-update-link" href="' + esc(release.html_url || 'https://github.com/soderlund91/ArrCompanion/releases') + '" target="_blank">Update available: v' + esc(latest) + '</a>';
                }).catch(function () { });
        }).catch(function () { });
    }

    // ---- Settings (the saved ones) ----

    function instances() { return (state.config && state.config.Instances) || []; }
    function instanceOf(kind) {
        return instances().filter(function (i) { return i.Kind === kind; })[0] || null;
    }
    function isSetUp(i) { return !!(i && (i.Url || '').trim() && (i.ApiKey || '').trim()); }
    function hasDefaults(i) { return !!(i && (i.RootFolderPath || '').trim() && i.QualityProfileId > 0); }
    function instanceLabel(i, k) { return (i && (i.Name || '').trim()) || k.label; }

    // Your languages: the codes set under Settings; English when none is.
    function myLangCodes(codes) {
        var out = [];
        (codes || []).forEach(function (c) { c = String(c || '').trim().toLowerCase(); if (c && out.indexOf(c) < 0) out.push(c); });
        return out.length ? out : ['en'];
    }
    function myLangs() { return myLangCodes(state.config && state.config.MyLanguages); }
    function isMine(code) { return !!code && myLangs().indexOf(String(code).toLowerCase()) >= 0; }

    function loadConfig() {
        return window.ApiClient.getPluginConfiguration(pluginId).then(function (c) {
            c.Instances = c.Instances || [];
            c.Rules = c.Rules || [];
            c.MyLanguages = myLangCodes(c.MyLanguages);
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

    // The rows at once, labelled with the Sonarr / Radarr lists the plugin kept; refresh: those
    // lists are read again meanwhile, and the rows are labelled again when they arrive.
    function loadMissing(view, refresh, full) {
        var list = view.querySelector('#acMissingList');
        var load = ++state.loads;
        clearTimeout(state.enrichTimer);
        if (!state.catalog) list.innerHTML = '<div class="ac-empty"><span class="ac-spin"></span></div>';
        // poll: an answer while the details are looked up; the page is only drawn again when it shows more.
        function show(res, poll) {
            if (load !== state.loads) return false;
            var sig = detailSig(res);
            if (poll && res.Enriching && sig === state.detailSig) { state.catalog = res; return true; }
            state.detailSig = sig;
            state.catalog = res;
            var keys = {};
            (res.Rows || []).forEach(function (r) { keys[r.Key] = true; });
            Object.keys(state.selected).forEach(function (k) { if (!keys[k]) delete state.selected[k]; });
            fillListFilter(view);
            fillLangFilter(view);
            renderMissing(view);
            fileFolds(view);
            view.querySelector('#acRefresh').classList.toggle('ac-refreshing', !!res.Refreshing);
            return true;
        }
        function failed(err) {
            if (load !== state.loads) return;
            view.querySelector('#acRefresh').classList.remove('ac-refreshing');
            view.querySelector('#acMissingBanner').innerHTML = '<div class="ac-banner error">The missing titles could not be read: ' + esc(err.message) + '</div>';
            if (!state.catalog) list.innerHTML = '';
        }
        // The titles' languages and the match are looked up in the background: asked again until done.
        function whileEnriching(res) {
            if (load !== state.loads || !res || !res.Enriching) return;
            state.enrichTimer = setTimeout(function () {
                if (load !== state.loads || !view.isConnected) return;
                api('ArrCompanion/Missing?Stored=true').then(function (again) { if (show(again, true)) whileEnriching(again); }, function () { });
            }, 3000);
        }
        return api('ArrCompanion/Missing?Stored=true&Refresh=' + (refresh ? 'true' : 'false') + (full ? '&Full=true' : '')).then(function (res) {
            if (!show(res)) return;
            if (!res.Refreshing) { whileEnriching(res); return; }
            // Waits for the lists being read, then labels the rows again.
            return api('ArrCompanion/Missing').then(function (again) { if (show(again)) whileEnriching(again); }, failed);
        }, failed);
    }

    // What the lookups have filled in so far: languages and matches.
    function detailSig(res) {
        var n = 0, langs = 0, match = 0;
        ((res && res.Rows) || []).forEach(function (r) { if (r.HasDetails) n++; if (r.Language) langs++; if (r.Match != null) match += r.Match + 1; });
        return [(res && res.Rows || []).length, n, langs, match, res && res.HasMatch].join('|');
    }

    // "Any language" and the languages of the titles, with how many each has.
    function fillLangFilter(view) {
        var sel = view.querySelector('#acFilterLang');
        if (sel === document.activeElement) return;   // open or in use: left as it is
        var current = sel.value;
        var counts = {}, names = {}, unknown = 0;
        ((state.catalog && state.catalog.Rows) || []).forEach(function (r) {
            if (!r.Language) { unknown++; return; }
            counts[r.Language] = (counts[r.Language] || 0) + 1;
            names[r.Language] = r.LanguageName || r.Language;
        });
        var codes = Object.keys(counts).sort(function (a, b) { return counts[b] - counts[a] || names[a].localeCompare(names[b]); });
        if (current && current !== '?' && current !== '@mine' && current !== '@other' && !counts[current]) current = '';
        var mine = 0, other = 0;
        codes.forEach(function (c) { if (isMine(c)) mine += counts[c]; else other += counts[c]; });
        sel.innerHTML = option('', 'Any language', !current)
            + option('@mine', 'Your languages · ' + mine, current === '@mine')
            + option('@other', 'Other languages · ' + other, current === '@other')
            + codes.map(function (c) { return option(c, names[c] + ' · ' + counts[c], c === current); }).join('')
            + (unknown && codes.length ? option('?', 'Not known · ' + unknown, current === '?') : '');
        sel.style.display = codes.length ? '' : 'none';
    }

    // The languages the page knows a name for: the titles', and any a rule names.
    // all: every language the server has a name for (Settings › Your languages).
    function langNames(all) {
        var names = {};
        ((state.catalog && state.catalog.Rows) || []).forEach(function (r) { if (r.Language) names[r.Language] = r.LanguageName || r.Language; });
        if (all) (state.languages || []).forEach(function (l) { if (!names[l.Code]) names[l.Code] = l.Name; });
        return names;
    }

    function fillListFilter(view) {
        var sel = view.querySelector('#acFilterList');
        if (sel === document.activeElement) return;
        var current = sel.value;
        var lists = (state.catalog && state.catalog.Lists) || [];
        var counts = {};
        ((state.catalog && state.catalog.Rows) || []).forEach(function (r) {
            (r.Places || []).forEach(function (p) { counts[p.List] = (counts[p.List] || 0) + 1; });
        });
        sel.innerHTML = option('', 'All lists', !current) + lists.map(function (l) { return option(l, l + ' · ' + (counts[l] || 0), l === current); }).join('');
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
        if (file.Found) {
            var unranked = {};
            (c.Rows || []).forEach(function (r) {
                if (r.Status !== 'missing') return;
                (r.Places || []).forEach(function (p) { if (p.Rank == null) unranked[p.List] = true; });
            });
            var names = Object.keys(unranked);
            if (names.length)
                html += '<div class="ac-banner warn">The place on the list is not known yet for ' + names.map(function (n) { return '<b>' + esc(n) + '</b>'; }).join(', ')
                    + '. It is read from Home Screen Companion\'s lists: run its sync once more.</div>';
        }
        (c.Errors || []).forEach(function (e) { html += '<div class="ac-banner error">' + esc(e) + '</div>'; });
        view.querySelector('#acMissingBanner').innerHTML = html;
    }

    function bestPlace(row, list) {
        var places = (row.Places || []).filter(function (p) { return !list || p.List === list; });
        var ranked = places.filter(function (p) { return p.Rank != null; }).sort(function (a, b) { return a.Rank - b.Rank; });
        return ranked[0] || places[0] || null;
    }

    // '' any, '?' not known, '@mine' your languages, '@other' known and not yours, else that language.
    function langFilterLets(lang, code) {
        if (lang === '?') return !code;
        if (lang === '@mine') return isMine(code);
        if (lang === '@other') return !!code && !isMine(code);
        return code === lang;
    }

    // The rows the filters let through; ignoreType: all but the Movies / Shows chip (for the chips' counts);
    // withInArr: as if In Sonarr / Radarr were on (for its count).
    function visibleRows(view, ignoreType, withInArr) {
        var rows = (state.catalog && state.catalog.Rows) || [];
        var q = view.querySelector('#acSearch').value.trim().toLowerCase();
        var type = ignoreType ? '' : state.type;
        var list = view.querySelector('#acFilterList').value;
        var top = int(view.querySelector('#acFilterTop').value);
        var lang = view.querySelector('#acFilterLang').value;
        var sort = view.querySelector('#acSort').value;

        var out = rows.filter(function (r) {
            if (q && (r.Title || '').toLowerCase().indexOf(q) < 0 && (r.Imdb || '').toLowerCase().indexOf(q) < 0) return false;
            if (type && r.Type !== type) return false;
            if (lang && !langFilterLets(lang, r.Language)) return false;
            if (state.hiddenView ? r.Status !== 'ignored' : !isOpen(r) && !((state.showInArr || withInArr) && r.Status === 'inArr')) return false;
            var places = (r.Places || []).filter(function (p) { return !list || p.List === list; });
            if (list && !places.length) return false;
            if (top && !places.some(function (p) { return p.Rank != null && p.Rank <= top; })) return false;
            return true;
        });

        function rankOf(r) { var p = bestPlace(r, list); return p && p.Rank != null ? p.Rank : 1e9; }
        out.sort(function (a, b) {
            if (sort === 'title') return (a.Title || '').localeCompare(b.Title || '');
            if (sort === 'year') return (b.Year || 0) - (a.Year || 0) || rankOf(a) - rankOf(b);
            if (sort === 'match') return (b.Match == null ? -1 : b.Match) - (a.Match == null ? -1 : a.Match) || rankOf(a) - rankOf(b);
            if (sort === 'lists') return (b.Places || []).length - (a.Places || []).length || rankOf(a) - rankOf(b);
            return rankOf(a) - rankOf(b) || (b.Places || []).length - (a.Places || []).length || (a.Title || '').localeCompare(b.Title || '');
        });
        return out;
    }

    function sortedPlaces(row, list) {
        return (row.Places || []).slice().sort(function (a, b) {
            if (list) { if (a.List === list && b.List !== list) return -1; if (b.List === list && a.List !== list) return 1; }
            return (a.Rank == null ? 1e9 : a.Rank) - (b.Rank == null ? 1e9 : b.Rank) || a.List.localeCompare(b.List);
        });
    }

    // One line per list: "#1  Trending  of 20". At most `max` lines, then "+ n more lists".
    function placeLines(row, list, max) {
        var places = sortedPlaces(row, list);
        var shown = max && places.length > max ? places.slice(0, max) : places;
        var html = shown.map(function (p) {
            var cls = 'ac-place' + (p.Rank == null ? ' unknown' : p.Rank <= 3 ? ' top' : '') + (list && p.List === list ? ' focus' : '');
            var title = p.Rank == null ? esc(p.List) + ': place not known' : 'Place ' + p.Rank + (p.Of ? ' of ' + p.Of : '') + ' on ' + esc(p.List);
            return '<div class="' + cls + '" title="' + title + '">'
                + '<span class="ac-rank">' + (p.Rank == null ? '–' : '#' + p.Rank) + '</span>'
                + '<span class="ac-list-name">' + esc(p.List) + '</span>'
                + (p.Rank != null && p.Of ? '<span class="ac-of">of ' + p.Of + '</span>' : '')
                + '</div>';
        }).join('');
        if (shown.length < places.length) html += '<div class="ac-place unknown"><span class="ac-rank"></span><span class="ac-of">+ ' + (places.length - shown.length) + ' more lists</span></div>';
        return '<div class="ac-places">' + html + '</div>';
    }

    // The lists a title is on, as tags with its place: "IMDb Top 250 #3". The filtered list first.
    function placeTags(row, list) {
        return '<div class="ac-srcs">' + sortedPlaces(row, list).map(function (p) {
            var cls = 'ac-src' + (p.Rank != null && p.Rank <= 3 ? ' top' : '') + (list && p.List === list ? ' focus' : '');
            var title = p.Rank == null ? esc(p.List) + ': place not known' : 'Place ' + p.Rank + (p.Of ? ' of ' + p.Of : '') + ' on ' + esc(p.List);
            return '<span class="' + cls + '" title="' + title + '">' + esc(p.List) + (p.Rank != null ? ' <b>#' + p.Rank + '</b>' : '') + '</span>';
        }).join('') + '</div>';
    }

    function imdbLink(row) {
        return row.Imdb ? '<a href="https://www.imdb.com/title/' + encodeURIComponent(row.Imdb) + '/" target="_blank" rel="noopener">' + esc(row.Imdb) + '</a>' : '';
    }

    // + Radarr (add), ✓ Radarr (already there: monitored, downloaded / episodes on hover), or a warning add without a server.
    function arrPill(row) {
        var k = kindOf(row.Type);
        var server = esc(instanceLabel(instanceOf(k.kind), k));
        if (row.Status === 'inArr') {
            var detail = (row.ArrMonitored ? 'monitored' : 'unmonitored') + (k.kind === 'sonarr'
                ? (row.ArrEpisodes > 0 ? ', ' + row.ArrEpisodeFiles + ' of ' + row.ArrEpisodes + ' episodes' : '')
                : ', ' + (row.ArrDownloaded ? 'downloaded' : 'not downloaded'));
            return '<span class="ac-arr in" title="In ' + server + ': ' + detail + '"><i class="md-icon">check</i>' + server + '</span>';
        }
        if (row.Status === 'noServer')
            return '<button type="button" class="ac-arr warn" data-action="add" data-key="' + esc(row.Key) + '" title="' + k.label + ' is not set up yet: set it up under Settings to add this"><i class="md-icon">link_off</i>' + server + '</button>';
        if (isOpen(row))
            return '<button type="button" class="ac-arr add" data-action="add" data-key="' + esc(row.Key) + '" title="Add to ' + server + '"><i class="md-icon">add</i>' + server + '</button>';
        return '';
    }

    // "Match 82%": how well the title fits what the server watched, in four bands; the reason on hover.
    function matchBand(m) { return m >= 70 ? 'high' : m >= 50 ? 'good' : m >= 30 ? 'fair' : 'low'; }
    function matchPill(row) {
        if (row.Match == null) return '';
        return '<span class="ac-match ' + matchBand(row.Match) + '"' + (row.MatchReason ? ' title="' + esc(row.MatchReason) + '"' : '') + '>Match <b>' + row.Match + '%</b></span>';
    }

    // The original language: green in one of your languages, orange in any other, grey once looked up and not known.
    function langPill(row) {
        if (!row.Language) return row.HasDetails ? '<span class="ac-lang unknown" title="Original language not known">Unknown</span>' : '';
        return '<span class="ac-lang ' + (isMine(row.Language) ? 'mine' : 'foreign') + '" title="' + langTitle(row) + '">' + esc(row.LanguageName || row.Language.toUpperCase()) + '</span>';
    }

    function langTitle(row) {
        return esc(row.LanguageName) + ((row.Countries || []).length ? ' · ' + esc(row.Countries.join(', ')) : '');
    }

    function renderTypeChips(view) {
        var rows = visibleRows(view, true);
        var movies = rows.filter(function (r) { return r.Type !== 'show'; }).length;
        var chips = [['', 'list', 'All', rows.length], ['movie', 'movie', 'Movies', movies], ['show', 'tv', 'Shows', rows.length - movies]];
        view.querySelector('#acTypeChips').innerHTML = chips.map(function (c) {
            return '<button type="button" class="ac-filter-chip' + (state.type === c[0] ? ' active' : '') + '" data-type="' + c[0] + '"><i class="md-icon">' + c[1] + '</i>' + c[2] + ' ' + c[3] + '</button>';
        }).join('');
        // Titles Sonarr / Radarr already have (not downloaded yet, or not in the library): shown too when on.
        var inArr = visibleRows(view, false, true).filter(function (r) { return r.Status === 'inArr'; }).length;
        var toggle = view.querySelector('#acInArr');
        toggle.style.display = state.hiddenView ? 'none' : '';
        toggle.classList.toggle('active', state.showInArr);
        toggle.setAttribute('aria-pressed', state.showInArr ? 'true' : 'false');
        toggle.innerHTML = '<i class="md-icon">' + (state.showInArr ? 'check_box' : 'check_box_outline_blank') + '</i>In Sonarr / Radarr ' + inArr;
        view.querySelectorAll('#acView button').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-view') === state.view); });
    }

    function posterHtml(row) {
        var url = state.posters[row.Key];
        return '<div class="ac-poster" data-poster-key="' + esc(row.Key) + '">'
            + (url ? '<img loading="lazy" alt="" src="' + esc(url) + '" />' : '<i class="md-icon">' + (row.Type === 'show' ? 'tv' : 'movie') + '</i>')
            + '</div>';
    }

    // Posters of the rows shown that are not known yet, 100 at a time.
    function loadPosters(view, rows) {
        if (state.noTmdbKey) return;
        var keys = rows.map(function (r) { return r.Key; })
            .filter(function (k) { return !(k in state.posters) && !state.posterAsked[k]; });
        if (!keys.length) return;
        var batch = keys.slice(0, 100);
        batch.forEach(function (k) { state.posterAsked[k] = true; });
        api('ArrCompanion/Posters', 'POST', { Keys: batch }).then(function (res) {
            if (res && !res.HasKey) state.noTmdbKey = true;
            ((res && res.Posters) || []).forEach(function (p) {
                state.posters[p.Key] = p.Url || '';
                if (!p.Url) return;
                view.querySelectorAll('[data-poster-key]').forEach(function (el) {
                    if (el.getAttribute('data-poster-key') === p.Key) el.innerHTML = '<img loading="lazy" alt="" src="' + esc(p.Url) + '" />';
                });
            });
            batch.forEach(function (k) { delete state.posterAsked[k]; if (!(k in state.posters)) state.posters[k] = ''; });
            if (keys.length > batch.length) loadPosters(view, rows);
        }).catch(function () {
            batch.forEach(function (k) { delete state.posterAsked[k]; state.posters[k] = ''; });
        });
    }

    // Emby's own checkbox (emby-checkbox inside a label), as Home Screen Companion uses it.
    function checkbox(attrs, checked, text) {
        return '<label class="checkboxContainer ac-cb"><input type="checkbox" is="emby-checkbox" ' + attrs + (checked ? ' checked' : '') + ' /><span>' + (text || '') + '</span></label>';
    }

    function statusPill(row) {
        var k = kindOf(row.Type);
        if (row.Status === 'ignored') return '<span class="ac-pill neutral">Hidden</span>';
        if (row.Status === 'noServer') return '<span class="ac-pill warn">No ' + k.label + ' set up</span>';
        if (row.AddedBefore && row.Status !== 'inArr') return '<span class="ac-pill neutral" title="Added once by Arr Companion and since removed. Rules leave it alone.">Added before</span>';
        return '';
    }

    function renderMissing(view) {
        renderBanner(view);
        renderTypeChips(view);
        var listEl = view.querySelector('#acMissingList');
        var rows = visibleRows(view);
        var list = view.querySelector('#acFilterList').value;
        if (!rows.length) {
            var none = state.catalog && state.catalog.File && state.catalog.File.Found ? 'Nothing matches.' : 'No titles yet.';
            listEl.innerHTML = '<div class="ac-empty">' + none + '</div>';
        } else {
            var grid = state.view === 'posters';
            var hasRank = rows.some(function (r) { var p = bestPlace(r, list); return p && p.Rank != null; });
            listEl.innerHTML = '<div class="' + (grid ? 'ac-grid-view' : 'ac-list') + (hasRank ? '' : ' ac-norank') + '">' + rows.map(function (r) {
                var selectable = isOpen(r) || r.Status === 'ignored';
                var picked = selectable && state.selected[r.Key];
                var check = selectable ? checkbox('class="acSel" data-key="' + esc(r.Key) + '"', picked) : '';
                var rowAttrs = ' data-key="' + esc(r.Key) + '"';
                var rowCls = (selectable ? ' selectable' : '') + (picked ? ' selected' : '');
                var ignoreBtn = '<button type="button" class="ac-btn small ghost" data-action="ignore" data-key="' + esc(r.Key) + '" title="Hide: not shown as missing, and no rule adds it. Hidden titles are under Show hidden."><i class="md-icon">visibility_off</i></button>';
                var unignoreBtn = '<button type="button" class="ac-btn small ghost" data-action="unignore" data-key="' + esc(r.Key) + '" title="Show it again"><i class="md-icon">visibility</i>' + (grid ? '' : 'Unhide') + '</button>';
                var actions = arrPill(r) + (isOpen(r) ? ignoreBtn : r.Status === 'ignored' ? unignoreBtn : '');
                var dim = r.Status === 'inArr' || r.Status === 'ignored' ? ' dim' : '';
                var best = bestPlace(r, list);
                var rank = best && best.Rank != null ? best.Rank : null;
                if (grid) {
                    var cardMeta = [];
                    if (r.Year) cardMeta.push(r.Year);
                    cardMeta.push(esc((r.Places || []).map(function (p) { return p.List; }).join(', ')));
                    return '<div class="ac-card' + dim + rowCls + '"' + rowAttrs + '>'
                        + '<div class="ac-cover">' + posterHtml(r)
                        + (rank != null ? '<div class="ac-card-rank' + (rank <= 3 ? ' top' : '') + '">#' + rank + '</div>' : '')
                        + (selectable ? '<div class="ac-card-sel">' + checkbox('class="acSel emby-checkbox-notext" data-key="' + esc(r.Key) + '"', picked) + '</div>' : '')
                        + (langPill(r) || r.Match != null ? '<div class="ac-card-foot">' + matchPill(r) + langPill(r) + '</div>' : '')
                        + '</div>'
                        + '<div class="ac-card-title" title="' + esc(r.Title) + '">' + esc(r.Title) + '</div>'
                        + '<div class="ac-card-meta">' + cardMeta.join(' · ') + statusPill(r) + '</div>'
                        + (actions ? '<div class="ac-card-actions">' + actions + '</div>' : '')
                        + '</div>';
                }
                var meta = [];
                if (r.Year) meta.push(r.Year);
                meta.push(r.Type === 'show' ? 'Series' : 'Movie');
                if (r.Imdb) meta.push(imdbLink(r));
                return '<div class="ac-item' + dim + rowCls + '"' + rowAttrs + '>'
                    + '<div class="ac-item-check">' + check + '</div>'
                    + '<div class="ac-rank-col">' + (rank != null ? rank : '') + '</div>'
                    + posterHtml(r)
                    + '<div class="ac-item-info">'
                    + '<div class="ac-item-title">' + esc(r.Title) + '</div>'
                    + '<div class="ac-item-meta"><span>' + meta.join(' · ') + '</span>' + langPill(r) + matchPill(r) + statusPill(r) + '</div>'
                    + placeTags(r, list)
                    + '</div>'
                    + '<div class="ac-item-actions">' + actions + '</div>'
                    + '</div>';
            }).join('') + '</div>';
            loadPosters(view, rows);
        }
        updateBulk(view, rows);
    }

    // Not in Sonarr / Radarr yet: can be picked, ignored and added (an add without its server says so).
    function isOpen(r) { return r.Status === 'missing' || r.Status === 'noServer'; }

    function selectedKeys() { return Object.keys(state.selected).filter(function (k) { return state.selected[k]; }); }

    function updateBulk(view, rows) {
        rows = rows || visibleRows(view);
        var selectable = rows.filter(function (r) { return isOpen(r) || r.Status === 'ignored'; });
        var keys = selectedKeys();
        var rowByKey = {};
        ((state.catalog && state.catalog.Rows) || []).forEach(function (r) { rowByKey[r.Key] = r; });
        var addable = keys.filter(function (k) { return rowByKey[k] && isOpen(rowByKey[k]); });
        var viewingHidden = state.hiddenView;
        var hideable = viewingHidden ? keys.filter(function (k) { return rowByKey[k] && rowByKey[k].Status === 'ignored'; }) : addable;
        view.querySelector('#acCount').textContent = rows.length + ' shown' + (keys.length ? ' · ' + keys.length + ' selected' : '');
        var all = view.querySelector('#acSelectAll');
        all.checked = selectable.length > 0 && selectable.every(function (r) { return state.selected[r.Key]; });
        all.disabled = selectable.length === 0;
        var add = view.querySelector('#acAddSelected');
        add.disabled = addable.length === 0;
        add.innerHTML = '<i class="md-icon">add</i>Add ' + (addable.length ? addable.length + ' ' : '') + 'selected';
        var hideBtn = view.querySelector('#acIgnoreSelected');
        hideBtn.disabled = hideable.length === 0;
        hideBtn.innerHTML = viewingHidden ? '<i class="md-icon">visibility</i>Unhide' : '<i class="md-icon">visibility_off</i>Hide';
        var hidden = ((state.catalog && state.catalog.Rows) || []).filter(function (r) { return r.Status === 'ignored'; }).length;
        var showHidden = view.querySelector('#acShowHidden');
        showHidden.style.display = viewingHidden || hidden ? '' : 'none';
        showHidden.innerHTML = viewingHidden ? '<i class="md-icon">arrow_back</i>Back to missing' : '<i class="md-icon">visibility</i>Show hidden (' + hidden + ')';
    }

    function ignore(view, keys, on) {
        if (!keys.length) return Promise.resolve();
        return api('ArrCompanion/Ignore', 'POST', { Keys: keys, Ignore: on }).then(function () {
            keys.forEach(function (k) { delete state.selected[k]; });
            toast((on ? 'Hidden: ' : 'Shown again: ') + keys.length + (keys.length === 1 ? ' title' : ' titles'));
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
            + checkbox('data-f="Monitored"', instance.Monitored !== false, 'Monitored')
            + checkbox('data-f="Search"', instance.SearchOnAdd !== false, 'Search when added');
    }

    function openAddDialog(view, keys) {
        var rowByKey = {};
        ((state.catalog && state.catalog.Rows) || []).forEach(function (r) { rowByKey[r.Key] = r; });
        var rows = keys.map(function (k) { return rowByKey[k]; }).filter(function (r) { return r && isOpen(r); });
        if (!rows.length) return;
        var kinds = KINDS.filter(function (k) { return rows.some(function (r) { return r.Type === k.type; }); });

        var backdrop = document.createElement('div');
        backdrop.className = 'ac-modal-backdrop';
        backdrop.setAttribute('data-ac-theme', theme);
        var title = rows.length === 1 ? esc(rows[0].Title) + (rows[0].Year ? ' (' + rows[0].Year + ')' : '') : 'Add ' + rows.length + ' titles';
        var sub = rows.length === 1
            ? placeLines(rows[0], '', 0)
            : kinds.map(function (k) { var n = rows.filter(function (r) { return r.Type === k.type; }).length; return n + (k.type === 'show' ? (n === 1 ? ' show' : ' shows') : (n === 1 ? ' movie' : ' movies')); }).join(', ');
        backdrop.innerHTML = '<div class="ac-modal" role="dialog" aria-modal="true">'
            + '<h3>' + title + '</h3>'
            + '<div style="margin-bottom:12px;">' + sub + '</div>'
            + kinds.map(function (k) {
                var instance = instanceOf(k.kind);
                return '<div class="ac-kind" data-kind="' + k.kind + '">'
                    + '<div class="ac-eyebrow">' + esc(instanceLabel(instance, k)) + '</div>'
                    + (isSetUp(instance) ? choiceFields(k, instance)
                        : '<div class="ac-banner warn"><b>' + k.label + ' is not set up yet.</b> These ' + (k.type === 'show' ? 'shows' : 'movies')
                            + ' cannot be sent until it is: add its URL and API key under Settings.'
                            + '<div style="margin-top:8px;"><button type="button" class="ac-btn small ac-to-settings"><i class="md-icon">settings</i>Open Settings</button></div></div>')
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
        backdrop.querySelectorAll('.ac-to-settings').forEach(function (b) {
            b.addEventListener('click', function () { close(); showTab(view, 'settings'); });
        });

        kinds.forEach(function (k) {
            var instance = instanceOf(k.kind);
            if (!isSetUp(instance)) return;
            var box = backdrop.querySelector('.ac-kind[data-kind="' + k.kind + '"]');
            loadOptions(instance).then(function (res) {
                if (!res || !res.Success) {
                    box.insertAdjacentHTML('beforeend', '<div class="ac-banner error">' + esc((res && res.Message) || 'Could not read the options.') + '</div>');
                }
                // The root folder last picked here for this server, else the one under Settings.
                var last = instance.LastRootFolderPath || '';
                var known = ((res && res.RootFolders) || []).some(function (f) { return f.Path === last; });
                box.querySelector('[data-f="RootFolderPath"]').innerHTML = folderOptions(res, known ? last : instance.RootFolderPath || '');
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
                    var cls = x.Success ? 'ok' : x.AlreadyIn ? 'info' : x.NotSent ? 'warn' : 'err';
                    var label = x.Success ? 'Added' : x.AlreadyIn ? 'Already there' : x.NotSent ? 'Not sent' : 'Failed';
                    return '<div style="margin:4px 0;"><span class="ac-pill ' + cls + '">' + label + '</span> ' + esc(x.Title || x.Key)
                        + (x.Success ? '' : ' <span class="ac-muted">' + esc(x.Message) + '</span>') + '</div>';
                }).join('');
                results.forEach(function (x) { if (x.Success || x.AlreadyIn) delete state.selected[x.Key]; });
                // The server keeps the root folder an add used; the next dialog starts on it.
                kinds.forEach(function (k) {
                    var choice = body[k.kind === 'radarr' ? 'Radarr' : 'Sonarr'];
                    var added = results.some(function (x) { return x.Success && rowByKey[x.Key] && rowByKey[x.Key].Type === k.type; });
                    var instance = choice && instances().filter(function (i) { return i.Id === choice.InstanceId; })[0];
                    if (added && instance && choice.RootFolderPath) instance.LastRootFolderPath = choice.RootFolderPath;
                });
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
        var names = langNames();
        function langs(codes) { return codes.map(function (c) { return names[c] || c.toUpperCase(); }).join(', '); }
        return (r.TopN > 0 ? 'Top ' + r.TopN + ' ' + what + ' of ' + where : 'All ' + what + ' on ' + where)
            + (r.MinYear > 0 ? ' from ' + r.MinYear : '')
            + ((r.Languages || []).length ? ' in ' + langs(r.Languages) : '')
            + ((r.ExcludeLanguages || []).length ? ' not in ' + langs(r.ExcludeLanguages) : '')
            + (r.MyLanguagesMode === 'only' ? ' in your languages' : r.MyLanguagesMode === 'not' ? ' not in your languages' : '')
            + (r.MinMatch > 0 ? ', match at least ' + r.MinMatch + '%' : '')
            + (r.MaxPerRun > 0 ? ', at most ' + r.MaxPerRun + ' per run' : '');
    }

    // A rule's languages: chips, and a pick list of the titles' languages to add one.
    function langField(label, name, codes) {
        var names = langNames();
        codes = codes || [];
        var left = Object.keys(names).filter(function (c) { return codes.indexOf(c) < 0; })
            .sort(function (a, b) { return names[a].localeCompare(names[b]); });
        return '<div class="ac-field"><span>' + label + '</span><div class="ac-langs" data-r-langs="' + name + '">'
            + codes.map(function (c) {
                return '<span class="ac-lang-chip" data-code="' + esc(c) + '">' + esc(names[c] || c.toUpperCase())
                    + '<button type="button" data-lang-remove title="Remove"><i class="md-icon">close</i></button></span>';
            }).join('')
            + '<select class="ac-select ac-lang-add"' + (left.length ? '' : ' disabled') + '>' + option('', codes.length ? 'Add…' : 'Any', true)
            + left.map(function (c) { return option(c, names[c], false); }).join('') + '</select>'
            + '</div></div>';
    }

    function ruleCard(r) {
        var lists = ((state.catalog && state.catalog.Lists) || []).slice();
        if (r.List && lists.indexOf(r.List) < 0) lists.push(r.List);
        return '<div class="ac-rule' + (r.Enabled ? '' : ' off') + '" data-rule="' + esc(r.Id) + '">'
            + '<div class="ac-rule-head">'
            + checkbox('data-r="Enabled" title="On"', r.Enabled, '')
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
            + '<div class="ac-rule-grid ac-rule-grid-wide">'
            + langField('Only these languages', 'Languages', r.Languages)
            + langField('Not these languages', 'ExcludeLanguages', r.ExcludeLanguages)
            + '<label class="ac-field"><span>Your languages</span><select class="ac-select" data-r="MyLanguagesMode">'
                + option('', 'Any', !r.MyLanguagesMode) + option('only', 'Only your languages', r.MyLanguagesMode === 'only') + option('not', 'Not your languages', r.MyLanguagesMode === 'not') + '</select></label>'
            + '<label class="ac-field"><span>Match at least % (0 = any)</span><input type="number" min="0" max="100" class="ac-input" data-r="MinMatch" value="' + (r.MinMatch || 0) + '" /></label>'
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
            r.MinMatch = Math.min(100, int(v('MinMatch').value));
            r.MyLanguagesMode = v('MyLanguagesMode').value;
            ['Languages', 'ExcludeLanguages'].forEach(function (name) {
                r[name] = Array.prototype.map.call(card.querySelectorAll('[data-r-langs="' + name + '"] .ac-lang-chip'), function (c) { return c.getAttribute('data-code'); });
            });
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
                    + ' <span class="ac-muted">' + (p.LanguageName ? esc(p.LanguageName) + ' · ' : '') + (p.Match != null ? 'Match ' + p.Match + '% · ' : '')
                    + (p.Rank != null ? '#' + p.Rank + ' on ' : 'on ') + esc(p.List) + ' → ' + esc(p.InstanceName)
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

    // Emby's own checkbox as a Home Screen Companion settings row.
    function settingsCheckbox(attrs, checked, text) {
        return '<div class="checkboxContainer"><label><input type="checkbox" is="emby-checkbox" ' + attrs + (checked ? ' checked' : '') + ' /><span>' + text + '</span></label></div>';
    }

    // Settings panels: open or closed as the user left them, else as given.
    function foldOpen(key, dflt) { return key in state.folds ? !!state.folds[key] : dflt; }
    function setFold(header, open) {
        header.nextElementSibling.style.display = open ? 'block' : 'none';
        var chevron = header.querySelector('.ac-chevron');
        if (chevron) chevron.style.transform = open ? 'rotate(180deg)' : '';
    }

    // One server, a panel of its own inside Servers (Home Screen Companion's provider panels).
    function serverPanel(k) {
        var saved = instanceOf(k.kind);
        var i = saved || { Kind: k.kind, Monitored: true, SearchOnAdd: true, SeasonFolder: true, MinimumAvailability: 'released', SeriesMonitor: 'all' };
        var open = foldOpen('server-' + k.kind, !isSetUp(saved));
        function select(label, name, html) {
            return '<div class="inputContainer"><label class="selectLabel">' + label + '</label><select is="emby-select" data-s="' + name + '" style="width:100%;">' + html + '</select></div>';
        }
        var extra = k.kind === 'radarr'
            ? select('Minimum availability', 'MinimumAvailability', MIN_AVAILABILITY.map(function (o) { return option(o[0], o[1], o[0] === (i.MinimumAvailability || 'released')); }).join(''))
            : select('Monitor', 'SeriesMonitor', SERIES_MONITOR.map(function (o) { return option(o[0], o[1], o[0] === (i.SeriesMonitor || 'all')); }).join(''));
        return '<div class="ac-section" data-server="' + k.kind + '">'
            + '<div class="ac-section-header" data-fold="server-' + k.kind + '">'
            + '<i class="md-icon ac-section-icon">cloud_download</i>'
            + '<span class="ac-section-title">' + k.label + '</span>'
            + '<i class="md-icon ac-chevron"' + (open ? ' style="transform: rotate(180deg);"' : '') + '>expand_more</i>'
            + '</div>'
            + '<div class="ac-section-body" style="display:' + (open ? 'block' : 'none') + ';">'
            + '<div class="ac-s-row">'
            + '<div class="inputContainer"><input is="emby-input" type="text" data-s="Url" label="' + k.label + ' Server URL" placeholder="http://localhost:' + k.port + '" autocomplete="off" spellcheck="false" value="' + esc(i.Url || '') + '" />'
            + '<div class="ac-s-test"><button type="button" is="emby-button" class="raised ac-btn-neutral" data-server-action="test"><i class="md-icon" style="margin-right:5px;">wifi_tethering</i><span>Test Connection</span></button><span class="ac-muted" data-test-result></span></div></div>'
            + '<div class="inputContainer"><input is="emby-input" type="text" class="ac-key" data-s="ApiKey" label="' + k.label + ' API Key" autocomplete="off" spellcheck="false" data-lpignore="true" data-1p-ignore value="' + esc(i.ApiKey || '') + '" />'
            + '<label class="ac-show-key"><input type="checkbox" data-show-key /> Show</label>'
            + '<div class="fieldDescription">In ' + k.label + ' under Settings › General. It stays on the Emby server.</div></div>'
            + '</div>'
            + '<div class="ac-eyebrow" style="margin: 4px 0 8px;">Defaults for new ' + (k.type === 'show' ? 'shows' : 'movies') + ' (rules always use these)</div>'
            + '<div class="ac-s-row">'
            + select('Root folder', 'RootFolderPath', folderOptions(null, i.RootFolderPath || ''))
            + select('Quality profile', 'QualityProfileId', profileOptions(null, i.QualityProfileId || 0))
            + '</div>'
            + '<div class="ac-s-row">'
            + extra
            + '<div class="ac-s-checks">'
            + settingsCheckbox('data-s="Monitored"', i.Monitored !== false, 'Monitored')
            + settingsCheckbox('data-s="SearchOnAdd"', i.SearchOnAdd !== false, 'Search when added')
            + (k.kind === 'sonarr' ? settingsCheckbox('data-s="SeasonFolder"', i.SeasonFolder !== false, 'Season folders') : '')
            + '</div>'
            + '</div>'
            + '<div class="ac-banner error" data-options-error style="display:none;"></div>'
            + '</div>'
            + '</div>';
    }

    // The missing titles file's panels: open when the file is not there (known once the list is read).
    function fileFolds(view) {
        var noFile = !!(state.catalog && state.catalog.File && !state.catalog.File.Found);
        ['other', 'file'].forEach(function (key) {
            setFold(view.querySelector('[data-fold="' + key + '"]'), foldOpen(key, noFile));
        });
    }

    function renderServers(view) {
        if (!state.config) return;
        // Sonarr first, as the panel's label has them.
        view.querySelector('#acServers').innerHTML = [KINDS[1], KINDS[0]].map(serverPanel).join('');
        // Servers is open; the file's panels open when the file is not there.
        [['servers', true], ['posters', false], ['languages', false]].forEach(function (f) {
            setFold(view.querySelector('[data-fold="' + f[0] + '"]'), foldOpen(f[0], f[1]));
        });
        fileFolds(view);
        view.querySelector('#acListPath').value = state.config.MissingListPath || '';
        view.querySelector('#acTmdbKey').value = state.config.TmdbApiKey || '';
        renderMyLangs(view, myLangs());
        if (!state.languages) loadLanguages().then(function () { if (view._acSettingsSaved !== undefined) renderMyLangs(view, readMyLangs(view)); });
        var file = state.catalog && state.catalog.File;
        view.querySelector('#acListPathHint').innerHTML = 'Empty: the file Home Screen Companion keeps (Missing titles, "Keep the list in a file", JSON).'
            + (file && file.Path ? '<br />Now reading: <code>' + esc(file.Path) + '</code>' + (file.Found ? '' : ' (not there yet)') : '');
        KINDS.forEach(function (k) {
            var i = instanceOf(k.kind);
            if (isSetUp(i)) refreshServerOptions(view, k, false);
        });
        view._acSettingsSaved = settingsState(view);
        checkSettings(view);
    }

    // Save Settings is on only when a setting differs from what was loaded or saved (as in Home Screen Companion).
    function settingsState(view) {
        var tab = view.querySelector('[data-tab-content="settings"]');
        return JSON.stringify(Array.prototype.map.call(tab.querySelectorAll('[data-s], #acListPath, #acTmdbKey'), function (el) {
            var server = el.closest('[data-server]');
            return (server ? server.getAttribute('data-server') + '.' : '') + (el.getAttribute('data-s') || el.id) + '=' + (el.type === 'checkbox' ? el.checked : el.value.trim());
        }).concat(['MyLanguages=' + readMyLangs(view).join(',')]));
    }
    function checkSettings(view) {
        var btn = view.querySelector('#acSaveSettings');
        var dirty = view._acSettingsSaved !== undefined && settingsState(view) !== view._acSettingsSaved;
        btn.disabled = !dirty;
        btn.style.opacity = dirty ? '1' : '0.5';
    }

    // Your languages under Settings: chips (the last one stays), and a pick list of the others.
    function loadLanguages() {
        return api('ArrCompanion/Languages').then(function (list) { state.languages = list || []; }, function () { state.languages = []; });
    }
    function readMyLangs(view) {
        return Array.prototype.map.call(view.querySelectorAll('#acMyLangs .ac-lang-chip'), function (c) { return c.getAttribute('data-code'); });
    }
    function renderMyLangs(view, codes) {
        var names = langNames(true);
        var left = Object.keys(names).filter(function (c) { return codes.indexOf(c) < 0; })
            .sort(function (a, b) { return names[a].localeCompare(names[b]); });
        view.querySelector('#acMyLangs').innerHTML = codes.map(function (c) {
            return '<span class="ac-lang-chip" data-code="' + esc(c) + '">' + esc(names[c] || c.toUpperCase())
                + (codes.length > 1 ? '<button type="button" data-mylang-remove title="Remove"><i class="md-icon">close</i></button>' : '') + '</span>';
        }).join('')
            + '<select class="ac-select" id="acMyLangAdd"' + (left.length ? '' : ' disabled') + '>' + option('', 'Add…', true)
            + left.map(function (c) { return option(c, names[c], false); }).join('') + '</select>';
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
        var tmdbKey = view.querySelector('#acTmdbKey').value.trim();
        var myLanguages = readMyLangs(view);
        var btn = view.querySelector('#acSaveSettings');
        var saving = settingsState(view);
        btn.disabled = true;
        btn.style.opacity = '0.5';
        saveConfig(function (server) {
            // The page's instances replace theirs; any others (more of a kind, later) are kept.
            var shown = {};
            panels.forEach(function (p) { shown[p.Id] = p; });
            var kept = (server.Instances || []).filter(function (i) { return !shown[i.Id]; });
            server.Instances = panels.filter(function (p) { return p.Url || p.ApiKey || instances().some(function (i) { return i.Id === p.Id; }); }).concat(kept);
            server.MissingListPath = listPath;
            server.TmdbApiKey = tmdbKey;
            server.MyLanguages = myLanguages;
        }).then(function () {
            toast('Settings saved');
            view._acSettingsSaved = saving;
            checkSettings(view);
            state.posters = {};
            state.noTmdbKey = false;
            return loadMissing(view, true);
        }, function (err) {
            toast('Could not save: ' + err.message);
            checkSettings(view);
        });
    }

    // ---- Wiring ----

    function bind(view) {
        view.querySelector('#acTabs').addEventListener('click', function (e) {
            var b = e.target.closest('.ac-tab');
            if (b) showTab(view, b.getAttribute('data-tab'));
        });

        // Missing
        ['#acSearch', '#acFilterList', '#acFilterLang', '#acFilterTop', '#acSort'].forEach(function (sel) {
            view.querySelector(sel).addEventListener(sel === '#acSearch' ? 'input' : 'change', function () { renderMissing(view); });
        });
        view.querySelector('#acTypeChips').addEventListener('click', function (e) {
            var chip = e.target.closest('[data-type]');
            if (!chip) return;
            state.type = chip.getAttribute('data-type');
            renderMissing(view);
        });
        view.querySelector('#acInArr').addEventListener('click', function () {
            state.showInArr = !state.showInArr;
            storeShowInArr(state.showInArr);
            renderMissing(view);
        });
        view.querySelector('#acView').addEventListener('click', function (e) {
            var b = e.target.closest('[data-view]');
            if (!b) return;
            state.view = b.getAttribute('data-view');
            storeView(state.view);
            renderMissing(view);
        });
        view.querySelector('#acRefresh').addEventListener('click', function () { loadMissing(view, true, true); });
        function pick(key, on) {
            if (on) state.selected[key] = true; else delete state.selected[key];
            view.querySelectorAll('#acMissingList [data-key].selectable').forEach(function (row) {
                if (row.getAttribute('data-key') !== key) return;
                row.classList.toggle('selected', on);
                var cb = row.querySelector('.acSel');
                if (cb && cb.checked !== on) cb.checked = on;
            });
            updateBulk(view);
        }
        view.querySelector('#acMissingList').addEventListener('change', function (e) {
            if (!e.target.classList.contains('acSel')) return;
            pick(e.target.getAttribute('data-key'), e.target.checked);
        });
        view.querySelector('#acMissingList').addEventListener('click', function (e) {
            var b = e.target.closest('[data-action]');
            if (!b) {
                // The checkbox's own label toggles it (and fires change); a button or link does its own thing.
                if (e.target.closest('label, button, a, input, select')) return;
                if (window.getSelection && String(window.getSelection()).length) return;   // selecting text
                var row = e.target.closest('.selectable[data-key]');
                if (row) pick(row.getAttribute('data-key'), !state.selected[row.getAttribute('data-key')]);
                return;
            }
            var key = b.getAttribute('data-key'), action = b.getAttribute('data-action');
            if (action === 'add') openAddDialog(view, [key]);
            else if (action === 'ignore') ignore(view, [key], true);
            else if (action === 'unignore') ignore(view, [key], false);
        });
        view.querySelector('#acSelectAll').addEventListener('change', function (e) {
            visibleRows(view).forEach(function (r) {
                if (!isOpen(r) && r.Status !== 'ignored') return;
                if (e.target.checked) state.selected[r.Key] = true; else delete state.selected[r.Key];
            });
            renderMissing(view);
        });
        view.querySelector('#acAddSelected').addEventListener('click', function () { openAddDialog(view, selectedKeys()); });
        view.querySelector('#acIgnoreSelected').addEventListener('click', function () {
            var rowByKey = {};
            ((state.catalog && state.catalog.Rows) || []).forEach(function (r) { rowByKey[r.Key] = r; });
            var unhide = state.hiddenView;
            ignore(view, selectedKeys().filter(function (k) { return rowByKey[k] && (unhide ? rowByKey[k].Status === 'ignored' : isOpen(rowByKey[k])); }), !unhide);
        });
        view.querySelector('#acShowHidden').addEventListener('click', function () {
            state.hiddenView = !state.hiddenView;
            state.selected = {};
            renderMissing(view);
        });

        // Rules
        view.querySelector('#acAddRule').addEventListener('click', function () {
            readRules(view);
            state.config.Rules.push({ Id: guid(), Name: '', Enabled: true, List: '', MediaType: '', TopN: 3, MinYear: 0, MaxPerRun: 0, InstanceId: '', Languages: [], ExcludeLanguages: [], MinMatch: 0, MyLanguagesMode: '' });
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
        // A rule's languages: the card is drawn again with the language added or removed.
        function redrawRule(card) {
            readRules(view);
            var r = state.config.Rules.filter(function (x) { return x.Id === card.getAttribute('data-rule'); })[0];
            if (!r) return;
            var preview = card.querySelector('[data-preview]').innerHTML;
            card.outerHTML = ruleCard(r);
            var fresh = view.querySelector('.ac-rule[data-rule="' + r.Id + '"]');
            if (fresh) fresh.querySelector('[data-preview]').innerHTML = preview;
        }
        view.querySelector('#acRules').addEventListener('change', function (e) {
            if (e.target.classList.contains('ac-lang-add')) {
                var code = e.target.value, box = e.target.closest('[data-r-langs]');
                if (!code) return;
                box.insertAdjacentHTML('afterbegin', '<span class="ac-lang-chip" data-code="' + esc(code) + '"></span>');
                redrawRule(e.target.closest('.ac-rule'));
                return;
            }
            e.target.dispatchEvent(new Event('input', { bubbles: true }));
        });
        view.querySelector('#acRules').addEventListener('click', function (e) {
            var rm = e.target.closest('[data-lang-remove]');
            if (rm) {
                var card = rm.closest('.ac-rule');
                rm.closest('.ac-lang-chip').remove();
                redrawRule(card);
                return;
            }
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

        // Help
        var help = view.querySelector('#acHelpOverlay');
        function closeHelp() { help.classList.remove('ac-visible'); }
        view.querySelector('#acOpenHelp').addEventListener('click', function () { help.classList.add('ac-visible'); });
        help.addEventListener('click', function (e) {
            if (e.target === help || e.target.closest('[data-help-close]')) closeHelp();
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && help.classList.contains('ac-visible')) closeHelp();
        });

        // History
        view.querySelector('#acHistoryRefresh').addEventListener('click', function () { loadHistory(view); });

        // Settings
        view.querySelector('#acServers').addEventListener('click', function (e) {
            var b = e.target.closest('[data-server-action="test"]');
            if (!b) return;
            var kind = b.closest('[data-server]').getAttribute('data-server');
            testServer(view, KINDS.filter(function (k) { return k.kind === kind; })[0]);
        });
        view.querySelector('#acSaveSettings').addEventListener('click', function () { if (!this.disabled) saveSettings(view); });
        view.querySelector('#acMyLangs').addEventListener('change', function (e) {
            if (e.target.id !== 'acMyLangAdd' || !e.target.value) return;
            e.stopPropagation();
            renderMyLangs(view, readMyLangs(view).concat([e.target.value]));
            checkSettings(view);
        });
        view.querySelector('#acMyLangs').addEventListener('click', function (e) {
            var rm = e.target.closest('[data-mylang-remove]');
            if (!rm) return;
            var code = rm.closest('.ac-lang-chip').getAttribute('data-code');
            renderMyLangs(view, readMyLangs(view).filter(function (c) { return c !== code; }));
            checkSettings(view);
        });
        ['input', 'change'].forEach(function (type) {
            view.querySelector('[data-tab-content="settings"]').addEventListener(type, function (e) {
                if (!e.target.hasAttribute('data-show-key')) checkSettings(view);
            });
        });
        // Settings panels fold open and closed (Home Screen Companion's settings-panel-toggle).
        view.querySelector('[data-tab-content="settings"]').addEventListener('click', function (e) {
            var header = e.target.closest('[data-fold]');
            if (!header) return;
            var open = header.nextElementSibling.style.display === 'none';
            setFold(header, open);
            state.folds[header.getAttribute('data-fold')] = open;
        });
        view.querySelector('[data-tab-content="settings"]').addEventListener('change', function (e) {
            if (!e.target.hasAttribute('data-show-key')) return;
            var input = e.target.closest('.inputContainer').querySelector('.ac-key');
            if (input) input.classList.toggle('shown', e.target.checked);
        });
    }

    return function (view) {
        view.addEventListener('viewshow', function () {
            applyTheme(view);
            // Emby can set its theme and drawer just after viewshow on a fresh load: check again once painted.
            setTimeout(function () { applyTheme(view); }, 500);
            loadFooter(view);
            state.view = storedView() === 'posters' ? 'posters' : 'list';
            state.showInArr = storedShowInArr();
            if (!view.dataset.acInit) {
                view.dataset.acInit = '1';
                bind(view);
                window.addEventListener('resize', function () { if (view.isConnected && view.offsetParent !== null) applyTheme(view); });
            }
            loadConfig().then(function () {
                return loadMissing(view, true);
            }).then(function () {
                var active = view.querySelector('.ac-tab.active');
                if (active && active.getAttribute('data-tab') !== 'missing') showTab(view, active.getAttribute('data-tab'));
            });
        });
    };
});
