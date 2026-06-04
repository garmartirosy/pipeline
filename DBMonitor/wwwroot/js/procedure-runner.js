/* procedure-runner.js — vanilla JS. Requires result-grid.js loaded first. */
(function () {
    'use strict';

    var ctx = window.__procCtx;
    if (!ctx || ctx.isFunction) return; // function view: no runner needed

    var csrfToken = document.querySelector('input[name="__RequestVerificationToken"]').value;

    // ── DOM refs ──────────────────────────────────────────────────────────────

    var elBtnRun       = document.getElementById('btn-run');
    var elBtnReset     = document.getElementById('btn-reset');
    var elTimeout      = document.getElementById('txt-timeout');
    var elMaxRows      = document.getElementById('sel-maxrows');
    var elParamForm    = document.getElementById('param-form');
    var elError        = document.getElementById('exec-error');
    var elInfo         = document.getElementById('exec-info');
    var elExecBadge    = document.getElementById('exec-badge');
    var elRowsBadge    = document.getElementById('rows-badge');
    var elResultsArea  = document.getElementById('results-area');
    var elResultTabs   = document.getElementById('result-tabs');
    var elResultPanes  = document.getElementById('result-panels');
    var elOutputPanel  = document.getElementById('output-params-panel');
    var elOutputBody   = document.getElementById('output-params-body');
    var elBtnToggleDef = document.getElementById('btn-toggle-def');
    var elDefPanel     = document.getElementById('def-panel');

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    elBtnRun.addEventListener('click', runProcedure);
    elBtnReset && elBtnReset.addEventListener('click', resetForm);

    elBtnToggleDef && elBtnToggleDef.addEventListener('click', function () {
        var hidden = elDefPanel.style.display === 'none';
        elDefPanel.style.display = hidden ? '' : 'none';
        elBtnToggleDef.textContent = hidden ? 'Hide' : 'Show';
    });

    // Wire NULL and UseDefault checkboxes for mutual exclusion
    elParamForm.querySelectorAll('.param-row').forEach(wireRow);

    // ── Row wiring ────────────────────────────────────────────────────────────

    function wireRow(row) {
        var nullChk = row.querySelector('.param-null');
        var defChk  = row.querySelector('.param-default');
        var valInp  = row.querySelector('.param-value');

        if (nullChk && valInp) {
            nullChk.addEventListener('change', function () {
                valInp.disabled = nullChk.checked;
                if (nullChk.checked && defChk) defChk.checked = false;
            });
        }
        if (defChk && valInp) {
            defChk.addEventListener('change', function () {
                valInp.disabled = defChk.checked;
                if (defChk.checked && nullChk) nullChk.checked = false;
            });
        }
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    function runProcedure() {
        setRunning(true);
        clearResults();

        var body = {
            schema:        ctx.schema,
            name:          ctx.name,
            parameters:    collectParameters(),
            timeoutSeconds: parseInt(elTimeout.value, 10) || 30,
            maxRows:        parseInt(elMaxRows.value, 10)  || 1000,
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
        })
        .catch(function (err) { showError(err.message); })
        .finally(function () { setRunning(false); });
    }

    function collectParameters() {
        var params = [];
        elParamForm.querySelectorAll('.param-row[data-name]').forEach(function (row) {
            if (row.dataset.outputOnly === 'true') return; // pure output: no client value

            var nullChk = row.querySelector('.param-null');
            var defChk  = row.querySelector('.param-default');
            var valInp  = row.querySelector('.param-value');

            params.push({
                name:       row.dataset.name,
                rawValue:   valInp ? valInp.value : null,
                isNull:     nullChk ? nullChk.checked : false,
                useDefault: defChk  ? defChk.checked  : false,
            });
        });
        return params;
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

        if (data.message) {
            elInfo.textContent = data.message;
            elInfo.classList.remove('d-none');
        }

        // Output parameters + RETURN_VALUE
        renderOutputParams(data);

        var sets = data.resultSets || [];
        if (!sets.length) {
            if (elInfo.classList.contains('d-none')) {
                elInfo.textContent = 'No result sets returned. Records affected: ' + data.recordsAffected + '.';
                elInfo.classList.remove('d-none');
            }
        } else {
            elResultsArea.classList.remove('d-none');
            window.ResultGrid.render(elResultTabs, elResultPanes, sets);
        }
    }

    function renderOutputParams(data) {
        var outputVals  = data.outputValues  || {};
        var returnValue = data.returnValue;
        var hasOutputs  = Object.keys(outputVals).length > 0;
        var hasReturn   = returnValue !== null && returnValue !== undefined;

        if (!hasOutputs && !hasReturn) { elOutputPanel.classList.add('d-none'); return; }

        elOutputPanel.classList.remove('d-none');
        var rows = '';

        if (hasReturn) {
            rows += '<tr><td class="fw-semibold text-muted small">RETURN VALUE</td><td><code>' +
                    esc(String(returnValue)) + '</code></td></tr>';
        }
        Object.keys(outputVals).forEach(function (key) {
            rows += '<tr><td><code>@' + esc(key) + '</code></td><td>' +
                    renderOutputVal(outputVals[key]) + '</td></tr>';
        });
        elOutputBody.innerHTML = rows;
    }

    function renderOutputVal(val) {
        if (val === null || val === undefined)
            return '<span class="text-muted fst-italic">NULL</span>';
        var s = String(val);
        if (s.startsWith('(binary,'))
            return '<span class="text-muted fst-italic">' + esc(s) + '</span>';
        return '<code>' + esc(s) + '</code>';
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    function resetForm() {
        elParamForm.querySelectorAll('.param-value').forEach(function (inp) {
            if (inp.tagName === 'SELECT') inp.selectedIndex = 0;
            else inp.value = '';
            inp.disabled = false;
        });
        elParamForm.querySelectorAll('.param-null, .param-default').forEach(function (chk) {
            chk.checked = false;
        });
        clearResults();
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    function setRunning(on) {
        elBtnRun.disabled    = on;
        elBtnRun.textContent = on ? 'Running…' : '▶ Run';
    }

    function clearResults() {
        elExecBadge.classList.add('d-none');
        elRowsBadge.classList.add('d-none');
        elResultsArea.classList.add('d-none');
        elResultTabs.innerHTML  = '';
        elResultPanes.innerHTML = '';
        elOutputPanel.classList.add('d-none');
        elOutputBody.innerHTML = '';
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
