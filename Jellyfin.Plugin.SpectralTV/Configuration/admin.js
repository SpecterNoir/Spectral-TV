const SpectralTvAdmin = (() => {
    const state = {
        root: null,
        channels: [],
        selectedId: null,
        programming: null,
        logoSets: [],
        setup: null,
        general: null,
        timezones: [],
        step: 'channel',
        searchTimers: {},
        toastTimer: null,
        rebuildTimer: null,
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

    function errorMessage(text, fallback) {
        if (!text) return fallback;
        try {
            const parsed = JSON.parse(text);
            return parsed.message || parsed.detail || parsed.title || fallback;
        } catch {
            return text.length < 500 ? text : fallback;
        }
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
            throw new Error(errorMessage(text, `${method} failed (${response.status})`));
        }
        if (response.status === 204) return null;
        const text = await response.text();
        return text ? normalize(JSON.parse(text)) : null;
    }

    function setBusy(busy, label) {
        state.root?.classList.toggle('st-busy', !!busy);
        const status = byId('st-header-status');
        if (status) status.textContent = busy ? (label || 'Working…') : `${state.channels.length} channel${state.channels.length === 1 ? '' : 's'}`;
    }

    function toast(message, kind = '') {
        const element = byId('st-toast');
        if (!element) return;
        window.clearTimeout(state.toastTimer);
        element.textContent = message;
        element.className = `st-toast${kind ? ' is-' + kind : ''}`;
        state.toastTimer = window.setTimeout(() => element.classList.add('st-hidden'), 5200);
    }

    async function action(label, work, successMessage) {
        setBusy(true, label);
        try {
            const result = await work();
            if (successMessage) toast(successMessage, 'good');
            return result;
        } catch (error) {
            toast(error?.message || String(error), 'error');
            throw error;
        } finally {
            setBusy(false);
        }
    }

    function safely(promise) {
        Promise.resolve(promise).catch(() => {
            // action() already turns failures into a visible toast. Prevent an additional
            // unhandled-promise error from Jellyfin's event dispatcher.
        });
    }

    function selectedChannel() {
        return state.channels.find((channel) => channel.id === state.selectedId) || null;
    }

    function toggle(id, visible) {
        byId(id)?.classList.toggle('st-hidden', !visible);
    }

    function switchStep(step) {
        state.step = step;
        all('[data-step]').forEach((button) => button.classList.toggle('is-active', button.dataset.step === step));
        all('[data-panel]').forEach((panel) => panel.classList.toggle('is-active', panel.dataset.panel === step));
        state.root?.querySelector('.st-shell')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    function formatChannelNumber(value) {
        const number = Number(value);
        return Number.isInteger(number) ? String(number) : number.toFixed(1);
    }

    function friendlyType(type) {
        const names = { Series: 'Series', Season: 'Season', Episode: 'Episode', Movie: 'Movie', Video: 'Video clip' };
        return names[type] || type || 'Media';
    }

    function formatDate(value) {
        if (!value) return 'Not built yet';
        const date = new Date(value);
        return Number.isNaN(date.valueOf()) ? 'Not built yet' : date.toLocaleString();
    }

    function nextChannelNumber() {
        if (!state.channels.length) return 1;
        return Math.max(...state.channels.map((channel) => Math.ceil(Number(channel.number) || 0))) + 1;
    }

    function renderChannels() {
        const list = byId('st-channel-list');
        const count = byId('st-channel-count');
        if (!list || !count) return;
        count.textContent = `${state.channels.length} channel${state.channels.length === 1 ? '' : 's'}`;
        if (!state.channels.length) {
            list.innerHTML = '<div class="st-empty">No channels yet.<br>Create your first one.</div>';
            return;
        }
        list.innerHTML = state.channels.map((channel) => `
            <button type="button" class="st-channel-item${channel.id === state.selectedId ? ' is-active' : ''}" data-channel-id="${channel.id}">
                <span class="st-channel-number">${escapeHtml(formatChannelNumber(channel.number))}</span>
                <span class="st-channel-name">${escapeHtml(channel.name)}</span>
                <span class="st-channel-state${channel.enabled ? ' is-on' : ''}" title="${channel.enabled ? 'Enabled' : 'Disabled'}"></span>
            </button>`).join('');
    }

    function renderChannelForm() {
        const channel = selectedChannel();
        toggle('st-channel-empty', !channel);
        toggle('st-channel-form', !!channel);
        toggle('st-branding-card', !!channel);
        if (!channel) return;
        byId('st-channel-name').value = channel.name || '';
        byId('st-channel-number').value = formatChannelNumber(channel.number);
        byId('st-channel-aspect').value = String(Number(channel.aspectRatio || 0));
        byId('st-channel-bug').value = String(Number(channel.bugPlacement || 0));
        byId('st-channel-enabled').checked = channel.enabled !== false;
        byId('st-channel-scanlines').checked = !!channel.scanlinesEnabled;

        const hasLogo = !!(channel.logoSetId && channel.logoFileName);
        toggle('st-clear-logo', hasLogo);
        toggle('st-logo-preview', hasLogo);
        if (hasLogo) {
            byId('st-logo-preview').src = resolveUrl(`SpectralTV/api/logos/${channel.id}/${encodeURIComponent(channel.logoFileName)}`);
        }
    }

    function channelPayload(channel, overrides = {}) {
        return {
            number: Number(channel.number),
            name: channel.name,
            enabled: channel.enabled !== false,
            aspectRatio: Number(channel.aspectRatio || 0),
            scanlinesEnabled: !!channel.scanlinesEnabled,
            bugPlacement: Number(channel.bugPlacement || 0),
            logoSetId: channel.logoSetId || null,
            logoFileName: channel.logoFileName || null,
            ...overrides
        };
    }

    async function loadChannels(preferredId) {
        const channels = await request('/channels');
        state.channels = Array.isArray(channels) ? channels : [];
        const requested = preferredId || state.selectedId;
        state.selectedId = state.channels.some((channel) => channel.id === requested)
            ? requested
            : (state.channels[0]?.id || null);
        renderChannels();
        await loadSelectedChannel();
        setBusy(false);
    }

    async function selectChannel(id) {
        if (state.selectedId === id) return;
        state.selectedId = id;
        renderChannels();
        await action('Loading channel…', loadSelectedChannel);
    }

    async function loadSelectedChannel() {
        renderChannelForm();
        if (!state.selectedId) {
            state.programming = null;
            renderProgramming();
            renderBreaks();
            renderFinish();
            return;
        }
        state.programming = await request(`/programming/${encodeURIComponent(state.selectedId)}`);
        renderProgramming();
        renderBreaks();
        renderFinish();
    }

    function openNewChannel() {
        byId('st-new-number').value = String(nextChannelNumber());
        byId('st-new-name').value = '';
        toggle('st-modal', true);
        window.setTimeout(() => byId('st-new-name')?.focus(), 0);
    }

    function closeNewChannel() {
        toggle('st-modal', false);
    }

    async function createChannel(event) {
        event.preventDefault();
        const name = byId('st-new-name').value.trim();
        const number = Number(byId('st-new-number').value);
        if (!name) return toast('Enter a channel name.', 'error');
        if (!Number.isFinite(number) || number < 1) return toast('Enter a valid channel number.', 'error');
        const created = await action('Creating channel…', async () => {
            const channel = await request('/channels', { method: 'POST', body: {
                number,
                name,
                enabled: true,
                aspectRatio: Number(byId('st-new-aspect').value || 0),
                scanlinesEnabled: false,
                bugPlacement: 0,
                logoSetId: null,
                logoFileName: null
            }});
            return channel;
        }, `Channel “${name}” created.`);
        closeNewChannel();
        await loadChannels(created.id);
        switchStep('programming');
    }

    async function saveChannel(event) {
        event.preventDefault();
        const current = selectedChannel();
        if (!current) return;
        const name = byId('st-channel-name').value.trim();
        const number = Number(byId('st-channel-number').value);
        if (!name) return toast('Enter a channel name.', 'error');
        if (!Number.isFinite(number) || number < 1) return toast('Enter a valid channel number.', 'error');
        await action('Saving channel…', async () => {
            await request(`/channels/${current.id}`, { method: 'PUT', body: channelPayload(current, {
                name,
                number,
                enabled: byId('st-channel-enabled').checked,
                aspectRatio: Number(byId('st-channel-aspect').value || 0),
                scanlinesEnabled: byId('st-channel-scanlines').checked,
                bugPlacement: Number(byId('st-channel-bug').value || 0)
            })});
            await loadChannels(current.id);
        }, 'Channel saved.');
    }

    async function deleteChannel() {
        const channel = selectedChannel();
        if (!channel) return;
        if (!window.confirm(`Delete “${channel.name}”? This removes the virtual channel and its schedule, but never deletes your Jellyfin media.`)) return;
        await action('Deleting channel…', async () => {
            await request(`/channels/${channel.id}`, { method: 'DELETE' });
            state.selectedId = null;
            await loadChannels();
        }, `Channel “${channel.name}” deleted.`);
    }

    async function loadLogoSets() {
        const sets = await request('/logos/sets');
        state.logoSets = Array.isArray(sets) ? sets : [];
    }

    async function getCustomLogoSet() {
        let set = state.logoSets.find((item) => item.isCustom && item.name === 'My Channel Logos')
            || state.logoSets.find((item) => item.isCustom);
        if (!set) {
            set = await request('/logos/sets/custom', { method: 'POST', body: { name: 'My Channel Logos' } });
            state.logoSets.push(set);
        }
        return set;
    }

    async function uploadLogo() {
        const channel = selectedChannel();
        const file = byId('st-logo-file').files?.[0];
        if (!channel) return toast('Select a channel first.', 'error');
        if (!file) return toast('Choose a PNG, JPG, or WebP image first.', 'error');
        await action('Uploading logo…', async () => {
            const set = await getCustomLogoSet();
            const form = new FormData();
            form.append('file', file);
            form.append('displayName', channel.name);
            const entry = await request(`/logos/sets/${set.id}/logos`, { method: 'POST', body: form });
            await request(`/channels/${channel.id}`, { method: 'PUT', body: channelPayload(channel, {
                logoSetId: set.id,
                logoFileName: entry.fileName
            })});
            byId('st-logo-file').value = '';
            await Promise.all([loadLogoSets(), loadChannels(channel.id)]);
        }, 'Channel logo updated.');
    }

    async function clearLogo() {
        const channel = selectedChannel();
        if (!channel) return;
        await action('Removing logo…', async () => {
            await request(`/channels/${channel.id}`, { method: 'PUT', body: channelPayload(channel, {
                logoSetId: null,
                logoFileName: null,
                bugPlacement: 5
            })});
            await loadChannels(channel.id);
        }, 'Channel logo removed.');
    }

    function enabledSources() {
        return (state.programming?.sources || []).filter((source) => source.enabled !== false);
    }

    function renderProgramming() {
        const hasChannel = !!selectedChannel();
        toggle('st-program-empty', !hasChannel);
        toggle('st-program-content', hasChannel);
        if (!hasChannel) return;
        const sources = state.programming?.sources || [];
        const active = enabledSources();
        const total = active.reduce((sum, source) => sum + Number(source.targetAirtimePercent || 0), 0);
        const summary = byId('st-program-summary');
        if (summary) {
            summary.innerHTML = sources.length
                ? `<span><strong>${active.length}</strong> active source${active.length === 1 ? '' : 's'}</span><span class="st-muted">Shares total ${escapeHtml(Math.round(total * 10) / 10)} and normalize automatically</span>`
                : '<span><strong>No programming yet</strong></span><span class="st-muted">Search below to add your first show or movie</span>';
        }
        const list = byId('st-program-list');
        if (!list) return;
        if (!sources.length) {
            list.innerHTML = '<div class="st-empty">Your channel is empty. Search above and select a result to add it.</div>';
            return;
        }
        list.innerHTML = sources.map((source) => {
            const effective = source.enabled !== false && total > 0 ? (Number(source.targetAirtimePercent || 0) / total) * 100 : 0;
            return `<article class="st-source" data-program-id="${source.id}">
                <div class="st-source-title"><strong>${escapeHtml(source.name)}</strong><small>${escapeHtml(friendlyType(source.type))}${source.runtimeMinutes ? ` · ${source.runtimeMinutes} min` : ''}</small></div>
                <div class="st-source-share"><input class="emby-input" data-field="share" type="number" min="0.1" step="0.1" value="${Number(source.targetAirtimePercent || 1)}"><small>${effective.toFixed(1)}%</small><div class="st-meter"><span style="width:${Math.min(100, effective)}%"></span></div></div>
                <select class="emby-select" data-field="mode"><option value="0"${Number(source.playbackMode) === 0 ? ' selected' : ''}>Sequential</option><option value="1"${Number(source.playbackMode) === 1 ? ' selected' : ''}>Random</option></select>
                <div class="st-actions" style="margin:0"><label title="Include this source"><input data-field="enabled" type="checkbox"${source.enabled !== false ? ' checked' : ''}></label><button type="button" class="st-button st-button-danger st-button-small" data-remove-program="${source.id}">Remove</button></div>
            </article>`;
        }).join('');
    }

    function hideSearchResults(id) {
        const results = byId(id);
        if (!results) return;
        results.innerHTML = '';
        results.classList.remove('has-results');
    }

    async function searchCatalog(inputId, resultsId, purpose) {
        const input = byId(inputId);
        const results = byId(resultsId);
        if (!input || !results) return;
        const query = input.value.trim();
        if (query.length < 2) return hideSearchResults(resultsId);
        results.innerHTML = '<div class="st-result"><span>Searching…</span></div>';
        results.classList.add('has-results');
        try {
            const items = await request(`/catalog/search?q=${encodeURIComponent(query)}&purpose=${encodeURIComponent(purpose)}&limit=30`);
            if (input.value.trim() !== query) return;
            results.innerHTML = items.length ? items.map((item) => `
                <button type="button" class="st-result" data-pick="${purpose}" data-item-id="${item.id}" data-item-type="${escapeHtml(item.type)}">
                    <span><strong>${escapeHtml(item.name)}</strong><span class="st-result-meta">${escapeHtml(friendlyType(item.type))}${item.year ? ' · ' + item.year : ''}${item.runtimeMinutes ? ' · ' + item.runtimeMinutes + ' min' : ''}</span></span><span>Add</span>
                </button>`).join('') : '<div class="st-result"><span>No matching items</span></div>';
        } catch (error) {
            results.innerHTML = `<div class="st-result"><span>${escapeHtml(error.message)}</span></div>`;
        }
    }

    function queueSearch(inputId, resultsId, purpose) {
        window.clearTimeout(state.searchTimers[inputId]);
        state.searchTimers[inputId] = window.setTimeout(() => searchCatalog(inputId, resultsId, purpose), 250);
    }

    async function addProgram(itemId) {
        const channel = selectedChannel();
        if (!channel) return;
        await action('Adding programming…', async () => {
            await request(`/programming/${channel.id}/sources`, { method: 'POST', body: {
                jellyfinItemId: itemId,
                targetAirtimePercent: Number(byId('st-program-share').value || 1),
                playbackMode: Number(byId('st-program-order').value || 0),
                enabled: true
            }});
            byId('st-program-search').value = '';
            hideSearchResults('st-program-results');
            await loadSelectedChannel();
        }, 'Programming added.');
    }

    async function saveProgram(article) {
        const id = article.dataset.programId;
        await action('Updating programming…', async () => {
            await request(`/programming/sources/${id}`, { method: 'PUT', body: {
                targetAirtimePercent: Number(article.querySelector('[data-field="share"]').value || 1),
                playbackMode: Number(article.querySelector('[data-field="mode"]').value || 0),
                enabled: article.querySelector('[data-field="enabled"]').checked
            }});
            await loadSelectedChannel();
        }, 'Programming updated.');
    }

    async function removeProgram(id) {
        if (!window.confirm('Remove this item from the channel? Your Jellyfin media will not be changed.')) return;
        await action('Removing programming…', async () => {
            await request(`/programming/sources/${id}`, { method: 'DELETE' });
            await loadSelectedChannel();
        }, 'Programming removed.');
    }

    function nearestFrequency(value) {
        return [25, 50, 75, 100].reduce((best, current) => Math.abs(current - value) < Math.abs(best - value) ? current : best, 75);
    }

    function currentBreakFrequency() {
        return Number(all('input[name="st-break-frequency"]:checked')[0]?.value || 75);
    }

    function renderBreaks() {
        const hasChannel = !!selectedChannel();
        toggle('st-breaks-empty', !hasChannel);
        toggle('st-breaks-content', hasChannel);
        toggle('st-filler-card', hasChannel);
        if (!hasChannel) return;
        const settings = state.programming?.settings || {};
        const enabled = settings.fillerEnabled === true;
        byId('st-breaks-enabled').checked = enabled;
        toggle('st-break-settings', enabled);
        const frequency = nearestFrequency(Number(settings.fillerChancePercent ?? 75));
        all('input[name="st-break-frequency"]').forEach((radio) => { radio.checked = Number(radio.value) === frequency; });
        byId('st-break-min').value = String(settings.minFillerItems ?? 1);
        byId('st-break-max').value = String(settings.maxFillerItems ?? 2);
        byId('st-break-seconds').value = String(settings.maxFillerSeconds ?? 180);
        byId('st-break-repeat').value = String(settings.fillerRepeatWindow ?? 12);
        renderFillers();
    }

    async function saveBreakSettings(message = 'Break settings saved.') {
        const channel = selectedChannel();
        if (!channel) return;
        const min = Number(byId('st-break-min').value || 0);
        const max = Number(byId('st-break-max').value || 0);
        if (max < min) return toast('Maximum items must be at least the minimum.', 'error');
        await action('Saving break settings…', async () => {
            await request(`/programming/${channel.id}/settings`, { method: 'PUT', body: {
                channelId: channel.id,
                enabled: true,
                fillerEnabled: byId('st-breaks-enabled').checked,
                fillerChancePercent: currentBreakFrequency(),
                minFillerItems: min,
                maxFillerItems: max,
                maxFillerSeconds: Number(byId('st-break-seconds').value || 0),
                fillerRepeatWindow: Number(byId('st-break-repeat').value || 0)
            }});
            await loadSelectedChannel();
        }, message);
    }

    function renderFillers() {
        const fillers = state.programming?.fillers || [];
        const list = byId('st-filler-list');
        if (!list) return;
        if (!fillers.length) {
            list.innerHTML = '<div class="st-empty">No break clips yet. Search above to add promos, bumpers, commercials, or station IDs.</div>';
            return;
        }
        const kinds = ['Promo', 'Bumper', 'Commercial', 'Station ID'];
        list.innerHTML = fillers.map((item) => `<article class="st-source" data-filler-id="${item.id}">
            <div class="st-source-title"><strong>${escapeHtml(item.name)}</strong><small>${item.runtimeSeconds ? item.runtimeSeconds + ' sec' : escapeHtml(friendlyType(item.type))}</small></div>
            <select class="emby-select" data-field="kind">${kinds.map((name, index) => `<option value="${index}"${Number(item.kind) === index ? ' selected' : ''}>${name}</option>`).join('')}</select>
            <label class="st-field"><span>Weight</span><input class="emby-input" data-field="weight" type="number" min="1" max="1000" value="${Number(item.weight || 1)}"></label>
            <div class="st-actions" style="margin:0"><label title="Include this clip"><input data-field="enabled" type="checkbox"${item.enabled !== false ? ' checked' : ''}></label><button type="button" class="st-button st-button-danger st-button-small" data-remove-filler="${item.id}">Remove</button></div>
        </article>`).join('');
    }

    async function addFiller(itemId, itemType) {
        const channel = selectedChannel();
        if (!channel) return;
        if (itemType === 'Series' || itemType === 'Season') return toast('Break items must be individual playable clips.', 'error');
        await action('Adding break clip…', async () => {
            await request(`/programming/${channel.id}/fillers`, { method: 'POST', body: {
                jellyfinItemId: itemId,
                kind: Number(byId('st-filler-kind').value || 0),
                weight: Number(byId('st-filler-weight').value || 1),
                enabled: true
            }});
            byId('st-filler-search').value = '';
            hideSearchResults('st-filler-results');
            await loadSelectedChannel();
        }, 'Break clip added.');
    }

    async function saveFiller(article) {
        const id = article.dataset.fillerId;
        await action('Updating break clip…', async () => {
            await request(`/programming/fillers/${id}`, { method: 'PUT', body: {
                kind: Number(article.querySelector('[data-field="kind"]').value || 0),
                weight: Number(article.querySelector('[data-field="weight"]').value || 1),
                enabled: article.querySelector('[data-field="enabled"]').checked
            }});
            await loadSelectedChannel();
        }, 'Break clip updated.');
    }

    async function removeFiller(id) {
        if (!window.confirm('Remove this clip from the break library? Your Jellyfin media will not be changed.')) return;
        await action('Removing break clip…', async () => {
            await request(`/programming/fillers/${id}`, { method: 'DELETE' });
            await loadSelectedChannel();
        }, 'Break clip removed.');
    }

    function renderFinish() {
        const channel = selectedChannel();
        toggle('st-finish-empty', !channel);
        toggle('st-finish-content', !!channel);
        if (!channel) return;
        const sourceCount = enabledSources().length;
        const lastBuilt = channel.lastPlayoutBuiltAt;
        const checks = [
            [true, `Channel ${formatChannelNumber(channel.number)} · ${channel.name}`],
            [sourceCount > 0, sourceCount > 0 ? `${sourceCount} programming source${sourceCount === 1 ? '' : 's'} ready` : 'Add at least one programming source'],
            [channel.enabled !== false, channel.enabled !== false ? 'Channel is enabled' : 'Channel is disabled'],
            [!!lastBuilt, lastBuilt ? `Last built ${formatDate(lastBuilt)}` : 'Schedule has not been built yet']
        ];
        byId('st-readiness').innerHTML = checks.map(([ready, text]) => `<div class="st-checkline${ready ? ' is-ready' : ''}"><i>${ready ? '✓' : '!'}</i><span>${escapeHtml(text)}</span></div>`).join('');
        byId('st-rebuild').disabled = sourceCount === 0;
    }

    async function rebuildChannel() {
        const channel = selectedChannel();
        if (!channel || !enabledSources().length) return toast('Add at least one programming source first.', 'error');
        await action('Starting build…', () => request(`/programming/${channel.id}/rebuild`, { method: 'POST' }));
        byId('st-rebuild-status').textContent = 'Queued…';
        pollRebuild(channel.id, 0);
    }

    async function pollRebuild(channelId, attempt) {
        window.clearTimeout(state.rebuildTimer);
        if (state.disposed || state.selectedId !== channelId || attempt > 120) return;
        try {
            const status = await request(`/programming/${channelId}/rebuild/status`);
            const label = status?.state || 'idle';
            byId('st-rebuild-status').textContent = label === 'running' ? 'Building…' : label === 'queued' ? 'Queued…' : label;
            if (label === 'completed') {
                toast(`Schedule built with ${status.playoutItemCount || 0} future items.`, 'good');
                await loadChannels(channelId);
                byId('st-rebuild-status').textContent = 'Complete';
                return;
            }
            if (label === 'failed') {
                byId('st-rebuild-status').textContent = 'Failed';
                toast(status.error || 'The schedule build failed.', 'error');
                return;
            }
            state.rebuildTimer = window.setTimeout(() => pollRebuild(channelId, attempt + 1), 1500);
        } catch (error) {
            byId('st-rebuild-status').textContent = 'Could not check status';
            toast(error.message, 'error');
        }
    }

    async function loadSetup() {
        const [setup, urls] = await Promise.all([request('/setup/settings'), request('/setup/urls')]);
        state.setup = { ...setup, ...urls };
        byId('st-public-base').value = state.setup.publicBaseUrl || state.setup.baseUrl || '';
        byId('st-m3u-url').textContent = state.setup.m3u || 'Unavailable';
        byId('st-xmltv-url').textContent = state.setup.epg || 'Unavailable';
    }

    async function saveBaseUrl() {
        const publicBaseUrl = byId('st-public-base').value.trim();
        await action('Saving server address…', async () => {
            await request('/setup/settings', { method: 'PUT', body: { publicBaseUrl } });
            await loadSetup();
        }, 'Jellyfin connection addresses updated.');
    }

    async function loadGeneral() {
        const [general, timezones] = await Promise.all([request('/general/settings'), request('/general/timezones')]);
        state.general = general || {};
        state.timezones = Array.isArray(timezones) ? timezones : [];
        const select = byId('st-timezone');
        select.innerHTML = state.timezones.map((timezone) => `<option value="${escapeHtml(timezone.id)}">${escapeHtml(timezone.label)}</option>`).join('');
        select.value = state.general.scheduleTimeZone || '';
        byId('st-guide-days').value = String(state.general.playoutDaysToBuild || 3);
        byId('st-debug').checked = !!state.general.debugLogging;
    }

    async function saveGeneral() {
        await action('Saving server settings…', async () => {
            await request('/general/settings', { method: 'PUT', body: {
                scheduleTimeZone: byId('st-timezone').value,
                playoutDaysToBuild: Number(byId('st-guide-days').value || 3),
                debugLogging: byId('st-debug').checked
            }});
            await loadGeneral();
        }, 'Server settings saved.');
    }

    async function rebuildAll() {
        if (!window.confirm('Rebuild schedules for every enabled Spectral TV channel?')) return;
        await action('Queueing all channels…', () => request('/tasks/rebuild-all', { method: 'POST' }), 'All enabled channels were queued for rebuild.');
    }

    async function copyText(targetId) {
        const text = byId(targetId)?.textContent?.trim();
        if (!text || text === 'Unavailable' || text === 'Loading…') return toast('That address is not available yet.', 'error');
        try {
            await navigator.clipboard.writeText(text);
        } catch {
            const input = document.createElement('textarea');
            input.value = text;
            input.style.position = 'fixed';
            input.style.opacity = '0';
            document.body.appendChild(input);
            input.select();
            document.execCommand('copy');
            input.remove();
        }
        toast('Copied to clipboard.', 'good');
    }

    function bindEvents() {
        state.root.addEventListener('click', (event) => {
            const step = event.target.closest('[data-step]');
            if (step) return switchStep(step.dataset.step);
            const channel = event.target.closest('[data-channel-id]');
            if (channel) return safely(selectChannel(channel.dataset.channelId));
            const picked = event.target.closest('[data-pick]');
            if (picked) return safely(picked.dataset.pick === 'programming'
                ? addProgram(picked.dataset.itemId)
                : addFiller(picked.dataset.itemId, picked.dataset.itemType));
            const removeProgramButton = event.target.closest('[data-remove-program]');
            if (removeProgramButton) return safely(removeProgram(removeProgramButton.dataset.removeProgram));
            const removeFillerButton = event.target.closest('[data-remove-filler]');
            if (removeFillerButton) return safely(removeFiller(removeFillerButton.dataset.removeFiller));
            const copyButton = event.target.closest('[data-copy]');
            if (copyButton) return safely(copyText(copyButton.dataset.copy));
        });

        state.root.addEventListener('change', (event) => {
            const program = event.target.closest('[data-program-id]');
            if (program && event.target.matches('[data-field]')) return safely(saveProgram(program));
            const filler = event.target.closest('[data-filler-id]');
            if (filler && event.target.matches('[data-field]')) return safely(saveFiller(filler));
        });

        byId('st-new-channel').addEventListener('click', openNewChannel);
        byId('st-modal-close').addEventListener('click', closeNewChannel);
        byId('st-modal-cancel').addEventListener('click', closeNewChannel);
        byId('st-modal').addEventListener('click', (event) => { if (event.target === byId('st-modal')) closeNewChannel(); });
        byId('st-new-channel-form').addEventListener('submit', (event) => safely(createChannel(event)));
        byId('st-channel-form').addEventListener('submit', (event) => safely(saveChannel(event)));
        byId('st-delete-channel').addEventListener('click', () => safely(deleteChannel()));
        byId('st-upload-logo').addEventListener('click', () => safely(uploadLogo()));
        byId('st-clear-logo').addEventListener('click', () => safely(clearLogo()));
        byId('st-program-search').addEventListener('input', () => queueSearch('st-program-search', 'st-program-results', 'programming'));
        byId('st-filler-search').addEventListener('input', () => queueSearch('st-filler-search', 'st-filler-results', 'filler'));
        byId('st-breaks-enabled').addEventListener('change', () => {
            toggle('st-break-settings', byId('st-breaks-enabled').checked);
            safely(saveBreakSettings(byId('st-breaks-enabled').checked ? 'Breaks enabled.' : 'Breaks disabled.'));
        });
        byId('st-save-breaks').addEventListener('click', () => safely(saveBreakSettings()));
        byId('st-rebuild').addEventListener('click', () => safely(rebuildChannel()));
        byId('st-save-base').addEventListener('click', () => safely(saveBaseUrl()));
        byId('st-save-server').addEventListener('click', () => safely(saveGeneral()));
        byId('st-rebuild-all').addEventListener('click', () => safely(rebuildAll()));
        state.root.addEventListener('keydown', (event) => { if (event.key === 'Escape') closeNewChannel(); });
    }

    async function init(root) {
        state.root = root || document.querySelector('#SpectralTVConfigPage');
        if (!state.root || state.root.dataset.spectralBound === '1') return;
        state.root.dataset.spectralBound = '1';
        state.disposed = false;
        bindEvents();
        setBusy(true, 'Loading…');
        const results = await Promise.allSettled([loadLogoSets(), loadSetup(), loadGeneral(), loadChannels()]);
        const failure = results.find((result) => result.status === 'rejected');
        if (failure) toast(failure.reason?.message || 'Some settings could not be loaded.', 'error');
        setBusy(false);
    }

    function dispose() {
        state.disposed = true;
        Object.values(state.searchTimers).forEach((timer) => window.clearTimeout(timer));
        window.clearTimeout(state.toastTimer);
        window.clearTimeout(state.rebuildTimer);
        if (state.root) delete state.root.dataset.spectralBound;
        state.root = null;
    }

    return { init, dispose };
})();

export default function (view) {
    const boot = () => SpectralTvAdmin.init(view);
    view.addEventListener('viewshow', boot);
    view.addEventListener('viewdestroy', () => SpectralTvAdmin.dispose());
    boot();
}
