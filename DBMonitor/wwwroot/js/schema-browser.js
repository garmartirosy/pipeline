/* Schema browser — vanilla JS */
(function () {
    'use strict';

    var container  = document.getElementById('schema-browser');
    var profileId  = container.dataset.profileId;
    var provider   = container.dataset.provider;   // "SqlServer" or "PostgreSql"
    var isSql      = provider === 'SqlServer';

    var mainPane   = document.getElementById('main-pane');
    var sidebarEl  = document.getElementById('sidebar-content');
    var searchBox  = document.getElementById('sidebar-search');

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    loadObjects();
    searchBox.addEventListener('input', filterSidebar);
    wireResizeHandle();
    wireKeyboard();

    // ── Object loading ────────────────────────────────────────────────────────

    function loadObjects() {
        fetch('/Schema/Objects/' + profileId)
            .then(assertOk)
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.error) { showSidebarError(data.error); return; }
                renderSidebar(data);
            })
            .catch(function (err) { showSidebarError(err.message); });
    }

    // ── Sidebar rendering ─────────────────────────────────────────────────────

    function renderSidebar(data) {
        sidebarEl.innerHTML = '';
        var sections = [
            { key: 'tables',     label: 'Tables',            items: data.tables     || [], type: 'table',     iconCls: 'obj-icon-table', iconText: 'T' },
            { key: 'views',      label: 'Views',             items: data.views      || [], type: 'view',      iconCls: 'obj-icon-view',  iconText: 'V' },
            { key: 'procedures', label: 'Stored Procedures', items: data.procedures || [], type: 'procedure',  iconCls: 'obj-icon-proc',  iconText: 'P' },
            { key: 'functions',  label: 'Functions',         items: data.functions  || [], type: 'function',  iconCls: 'obj-icon-func',  iconText: 'F' },
        ];
        sections.forEach(function (s) {
            if (s.items.length > 0) sidebarEl.appendChild(buildSection(s));
        });
    }

    function buildSection(section) {
        var expanded = section.key === 'tables';
        var wrap = document.createElement('div');
        wrap.className = 'sidebar-section';
        wrap.dataset.sectionKey = section.key;

        // Header row
        var header = document.createElement('div');
        header.className = 'sidebar-section-header d-flex align-items-center';
        header.innerHTML =
            '<span class="me-auto">' + esc(section.label) + '</span>' +
            '<span class="badge bg-secondary rounded-pill me-1 section-count">' + section.items.length + '</span>' +
            '<span class="toggle-icon text-muted">' + (expanded ? '▾' : '▸') + '</span>';

        // Item container
        var itemsWrap = document.createElement('div');
        itemsWrap.className = 'sidebar-items-wrap';
        if (!expanded) itemsWrap.style.display = 'none';

        // Group by schema if there's more than one
        var schemas = uniqueSchemas(section.items);
        if (schemas.length > 1) {
            schemas.forEach(function (schema) {
                var group = document.createElement('div');
                group.className = 'schema-group';
                group.dataset.schema = schema;

                var lbl = document.createElement('div');
                lbl.className = 'schema-group-label';
                lbl.textContent = schema;
                group.appendChild(lbl);

                section.items
                    .filter(function (i) { return i.schema === schema; })
                    .forEach(function (item) {
                        group.appendChild(buildItem(item, section));
                    });

                itemsWrap.appendChild(group);
            });
        } else {
            section.items.forEach(function (item) {
                itemsWrap.appendChild(buildItem(item, section));
            });
        }

        header.addEventListener('click', function () {
            var hidden = itemsWrap.style.display === 'none';
            itemsWrap.style.display = hidden ? '' : 'none';
            header.querySelector('.toggle-icon').textContent = hidden ? '▾' : '▸';
        });

        wrap.appendChild(header);
        wrap.appendChild(itemsWrap);
        return wrap;
    }

    function buildItem(item, section) {
        var displayName = item.name;

        var wrap = document.createElement('div');
        wrap.dataset.search = (item.schema + '.' + item.name).toLowerCase();
        wrap.dataset.schema  = item.schema;
        wrap.dataset.name    = item.name;
        wrap.dataset.type    = section.type;

        var a = document.createElement('a');
        a.href = '#';
        a.className = 'sidebar-item';
        a.title = item.schema + '.' + item.name;
        a.tabIndex = 0;
        a.innerHTML =
            '<span class="obj-icon ' + section.iconCls + '">' + section.iconText + '</span>' +
            esc(displayName);

        a.addEventListener('click', function (e) {
            e.preventDefault();
            setActiveItem(a);
            if (section.type === 'table' || section.type === 'view') {
                loadColumns(item.schema, item.name);
            } else {
                loadRoutine(item.schema, item.name, section.type);
            }
        });

        wrap.appendChild(a);
        return wrap;
    }

    function setActiveItem(el) {
        document.querySelectorAll('.sidebar-item.active').forEach(function (x) {
            x.classList.remove('active');
        });
        el.classList.add('active');
    }

    function uniqueSchemas(items) {
        var seen = {};
        var result = [];
        items.forEach(function (i) {
            if (!seen[i.schema]) { seen[i.schema] = true; result.push(i.schema); }
        });
        return result.sort();
    }

    // ── Keyboard navigation ───────────────────────────────────────────────────

    function wireKeyboard() {
        searchBox.addEventListener('keydown', function (e) {
            if (e.key === 'ArrowDown') { e.preventDefault(); focusNthItem(0); }
        });

        sidebarEl.addEventListener('keydown', function (e) {
            var items = visibleItems();
            var active = document.activeElement;
            var idx = items.indexOf(active);
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (idx < items.length - 1) items[idx + 1].focus();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                if (idx > 0) items[idx - 1].focus();
                else searchBox.focus();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (active && active.classList.contains('sidebar-item')) active.click();
            }
        });
    }

    function visibleItems() {
        return Array.from(sidebarEl.querySelectorAll('.sidebar-item')).filter(function (el) {
            return el.offsetParent !== null;
        });
    }

    function focusNthItem(n) {
        var items = visibleItems();
        if (items[n]) items[n].focus();
    }

    // ── Search filter ─────────────────────────────────────────────────────────

    function filterSidebar() {
        var q = searchBox.value.toLowerCase().trim();

        document.querySelectorAll('.sidebar-section').forEach(function (section) {
            var items   = section.querySelectorAll('[data-search]');
            var wrap    = section.querySelector('.sidebar-items-wrap');
            var header  = section.querySelector('.sidebar-section-header');
            var counter = header ? header.querySelector('.section-count') : null;
            var matched = 0;

            items.forEach(function (item) {
                var show = !q || item.dataset.search.includes(q);
                item.style.display = show ? '' : 'none';
                if (show) matched++;
            });

            // Show/hide the whole section
            section.style.display = matched === 0 && q ? 'none' : '';

            // Auto-expand when filtering
            if (q && matched > 0 && wrap) wrap.style.display = '';

            // Update count badge
            if (counter) counter.textContent = q ? (matched + '/' + items.length) : items.length;
        });
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    function loadColumns(schema, table) {
        setMainLoading();
        fetch('/Schema/Columns/' + profileId + '?schema=' + enc(schema) + '&table=' + enc(table))
            .then(assertOk)
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.error) { showMainError(data.error); return; }
                renderColumns(schema, table, data);
            })
            .catch(function (err) { showMainError(err.message); });
    }

    function renderColumns(schema, table, columns) {
        var qSchema  = isSql ? '[' + schema + ']' : '"' + schema + '"';
        var qTable   = isSql ? '[' + table  + ']' : '"' + table  + '"';
        var fullName = qSchema + '.' + qTable;

        var viewDataHref = '/Schema/TableData/' + profileId +
            '?schema=' + enc(schema) + '&table=' + enc(table);
        var importHref   = '/Import/Start/' + profileId +
            '?schema=' + enc(schema) + '&table=' + enc(table);
        var editorHref   = '/Sql/Editor/' + profileId;

        var rows = columns.map(function (c) {
            var typeLabel = buildColumnTypeLabel(c);
            var badges = '';
            if (c.isPrimaryKey) badges += '<span class="col-badge badge-pk">PK</span>';
            if (c.isIdentity)   badges += '<span class="col-badge badge-identity">ID</span>';
            if (c.isComputed)   badges += '<span class="col-badge badge-computed">CALC</span>';

            var nullable = c.isNullable
                ? '<span class="text-muted">yes</span>'
                : '<strong>no</strong>';

            var defVal = c.hasDefault && c.defaultValue
                ? '<span class="text-muted small text-truncate d-inline-block" style="max-width:130px" title="' + esc(c.defaultValue) + '">' + esc(c.defaultValue) + '</span>'
                : '<span class="text-muted">—</span>';

            var colName = isSql ? '[' + c.name + ']' : '"' + c.name + '"';

            return '<tr>' +
                '<td class="text-muted text-end pe-2" style="width:30px">' + c.ordinalPosition + '</td>' +
                '<td>' +
                    badges +
                    '<code class="copy-target" data-col="' + esc(colName) + '" style="cursor:pointer" title="Click to copy">' + esc(c.name) + '</code>' +
                    '<span class="copy-col-name" title="Copy">⎘</span>' +
                '</td>' +
                '<td class="font-monospace small text-muted">' + esc(typeLabel) + '</td>' +
                '<td>' + nullable + '</td>' +
                '<td>' + defVal + '</td>' +
                '</tr>';
        }).join('');

        var colNames    = columns.map(function (c) { return isSql ? '[' + c.name + ']' : '"' + c.name + '"'; });
        var insertCols  = colNames.filter(function (_, i) { return !columns[i].isIdentity && !columns[i].isComputed; });
        var insertVals  = insertCols.map(function (c) { return '/* ' + c.replace(/^\[|\]$|^"|"$/g, '') + ' */'; });
        var limit100    = isSql ? 'SELECT TOP 100 *\nFROM ' + fullName : 'SELECT *\nFROM ' + fullName + '\nLIMIT 100';
        var countSql    = 'SELECT COUNT(*)\nFROM ' + fullName;
        var insertSql   = 'INSERT INTO ' + fullName + '\n    (' + insertCols.join(', ') + ')\nVALUES\n    (' + insertVals.join(', ') + ')';

        var html =
            '<div class="mb-3 d-flex gap-2 flex-wrap">' +
            '<a href="' + viewDataHref + '" class="btn btn-sm btn-success">View data &rarr;</a>' +
            '<a href="' + importHref + '" class="btn btn-sm btn-outline-info">Import CSV &rarr;</a>' +
            '<a href="' + editorHref   + '" class="btn btn-sm btn-outline-primary">SQL Editor &rarr;</a>' +
            '</div>' +
            '<h5 class="mb-2"><code>' + esc(schema + '.' + table) + '</code></h5>' +

            '<div class="mb-3">' +
            '<div class="text-muted small mb-1 fw-semibold">Quick SQL</div>' +
            '<div class="quick-sql-bar">' +
            '<button class="quick-sql-btn" data-sql="' + esc(limit100)   + '">SELECT *</button>' +
            '<button class="quick-sql-btn" data-sql="' + esc(countSql)   + '">COUNT(*)</button>' +
            (insertCols.length ? '<button class="quick-sql-btn" data-sql="' + esc(insertSql) + '">INSERT template</button>' : '') +
            '</div>' +
            '</div>';

        if (!columns.length) {
            html += '<p class="text-muted">No columns found.</p>';
        } else {
            html +=
                '<div class="text-muted small mb-1 fw-semibold">Columns <span class="badge bg-light text-dark border">' + columns.length + '</span> <span class="text-muted" style="font-size:.7rem">— click column name to copy</span></div>' +
                '<div class="table-responsive">' +
                '<table class="table table-sm table-hover mb-0">' +
                '<thead class="table-light"><tr>' +
                '<th style="width:30px">#</th>' +
                '<th>Column</th>' +
                '<th>Type</th>' +
                '<th>Nullable</th>' +
                '<th>Default</th>' +
                '</tr></thead>' +
                '<tbody>' + rows + '</tbody>' +
                '</table></div>';
        }

        mainPane.innerHTML = html;

        // Wire copy-on-click for column names
        mainPane.querySelectorAll('.copy-target').forEach(function (el) {
            el.addEventListener('click', function () {
                copyText(el.dataset.col, el);
            });
        });

        // Wire quick SQL buttons → copy to clipboard then open editor
        mainPane.querySelectorAll('.quick-sql-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var sql = btn.dataset.sql;
                copyText(sql, btn);
            });
        });
    }

    function buildColumnTypeLabel(c) {
        var dt = c.dataType || '';
        if ((dt === 'decimal' || dt === 'numeric') && c.precision)
            return dt + '(' + c.precision + ',' + (c.scale || 0) + ')';
        if (dt === 'nvarchar' || dt === 'varchar' || dt === 'char' || dt === 'nchar' ||
            dt === 'character varying' || dt === 'character')
        {
            if (c.maxLength === -1) return dt + '(MAX)';
            if (c.maxLength)        return dt + '(' + c.maxLength + ')';
        }
        return dt;
    }

    // ── Routines ──────────────────────────────────────────────────────────────

    function loadRoutine(schema, name, type) {
        setMainLoading();
        fetch('/Schema/Routine/' + profileId + '?schema=' + enc(schema) + '&name=' + enc(name))
            .then(assertOk)
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.error) { showMainError(data.error); return; }
                renderRoutine(data, type || 'procedure');
            })
            .catch(function (err) { showMainError(err.message); });
    }

    function renderRoutine(routine, type) {
        var isProcedure = type === 'procedure';
        var isFunction  = type === 'function';

        var execBtn = '';
        if (isProcedure) {
            var procUrl = '/Sql/Procedure/' + profileId +
                '?schema=' + enc(routine.schema) + '&name=' + enc(routine.name);
            execBtn = '<a href="' + procUrl + '" class="btn btn-sm btn-success me-2">Execute &rarr;</a>';
        } else if (isFunction) {
            var qSchema = isSql ? '[' + routine.schema + ']' : '"' + routine.schema + '"';
            var qName   = isSql ? '[' + routine.name   + ']' : '"' + routine.name   + '"';
            var funcSql = 'SELECT * FROM ' + qSchema + '.' + qName + '()';
            var editorUrl = '/Sql/Editor/' + profileId + '?sql=' + encodeURIComponent(funcSql);
            execBtn = '<a href="' + editorUrl + '" class="btn btn-sm btn-outline-success me-2">Execute as SELECT &rarr;</a>';
        }

        var paramsHtml;
        if (!routine.parameters || !routine.parameters.length) {
            paramsHtml = '<p class="text-muted small">No parameters.</p>';
        } else {
            var paramRows = routine.parameters.map(function (p) {
                var tl = buildRoutineTypeLabel(p);
                var defText = p.hasDefault
                    ? (p.defaultValueText ? '<code class="small">' + esc(p.defaultValueText) + '</code>' : '<span class="text-muted">yes</span>')
                    : '<span class="text-muted">—</span>';
                var dir = p.direction === 'InputOutput' ? 'OUTPUT'
                        : p.direction === 'Output'      ? 'OUTPUT' : 'IN';
                return '<tr>' +
                    '<td><code>' + esc(p.name) + '</code></td>' +
                    '<td class="text-muted font-monospace small">' + esc(tl) + '</td>' +
                    '<td><span class="badge bg-light text-dark border">' + dir + '</span></td>' +
                    '<td>' + defText + '</td>' +
                    '</tr>';
            }).join('');
            paramsHtml =
                '<div class="table-responsive mb-3">' +
                '<table class="table table-sm table-hover mb-0">' +
                '<thead class="table-light"><tr><th>Name</th><th>Type</th><th>Direction</th><th>Default</th></tr></thead>' +
                '<tbody>' + paramRows + '</tbody>' +
                '</table></div>';
        }

        mainPane.innerHTML =
            '<div class="mb-3 d-flex gap-2 align-items-center">' +
            execBtn +
            '<button class="btn btn-sm btn-outline-secondary" id="btn-copy-def">Copy definition</button>' +
            '</div>' +
            '<h5 class="mb-1"><code>' + esc(routine.schema + '.' + routine.name) + '</code></h5>' +
            '<p class="text-muted small mb-3">Type: ' + esc(routine.type) + '</p>' +
            '<h6 class="fw-semibold">Parameters</h6>' +
            paramsHtml +
            '<h6 class="fw-semibold">Definition</h6>' +
            '<div class="def-block" id="def-block">' + esc(routine.definition) + '</div>';

        var def = routine.definition;
        document.getElementById('btn-copy-def').addEventListener('click', function () {
            copyText(def, document.getElementById('btn-copy-def'));
        });
    }

    function buildRoutineTypeLabel(p) {
        var bt = p.dataType || '';
        if ((bt === 'decimal' || bt === 'numeric') && p.precision)
            return bt + '(' + p.precision + ',' + (p.scale || 0) + ')';
        if (p.maxLength != null && p.maxLength !== 0)
            return bt + '(' + (p.maxLength === -1 ? 'MAX' : p.maxLength) + ')';
        return bt;
    }

    // ── Resize handle ─────────────────────────────────────────────────────────

    function wireResizeHandle() {
        var handle  = document.getElementById('resize-handle');
        var sidebar = document.getElementById('sidebar');
        if (!handle || !sidebar) return;

        var dragging = false;
        var startX = 0;
        var startW = 0;

        handle.addEventListener('mousedown', function (e) {
            dragging = true;
            startX   = e.clientX;
            startW   = sidebar.offsetWidth;
            handle.classList.add('dragging');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        });

        document.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            var newW = Math.max(160, Math.min(520, startW + (e.clientX - startX)));
            sidebar.style.width = newW + 'px';
        });

        document.addEventListener('mouseup', function () {
            if (!dragging) return;
            dragging = false;
            handle.classList.remove('dragging');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        });
    }

    // ── Clipboard helper ──────────────────────────────────────────────────────

    function copyText(text, btn) {
        var original = btn.textContent;
        if (navigator.clipboard) {
            navigator.clipboard.writeText(text).then(function () {
                flash(btn, '✓ Copied');
            }).catch(function () { fallbackCopy(text, btn); });
        } else {
            fallbackCopy(text, btn);
        }
    }

    function fallbackCopy(text, btn) {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity  = '0';
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); flash(btn, '✓ Copied'); } catch (_) {}
        document.body.removeChild(ta);
    }

    function flash(el, msg) {
        var orig = el.textContent;
        el.textContent = msg;
        setTimeout(function () { el.textContent = orig; }, 1200);
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    function setMainLoading() {
        mainPane.innerHTML = '<div class="text-center text-muted py-4 small">Loading…</div>';
    }

    function showMainError(msg) {
        mainPane.innerHTML = '<div class="alert alert-danger small">' + esc(msg) + '</div>';
    }

    function showSidebarError(msg) {
        sidebarEl.innerHTML = '<div class="alert alert-danger m-2 small">' + esc(msg) + '</div>';
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    function assertOk(response) {
        if (!response.ok) throw new Error('HTTP ' + response.status);
        return response;
    }

    function enc(s) { return encodeURIComponent(s); }

    function esc(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }
})();
