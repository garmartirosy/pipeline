/* Table data viewer — vanilla JS, no extra dependencies. */
(function () {
    'use strict';

    var ctx       = window.__tableCtx;
    var csrfToken = document.querySelector('input[name="__RequestVerificationToken"]').value;

    // ── State ─────────────────────────────────────────────────────────────────

    var state = {
        page:       1,
        pageSize:   50,
        orderBy:    null,
        descending: false,
        filters:    [],   // [{column, op, value}]
        columns:    [],   // ColumnDescriptor[] from last response
        totalCount: 0,
    };

    // ── DOM refs ──────────────────────────────────────────────────────────────

    var elLoading    = document.getElementById('grid-loading');
    var elError      = document.getElementById('grid-error');
    var elWrap       = document.getElementById('grid-wrap');
    var elThead      = document.getElementById('data-thead');
    var elTbody      = document.getElementById('data-tbody');
    var elStats      = document.getElementById('row-stats');
    var elElapsed    = document.getElementById('elapsed-badge');
    var elPagBar     = document.getElementById('pagination-bar');
    var elPageLabel  = document.getElementById('page-label');
    var elFilterArea = document.getElementById('filter-area');
    var elPageSize   = document.getElementById('page-size-select');

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    loadData();

    document.getElementById('btn-refresh').addEventListener('click', function () {
        state.page = 1;
        loadData();
    });

    document.getElementById('btn-add-filter').addEventListener('click', addFilterRow);

    document.getElementById('btn-export-csv').addEventListener('click', exportCurrentPageCsv);
    document.getElementById('btn-copy-tsv').addEventListener('click', copyCurrentPageTsv);

    elPageSize.addEventListener('change', function () {
        state.pageSize = parseInt(this.value, 10);
        state.page = 1;
        loadData();
    });

    document.getElementById('btn-first').addEventListener('click', function () { goPage(1); });
    document.getElementById('btn-prev' ).addEventListener('click', function () { goPage(state.page - 1); });
    document.getElementById('btn-next' ).addEventListener('click', function () { goPage(state.page + 1); });
    document.getElementById('btn-last' ).addEventListener('click', function () {
        goPage(Math.ceil(state.totalCount / state.pageSize));
    });

    // ── Data loading ──────────────────────────────────────────────────────────

    function loadData() {
        setLoading(true);

        var body = {
            schema:     ctx.schema,
            table:      ctx.table,
            page:       state.page,
            pageSize:   state.pageSize,
            orderBy:    state.orderBy,
            descending: state.descending,
            filters:    state.filters,
        };

        fetch(ctx.dataUrl, {
            method:  'POST',
            headers: {
                'Content-Type':              'application/json',
                'RequestVerificationToken':  csrfToken,
            },
            body: JSON.stringify(body),
        })
        .then(assertOk)
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (data.error) { showError(data.error); return; }
            render(data);
        })
        .catch(function (err) { showError(err.message); })
        .finally(function () { setLoading(false); });
    }

    function goPage(n) {
        var total = Math.max(1, Math.ceil(state.totalCount / state.pageSize));
        state.page = Math.min(Math.max(1, n), total);
        loadData();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    var lastRows = [];

    function render(data) {
        state.columns    = data.columns || [];
        state.totalCount = data.totalCount || 0;
        lastRows = data.rows || [];
        document.getElementById('export-note').style.display = lastRows.length ? '' : 'none';

        hideError();
        elWrap.classList.remove('d-none');

        renderHeaders(state.columns);
        renderRows(data.rows || [], state.columns);
        renderStats(data);
        renderPagination(data);
    }

    function renderHeaders(columns) {
        if (!columns.length) { elThead.innerHTML = ''; return; }

        var cells = columns.map(function (col) {
            var isSorted = state.orderBy === col.name;
            var arrow    = isSorted ? (state.descending ? ' ▼' : ' ▲') : '';
            return '<th class="user-select-none" style="cursor:pointer;white-space:nowrap" data-col="' + esc(col.name) + '">' +
                   esc(col.name) + '<span class="text-muted small">' + esc(arrow) + '</span>' +
                   '</th>';
        }).join('');

        elThead.innerHTML = '<tr>' + cells + '</tr>';

        elThead.querySelectorAll('th[data-col]').forEach(function (th) {
            th.addEventListener('click', function () {
                var col = this.dataset.col;
                if (state.orderBy === col) {
                    state.descending = !state.descending;
                } else {
                    state.orderBy    = col;
                    state.descending = false;
                }
                state.page = 1;
                loadData();
            });
        });
    }

    function renderRows(rows, columns) {
        if (!columns.length) {
            elTbody.innerHTML = '';
            return;
        }

        if (!rows.length) {
            elTbody.innerHTML =
                '<tr><td colspan="' + columns.length + '" class="text-center text-muted py-3">No rows</td></tr>';
            return;
        }

        elTbody.innerHTML = rows.map(function (row) {
            var cells = row.map(function (val) { return '<td>' + renderCell(val) + '</td>'; }).join('');
            return '<tr>' + cells + '</tr>';
        }).join('');
    }

    function renderCell(val) {
        if (val === null || val === undefined) {
            return '<span class="text-muted fst-italic small">NULL</span>';
        }
        var s = String(val);
        if (s.startsWith('(binary,')) {
            return '<span class="text-muted fst-italic small">' + esc(s) + '</span>';
        }
        if (s.length > 120) {
            return '<span title="' + esc(s) + '">' + esc(s.slice(0, 120)) + '&hellip;</span>';
        }
        return esc(s);
    }

    function renderStats(data) {
        var totalPages = Math.max(1, Math.ceil(data.totalCount / data.pageSize));
        elStats.textContent = data.totalCount.toLocaleString() + ' rows';
        elElapsed.textContent = data.elapsedMs + ' ms';
        elElapsed.classList.remove('d-none');
    }

    function renderPagination(data) {
        var totalPages = Math.max(1, Math.ceil(data.totalCount / data.pageSize));
        var curPage    = data.page;

        elPagBar.classList.remove('d-none');
        elPageLabel.textContent = 'Page ' + curPage + ' of ' + totalPages;

        document.getElementById('btn-first').disabled = curPage <= 1;
        document.getElementById('btn-prev' ).disabled = curPage <= 1;
        document.getElementById('btn-next' ).disabled = curPage >= totalPages;
        document.getElementById('btn-last' ).disabled = curPage >= totalPages;
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    var OPS = ['Equals','NotEquals','Contains','StartsWith','GreaterThan','LessThan','IsNull','IsNotNull'];
    var NO_VALUE_OPS = ['IsNull', 'IsNotNull'];

    function addFilterRow() {
        if (!state.columns.length) { alert('Load data first, then add filters.'); return; }

        var row   = document.createElement('div');
        row.className = 'd-flex gap-1 align-items-center mb-1 flex-wrap';

        var colSel = document.createElement('select');
        colSel.className = 'form-select form-select-sm';
        colSel.style.width = '180px';
        state.columns.forEach(function (c) {
            var o = document.createElement('option');
            o.value = c.name; o.textContent = c.name;
            colSel.appendChild(o);
        });

        var opSel = document.createElement('select');
        opSel.className = 'form-select form-select-sm';
        opSel.style.width = '140px';
        OPS.forEach(function (op) {
            var o = document.createElement('option');
            o.value = op; o.textContent = op;
            opSel.appendChild(o);
        });

        var valInput = document.createElement('input');
        valInput.type = 'text';
        valInput.className = 'form-control form-control-sm';
        valInput.style.width = '200px';
        valInput.placeholder = 'value';

        var applyBtn = document.createElement('button');
        applyBtn.className = 'btn btn-sm btn-primary';
        applyBtn.textContent = 'Apply';

        var removeBtn = document.createElement('button');
        removeBtn.className = 'btn btn-sm btn-outline-secondary';
        removeBtn.textContent = '×';

        row.appendChild(colSel);
        row.appendChild(opSel);
        row.appendChild(valInput);
        row.appendChild(applyBtn);
        row.appendChild(removeBtn);
        elFilterArea.appendChild(row);

        opSel.addEventListener('change', function () {
            valInput.style.display = NO_VALUE_OPS.includes(this.value) ? 'none' : '';
        });

        applyBtn.addEventListener('click', function () {
            applyFilters();
        });

        valInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') applyFilters();
        });

        removeBtn.addEventListener('click', function () {
            elFilterArea.removeChild(row);
            applyFilters();
        });
    }

    function applyFilters() {
        state.filters = [];
        elFilterArea.querySelectorAll('div').forEach(function (row) {
            var colSel   = row.querySelector('select:first-child');
            var opSel    = row.querySelectorAll('select')[1];
            var valInput = row.querySelector('input');
            if (!colSel || !opSel) return;
            state.filters.push({
                column: colSel.value,
                op:     opSel.value,
                value:  valInput && valInput.style.display !== 'none' ? valInput.value : null,
            });
        });
        state.page = 1;
        loadData();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    function exportCurrentPageCsv() {
        var lines = [];
        lines.push(state.columns.map(function (c) { return csvCell(c.name); }).join(','));
        lastRows.forEach(function (row) {
            lines.push(row.map(function (v) { return csvCell(v === null ? '' : String(v)); }).join(','));
        });
        var blob = new Blob([lines.join('\r\n')], { type: 'text/csv' });
        var url  = URL.createObjectURL(blob);
        var a    = document.createElement('a');
        a.href     = url;
        a.download = ctx.schema + '_' + ctx.table + '.csv';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    function copyCurrentPageTsv() {
        var lines = [];
        lines.push(state.columns.map(function (c) { return c.name; }).join('\t'));
        lastRows.forEach(function (row) {
            lines.push(row.map(function (v) {
                return v === null ? '' : String(v).replace(/\t/g, ' ').replace(/\r?\n/g, ' ');
            }).join('\t'));
        });
        var text = lines.join('\r\n');
        var btn  = document.getElementById('btn-copy-tsv');
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(function () {
                var orig = btn.textContent;
                btn.textContent = 'Copied!';
                setTimeout(function () { btn.textContent = orig; }, 1500);
            });
        } else {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed'; ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand('copy'); } catch (e) { /* ignore */ }
            document.body.removeChild(ta);
        }
    }

    function csvCell(v) {
        var s = String(v == null ? '' : v);
        if (s.search(/[",\r\n]/) !== -1) s = '"' + s.replace(/"/g, '""') + '"';
        return s;
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    function setLoading(on) {
        elLoading.classList.toggle('d-none', !on);
        elWrap.classList.toggle('d-none',    on);
    }

    function showError(msg) {
        elError.textContent = msg;
        elError.classList.remove('d-none');
        elWrap.classList.add('d-none');
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
