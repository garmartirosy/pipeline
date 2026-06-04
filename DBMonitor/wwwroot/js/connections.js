/* Connections list — pin toggle + drag-to-reorder */
(function () {
    'use strict';

    var ctx = window.__connectionsCtx;

    // ── Pin toggle ────────────────────────────────────────────────────────────

    document.querySelectorAll('.pin-btn').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var id = btn.dataset.id;
            fetch(ctx.togglePinUrl, {
                method:  'POST',
                headers: {
                    'Content-Type':             'application/x-www-form-urlencoded',
                    'RequestVerificationToken': ctx.csrfToken,
                },
                body: 'id=' + encodeURIComponent(id),
            })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                btn.dataset.pinned = data.isPinned ? 'true' : 'false';
                btn.classList.toggle('pin-active',   data.isPinned);
                btn.classList.toggle('pin-inactive', !data.isPinned);
                btn.title      = data.isPinned ? 'Unpin' : 'Pin';
                btn.ariaLabel  = data.isPinned ? 'Unpin' : 'Pin';
            })
            .catch(function (e) { console.error('Pin toggle failed', e); });
        });
    });

    // ── Drag-to-reorder ───────────────────────────────────────────────────────

    var tbody = document.getElementById('sortable-connections');
    if (!tbody) return;

    var dragSrc = null;

    tbody.querySelectorAll('tr').forEach(function (row) {
        if (row.querySelector('.drag-handle')) addDragListeners(row);
    });

    function addDragListeners(row) {
        var handle = row.querySelector('.drag-handle');
        if (!handle) return;

        row.setAttribute('draggable', 'true');

        row.addEventListener('dragstart', function (e) {
            dragSrc = row;
            row.style.opacity = '0.5';
            e.dataTransfer.effectAllowed = 'move';
        });

        row.addEventListener('dragend', function () {
            row.style.opacity = '';
            tbody.querySelectorAll('tr').forEach(function (r) {
                r.classList.remove('drag-over');
            });
            saveOrder();
        });

        row.addEventListener('dragover', function (e) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            if (dragSrc && dragSrc !== row) row.classList.add('drag-over');
        });

        row.addEventListener('dragleave', function () {
            row.classList.remove('drag-over');
        });

        row.addEventListener('drop', function (e) {
            e.stopPropagation();
            e.preventDefault();
            row.classList.remove('drag-over');
            if (!dragSrc || dragSrc === row) return;
            var rows = Array.from(tbody.querySelectorAll('tr'));
            var srcIdx = rows.indexOf(dragSrc);
            var dstIdx = rows.indexOf(row);
            if (srcIdx < dstIdx) {
                row.parentNode.insertBefore(dragSrc, row.nextSibling);
            } else {
                row.parentNode.insertBefore(dragSrc, row);
            }
        });
    }

    function saveOrder() {
        var items = Array.from(tbody.querySelectorAll('tr')).map(function (r, i) {
            return { id: r.dataset.id, sortOrder: i };
        });
        fetch(ctx.reorderUrl, {
            method:  'POST',
            headers: {
                'Content-Type':             'application/json',
                'RequestVerificationToken': ctx.csrfToken,
            },
            body: JSON.stringify(items),
        }).catch(function (e) { console.error('Reorder failed', e); });
    }
})();
