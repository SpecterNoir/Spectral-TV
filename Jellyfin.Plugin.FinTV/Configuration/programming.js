(function () {
    'use strict';

    let page = null;
    let channels = [];
    let state = null;
    let searchTimer = null;

    function $(id) {
        return page ? page.querySelector('#' + id) : document.getElementById(id);
    }

    function escapeHtml(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function normalizeApiValue(value) {
        if (Array.isArray(value)) return value.map(normalizeApiValue);
        if (!value || typeof value !== 'object') return value;
        const result = {};
        Object.keys(value).forEach((key) => {
            const camel = key.length ? key.charAt(0).toLowerCase() + key.slice(1) : key;
            result[camel] = normalizeApiValue(value[key]);
        });
        return result;
    }

    function resolveUrl(path) {
        const normalized = path.startsWith('/') ? path.slice(1) : path;
        if (typeof ApiClient !== 'undefined' && typeof ApiClient.getUrl === 'function') {
            return ApiClient.getUrl(normalized);
        }
        return '/' + normalized;
    }

    async function api(path, options) {
        options = options || {};
        const url = resolveUrl('FinTV/api' + (path.startsWith('/') ? path : '/' + path));
        const method = options.method || 'GET';
        const body = options.body == null ? undefined : (typeof options.body === 'string' ? options.body : JSON.stringify(options.body));

        const headers = { accept: 'application/json' };
        if (body) headers['Content-Type'] = 'application/json';
        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
            const token = ApiClient.accessToken();
            if (token) headers['X-Emby-Token'] = token;
        }

        const response = await fetch(url, {
            method,
            credentials: 'same-origin',
            headers,
            body
        });

        if (!response.ok) {
            const text = await response.text();
            let message = text || response.statusText || 'Request failed';
            try {
                const parsed = JSON.parse(text);
                message = parsed.message || parsed.detail || parsed.title || message;
            } catch (ignore) {
                // Keep raw text.
            }
            throw new Error(message);
        }

        if (response.status === 204 || response.status === 205) return null;
        const text = await response.text();
        if (!text) return null;
        return normalizeApiValue(JSON.parse(text));
    }

    function showStatus(message, kind) {
        const el = $('vtv-status');
        if (!el) return;
        el.className = 'vtv-status show' + (kind ? ' ' + kind : '');
        el.textContent = message;
    }

    function clearStatus() {
        const el = $('vtv-status');
        if (!el) return;
        el.className = 'vtv-status';
        el.textContent = '';
    }

    function currentChannelId() {
        return $('vtv-channel')?.value || '';
    }

    async function loadChannels() {
        const all = await api('/channels');
        channels = (all || []).filter((c) => Number(c.contentType) !== 4);
        const select = $('vtv-channel');
        if (!select) return;

        const previous = select.value;
        select.innerHTML = channels.map((c) => `<option value="${c.id}">${escapeHtml(String(c.number))} — ${escapeHtml(c.name)}</option>`).join('');
        if (previous && channels.some((c) => c.id === previous)) {
            select.value = previous;
        }

        if (!channels.length) {
            showStatus('Create a non-weather FinTV channel first.', 'warn');
            state = null;
            renderState();
            return;
        }

        clearStatus();
        await loadSelectedChannel();
    }

    async function loadSelectedChannel() {
        const id = currentChannelId();
        if (!id) return;
        state = await api('/programming/' + encodeURIComponent(id));
        renderState();
    }

    function renderState() {
        const settings = state?.settings || {};
        if ($('vtv-enabled')) $('vtv-enabled').checked = !!settings.enabled;
        if ($('vtv-filler-enabled')) $('vtv-filler-enabled').checked = settings.fillerEnabled !== false;
        if ($('vtv-filler-chance')) $('vtv-filler-chance').value = settings.fillerChancePercent ?? 100;
        if ($('vtv-filler-min')) $('vtv-filler-min').value = settings.minFillerItems ?? 1;
        if ($('vtv-filler-max')) $('vtv-filler-max').value = settings.maxFillerItems ?? 2;
        if ($('vtv-filler-max-seconds')) $('vtv-filler-max-seconds').value = settings.maxFillerSeconds ?? 180;
        if ($('vtv-filler-repeat')) $('vtv-filler-repeat').value = settings.fillerRepeatWindow ?? 12;

        renderPrograms();
        renderFillers();
    }

    function renderPrograms() {
        const items = state?.sources || [];
        const body = $('vtv-program-body');
        const totalEl = $('vtv-total');
        if (!body || !totalEl) return;

        const total = items.filter((x) => x.enabled !== false).reduce((sum, x) => sum + Number(x.targetAirtimePercent || 0), 0);
        const rounded = Math.round(total * 10) / 10;
        totalEl.textContent = items.length
            ? `Enabled airtime targets total ${rounded}%. Values are normalized automatically.`
            : 'No programming sources yet.';
        totalEl.className = 'vtv-total ' + (Math.abs(total - 100) < 0.11 ? 'good' : 'warn');

        if (!items.length) {
            body.innerHTML = '<tr><td colspan="6" class="vtv-muted">Search above and add at least one series, season, episode, or movie.</td></tr>';
            return;
        }

        body.innerHTML = items.map((source) => `
            <tr data-source-id="${source.id}">
                <td><strong>${escapeHtml(source.name)}</strong></td>
                <td>${escapeHtml(source.type)}</td>
                <td><input data-role="share" type="number" min="0.1" step="0.1" class="emby-input" value="${Number(source.targetAirtimePercent || 0)}"></td>
                <td><select data-role="mode" class="emby-select"><option value="0"${Number(source.playbackMode) === 0 ? ' selected' : ''}>Sequential</option><option value="1"${Number(source.playbackMode) === 1 ? ' selected' : ''}>Random</option></select></td>
                <td><input data-role="enabled" type="checkbox"${source.enabled !== false ? ' checked' : ''}></td>
                <td><button type="button" data-action="save" class="emby-button">Save</button> <button type="button" data-action="remove" class="emby-button danger">Remove</button></td>
            </tr>`).join('');

        body.querySelectorAll('tr[data-source-id]').forEach((row) => {
            const id = row.dataset.sourceId;
            row.querySelector('[data-action="save"]').onclick = () => saveProgramSource(row, id);
            row.querySelector('[data-action="remove"]').onclick = () => removeProgramSource(id);
        });
    }

    function renderFillers() {
        const items = state?.fillers || [];
        const body = $('vtv-filler-body');
        if (!body) return;

        if (!items.length) {
            body.innerHTML = '<tr><td colspan="5" class="vtv-muted">No promo or bumper items have been added.</td></tr>';
            return;
        }

        body.innerHTML = items.map((source) => `
            <tr data-filler-id="${source.id}">
                <td><strong>${escapeHtml(source.name)}</strong>${source.runtimeSeconds ? `<div class="vtv-sub">${source.runtimeSeconds}s</div>` : ''}</td>
                <td><select data-role="kind" class="emby-select">
                    <option value="0"${Number(source.kind) === 0 ? ' selected' : ''}>Promo</option>
                    <option value="1"${Number(source.kind) === 1 ? ' selected' : ''}>Bumper</option>
                    <option value="2"${Number(source.kind) === 2 ? ' selected' : ''}>Commercial</option>
                    <option value="3"${Number(source.kind) === 3 ? ' selected' : ''}>Station ID</option>
                </select></td>
                <td><input data-role="weight" type="number" min="1" max="1000" class="emby-input" value="${Number(source.weight || 1)}"></td>
                <td><input data-role="enabled" type="checkbox"${source.enabled !== false ? ' checked' : ''}></td>
                <td><button type="button" data-action="save" class="emby-button">Save</button> <button type="button" data-action="remove" class="emby-button danger">Remove</button></td>
            </tr>`).join('');

        body.querySelectorAll('tr[data-filler-id]').forEach((row) => {
            const id = row.dataset.fillerId;
            row.querySelector('[data-action="save"]').onclick = () => saveFillerSource(row, id);
            row.querySelector('[data-action="remove"]').onclick = () => removeFillerSource(id);
        });
    }

    async function saveSettings() {
        const id = currentChannelId();
        if (!id) return;
        const min = Number($('vtv-filler-min').value || 0);
        const max = Number($('vtv-filler-max').value || 0);
        if (max < min) {
            showStatus('Maximum filler items must be at least the minimum.', 'warn');
            return;
        }

        await api('/programming/' + id + '/settings', {
            method: 'PUT',
            body: {
                channelId: id,
                enabled: $('vtv-enabled').checked,
                fillerEnabled: $('vtv-filler-enabled').checked,
                fillerChancePercent: Number($('vtv-filler-chance').value || 0),
                minFillerItems: min,
                maxFillerItems: max,
                maxFillerSeconds: Number($('vtv-filler-max-seconds').value || 0),
                fillerRepeatWindow: Number($('vtv-filler-repeat').value || 0)
            }
        });
        showStatus('Channel behavior saved. Rebuild the channel to apply it immediately.', 'good');
        await loadSelectedChannel();
    }

    async function searchCatalog(query, resultsId, onPick) {
        const results = $(resultsId);
        if (!results) return;
        if (!query || query.trim().length < 2) {
            results.innerHTML = '';
            return;
        }

        const data = await api('/catalog/search?q=' + encodeURIComponent(query.trim()) + '&limit=30');
        results.innerHTML = (data || []).map((item) => `
            <div class="vtv-result" data-id="${item.id}" data-type="${escapeHtml(item.type)}">
                <strong>${escapeHtml(item.name)}</strong>
                <div class="vtv-sub">${escapeHtml(item.type)}${item.year ? ' · ' + item.year : ''}${item.runtimeMinutes ? ' · ' + item.runtimeMinutes + ' min' : ''}</div>
            </div>`).join('') || '<div class="vtv-result vtv-muted">No matches</div>';

        results.querySelectorAll('.vtv-result[data-id]').forEach((row) => {
            row.onclick = () => onPick(row.dataset.id, row.dataset.type, row.querySelector('strong').textContent);
        });
    }

    async function addProgram(itemId) {
        const channelId = currentChannelId();
        await api('/programming/' + channelId + '/sources', {
            method: 'POST',
            body: {
                jellyfinItemId: itemId,
                targetAirtimePercent: Number($('vtv-new-share').value || 1),
                playbackMode: Number($('vtv-new-mode').value || 0),
                enabled: true
            }
        });
        $('vtv-program-search').value = '';
        $('vtv-program-results').innerHTML = '';
        showStatus('Programming source added.', 'good');
        await loadSelectedChannel();
    }

    async function saveProgramSource(row, id) {
        await api('/programming/sources/' + id, {
            method: 'PUT',
            body: {
                targetAirtimePercent: Number(row.querySelector('[data-role="share"]').value || 1),
                playbackMode: Number(row.querySelector('[data-role="mode"]').value || 0),
                enabled: row.querySelector('[data-role="enabled"]').checked
            }
        });
        showStatus('Programming source updated.', 'good');
        await loadSelectedChannel();
    }

    async function removeProgramSource(id) {
        if (!window.confirm('Remove this source from the channel? The Jellyfin media itself will not be changed.')) return;
        await api('/programming/sources/' + id, { method: 'DELETE' });
        showStatus('Programming source removed.', 'good');
        await loadSelectedChannel();
    }

    async function addFiller(itemId, type) {
        if (type === 'Series' || type === 'Season') {
            showStatus('Promos and bumpers must be individual playable media items, not a series or season.', 'warn');
            return;
        }
        const channelId = currentChannelId();
        await api('/programming/' + channelId + '/fillers', {
            method: 'POST',
            body: {
                jellyfinItemId: itemId,
                kind: Number($('vtv-new-filler-kind').value || 0),
                weight: Number($('vtv-new-filler-weight').value || 1),
                enabled: true
            }
        });
        $('vtv-filler-search').value = '';
        $('vtv-filler-results').innerHTML = '';
        showStatus('Promo / filler item added.', 'good');
        await loadSelectedChannel();
    }

    async function saveFillerSource(row, id) {
        await api('/programming/fillers/' + id, {
            method: 'PUT',
            body: {
                kind: Number(row.querySelector('[data-role="kind"]').value || 0),
                weight: Number(row.querySelector('[data-role="weight"]').value || 1),
                enabled: row.querySelector('[data-role="enabled"]').checked
            }
        });
        showStatus('Promo / filler source updated.', 'good');
        await loadSelectedChannel();
    }

    async function removeFillerSource(id) {
        if (!window.confirm('Remove this item from the channel filler pool? The Jellyfin media itself will not be changed.')) return;
        await api('/programming/fillers/' + id, { method: 'DELETE' });
        showStatus('Promo / filler source removed.', 'good');
        await loadSelectedChannel();
    }

    async function rebuildChannel() {
        const channelId = currentChannelId();
        if (!channelId) return;
        await api('/programming/' + channelId + '/rebuild', { method: 'POST' });
        showStatus('Channel rebuild started…');
        pollRebuild(channelId, 0);
    }

    async function pollRebuild(channelId, attempt) {
        if (attempt > 120 || currentChannelId() !== channelId) return;
        try {
            const status = await api('/programming/' + channelId + '/rebuild/status');
            const name = status?.state || 'idle';
            if (name === 'completed') {
                showStatus(`Rebuild complete. ${status.playoutItemCount || 0} future playout items are ready.`, 'good');
                await loadSelectedChannel();
                return;
            }
            if (name === 'failed') {
                showStatus('Rebuild failed: ' + (status.error || 'Unknown error'), 'warn');
                return;
            }
            showStatus('Channel rebuild ' + name + '…');
            window.setTimeout(() => pollRebuild(channelId, attempt + 1), 1500);
        } catch (err) {
            showStatus(err.message, 'warn');
        }
    }

    function bindSearch(inputId, resultsId, onPick) {
        const input = $(inputId);
        if (!input) return;
        input.oninput = () => {
            window.clearTimeout(searchTimer);
            searchTimer = window.setTimeout(() => {
                searchCatalog(input.value, resultsId, onPick).catch((err) => showStatus(err.message, 'warn'));
            }, 250);
        };
    }

    function bindEvents() {
        $('vtv-channel').onchange = () => loadSelectedChannel().catch((err) => showStatus(err.message, 'warn'));
        $('vtv-refresh').onclick = () => loadChannels().catch((err) => showStatus(err.message, 'warn'));
        $('vtv-save-settings').onclick = () => saveSettings().catch((err) => showStatus(err.message, 'warn'));
        $('vtv-rebuild').onclick = () => rebuildChannel().catch((err) => showStatus(err.message, 'warn'));
        bindSearch('vtv-program-search', 'vtv-program-results', (id) => addProgram(id).catch((err) => showStatus(err.message, 'warn')));
        bindSearch('vtv-filler-search', 'vtv-filler-results', (id, type) => addFiller(id, type).catch((err) => showStatus(err.message, 'warn')));
    }

    async function init(view) {
        page = view || document.querySelector('#FinTVProgrammingPage');
        if (!page) return;
        if (page.dataset.vtvBound === '1') return;
        page.dataset.vtvBound = '1';
        bindEvents();
        try {
            await loadChannels();
        } catch (err) {
            showStatus(err.message, 'warn');
        }
    }

    window.FinTVProgramming = { init };
})();

export default function (view) {
    function boot() {
        if (window.FinTVProgramming && window.FinTVProgramming.init) {
            window.FinTVProgramming.init(view);
        }
    }
    view.addEventListener('viewshow', boot);
    view.addEventListener('viewdestroy', function () {
        delete view.dataset.vtvBound;
    });
    boot();
}
