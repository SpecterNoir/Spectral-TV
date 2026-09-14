const SpectralTvChannelStudio = (() => {
    const state = {
        root: null,
        mode: 'live',
        liveChannels: [],
        liveSelectedId: null,
        liveProgramming: null,
        odChannels: [],
        odSelectedId: null,
        odSnapshot: null,
        searchTimers: {},
        toastTimer: null,
        disposed: false
    };

    const byId = (id) => state.root?.querySelector('#' + id) || null;
    const all = (selector) => Array.from(state.root?.querySelectorAll(selector) || []);

    function escapeHtml(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function normalize(value) {
        if (Array.isArray(value)) return value.map(normalize);
        if (!value || typeof value !== 'object') return value;
        const result = {};
        Object.keys(value).forEach((key) => {
            const normalizedKey = key.length ? key[0].toLowerCase() + key.slice(1) : key;
            result[normalizedKey] = normalize(value[key]);
        });
        return result;
    }

    function resolveUrl(path) {
        const clean = String(path || '').replace(/^\/+/, '');
        if (typeof ApiClient !== 'undefined' && typeof ApiClient.getUrl === 'function') {
            return ApiClient.getUrl(clean);
        }
        return '/' + clean;
    }

    function authHeaders() {
        const headers = {};
        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
            const token = ApiClient.accessToken();
            if (token) headers['X-Emby-Token'] = token;
        }
        return headers;
    }

    async function request(path, options = {}) {
        const method = options.method || 'GET';
        const headers = { ...authHeaders(), ...(options.headers || {}) };
        let body = options.body;
        if (body !== undefined && body !== null && !(body instanceof FormData)) {
            headers['Content-Type'] = 'application/json';
            body = JSON.stringify(body);
        }
        const response = await fetch(resolveUrl('SpectralTV/api/' + String(path).replace(/^\/+/, '')), {
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
            } catch { }
            throw new Error(message);
        }
        if (response.status === 204 || response.status === 205) return null;
        const text = await response.text();
        if (!text) return null;
        try { return normalize(JSON.parse(text)); } catch { return text; }
    }

    function toast(message, type = 'good') {
        const el = byId('cs-toast');
        if (!el) return;
        el.textContent = message;
        el.classList.remove('cs-hidden');
        el.style.borderColor = type === 'error' ? 'rgba(239,68,68,.55)' : 'rgba(74,222,128,.45)';
        window.clearTimeout(state.toastTimer);
        state.toastTimer = window.setTimeout(() => el.classList.add('cs-hidden'), 3200);
    }

    async function safely(work) {
        try { return await work; } catch (error) { toast(error?.message || 'Something went wrong.', 'error'); return null; }
    }

    function setMode(mode) {
        state.mode = mode;
        all('[data-cs-mode]').forEach((button) => button.classList.toggle('is-active', button.dataset.csMode === mode));
        byId('cs-panel-live')?.classList.toggle('is-active', mode === 'live');
        byId('cs-panel-ondemand')?.classList.toggle('is-active', mode === 'ondemand');
        if (mode === 'live') return safely(loadLive());
        return safely(loadOnDemand());
    }

    function friendlyType(value) {
        return String(value || '').replace(/([a-z])([A-Z])/g, '$1 $2');
    }

    async function searchCatalog(inputId, resultsId, purpose) {
        const input = byId(inputId);
        const results = byId(resultsId);
        if (!input || !results) return;
        const q = input.value.trim();
        if (q.length < 2) {
            results.innerHTML = '';
            results.classList.remove('has-results');
            return;
        }
        results.innerHTML = '<div class="cs-result">Searching…</div>';
        results.classList.add('has-results');
        try {
            const items = await request(`/catalog/search?q=${encodeURIComponent(q)}&limit=30`);
            if (input.value.trim() !== q) return;
            results.innerHTML = items?.length ? items.map((item) => `<button type="button" class="cs-result" data-cs-pick="${purpose}" data-item-id="${item.id}" data-item-type="${escapeHtml(item.type)}"><strong>${escapeHtml(item.name)}</strong><div class="cs-muted">${escapeHtml(friendlyType(item.type))}${item.year ? ' · ' + item.year : ''}${item.runtimeMinutes ? ' · ' + item.runtimeMinutes + ' min' : ''}</div></button>`).join('') : '<div class="cs-result">No matching items</div>';
        } catch (error) {
            results.innerHTML = `<div class="cs-result">${escapeHtml(error.message)}</div>`;
        }
    }

    function queueSearch(inputId, resultsId, purpose) {
        window.clearTimeout(state.searchTimers[inputId]);
        state.searchTimers[inputId] = window.setTimeout(() => searchCatalog(inputId, resultsId, purpose), 250);
    }

    function hideResults(id) {
        const el = byId(id);
        if (!el) return;
        el.innerHTML = '';
        el.classList.remove('has-results');
    }

    // ---- Live automatic rotation ---------------------------------------------------------------

    function nextLiveChannelNumber() {
        if (!state.liveChannels.length) return 1;
        return Math.max(...state.liveChannels.map((channel) => Math.ceil(Number(channel.number) || 0))) + 1;
    }

    function installLiveCreateUi() {
        const livePanel = byId('cs-panel-live');
        const firstCard = livePanel?.querySelector('.cs-card');
        const head = firstCard?.querySelector('.cs-card-head');
        if (!firstCard || !head || firstCard.querySelector('[data-live-create-panel]')) return;

        const newButton = document.createElement('button');
        newButton.type = 'button';
        newButton.className = 'cs-button cs-button-primary cs-button-small';
        newButton.dataset.liveNew = '1';
        newButton.textContent = '+ New live channel';
        head.appendChild(newButton);

        const panel = document.createElement('form');
        panel.className = 'cs-note cs-hidden';
        panel.dataset.liveCreatePanel = '1';
        panel.style.marginTop = '1rem';
        panel.innerHTML = `
            <div class="cs-grid cs-grid-3">
                <label class="cs-field"><span>Channel name</span><input data-live-create-name class="emby-input" type="text" required placeholder="Cartoon Network"></label>
                <label class="cs-field"><span>Channel number</span><input data-live-create-number class="emby-input" type="number" min="1" step="0.1" required></label>
                <label class="cs-field"><span>Picture format</span><select data-live-create-aspect class="emby-select"><option value="0">16:9 widescreen</option><option value="1">4:3 classic TV</option></select></label>
            </div>
            <div class="cs-actions">
                <button type="submit" class="cs-button cs-button-primary">Create channel</button>
                <button type="button" class="cs-button" data-live-create-cancel="1">Cancel</button>
                <span class="cs-muted">Logo, scanlines, and on-screen bug placement remain available under Advanced live channel setup.</span>
            </div>`;
        firstCard.appendChild(panel);
    }

    function openLiveCreate() {
        const panel = state.root?.querySelector('[data-live-create-panel]');
        if (!panel) return;
        const number = panel.querySelector('[data-live-create-number]');
        const name = panel.querySelector('[data-live-create-name]');
        const aspect = panel.querySelector('[data-live-create-aspect]');
        number.value = String(nextLiveChannelNumber());
        name.value = '';
        aspect.value = '0';
        panel.classList.remove('cs-hidden');
        window.setTimeout(() => name.focus(), 0);
    }

    function closeLiveCreate() {
        state.root?.querySelector('[data-live-create-panel]')?.classList.add('cs-hidden');
    }

    async function createLiveChannel() {
        const panel = state.root?.querySelector('[data-live-create-panel]');
        if (!panel) return;
        const name = panel.querySelector('[data-live-create-name]').value.trim();
        const number = Number(panel.querySelector('[data-live-create-number]').value);
        const aspectRatio = Number(panel.querySelector('[data-live-create-aspect]').value || 0);
        if (!name) return toast('Enter a channel name.', 'error');
        if (!Number.isFinite(number) || number < 1) return toast('Enter a valid channel number.', 'error');

        const created = await request('/channels', { method: 'POST', body: {
            number,
            name,
            enabled: true,
            aspectRatio,
            scanlinesEnabled: false,
            bugPlacement: 0,
            logoSetId: null,
            logoFileName: null
        }});
        closeLiveCreate();
        await loadLiveChannels(created.id);
        await loadLiveProgramming();
        toast(`Live channel “${name}” created. Add shows below, then save automatic mode.`);
    }

    async function loadLiveChannels(preferredId = null) {
        const channels = await request('/channels');
        state.liveChannels = Array.isArray(channels) ? channels : [];
        if (preferredId && state.liveChannels.some((c) => c.id === preferredId)) state.liveSelectedId = preferredId;
        if (!state.liveSelectedId || !state.liveChannels.some((c) => c.id === state.liveSelectedId)) state.liveSelectedId = state.liveChannels[0]?.id || null;
        const select = byId('cs-live-channel');
        select.innerHTML = state.liveChannels.length
            ? state.liveChannels.map((c) => `<option value="${c.id}">${escapeHtml(c.number)} · ${escapeHtml(c.name)}</option>`).join('')
            : '<option value="">No live channels yet</option>';
        select.value = state.liveSelectedId || '';
        if (!state.liveChannels.length) openLiveCreate();
    }

    async function loadLive() {
        await loadLiveChannels();
        await loadLiveProgramming();
    }

    async function loadLiveProgramming() {
        if (!state.liveSelectedId) {
            state.liveProgramming = null;
            renderLive();
            return;
        }
        state.liveProgramming = await request(`/programming/${state.liveSelectedId}`);
        renderLive();
    }

    function renderLive() {
        const hasChannel = !!state.liveSelectedId;
        ['cs-live-enabled','cs-live-selection','cs-live-save','cs-live-rebuild','cs-live-search','cs-live-weight','cs-live-order'].forEach((id) => {
            if (byId(id)) byId(id).disabled = !hasChannel;
        });

        const settings = state.liveProgramming?.settings || {};
        byId('cs-live-enabled').checked = hasChannel && settings.enabled === true;
        byId('cs-live-selection').value = String(settings.selectionMode ?? 1);
        const sources = state.liveProgramming?.sources || [];
        const list = byId('cs-live-sources');
        if (!hasChannel) {
            list.innerHTML = '<div class="cs-empty">Create your first live channel above, then add shows or movies here.</div>';
            return;
        }
        if (!sources.length) {
            list.innerHTML = '<div class="cs-empty">No automatic programming yet. Search above to add a show or movie.</div>';
            return;
        }
        list.innerHTML = sources.map((source) => `<article class="cs-row" data-live-source-id="${source.id}">
            <div class="cs-row-title"><strong>${escapeHtml(source.name)}</strong><small>${escapeHtml(friendlyType(source.type))}</small></div>
            <label class="cs-field"><span>Share</span><input data-field="weight" class="emby-input" type="number" min="0.1" step="0.1" value="${Number(source.targetAirtimePercent || 1)}"></label>
            <label class="cs-field"><span>Episodes</span><select data-field="mode" class="emby-select"><option value="0"${Number(source.playbackMode) === 0 ? ' selected' : ''}>Sequential</option><option value="1"${Number(source.playbackMode) === 1 ? ' selected' : ''}>Random</option></select></label>
            <label class="cs-check"><input data-field="enabled" type="checkbox"${source.enabled !== false ? ' checked' : ''}><span><strong>Enabled</strong></span></label>
            <button type="button" class="cs-button cs-button-danger cs-button-small" data-live-remove="${source.id}">Remove</button>
        </article>`).join('');
    }

    async function saveLiveSettings() {
        if (!state.liveSelectedId) return toast('Create or select a live channel first.', 'error');
        const previous = state.liveProgramming?.settings || {};
        await request(`/programming/${state.liveSelectedId}/settings`, { method: 'PUT', body: {
            enabled: byId('cs-live-enabled').checked,
            selectionMode: Number(byId('cs-live-selection').value || 0),
            fillerEnabled: previous.fillerEnabled === true,
            fillerChancePercent: Number(previous.fillerChancePercent ?? 100),
            minFillerItems: Number(previous.minFillerItems ?? 1),
            maxFillerItems: Number(previous.maxFillerItems ?? 2),
            maxFillerSeconds: Number(previous.maxFillerSeconds ?? 180),
            fillerRepeatWindow: Number(previous.fillerRepeatWindow ?? 12)
        }});
        await loadLiveProgramming();
        toast('Automatic live mode saved.');
    }

    async function addLiveSource(itemId) {
        if (!state.liveSelectedId) return toast('Create or select a live channel first.', 'error');
        await request(`/programming/${state.liveSelectedId}/sources`, { method: 'POST', body: {
            jellyfinItemId: itemId,
            targetAirtimePercent: Number(byId('cs-live-weight').value || 1),
            playbackMode: Number(byId('cs-live-order').value || 0),
            enabled: true
        }});
        byId('cs-live-search').value = '';
        hideResults('cs-live-results');
        await loadLiveProgramming();
        toast('Added to live programming.');
    }

    async function updateLiveSource(article) {
        await request(`/programming/sources/${article.dataset.liveSourceId}`, { method: 'PUT', body: {
            targetAirtimePercent: Number(article.querySelector('[data-field="weight"]').value || 1),
            playbackMode: Number(article.querySelector('[data-field="mode"]').value || 0),
            enabled: article.querySelector('[data-field="enabled"]').checked
        }});
        await loadLiveProgramming();
    }

    async function removeLiveSource(id) {
        await request(`/programming/sources/${id}`, { method: 'DELETE' });
        await loadLiveProgramming();
        toast('Removed from live programming.');
    }

    async function rebuildLive() {
        if (!state.liveSelectedId) return;
        await request(`/programming/${state.liveSelectedId}/rebuild`, { method: 'POST' });
        toast('Live channel rebuild queued.');
    }

    // ---- On-demand recipes --------------------------------------------------------------------

    async function loadOdChannels(preferredId = null) {
        const channels = await request('/on-demand');
        state.odChannels = Array.isArray(channels) ? channels : [];
        if (preferredId && state.odChannels.some((c) => c.id === preferredId)) state.odSelectedId = preferredId;
        if (!state.odSelectedId || !state.odChannels.some((c) => c.id === state.odSelectedId)) state.odSelectedId = state.odChannels[0]?.id || null;
        const select = byId('cs-od-channel');
        select.innerHTML = state.odChannels.length
            ? state.odChannels.map((c) => `<option value="${c.id}">${escapeHtml(c.name)}</option>`).join('')
            : '<option value="">No on-demand channels yet</option>';
        select.value = state.odSelectedId || '';
    }

    async function loadOnDemand() {
        await loadOdChannels();
        await loadOdSnapshot();
    }

    async function loadOdSnapshot() {
        if (!state.odSelectedId) {
            state.odSnapshot = null;
            renderOnDemand();
            return;
        }
        state.odSnapshot = await request(`/on-demand/${state.odSelectedId}`);
        renderOnDemand();
    }

    function sourceIndexMap() {
        const map = new Map();
        (state.odSnapshot?.sources || []).forEach((source, index) => map.set(source.id, index + 1));
        return map;
    }

    function patternToUi(json) {
        let ids = [];
        try { ids = JSON.parse(json || '[]'); } catch { }
        const map = sourceIndexMap();
        return ids.map((id) => map.get(id)).filter(Boolean).join(', ');
    }

    function patternFromUi() {
        const sources = state.odSnapshot?.sources || [];
        const indexes = byId('cs-od-pattern').value.split(',').map((value) => Number(value.trim())).filter((value) => Number.isInteger(value) && value > 0 && value <= sources.length);
        return JSON.stringify(indexes.map((index) => sources[index - 1].id));
    }

    function renderOnDemand() {
        const channel = state.odSnapshot?.channel || null;
        const has = !!channel;
        ['cs-od-name','cs-od-programming-mode','cs-od-rotation','cs-od-enabled','cs-od-pattern','cs-od-save','cs-od-delete','cs-od-preview','cs-od-resume','cs-od-next','cs-od-reset','cs-od-search','cs-od-filler-search'].forEach((id) => {
            if (byId(id)) byId(id).disabled = !has;
        });
        if (!has) {
            byId('cs-od-name').value = '';
            byId('cs-od-sources').innerHTML = '<div class="cs-empty">Create an on-demand channel to begin.</div>';
            byId('cs-od-fillers').innerHTML = '<div class="cs-empty">Promos can be added after you create a channel.</div>';
            byId('cs-od-preview-list').innerHTML = '';
            return;
        }

        byId('cs-od-name').value = channel.name || '';
        byId('cs-od-enabled').checked = channel.enabled !== false;
        byId('cs-od-programming-mode').value = String(channel.programmingMode ?? 0);
        byId('cs-od-rotation').value = String(channel.rotationMode ?? 0);
        byId('cs-od-pattern').value = patternToUi(channel.customPatternJson);
        byId('cs-od-filler-enabled').checked = channel.fillerEnabled !== false;
        byId('cs-od-before').checked = channel.fillerBeforeFirstProgram !== false;
        byId('cs-od-between').checked = channel.fillerBetweenPrograms !== false;
        byId('cs-od-switch-only').checked = channel.fillerOnSourceChangeOnly === true;
        byId('cs-od-filler-min').value = String(channel.minFillerItems ?? 1);
        byId('cs-od-filler-max').value = String(channel.maxFillerItems ?? 1);
        byId('cs-od-filler-repeat').value = String(channel.fillerRepeatWindow ?? 10);
        renderOdSources();
        renderOdFillers();
    }

    function renderOdSources() {
        const sources = state.odSnapshot?.sources || [];
        const list = byId('cs-od-sources');
        if (!sources.length) {
            list.innerHTML = '<div class="cs-empty">No programming sources yet. Add a show, season, exact episode, or movie.</div>';
            return;
        }
        list.innerHTML = sources.map((source, index) => `<article class="cs-row" data-od-source-id="${source.id}" data-sort-order="${source.sortOrder ?? index}">
            <div class="cs-row-title"><strong>#${index + 1} · ${escapeHtml(source.name)}</strong><small>${escapeHtml(friendlyType(source.type))}</small></div>
            <label class="cs-field"><span>Weight</span><input data-field="weight" class="emby-input" type="number" min="1" max="1000" value="${Number(source.weight || 1)}"></label>
            <label class="cs-field"><span>Episodes/turn</span><input data-field="block" class="emby-input" type="number" min="1" max="100" value="${Number(source.blockSize || 1)}"></label>
            <label class="cs-field"><span>Episode order</span><select data-field="mode" class="emby-select"><option value="0"${Number(source.playbackMode) === 0 ? ' selected' : ''}>Sequential</option><option value="1"${Number(source.playbackMode) === 1 ? ' selected' : ''}>Random</option></select></label>
            <div class="cs-actions" style="margin:0"><label title="Enabled"><input data-field="enabled" type="checkbox"${source.enabled !== false ? ' checked' : ''}></label><button type="button" class="cs-button cs-button-danger cs-button-small" data-od-source-remove="${source.id}">Remove</button></div>
        </article>`).join('');
    }

    function renderOdFillers() {
        const fillers = state.odSnapshot?.fillers || [];
        const list = byId('cs-od-fillers');
        if (!fillers.length) {
            list.innerHTML = '<div class="cs-empty">No promo/interstitial clips yet.</div>';
            return;
        }
        const kinds = ['Promo','Bumper','Commercial','Station ID'];
        list.innerHTML = fillers.map((filler, index) => `<article class="cs-row" data-od-filler-id="${filler.id}" data-sort-order="${filler.sortOrder ?? index}">
            <div class="cs-row-title"><strong>${escapeHtml(filler.name)}</strong><small>${escapeHtml(friendlyType(filler.type))}</small></div>
            <label class="cs-field"><span>Type</span><select data-field="kind" class="emby-select">${kinds.map((name, kind) => `<option value="${kind}"${Number(filler.kind) === kind ? ' selected' : ''}>${name}</option>`).join('')}</select></label>
            <label class="cs-field"><span>Weight</span><input data-field="weight" class="emby-input" type="number" min="1" max="1000" value="${Number(filler.weight || 1)}"></label>
            <label class="cs-check"><input data-field="enabled" type="checkbox"${filler.enabled !== false ? ' checked' : ''}><span><strong>Enabled</strong></span></label>
            <button type="button" class="cs-button cs-button-danger cs-button-small" data-od-filler-remove="${filler.id}">Remove</button>
        </article>`).join('');
    }

    async function createOdChannel() {
        const created = await request('/on-demand', { method: 'POST', body: {
            name: 'New On-Demand Channel', enabled: true, programmingMode: 0, rotationMode: 0,
            customPatternJson: '[]', fillerEnabled: true, fillerBeforeFirstProgram: true,
            fillerBetweenPrograms: true, fillerOnSourceChangeOnly: false,
            minFillerItems: 1, maxFillerItems: 1, fillerRepeatWindow: 10
        }});
        await loadOdChannels(created.id);
        await loadOdSnapshot();
        byId('cs-od-name').focus();
        byId('cs-od-name').select();
        toast('On-demand channel created.');
    }

    async function saveOdChannel() {
        const channel = state.odSnapshot?.channel;
        if (!channel) return;
        const name = byId('cs-od-name').value.trim();
        if (!name) return toast('Enter a channel name.', 'error');
        const min = Number(byId('cs-od-filler-min').value || 0);
        const max = Number(byId('cs-od-filler-max').value || 0);
        if (max < min) return toast('Maximum promo clips must be at least the minimum.', 'error');
        await request(`/on-demand/${channel.id}`, { method: 'PUT', body: {
            name,
            enabled: byId('cs-od-enabled').checked,
            programmingMode: Number(byId('cs-od-programming-mode').value || 0),
            rotationMode: Number(byId('cs-od-rotation').value || 0),
            customPatternJson: patternFromUi(),
            fillerEnabled: byId('cs-od-filler-enabled').checked,
            fillerBeforeFirstProgram: byId('cs-od-before').checked,
            fillerBetweenPrograms: byId('cs-od-between').checked,
            fillerOnSourceChangeOnly: byId('cs-od-switch-only').checked,
            minFillerItems: min,
            maxFillerItems: max,
            fillerRepeatWindow: Number(byId('cs-od-filler-repeat').value || 0)
        }});
        await loadOdChannels(channel.id);
        await loadOdSnapshot();
        toast('On-demand recipe saved.');
    }

    async function deleteOdChannel() {
        const channel = state.odSnapshot?.channel;
        if (!channel) return;
        if (!window.confirm(`Delete “${channel.name}”? Your Jellyfin media will not be changed.`)) return;
        await request(`/on-demand/${channel.id}`, { method: 'DELETE' });
        state.odSelectedId = null;
        await loadOnDemand();
        toast('On-demand channel deleted.');
    }

    async function addOdSource(itemId) {
        if (!state.odSelectedId) return;
        await request(`/on-demand/${state.odSelectedId}/sources`, { method: 'POST', body: {
            jellyfinItemId: itemId,
            weight: Number(byId('cs-od-weight').value || 1),
            blockSize: Number(byId('cs-od-block').value || 1),
            playbackMode: Number(byId('cs-od-order').value || 0),
            enabled: true
        }});
        byId('cs-od-search').value = '';
        hideResults('cs-od-results');
        await loadOdSnapshot();
        toast('Programming source added.');
    }

    async function updateOdSource(article) {
        await request(`/on-demand/sources/${article.dataset.odSourceId}`, { method: 'PUT', body: {
            weight: Number(article.querySelector('[data-field="weight"]').value || 1),
            blockSize: Number(article.querySelector('[data-field="block"]').value || 1),
            playbackMode: Number(article.querySelector('[data-field="mode"]').value || 0),
            enabled: article.querySelector('[data-field="enabled"]').checked,
            sortOrder: Number(article.dataset.sortOrder || 0)
        }});
        await loadOdSnapshot();
    }

    async function removeOdSource(id) {
        await request(`/on-demand/sources/${id}`, { method: 'DELETE' });
        await loadOdSnapshot();
        toast('Programming source removed.');
    }

    async function addOdFiller(itemId, itemType) {
        if (!state.odSelectedId) return;
        if (itemType === 'Series' || itemType === 'Season') return toast('Promos must be individual playable clips.', 'error');
        await request(`/on-demand/${state.odSelectedId}/fillers`, { method: 'POST', body: {
            jellyfinItemId: itemId,
            kind: Number(byId('cs-od-filler-kind').value || 0),
            weight: Number(byId('cs-od-filler-weight').value || 1),
            enabled: true
        }});
        byId('cs-od-filler-search').value = '';
        hideResults('cs-od-filler-results');
        await loadOdSnapshot();
        toast('Promo/interstitial added.');
    }

    async function updateOdFiller(article) {
        await request(`/on-demand/fillers/${article.dataset.odFillerId}`, { method: 'PUT', body: {
            kind: Number(article.querySelector('[data-field="kind"]').value || 0),
            weight: Number(article.querySelector('[data-field="weight"]').value || 1),
            enabled: article.querySelector('[data-field="enabled"]').checked,
            sortOrder: Number(article.dataset.sortOrder || 0)
        }});
        await loadOdSnapshot();
    }

    async function removeOdFiller(id) {
        await request(`/on-demand/fillers/${id}`, { method: 'DELETE' });
        await loadOdSnapshot();
        toast('Promo/interstitial removed.');
    }

    function formatTicks(ticks) {
        const seconds = Math.floor(Number(ticks || 0) / 10000000);
        if (seconds <= 0) return '';
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = seconds % 60;
        return h ? `${h}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}` : `${m}:${String(s).padStart(2,'0')}`;
    }

    function renderQueue(result) {
        const list = byId('cs-od-preview-list');
        const items = result?.items || [];
        if (!items.length) {
            list.innerHTML = '<div class="cs-empty">Nothing could be generated. Add at least one valid programming source.</div>';
            return;
        }
        list.innerHTML = items.map((item, index) => `<div class="cs-preview-item${item.kind === 'program' ? ' is-program' : ''}"><span class="cs-preview-kind">${escapeHtml(item.kind)}</span><span><strong>${index + 1}. ${escapeHtml(item.title)}</strong>${item.sourceName ? `<div class="cs-muted">${escapeHtml(item.sourceName)}</div>` : ''}${item.resumePositionTicks ? `<div class="cs-muted">Resume at ${formatTicks(item.resumePositionTicks)}</div>` : ''}</span></div>`).join('');
    }

    async function previewOd() {
        if (!state.odSelectedId) return;
        const result = await request(`/on-demand/${state.odSelectedId}/preview?count=12`);
        renderQueue(result);
    }

    function testProgressKey() {
        return byId('cs-od-progress-key').value.trim() || 'test';
    }

    async function testResume(completeCurrent) {
        if (!state.odSelectedId) return;
        const result = await request(`/on-demand/${state.odSelectedId}/next`, { method: 'POST', body: {
            progressKey: testProgressKey(), completeCurrent
        }});
        renderQueue(result);
        const current = result?.progress?.currentItemId;
        toast(current ? (completeCurrent ? 'Advanced to the next program.' : 'Current program resumed.') : 'Sequence updated.');
    }

    async function resetTestProgress() {
        if (!state.odSelectedId) return;
        await request(`/on-demand/${state.odSelectedId}/progress?progressKey=${encodeURIComponent(testProgressKey())}`, { method: 'DELETE' });
        byId('cs-od-preview-list').innerHTML = '';
        toast('Test progress reset.');
    }

    function bindEvents() {
        state.root.addEventListener('click', (event) => {
            const modeButton = event.target.closest('[data-cs-mode]');
            if (modeButton) return setMode(modeButton.dataset.csMode);
            const liveNew = event.target.closest('[data-live-new]');
            if (liveNew) return openLiveCreate();
            const liveCancel = event.target.closest('[data-live-create-cancel]');
            if (liveCancel) return closeLiveCreate();
            const pick = event.target.closest('[data-cs-pick]');
            if (pick) {
                if (pick.dataset.csPick === 'live') return safely(addLiveSource(pick.dataset.itemId));
                if (pick.dataset.csPick === 'od') return safely(addOdSource(pick.dataset.itemId));
                if (pick.dataset.csPick === 'od-filler') return safely(addOdFiller(pick.dataset.itemId, pick.dataset.itemType));
            }
            const liveRemove = event.target.closest('[data-live-remove]');
            if (liveRemove) return safely(removeLiveSource(liveRemove.dataset.liveRemove));
            const odRemove = event.target.closest('[data-od-source-remove]');
            if (odRemove) return safely(removeOdSource(odRemove.dataset.odSourceRemove));
            const fillerRemove = event.target.closest('[data-od-filler-remove]');
            if (fillerRemove) return safely(removeOdFiller(fillerRemove.dataset.odFillerRemove));
        });

        state.root.addEventListener('submit', (event) => {
            if (!event.target.matches('[data-live-create-panel]')) return;
            event.preventDefault();
            safely(createLiveChannel());
        });

        state.root.addEventListener('change', (event) => {
            const liveSource = event.target.closest('[data-live-source-id]');
            if (liveSource && event.target.matches('[data-field]')) return safely(updateLiveSource(liveSource));
            const odSource = event.target.closest('[data-od-source-id]');
            if (odSource && event.target.matches('[data-field]')) return safely(updateOdSource(odSource));
            const filler = event.target.closest('[data-od-filler-id]');
            if (filler && event.target.matches('[data-field]')) return safely(updateOdFiller(filler));
        });

        byId('cs-live-channel').addEventListener('change', () => { state.liveSelectedId = byId('cs-live-channel').value || null; safely(loadLiveProgramming()); });
        byId('cs-live-save').addEventListener('click', () => safely(saveLiveSettings()));
        byId('cs-live-rebuild').addEventListener('click', () => safely(rebuildLive()));
        byId('cs-live-search').addEventListener('input', () => queueSearch('cs-live-search', 'cs-live-results', 'live'));

        byId('cs-od-channel').addEventListener('change', () => { state.odSelectedId = byId('cs-od-channel').value || null; safely(loadOdSnapshot()); });
        byId('cs-od-new').addEventListener('click', () => safely(createOdChannel()));
        byId('cs-od-save').addEventListener('click', () => safely(saveOdChannel()));
        byId('cs-od-delete').addEventListener('click', () => safely(deleteOdChannel()));
        byId('cs-od-search').addEventListener('input', () => queueSearch('cs-od-search', 'cs-od-results', 'od'));
        byId('cs-od-filler-search').addEventListener('input', () => queueSearch('cs-od-filler-search', 'cs-od-filler-results', 'od-filler'));
        byId('cs-od-preview').addEventListener('click', () => safely(previewOd()));
        byId('cs-od-resume').addEventListener('click', () => safely(testResume(false)));
        byId('cs-od-next').addEventListener('click', () => safely(testResume(true)));
        byId('cs-od-reset').addEventListener('click', () => safely(resetTestProgress()));
    }

    async function init(root) {
        state.root = root || document.querySelector('#SpectralTVChannelStudioPage');
        if (!state.root || state.root.dataset.spectralStudioBound === '1') return;
        state.root.dataset.spectralStudioBound = '1';
        state.disposed = false;
        installLiveCreateUi();
        bindEvents();
        await safely(loadLive());
    }

    function dispose() {
        state.disposed = true;
        Object.values(state.searchTimers).forEach((timer) => window.clearTimeout(timer));
        window.clearTimeout(state.toastTimer);
        if (state.root) delete state.root.dataset.spectralStudioBound;
        state.root = null;
    }

    return { init, dispose };
})();

export default function (view) {
    const boot = () => SpectralTvChannelStudio.init(view);
    view.addEventListener('viewshow', boot);
    view.addEventListener('viewdestroy', () => SpectralTvChannelStudio.dispose());
    boot();
}
