/* CSV Import Wizard — vanilla JS, no extra dependencies.
   Wizard state lives in memory only (no localStorage — data sensitivity). */
(function () {
    'use strict';

    var ctx    = window.__importCtx;
    var token  = ctx.antiforgery;

    // ── State ─────────────────────────────────────────────────────────────────

    var state = {
        tempFileId:      null,
        inspection:      null,   // raw inspection result from Upload/Inspect
        targetCols:      [],     // ImportColumnInfo[] for the selected table
        mappings:        [],     // [{csvHeader, csvIndex, targetColumn, targetDataType, targetIsNullable, skip}]
        previewDone:     false,
        errorRows:       [],     // for "download errors CSV"
        runElapsedTimer: null,
        isNewTable:      false,
        newTableName:    '',
    };

    // ── DOM handles ───────────────────────────────────────────────────────────

    var elDropZone    = q('#drop-zone');
    var elFileInput   = q('#file-input');
    var elUploadBar   = q('#upload-bar');
    var elUploadProg  = q('#upload-progress');
    var elUploadStatus = q('#upload-status');
    var elUploadError = q('#upload-error');

    var elStepCfg    = q('#step-configure');
    var elCfgSchema  = q('#cfg-schema');
    var elCfgTable   = q('#cfg-table');
    var elCfgDelim   = q('#cfg-delim');
    var elCfgHeader  = q('#cfg-header');
    var elCfgEncoding = q('#cfg-encoding');
    var elCfgCulture = q('#cfg-culture');
    var elCfgBatch   = q('#cfg-batch');
    var elCfgBatchLbl = q('#cfg-batch-label');
    var elBtnMap     = q('#btn-map');
    var elMapHint    = q('#map-hint');
    var elTruncPanel = q('#truncate-confirm-panel');
    var elTruncInput = q('#truncate-confirm-input');

    var elStepMap    = q('#step-map');
    var elMapTbody   = q('#map-tbody');
    var elMapValMsg  = q('#map-validation-msg');
    var elBtnPreview = q('#btn-preview');

    var elStepPreview  = q('#step-preview');
    var elPreviewHead  = q('#preview-head');
    var elPreviewBody  = q('#preview-body');
    var elPreviewErrs  = q('#preview-errors');
    var elPreviewErrList = q('#preview-error-list');
    var elBtnRun       = q('#btn-run');
    var elRunSpinner   = q('#run-spinner');
    var elRunElapsed   = q('#run-elapsed');
    var elCfgAbort     = q('#cfg-abort');

    var elStepResult   = q('#step-result');
    var elResultHead   = q('#result-heading');
    var elResultMetrics = q('#result-metrics');
    var elResultMsg    = q('#result-message');
    var elResultErrsPnl = q('#result-errors-panel');
    var elResultErrsBody = q('#result-errors-body');
    var elBtnDlErr     = q('#btn-download-errors');
    var elBtnRestart   = q('#btn-restart');

    var elCfgNewTable     = q('#cfg-new-table');
    var elNewTablePanel   = q('#new-table-panel');
    var elNewTableName    = q('#cfg-new-table-name');
    var elStepInfer       = q('#step-infer');
    var elInferSql        = q('#infer-sql');
    var elInferError      = q('#infer-error');
    var elBtnCreateImport = q('#btn-create-import');
    var elCreateSpinner   = q('#create-spinner');
    var elCreateElapsed   = q('#create-elapsed');

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    wireUpload();
    loadObjectsForSchemaDropdown();
    wireConfig();
    wireMap();
    wireRun();
    wireInfer();
    wireRestart();

    // ── Upload wiring ─────────────────────────────────────────────────────────

    function wireUpload() {
        elDropZone.addEventListener('click', function () { elFileInput.click(); });
        elDropZone.addEventListener('dragover', function (e) {
            e.preventDefault();
            elDropZone.classList.add('border-primary');
        });
        elDropZone.addEventListener('dragleave', function () {
            elDropZone.classList.remove('border-primary');
        });
        elDropZone.addEventListener('drop', function (e) {
            e.preventDefault();
            elDropZone.classList.remove('border-primary');
            var file = e.dataTransfer.files[0];
            if (file) doUpload(file);
        });
        elFileInput.addEventListener('change', function () {
            if (elFileInput.files.length) doUpload(elFileInput.files[0]);
        });
    }

    function doUpload(file) {
        elUploadError.classList.add('d-none');
        elUploadProg.classList.remove('d-none');
        elUploadBar.style.width = '0%';
        elUploadStatus.textContent = 'Uploading ' + file.name + '…';

        var formData = new FormData();
        formData.append('file', file);

        var xhr = new XMLHttpRequest();
        xhr.open('POST', ctx.uploadUrl);
        xhr.setRequestHeader('RequestVerificationToken', token);

        xhr.upload.addEventListener('progress', function (e) {
            if (e.lengthComputable) {
                var pct = Math.round(e.loaded / e.total * 100);
                elUploadBar.style.width = pct + '%';
                elUploadStatus.textContent = 'Uploading… ' + pct + '%';
            }
        });

        xhr.addEventListener('load', function () {
            elUploadProg.classList.add('d-none');
            var data;
            try { data = JSON.parse(xhr.responseText); } catch (ex) {
                showUploadError('Unexpected server response.');
                return;
            }
            if (data.error) { showUploadError(data.error); return; }
            state.tempFileId = data.tempFileId;
            state.inspection = data.inspection;
            applyInspection(data.inspection);
            elStepCfg.classList.remove('d-none');
            elStepCfg.scrollIntoView({ behavior: 'smooth' });
        });

        xhr.addEventListener('error', function () {
            elUploadProg.classList.add('d-none');
            showUploadError('Upload failed — check your network connection.');
        });

        xhr.send(formData);
    }

    function showUploadError(msg) {
        elUploadError.textContent = msg;
        elUploadError.classList.remove('d-none');
    }

    // ── Apply inspection result to configure step defaults ────────────────────

    function applyInspection(insp) {
        // Delimiter
        var delimMap = { ',': ',', ';': ';', '\t': '&#9;', '|': '|' };
        var delimVal = insp.delimiter || ',';
        setSelectByValue(elCfgDelim, delimVal, 'auto');

        // Header
        elCfgHeader.checked = insp.hasHeader !== false;

        // Encoding
        setSelectByValue(elCfgEncoding, insp.encodingName || 'utf-8', 'auto');
    }

    // ── Schema / table dropdowns ──────────────────────────────────────────────

    var allTables = {}; // { schema: [tableName, …], … }

    function loadObjectsForSchemaDropdown() {
        fetch(ctx.objectsUrl)
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                if (data.error) {
                    elCfgSchema.innerHTML = '<option value="">— Could not load schemas —</option>';
                    elCfgSchema.title = data.error;
                    return;
                }
                allTables = {};
                (data.tables || []).forEach(function (t) {
                    if (!allTables[t.schema]) allTables[t.schema] = [];
                    allTables[t.schema].push(t.name);
                });

                // Populate schema dropdown
                elCfgSchema.innerHTML = '<option value="">— Select schema —</option>';
                Object.keys(allTables).sort().forEach(function (s) {
                    var opt = document.createElement('option');
                    opt.value = s; opt.textContent = s;
                    elCfgSchema.appendChild(opt);
                });

                // Apply presets
                var ps = ctx.presetSchema ? ctx.presetSchema.replace(/^"|"$/g, '') : '';
                var pt = ctx.presetTable  ? ctx.presetTable.replace(/^"|"$/g, '')  : '';
                if (ps && allTables[ps]) {
                    setSelectByValue(elCfgSchema, ps);
                    repopulateTableDropdown(ps, pt);
                }
            })
            .catch(function (err) {
                elCfgSchema.innerHTML = '<option value="">— Could not load schemas —</option>';
                elCfgSchema.title = err.message || 'Connection failed';
            });
    }

    function repopulateTableDropdown(schema, preselectTable) {
        elCfgTable.innerHTML = '<option value="">— Select table —</option>';
        var tables = allTables[schema] || [];
        tables.slice().sort().forEach(function (name) {
            var opt = document.createElement('option');
            opt.value = name; opt.textContent = name;
            elCfgTable.appendChild(opt);
        });
        if (preselectTable) setSelectByValue(elCfgTable, preselectTable);
        updateMapButton();
    }

    function updateMapButton() {
        var hasTable;
        if (state.isNewTable) {
            var name = elNewTableName ? elNewTableName.value.trim() : '';
            hasTable = elCfgSchema.value !== '' && name !== '';
            elMapHint.textContent = hasTable ? '' : 'Select a schema and enter a new table name';
            elBtnMap.textContent  = 'Infer Schema →';
        } else {
            hasTable = elCfgTable.value !== '';
            elMapHint.textContent = hasTable ? '' : 'Select a target table first';
            elBtnMap.textContent  = 'Map Columns →';
        }
        elBtnMap.disabled = !hasTable;
    }

    // ── Config wiring ─────────────────────────────────────────────────────────

    function wireConfig() {
        elCfgSchema.addEventListener('change', function () {
            repopulateTableDropdown(elCfgSchema.value, '');
        });
        elCfgTable.addEventListener('change', updateMapButton);

        // Batch size label
        if (elCfgBatch) {
            elCfgBatch.addEventListener('input', function () {
                elCfgBatchLbl.textContent = Number(elCfgBatch.value).toLocaleString();
            });
        }

        // Truncate confirm
        var truncRadios = document.querySelectorAll('input[name="cfg-exist"]');
        truncRadios.forEach(function (r) {
            r.addEventListener('change', function () {
                var isTrunc = document.querySelector('input[name="cfg-exist"]:checked')?.value
                    === 'TruncateThenInsert';
                elTruncPanel.classList.toggle('d-none', !isTrunc);
            });
        });

        // Re-inspect when delimiter/header/encoding change
        [elCfgDelim, elCfgEncoding].forEach(function (el) {
            el.addEventListener('change', reInspect);
        });
        elCfgHeader.addEventListener('change', reInspect);

        // New table toggle
        if (elCfgNewTable) {
            elCfgNewTable.addEventListener('change', function () {
                state.isNewTable = elCfgNewTable.checked;
                elNewTablePanel.classList.toggle('d-none', !state.isNewTable);
                elCfgTable.disabled = state.isNewTable;
                updateMapButton();
            });
            elNewTableName.addEventListener('input', updateMapButton);
        }

        // Map button
        elBtnMap.addEventListener('click', loadTargetColumnsAndShowMap);
    }

    function reInspect() {
        if (!state.tempFileId) return;
        var delim = elCfgDelim.value === 'auto' ? '' : elCfgDelim.value;
        var hasHeader = elCfgHeader.checked;
        var enc = elCfgEncoding.value === 'auto' ? null : elCfgEncoding.value;

        postJson(ctx.inspectUrl, {
            tempFileId:   state.tempFileId,
            delimiter:    delim,
            hasHeader:    hasHeader,
            encodingName: enc,
        }).then(function (data) {
            if (!data.error) {
                state.inspection = data;
            }
        });
    }

    // ── Map columns ───────────────────────────────────────────────────────────

    function loadTargetColumnsAndShowMap() {
        if (state.isNewTable) {
            inferSchema();
            return;
        }

        var schema = elCfgSchema.value;
        var table  = elCfgTable.value;
        if (!schema || !table) return;

        var url = ctx.columnsUrl + '?schema=' + encodeURIComponent(schema) + '&table=' + encodeURIComponent(table);
        elBtnMap.disabled    = true;
        elBtnMap.textContent = 'Loading…';
        fetch(url)
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (cols) {
                elBtnMap.disabled    = false;
                elBtnMap.textContent = 'Map Columns →';
                if (cols.error) { alert('Could not load columns: ' + cols.error); return; }
                state.targetCols = cols;
                buildMappingTable();
                elStepMap.classList.remove('d-none');
                elStepPreview.classList.add('d-none');
                elStepResult.classList.add('d-none');
                state.previewDone = false;
                elBtnRun.disabled = true;
                elStepMap.scrollIntoView({ behavior: 'smooth' });
            })
            .catch(function (err) {
                elBtnMap.disabled    = false;
                elBtnMap.textContent = 'Map Columns →';
                alert('Could not load columns: ' + (err.message || 'request failed'));
            });
    }

    function buildMappingTable() {
        var insp = state.inspection;
        var headers = insp ? (insp.headers || []) : [];
        var previewRow = insp && insp.previewRows && insp.previewRows.length > 0
            ? insp.previewRows[0] : [];

        // Non-insertable: identity columns are flagged; computed columns disallowed
        var targetOptions = state.targetCols.map(function (c) {
            var label = c.name + ' (' + c.dataType + ')';
            var classes = '';
            if (c.isIdentity)  label += ' — identity, skip recommended';
            if (c.isComputed)  label += ' — computed, not insertable';
            return { name: c.name, label: label, isComputed: c.isComputed, isIdentity: c.isIdentity,
                     dataType: c.dataType, isNullable: c.isNullable, hasDefault: c.hasDefault };
        });

        var html = '';
        headers.forEach(function (hdr, idx) {
            var sample = previewRow[idx] || '';
            var defaultTarget = guessTarget(hdr, sample, targetOptions);

            html += '<tr data-idx="' + idx + '">' +
                '<td><input type="checkbox" class="form-check-input map-skip" /></td>' +
                '<td class="font-monospace small">' + esc(hdr) + '</td>' +
                '<td class="text-muted small text-truncate" style="max-width:120px">' + esc(sample) + '</td>' +
                '<td>' +
                '<select class="form-select form-select-sm map-target">' +
                '<option value="">— skip —</option>' +
                targetOptions.map(function (t) {
                    var disabled = t.isComputed ? 'disabled' : '';
                    var selected = (defaultTarget === t.name) ? 'selected' : '';
                    return '<option value="' + esc(t.name) + '" ' + disabled + ' ' + selected + '>' +
                        esc(t.label) + '</option>';
                }).join('') +
                '</select></td>' +
                '<td class="text-muted small map-type">' +
                (defaultTarget ? esc(targetOptions.find(function (t) { return t.name === defaultTarget; })?.dataType || '') : '') +
                '</td>' +
                '<td class="text-muted small map-null">' +
                (defaultTarget ? (targetOptions.find(function (t) { return t.name === defaultTarget; })?.isNullable ? 'YES' : 'NO') : '') +
                '</td>' +
                '</tr>';
        });

        elMapTbody.innerHTML = html;

        // Wire up change events
        elMapTbody.querySelectorAll('.map-target').forEach(function (sel) {
            sel.addEventListener('change', function () {
                var tr = sel.closest('tr');
                var col = state.targetCols.find(function (c) { return c.name === sel.value; });
                tr.querySelector('.map-type').textContent = col ? col.dataType : '';
                tr.querySelector('.map-null').textContent = col ? (col.isNullable ? 'YES' : 'NO') : '';
                validateMappings();
            });
        });
        elMapTbody.querySelectorAll('.map-skip').forEach(function (cb) {
            cb.addEventListener('change', function () {
                var sel = cb.closest('tr').querySelector('.map-target');
                sel.disabled = cb.checked;
                validateMappings();
            });
        });

        validateMappings();
    }

    function guessTarget(csvHeader, sampleValue, targets) {
        // Try exact name match first (case-insensitive)
        var lower = csvHeader.trim().toLowerCase();
        var exact = targets.find(function (t) {
            return !t.isComputed && t.name.toLowerCase() === lower;
        });
        if (exact) return exact.name;
        // Partial match
        var partial = targets.find(function (t) {
            return !t.isComputed && (
                t.name.toLowerCase().includes(lower) ||
                lower.includes(t.name.toLowerCase())
            );
        });
        return partial ? partial.name : '';
    }

    function validateMappings() {
        // Find required columns (NOT NULL, no default, not identity, not computed) that are not mapped
        var mapped = new Set();
        elMapTbody.querySelectorAll('tr').forEach(function (tr) {
            var skip = tr.querySelector('.map-skip')?.checked;
            var val  = tr.querySelector('.map-target')?.value;
            if (!skip && val) mapped.add(val);
        });

        var missing = state.targetCols.filter(function (c) {
            return !c.isNullable && !c.hasDefault && !c.isIdentity && !c.isComputed
                && !mapped.has(c.name);
        });

        if (missing.length > 0) {
            elMapValMsg.textContent = 'Required columns not mapped: ' +
                missing.map(function (c) { return c.name; }).join(', ');
            elBtnPreview.disabled = true;
        } else {
            elMapValMsg.textContent = '';
            elBtnPreview.disabled = false;
        }
    }

    // ── Map wiring ────────────────────────────────────────────────────────────

    function wireMap() {
        elBtnPreview.addEventListener('click', runPreview);
    }

    // ── Collect mappings from the DOM ─────────────────────────────────────────

    function collectMappings() {
        var insp    = state.inspection;
        var headers = insp ? (insp.headers || []) : [];
        var mappings = [];
        elMapTbody.querySelectorAll('tr').forEach(function (tr) {
            var idx  = parseInt(tr.dataset.idx, 10);
            var skip = tr.querySelector('.map-skip')?.checked || false;
            var tgt  = tr.querySelector('.map-target')?.value || '';
            var col  = state.targetCols.find(function (c) { return c.name === tgt; });
            mappings.push({
                csvHeader:       headers[idx] || ('Column' + idx),
                csvIndex:        idx,
                targetColumn:    tgt || '',
                targetDataType:  col ? col.dataType : '',
                targetIsNullable: col ? col.isNullable : true,
                skip:            skip || !tgt,
            });
        });
        return mappings;
    }

    function collectConfig() {
        return {
            tempFileId:   state.tempFileId,
            schema:       elCfgSchema.value,
            table:        elCfgTable.value,
            mappings:     collectMappings(),
            delimiter:    elCfgDelim.value === 'auto'
                          ? (state.inspection?.delimiter || ',') : elCfgDelim.value,
            hasHeader:    elCfgHeader.checked,
            encodingName: elCfgEncoding.value === 'auto'
                          ? (state.inspection?.encodingName || 'utf-8') : elCfgEncoding.value,
            cultureName:  elCfgCulture.value,
            nullHandling: document.querySelector('input[name="cfg-null"]:checked')?.value || 'EmptyAsNull',
            existingDataMode: document.querySelector('input[name="cfg-exist"]:checked')?.value || 'Append',
            batchSize:    elCfgBatch ? parseInt(elCfgBatch.value, 10) : 5000,
            abortOnAnyError: elCfgAbort?.checked || false,
            truncateConfirmTableName: elTruncInput?.value || '',
        };
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    function runPreview() {
        var cfg = collectConfig();
        elBtnPreview.disabled = true;
        elBtnPreview.textContent = 'Loading…';

        postJson(ctx.previewUrl, cfg).then(function (data) {
            elBtnPreview.disabled = false;
            elBtnPreview.textContent = 'Preview conversion';

            if (data.error) { alert('Preview failed: ' + data.error); return; }

            // Build preview table
            elPreviewHead.innerHTML = '<th>#</th>' +
                (data.columns || []).map(function (c) {
                    return '<th class="text-truncate" style="max-width:120px">' + esc(c) + '</th>';
                }).join('');

            elPreviewBody.innerHTML = (data.rows || []).map(function (row) {
                var cells = (row.cells || []).map(function (cell) {
                    var cls = cell.ok ? '' : 'table-danger';
                    return '<td class="' + cls + ' small text-truncate" style="max-width:140px">' +
                        esc(String(cell.value ?? '')) + '</td>';
                }).join('');
                return '<tr><td class="text-muted">' + row.line + '</td>' + cells + '</tr>';
            }).join('');

            // Show/hide errors
            if (data.errors && data.errors.length > 0) {
                elPreviewErrList.innerHTML = data.errors.map(function (e) {
                    return '<div><strong>Line ' + e.lineNumber + ':</strong> ' + esc(e.error) + '</div>';
                }).join('');
                elPreviewErrs.classList.remove('d-none');
            } else {
                elPreviewErrs.classList.add('d-none');
            }

            elStepPreview.classList.remove('d-none');
            state.previewDone = true;
            elBtnRun.disabled = false;
            elStepPreview.scrollIntoView({ behavior: 'smooth' });
        }).catch(function () {
            elBtnPreview.disabled = false;
            elBtnPreview.textContent = 'Preview conversion';
            alert('Preview request failed.');
        });
    }

    // ── Schema inference (new table flow) ─────────────────────────────────────

    function inferSchema() {
        var schema = elCfgSchema.value;
        var name   = elNewTableName.value.trim();
        if (!schema || !name) return;

        state.newTableName = name;
        elBtnMap.disabled  = true;
        elBtnMap.textContent = 'Inferring…';

        var delim = elCfgDelim.value === 'auto'
            ? (state.inspection && state.inspection.delimiter ? state.inspection.delimiter : ',')
            : elCfgDelim.value;
        var enc = elCfgEncoding.value === 'auto'
            ? (state.inspection && state.inspection.encodingName ? state.inspection.encodingName : 'utf-8')
            : elCfgEncoding.value;

        postJson(ctx.inferSchemaUrl, {
            tempFileId:   state.tempFileId,
            schema:       schema,
            table:        name,
            delimiter:    delim,
            hasHeader:    elCfgHeader.checked,
            encodingName: enc,
        }).then(function (data) {
            elBtnMap.disabled    = false;
            elBtnMap.textContent = 'Infer Schema →';

            if (data.error) {
                alert('Schema inference failed: ' + data.error);
                return;
            }

            elInferSql.value = data.createSql;
            elInferError.classList.add('d-none');
            elStepInfer.classList.remove('d-none');
            elStepMap.classList.add('d-none');
            elStepPreview.classList.add('d-none');
            elStepResult.classList.add('d-none');
            elStepInfer.scrollIntoView({ behavior: 'smooth' });
        }).catch(function () {
            elBtnMap.disabled    = false;
            elBtnMap.textContent = 'Infer Schema →';
            alert('Schema inference request failed.');
        });
    }

    function wireInfer() {
        if (elBtnCreateImport) {
            elBtnCreateImport.addEventListener('click', runCreateAndImport);
        }
    }

    function runCreateAndImport() {
        var sql = elInferSql.value.trim();
        if (!sql) return;

        elBtnCreateImport.disabled = true;
        elCreateSpinner.classList.remove('d-none');
        elInferError.classList.add('d-none');

        var startMs = Date.now();
        state.runElapsedTimer = setInterval(function () {
            elCreateElapsed.textContent = 'Running… ' +
                ((Date.now() - startMs) / 1000).toFixed(0) + 's elapsed';
        }, 1000);

        var delim = elCfgDelim.value === 'auto'
            ? (state.inspection && state.inspection.delimiter ? state.inspection.delimiter : ',')
            : elCfgDelim.value;
        var enc = elCfgEncoding.value === 'auto'
            ? (state.inspection && state.inspection.encodingName ? state.inspection.encodingName : 'utf-8')
            : elCfgEncoding.value;

        postJson(ctx.executeCreateUrl, {
            tempFileId:    state.tempFileId,
            createTableSql: sql,
            schema:        elCfgSchema.value,
            table:         state.newTableName,
            delimiter:     delim,
            hasHeader:     elCfgHeader.checked,
            encodingName:  enc,
            batchSize:     elCfgBatch ? parseInt(elCfgBatch.value, 10) : 5000,
        }).then(function (data) {
            clearInterval(state.runElapsedTimer);
            elCreateSpinner.classList.add('d-none');
            elBtnCreateImport.disabled = false;

            if (data.error) {
                elInferError.textContent = data.error;
                elInferError.classList.remove('d-none');
                return;
            }
            renderResult(data);
        }).catch(function (err) {
            clearInterval(state.runElapsedTimer);
            elCreateSpinner.classList.add('d-none');
            elBtnCreateImport.disabled = false;
            elInferError.textContent = 'Request failed: ' + err.message;
            elInferError.classList.remove('d-none');
        });
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    function wireRun() {
        elBtnRun.addEventListener('click', runImport);
        elBtnDlErr.addEventListener('click', downloadErrors);
    }

    function runImport() {
        var cfg = collectConfig();

        // Validate truncate confirmation
        if (cfg.existingDataMode === 'TruncateThenInsert') {
            if (cfg.truncateConfirmTableName !== cfg.table) {
                alert('Please type the exact table name in the confirmation box.');
                return;
            }
        }

        elBtnRun.disabled = true;
        elRunSpinner.classList.remove('d-none');
        var startMs = Date.now();
        state.runElapsedTimer = setInterval(function () {
            var secs = ((Date.now() - startMs) / 1000).toFixed(0);
            elRunElapsed.textContent = 'Running… ' + secs + 's elapsed';
        }, 1000);

        postJson(ctx.executeUrl, cfg).then(function (data) {
            clearInterval(state.runElapsedTimer);
            elRunSpinner.classList.add('d-none');
            elBtnRun.disabled = false;

            if (data.error) {
                renderResult({ error: data.error });
                return;
            }
            renderResult(data);
        }).catch(function (err) {
            clearInterval(state.runElapsedTimer);
            elRunSpinner.classList.add('d-none');
            elBtnRun.disabled = false;
            renderResult({ error: 'Request failed: ' + err.message });
        });
    }

    function renderResult(data) {
        elStepResult.classList.remove('d-none');

        if (data.error) {
            elResultHead.textContent = 'Import failed';
            elResultHead.className   = 'card-header fw-semibold text-danger';
            elResultMetrics.innerHTML = '';
            elResultMsg.textContent   = data.error;
            elResultMsg.className     = 'alert alert-danger small';
            elResultMsg.classList.remove('d-none');
            elResultErrsPnl.classList.add('d-none');
            elStepResult.scrollIntoView({ behavior: 'smooth' });
            return;
        }

        var success = data.rowsInserted > 0 && !data.rolledBack;
        elResultHead.textContent = success ? 'Import complete' : 'Import finished with issues';
        elResultHead.className   = 'card-header fw-semibold ' + (success ? 'text-success' : 'text-warning');

        elResultMetrics.innerHTML =
            metric('Rows read',     data.rowsRead,     'secondary') +
            metric('Rows inserted', data.rowsInserted, success ? 'success' : 'warning') +
            metric('Rows rejected', data.rowsRejected, data.rowsRejected > 0 ? 'danger' : 'secondary') +
            metric('Elapsed',       (data.elapsedMs / 1000).toFixed(2) + 's', 'secondary');

        if (data.message) {
            elResultMsg.textContent = data.message;
            elResultMsg.className   = 'alert alert-warning small';
            elResultMsg.classList.remove('d-none');
        } else {
            elResultMsg.classList.add('d-none');
        }

        if (data.errors && data.errors.length > 0) {
            state.errorRows = data.errors;
            elResultErrsBody.innerHTML = data.errors.map(function (e, i) {
                return '<tr><td class="text-muted">' + e.lineNumber + '</td>' +
                    '<td>' + esc(e.error) + '</td>' +
                    '<td class="font-monospace small text-truncate" style="max-width:300px">' +
                    esc(e.csvLine) + '</td></tr>';
            }).join('');
            elResultErrsPnl.classList.remove('d-none');
        } else {
            state.errorRows = [];
            elResultErrsPnl.classList.add('d-none');
        }

        elStepResult.scrollIntoView({ behavior: 'smooth' });
    }

    function metric(label, value, colour) {
        return '<div class="col-auto">' +
            '<div class="card px-3 py-2 text-center border-' + colour + '">' +
            '<div class="small text-muted">' + label + '</div>' +
            '<div class="fw-bold fs-5">' + esc(String(value)) + '</div>' +
            '</div></div>';
    }

    // ── Download errors CSV ───────────────────────────────────────────────────

    function downloadErrors() {
        var rows = state.errorRows;
        if (!rows || !rows.length) return;
        var lines = ['"Line","Error","CSV Line"'];
        rows.forEach(function (e) {
            lines.push(
                '"' + e.lineNumber + '",' +
                '"' + (e.error  || '').replace(/"/g, '""') + '",' +
                '"' + (e.csvLine || '').replace(/"/g, '""') + '"'
            );
        });
        var blob = new Blob([lines.join('\r\n')], { type: 'text/csv' });
        var url  = URL.createObjectURL(blob);
        var a    = document.createElement('a');
        a.href = url; a.download = 'import-errors.csv';
        document.body.appendChild(a); a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    // ── Restart ───────────────────────────────────────────────────────────────

    function wireRestart() {
        elBtnRestart.addEventListener('click', function () {
            // Reset state
            state.tempFileId   = null;
            state.inspection   = null;
            state.targetCols   = [];
            state.mappings     = [];
            state.previewDone  = false;
            state.errorRows    = [];
            state.isNewTable   = false;
            state.newTableName = '';

            // Reset UI
            elUploadError.classList.add('d-none');
            elUploadProg.classList.add('d-none');
            elFileInput.value = '';
            elStepCfg.classList.add('d-none');
            elStepInfer.classList.add('d-none');
            elStepMap.classList.add('d-none');
            elStepPreview.classList.add('d-none');
            elStepResult.classList.add('d-none');
            elMapTbody.innerHTML    = '';
            elPreviewBody.innerHTML = '';

            // Reset new-table controls
            if (elCfgNewTable) {
                elCfgNewTable.checked = false;
                elNewTablePanel.classList.add('d-none');
                elCfgTable.disabled  = false;
                elNewTableName.value = '';
            }
            updateMapButton();

            window.scrollTo({ top: 0, behavior: 'smooth' });
        });
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    function postJson(url, body) {
        return fetch(url, {
            method:  'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token,
            },
            body: JSON.stringify(body),
        }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    function setSelectByValue(sel, value, fallback) {
        for (var i = 0; i < sel.options.length; i++) {
            if (sel.options[i].value === value) {
                sel.selectedIndex = i;
                return;
            }
        }
        if (fallback !== undefined) setSelectByValue(sel, fallback);
    }

    function q(sel) { return document.querySelector(sel); }

    function esc(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }
})();
