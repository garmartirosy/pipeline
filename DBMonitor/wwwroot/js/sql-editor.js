/* SQL editor — vanilla JS. Requires result-grid.js loaded first. */
(function () {
    'use strict';

    var ctx       = window.__sqlCtx;
    var csrfToken = document.querySelector('input[name="__RequestVerificationToken"]').value;

    // ── DOM refs ──────────────────────────────────────────────────────────────

    var elEditor      = document.getElementById('sql-editor');
    var elBtnRun      = document.getElementById('btn-run');
    var elBtnSave     = document.getElementById('btn-save-query');
    var elTimeout     = document.getElementById('txt-timeout');
    var elMaxRows     = document.getElementById('sel-maxrows');
    var elDestructive = document.getElementById('chk-destructive');
    var elError       = document.getElementById('exec-error');
    var elInfo        = document.getElementById('exec-info');
    var elExecBadge   = document.getElementById('exec-badge');
    var elRowsBadge   = document.getElementById('rows-badge');
    var elResultsArea = document.getElementById('results-area');
    var elResultTabs  = document.getElementById('result-tabs');
    var elResultPanes = document.getElementById('result-panels');
    var elHistoryList = document.getElementById('history-list');
    var elSavedList   = document.getElementById('saved-list');
    var elSchemaSide  = document.getElementById('schema-sidebar');
    var elSchemaSearch= document.getElementById('schema-search');
    var elBtnClear    = document.getElementById('btn-clear-history');
    var elTabHistory  = document.getElementById('tab-history');
    var elTabSaved    = document.getElementById('tab-saved');

    // Save modal
    var saveModal    = new bootstrap.Modal(document.getElementById('save-modal'));
    var elSaveName   = document.getElementById('save-name');
    var elSaveDesc   = document.getElementById('save-desc');
    var elSaveError  = document.getElementById('save-error');
    var elBtnConfirm = document.getElementById('btn-save-confirm');

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    // Pre-fill from ?sql= querystring (e.g. from the schema browser "Open as SQL" link)
    // TempData pre-fill is handled server-side in the textarea value.
    var qsSql = new URLSearchParams(window.location.search).get('sql');
    if (qsSql && !elEditor.value) elEditor.value = qsSql;

    loadHistory();
    loadSaved();
    loadSchema();

    elBtnRun.addEventListener('click', runQuery);
    elBtnSave.addEventListener('click', function () {
        elSaveName.value  = '';
        elSaveDesc.value  = '';
        elSaveError.classList.add('d-none');
        document.getElementById('scope-profile').checked = true;
        saveModal.show();
        setTimeout(function () { elSaveName.focus(); }, 300);
    });

    elBtnConfirm.addEventListener('click', saveQuery);

    elEditor.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
            e.preventDefault();
            runQuery();
        }
    });

    elSchemaSearch.addEventListener('input', filterSchema);

    elBtnClear.addEventListener('click', function () {
        elHistoryList.innerHTML = '<div class="text-center text-muted py-3 small">Cleared.</div>';
    });

    elTabHistory.addEventListener('click', function () {
        elTabHistory.classList.add('active');
        elTabSaved.classList.remove('active');
        elHistoryList.classList.remove('d-none');
        elSavedList.classList.add('d-none');
        elBtnClear.style.display = '';
    });

    elTabSaved.addEventListener('click', function () {
        elTabSaved.classList.add('active');
        elTabHistory.classList.remove('active');
        elSavedList.classList.remove('d-none');
        elHistoryList.classList.add('d-none');
        elBtnClear.style.display = 'none';
        loadSaved();
    });

    // ── Execute ───────────────────────────────────────────────────────────────

    function runQuery() {
        var sql = elEditor.value.trim();
        if (!sql) { showError('Enter a SQL statement first.'); return; }

        setRunning(true);
        clearResults();

        var body = {
            sql:              sql,
            timeoutSeconds:   parseInt(elTimeout.value, 10) || 30,
            maxRows:          parseInt(elMaxRows.value, 10)  || 1000,
            allowDestructive: elDestructive.checked,
        };

        fetch(ctx.executeUrl, {
            method:  'POST',
            headers: {
                'Content-Type':             'application/json',
                'RequestVerificationToken': csrfToken,
            },
            body: JSON.stringify(body),
        })
        .then(assertOk)
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (data.error) { showError(data.error); return; }
            renderResults(data);
            loadHistory();
        })
        .catch(function (err) { showError(err.message); })
        .finally(function () { setRunning(false); });
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    function renderResults(data) {
        hideError();
        elInfo.classList.add('d-none');

        elExecBadge.textContent = data.elapsedMs + ' ms';
        elExecBadge.classList.remove('d-none');

        var totalRows = (data.resultSets || []).reduce(function (n, rs) { return n + rs.rowCount; }, 0);
        elRowsBadge.textContent = totalRows.toLocaleString() + ' row' + (totalRows !== 1 ? 's' : '');
        elRowsBadge.classList.remove('d-none');

        if (data.rolledBack) {
            elInfo.textContent = data.message || 'Rolled back.';
            elInfo.classList.remove('d-none');
        } else if (data.message) {
            elInfo.textContent = data.message;
            elInfo.classList.remove('d-none');
        }

        var sets = data.resultSets || [];
        if (!sets.length) {
            var noResults = 'No result sets returned. Records affected: ' + data.recordsAffected + '.';
            elInfo.textContent = (elInfo.classList.contains('d-none') ? '' : elInfo.textContent + '  ') + noResults;
            elInfo.classList.remove('d-none');
            return;
        }

        elResultsArea.classList.remove('d-none');
        window.ResultGrid.render(elResultTabs, elResultPanes, sets);
    }

    // ── History ───────────────────────────────────────────────────────────────

    function loadHistory() {
        fetch(ctx.historyUrl)
            .then(assertOk)
            .then(function (r) { return r.json(); })
            .then(renderHistory)
            .catch(function () {
                elHistoryList.innerHTML = '<div class="text-muted small p-2">Failed to load history.</div>';
            });
    }

    function renderHistory(entries) {
        if (!entries.length) {
            elHistoryList.innerHTML = '<div class="text-center text-muted py-3 small">No history.</div>';
            return;
        }
        elHistoryList.innerHTML = entries.map(function (e) {
            var ts      = new Date(e.executedUtc + 'Z').toLocaleString();
            var ok      = e.success && !e.rolledBack;
            var icon    = ok ? '✓' : (e.rolledBack ? '⟲' : '✗');
            var cls     = ok ? 'text-success' : (e.rolledBack ? 'text-warning' : 'text-danger');
            var preview = (e.sql || '').replace(/\s+/g, ' ').trim();
            if (preview.length > 80) preview = preview.slice(0, 80) + '…';
            return '<div class="history-entry border-bottom p-2" style="cursor:pointer" data-sql="' + esc(e.sql || '') + '">' +
                   '<div class="d-flex justify-content-between">' +
                   '<span class="' + cls + ' fw-semibold small">' + icon + '</span>' +
                   '<span class="text-muted" style="font-size:0.7rem">' + esc(ts) + '</span>' +
                   '</div>' +
                   '<div class="small text-truncate font-monospace mt-1" title="' + esc(e.sql || '') + '">' + esc(preview) + '</div>' +
                   '<div class="text-muted" style="font-size:0.7rem">' + e.elapsedMs + ' ms</div>' +
                   '</div>';
        }).join('');

        elHistoryList.querySelectorAll('.history-entry').forEach(function (div) {
            div.addEventListener('click', function () {
                elEditor.value = div.dataset.sql;
                elEditor.focus();
            });
        });
    }

    // ── Saved queries ─────────────────────────────────────────────────────────

    function loadSaved() {
        fetch(ctx.savedListUrl)
            .then(assertOk)
            .then(function (r) { return r.json(); })
            .then(renderSaved)
            .catch(function () {
                elSavedList.innerHTML = '<div class="text-muted small p-2">Failed to load saved queries.</div>';
            });
    }

    function renderSaved(entries) {
        if (!entries.length) {
            elSavedList.innerHTML = '<div class="text-center text-muted py-3 small">No saved queries.<br>Click &#128190; Save query to add one.</div>';
            return;
        }
        elSavedList.innerHTML = entries.map(function (e) {
            var scope = e.profileId ? 'profile' : 'global';
            var preview = (e.sql || '').replace(/\s+/g, ' ').trim();
            if (preview.length > 80) preview = preview.slice(0, 80) + '…';
            return '<div class="saved-entry border-bottom p-2" style="cursor:pointer" data-id="' + esc(e.id) + '" data-sql="' + esc(e.sql || '') + '">' +
                   '<div class="d-flex justify-content-between">' +
                   '<span class="fw-semibold small text-truncate">' + esc(e.name) + '</span>' +
                   '<span class="badge bg-light text-muted border" style="font-size:0.65rem">' + esc(scope) + '</span>' +
                   '</div>' +
                   (e.description ? '<div class="text-muted" style="font-size:0.7rem">' + esc(e.description) + '</div>' : '') +
                   '<div class="small text-truncate font-monospace mt-1 text-muted" title="' + esc(e.sql || '') + '">' + esc(preview) + '</div>' +
                   '</div>';
        }).join('');

        elSavedList.querySelectorAll('.saved-entry').forEach(function (div) {
            div.addEventListener('click', function () {
                elEditor.value = div.dataset.sql;
                elEditor.focus();
                var id = div.dataset.id;
                fetch(ctx.markUsedUrl, {
                    method:  'POST',
                    headers: {
                        'Content-Type':             'application/x-www-form-urlencoded',
                        'RequestVerificationToken': csrfToken,
                    },
                    body: 'id=' + encodeURIComponent(id),
                }).catch(function () {});
            });
        });
    }

    function saveQuery() {
        var name = elSaveName.value.trim();
        if (!name) {
            elSaveError.textContent = 'Name is required.';
            elSaveError.classList.remove('d-none');
            return;
        }
        var sql = elEditor.value.trim();
        if (!sql) {
            elSaveError.textContent = 'The editor is empty — enter SQL first.';
            elSaveError.classList.remove('d-none');
            return;
        }
        var global = document.getElementById('scope-global').checked;
        elBtnConfirm.disabled = true;
        fetch(ctx.saveUrl, {
            method:  'POST',
            headers: {
                'Content-Type':             'application/json',
                'RequestVerificationToken': csrfToken,
            },
            body: JSON.stringify({
                name:        name,
                description: elSaveDesc.value.trim() || null,
                sql:         sql,
                profileId:   global ? null : ctx.profileId,
            }),
        })
        .then(assertOk)
        .then(function () {
            saveModal.hide();
            loadSaved();
        })
        .catch(function (err) {
            elSaveError.textContent = err.message;
            elSaveError.classList.remove('d-none');
        })
        .finally(function () { elBtnConfirm.disabled = false; });
    }

    // ── Schema sidebar ────────────────────────────────────────────────────────

    function loadSchema() {
        fetch(ctx.objectsUrl)
            .then(assertOk)
            .then(function (r) { return r.json(); })
            .then(renderSchema)
            .catch(function () {
                elSchemaSide.innerHTML = '<div class="text-muted small p-2">Failed to load schema.</div>';
            });
    }

    function renderSchema(data) {
        elSchemaSide.innerHTML = '';
        var sections = [
            { label: 'Tables',     items: data.tables     || [], type: 'table' },
            { label: 'Views',      items: data.views      || [], type: 'view' },
            { label: 'Procedures', items: data.procedures || [], type: 'procedure' },
            { label: 'Functions',  items: data.functions  || [], type: 'function' },
        ];
        sections.forEach(function (s) {
            if (!s.items.length) return;
            var group = document.createElement('div');
            group.className = 'schema-group';

            var hdr = document.createElement('div');
            hdr.className = 'px-2 py-1 text-muted small fw-semibold border-bottom user-select-none';
            hdr.style.cursor = 'pointer';
            hdr.textContent  = s.label + ' (' + s.items.length + ')';

            var list = document.createElement('ul');
            list.className = 'list-unstyled mb-0';

            s.items.forEach(function (item) {
                var displayName = (item.schema && item.schema !== 'dbo' && item.schema !== 'public')
                    ? item.schema + '.' + item.name
                    : item.name;
                var li = document.createElement('li');
                li.dataset.search = (item.schema + '.' + item.name).toLowerCase();

                var a = document.createElement('a');
                a.href      = '#';
                a.className = 'schema-item d-block px-3 py-1 text-decoration-none text-truncate small';
                a.title     = item.schema + '.' + item.name;
                a.textContent = displayName;
                a.addEventListener('click', function (e) {
                    e.preventDefault();
                    insertIntoEditor(item.schema, item.name, s.type);
                });

                li.appendChild(a);
                list.appendChild(li);
            });

            hdr.addEventListener('click', function () {
                list.style.display = list.style.display === 'none' ? '' : 'none';
            });

            group.appendChild(hdr);
            group.appendChild(list);
            elSchemaSide.appendChild(group);
        });
    }

    function filterSchema() {
        var q = elSchemaSearch.value.toLowerCase().trim();
        elSchemaSide.querySelectorAll('li[data-search]').forEach(function (li) {
            li.style.display = (!q || li.dataset.search.includes(q)) ? '' : 'none';
        });
    }

    function insertIntoEditor(schema, name, type) {
        var qualified = (schema && schema !== 'dbo' && schema !== 'public')
            ? schema + '.' + name : name;
        var snippet;
        if (type === 'table' || type === 'view') {
            snippet = ctx.provider === 'SqlServer'
                ? 'SELECT TOP 100 * FROM ' + qualified
                : 'SELECT * FROM ' + qualified + ' LIMIT 100';
        } else {
            snippet = qualified;
        }
        elEditor.value = elEditor.value ? elEditor.value + '\n' + snippet : snippet;
        elEditor.focus();
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    function setRunning(on) {
        elBtnRun.disabled    = on;
        elBtnRun.textContent = on ? 'Running…' : '▶ Run  Ctrl+Enter';
    }

    function clearResults() {
        elExecBadge.classList.add('d-none');
        elRowsBadge.classList.add('d-none');
        elResultsArea.classList.add('d-none');
        elResultTabs.innerHTML  = '';
        elResultPanes.innerHTML = '';
        elInfo.classList.add('d-none');
    }

    function showError(msg) {
        elError.textContent = msg;
        elError.classList.remove('d-none');
    }

    function hideError() {
        elError.classList.add('d-none');
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    function assertOk(resp) {
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        return resp;
    }

    function esc(s) {
        return String(s)
            .replace(/&/g,  '&amp;')
            .replace(/</g,  '&lt;')
            .replace(/>/g,  '&gt;')
            .replace(/"/g,  '&quot;')
            .replace(/'/g,  '&#39;');
    }
})();
