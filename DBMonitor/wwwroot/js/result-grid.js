/* result-grid.js — shared result-set grid renderer.
   Loaded before sql-editor.js and procedure-runner.js.
   Exposes window.ResultGrid = { render, exportCsv }. */
window.ResultGrid = (function () {
    'use strict';

    // Per-table sort state, keyed by tabId.
    var sortStates = {};

    // Render result sets into Bootstrap tab/pane elements.
    // tabsEl  — <ul> for tab headers
    // panesEl — <div> for tab content
    // resultSets — array from server: [{ columns, rows, truncated, rowCount }]
    function render(tabsEl, panesEl, resultSets) {
        sortStates = {};
        tabsEl.innerHTML  = '';
        panesEl.innerHTML = '';

        (resultSets || []).forEach(function (rs, i) {
            var tabId  = 'rs-tab-' + i;
            var active = i === 0 ? ' active' : '';
            var label  = 'Result ' + (i + 1) + ' (' + rs.rowCount.toLocaleString() + (rs.truncated ? '+' : '') + ')';

            var li = document.createElement('li');
            li.className = 'nav-item';
            li.innerHTML =
                '<button class="nav-link' + active + '" data-bs-toggle="tab" data-bs-target="#' + tabId + '" type="button">' +
                esc(label) + '</button>';
            tabsEl.appendChild(li);

            var pane = document.createElement('div');
            pane.className = 'tab-pane fade show' + active;
            pane.id = tabId;

            pane.appendChild(buildTable(tabId, rs));

            if (rs.truncated) {
                var warn = document.createElement('div');
                warn.className = 'alert alert-warning m-2 py-1 small mb-0';
                warn.textContent = 'Results truncated at ' + rs.rowCount.toLocaleString() + ' rows.';
                pane.appendChild(warn);
            }

            var footer = document.createElement('div');
            footer.className = 'p-2 border-top d-flex justify-content-end gap-2';
            var csvBtn = document.createElement('button');
            csvBtn.className = 'btn btn-sm btn-outline-secondary';
            csvBtn.textContent = 'Export CSV';
            csvBtn.addEventListener('click', function () { exportCsv(rs); });
            var tsvBtn = document.createElement('button');
            tsvBtn.className = 'btn btn-sm btn-outline-secondary';
            tsvBtn.textContent = 'Copy as TSV';
            tsvBtn.addEventListener('click', function () { copyTsv(rs, tsvBtn); });
            footer.appendChild(tsvBtn);
            footer.appendChild(csvBtn);
            pane.appendChild(footer);

            panesEl.appendChild(pane);
        });
    }

    function buildTable(tabId, rs) {
        sortStates[tabId] = { col: null, desc: false };

        var wrap = document.createElement('div');
        wrap.className = 'table-responsive';
        wrap.style.maxHeight = '32vh';
        wrap.style.overflow  = 'auto';

        var table = document.createElement('table');
        table.className = 'table table-sm table-hover mb-0';

        // Header
        var thead = document.createElement('thead');
        thead.className = 'table-light sticky-top';
        var headerRow = document.createElement('tr');
        (rs.columns || []).forEach(function (col, ci) {
            var th = document.createElement('th');
            th.style.cursor     = 'pointer';
            th.style.userSelect = 'none';
            th.style.whiteSpace = 'nowrap';
            th.dataset.col = String(ci);
            th.innerHTML   = esc(col) + ' <span class="text-muted sort-arrow"></span>';
            th.addEventListener('click', function () { clientSort(tabId, rs, ci, table); });
            headerRow.appendChild(th);
        });
        thead.appendChild(headerRow);
        table.appendChild(thead);

        // Body
        var tbody = document.createElement('tbody');
        fillRows(tbody, rs.rows, (rs.columns || []).length);
        table.appendChild(tbody);

        wrap.appendChild(table);
        return wrap;
    }

    function fillRows(tbody, rows, colCount) {
        if (!rows || !rows.length) {
            tbody.innerHTML =
                '<tr><td colspan="' + colCount + '" class="text-center text-muted py-3">No rows</td></tr>';
            return;
        }
        tbody.innerHTML = rows.map(function (row) {
            return '<tr>' + (row || []).map(function (v) {
                return '<td>' + renderCell(v) + '</td>';
            }).join('') + '</tr>';
        }).join('');
    }

    function renderCell(val) {
        if (val === null || val === undefined)
            return '<span class="text-muted fst-italic small">NULL</span>';
        var s = String(val);
        if (s.startsWith('(binary,'))
            return '<span class="text-muted fst-italic small">' + esc(s) + '</span>';
        if (s.length > 120)
            return '<span title="' + esc(s) + '">' + esc(s.slice(0, 120)) + '&hellip;</span>';
        return esc(s);
    }

    function clientSort(tabId, rs, colIdx, table) {
        var st = sortStates[tabId];
        if (st.col === colIdx) {
            st.desc = !st.desc;
        } else {
            st.col  = colIdx;
            st.desc = false;
        }

        var sorted = rs.rows.slice().sort(function (a, b) {
            var av = a[colIdx], bv = b[colIdx];
            if (av === null && bv === null) return 0;
            if (av === null) return 1;
            if (bv === null) return -1;
            var an = parseFloat(av), bn = parseFloat(bv);
            var cmp = (!isNaN(an) && !isNaN(bn))
                ? (an - bn)
                : String(av).localeCompare(String(bv));
            return st.desc ? -cmp : cmp;
        });

        fillRows(table.querySelector('tbody'), sorted, (rs.columns || []).length);

        table.querySelectorAll('th[data-col]').forEach(function (th) {
            var ci = parseInt(th.dataset.col, 10);
            th.querySelector('.sort-arrow').textContent =
                (ci === st.col) ? (st.desc ? ' ▼' : ' ▲') : '';
        });
    }

    function exportCsv(rs) {
        var lines = [];
        if (rs.columns && rs.columns.length)
            lines.push(rs.columns.map(csvCell).join(','));
        (rs.rows || []).forEach(function (row) {
            lines.push((row || []).map(function (v) {
                return csvCell(v === null ? '' : String(v));
            }).join(','));
        });
        var blob = new Blob([lines.join('\r\n')], { type: 'text/csv' });
        var url  = URL.createObjectURL(blob);
        var a    = document.createElement('a');
        a.href     = url;
        a.download = 'export.csv';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    function copyTsv(rs, btn) {
        var lines = [];
        if (rs.columns && rs.columns.length)
            lines.push(rs.columns.join('\t'));
        (rs.rows || []).forEach(function (row) {
            lines.push((row || []).map(function (v) {
                return v === null ? '' : String(v).replace(/\t/g, ' ').replace(/\r?\n/g, ' ');
            }).join('\t'));
        });
        var text = lines.join('\r\n');
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(function () {
                var orig = btn.textContent;
                btn.textContent = 'Copied!';
                setTimeout(function () { btn.textContent = orig; }, 1500);
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
        try {
            document.execCommand('copy');
            var orig = btn.textContent;
            btn.textContent = 'Copied!';
            setTimeout(function () { btn.textContent = orig; }, 1500);
        } catch (e) { /* ignore */ }
        document.body.removeChild(ta);
    }

    function csvCell(v) {
        var s = String(v == null ? '' : v);
        if (s.search(/[",\r\n]/) !== -1) s = '"' + s.replace(/"/g, '""') + '"';
        return s;
    }

    function esc(s) {
        return String(s)
            .replace(/&/g,  '&amp;')
            .replace(/</g,  '&lt;')
            .replace(/>/g,  '&gt;')
            .replace(/"/g,  '&quot;')
            .replace(/'/g,  '&#39;');
    }

    return { render: render, exportCsv: exportCsv, copyTsv: copyTsv };
})();
