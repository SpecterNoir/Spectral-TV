(function () {
    'use strict';

    const SECTION_VALUE = 'spectraltvchannels';
    const SECTION_LABEL = 'Channels';
    const SETTINGS_ENDPOINT = 'SpectralTV/api/viewer/home-section';
    const CHANNELS_ENDPOINT = 'SpectralTV/api/viewer/on-demand';
    const SELECT_PREFIX = 'selectHomeSection';
    const MAX_SECTIONS = 10;

    let selectedIndex = null;
    let settingsLoadedForUser = null;
    let renderGeneration = 0;

    function apiClient() {
        return window.ApiClient || null;
    }

    function currentUserId() {
        const api = apiClient();
        return api && typeof api.getCurrentUserId === 'function' ? api.getCurrentUserId() : null;
    }

    function apiUrl(path, query) {
        const api = apiClient();
        return api ? api.getUrl(path, query || {}) : path;
    }

    function requestHeaders(hasBody) {
        const api = apiClient();
        const headers = { Accept: 'application/json' };
        if (hasBody) headers['Content-Type'] = 'application/json';

        if (api && typeof api.setRequestHeaders === 'function') {
            api.setRequestHeaders(headers);
        } else if (api && typeof api.accessToken === 'function') {
            const token = api.accessToken();
            if (token) headers['X-Emby-Token'] = token;
        }

        return headers;
    }

    async function apiJson(path, options) {
        const api = apiClient();
        if (!api) throw new Error('Jellyfin ApiClient is not ready.');

        const method = options && options.method ? options.method : 'GET';
        const hasBody = options && options.body !== undefined && options.body !== null;
        const response = await window.fetch(apiUrl(path, options && options.query), {
            method: method,
            credentials: 'same-origin',
            headers: requestHeaders(hasBody),
            body: hasBody ? JSON.stringify(options.body) : undefined
        });

        if (!response.ok) {
            const text = await response.text();
            let message = text || response.statusText || 'Request failed';
            try {
                const parsed = JSON.parse(text);
                message = parsed.message || parsed.detail || parsed.title || message;
            } catch (_) { }
            throw new Error(message);
        }

        if (response.status === 204 || response.status === 205) return null;
        const text = await response.text();
        if (!text) return null;
        try { return JSON.parse(text); } catch (_) { return text; }
    }

    async function loadSelection() {
        const userId = currentUserId();
        if (!userId) return null;
        if (settingsLoadedForUser === userId) return selectedIndex;

        try {
            const result = await apiJson(SETTINGS_ENDPOINT);
            selectedIndex = Number.isInteger(result && result.sectionIndex) ? result.sectionIndex : null;
            settingsLoadedForUser = userId;
        } catch (error) {
            console.debug('[Spectral TV] Could not load Channels home position.', error);
        }
        return selectedIndex;
    }

    async function saveSelection(index) {
        selectedIndex = Number.isInteger(index) ? index : null;
        settingsLoadedForUser = currentUserId();
        try {
            await apiJson(SETTINGS_ENDPOINT, {
                method: 'POST',
                body: { sectionIndex: selectedIndex }
            });
        } catch (error) {
            console.warn('[Spectral TV] Could not save Channels home position.', error);
        }
    }

    function getHomeSelects() {
        const result = [];
        for (let i = 1; i <= MAX_SECTIONS; i++) {
            const select = document.getElementById(SELECT_PREFIX + i);
            if (select) result.push(select);
        }
        return result;
    }

    async function patchHomeSettings() {
        const selects = getHomeSelects();
        if (!selects.length) return false;

        for (const select of selects) {
            if (!select.querySelector('option[value="' + SECTION_VALUE + '"]')) {
                const option = document.createElement('option');
                option.value = SECTION_VALUE;
                option.textContent = SECTION_LABEL;
                const none = select.querySelector('option[value="none"]');
                if (none) select.insertBefore(option, none);
                else select.appendChild(option);
            }
        }

        await loadSelection();
        if (Number.isInteger(selectedIndex) && selectedIndex >= 0 && selectedIndex < selects.length) {
            selects[selectedIndex].value = SECTION_VALUE;
        }

        if (!selects[0].dataset.spectralTvBound) {
            for (const select of selects) {
                select.dataset.spectralTvBound = '1';
                select.addEventListener('change', function () {
                    if (select.value !== SECTION_VALUE) return;
                    for (const other of selects) {
                        if (other !== select && other.value === SECTION_VALUE) other.value = 'none';
                    }
                });
            }

            const form = selects[0].closest('form');
            if (form && !form.dataset.spectralTvBound) {
                form.dataset.spectralTvBound = '1';
                form.addEventListener('submit', function () {
                    const currentSelects = getHomeSelects();
                    const index = currentSelects.findIndex(select => select.value === SECTION_VALUE);
                    void saveSelection(index >= 0 ? index : null);

                    // Jellyfin 12 serializes these values into a fixed HomeSectionType enum. Keep
                    // its native save valid by sending None for our custom slot; Spectral stores the
                    // real slot separately in this same viewer's display preferences.
                    if (index >= 0) currentSelects[index].value = 'none';

                    window.setTimeout(function () {
                        if (index >= 0 && currentSelects[index] && document.body.contains(currentSelects[index])) {
                            currentSelects[index].value = SECTION_VALUE;
                        }
                    }, 500);
                }, true);
            }
        }

        return true;
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function channelCard(channel) {
        const name = escapeHtml(channel.name || 'Channel');
        const current = channel.currentTitle ? '<div class="cardText cardTextCentered cardText-secondary"><bdi>' + escapeHtml(channel.currentTitle) + '</bdi></div>' : '';
        const id = escapeHtml(channel.id || '');
        return '<div class="card overflowPortraitCard card-hoverable spectralTvChannelCard" data-spectral-channel="' + id + '">' +
            '<div class="cardBox cardBox-bottompadded">' +
                '<div class="cardScalable">' +
                    '<div class="cardPadder cardPadder-overflowPortrait"></div>' +
                    '<button type="button" class="cardImageContainer coveredImage cardContent itemAction spectralTvChannelButton" aria-label="' + name + '">' +
                        '<span class="spectralTvChannelTileName">' + name + '</span>' +
                    '</button>' +
                '</div>' +
                '<div class="cardText cardTextCentered cardText-first"><bdi>' + name + '</bdi></div>' +
                current +
            '</div>' +
        '</div>';
    }

    function ensureStyles() {
        if (document.getElementById('spectralTvNativeChannelsStyles')) return;
        const style = document.createElement('style');
        style.id = 'spectralTvNativeChannelsStyles';
        style.textContent = '\n' +
            '.spectralTvChannelButton{border:0;width:100%;height:100%;padding:0;background:linear-gradient(145deg,rgba(42,44,55,.96),rgba(15,16,22,.98));color:inherit;cursor:pointer;display:flex;align-items:center;justify-content:center;}\n' +
            '.spectralTvChannelTileName{font-size:1.35em;font-weight:600;text-align:center;padding:1em;line-height:1.15;}\n' +
            '.spectralTvChannelsEmpty{padding-left:3.3%;opacity:.75;}\n';
        document.head.appendChild(style);
    }

    async function openChannel(channelId) {
        if (!channelId) return;
        try {
            const sync = await apiJson('SpectralTV/api/viewer/on-demand/' + encodeURIComponent(channelId) + '/playlist', {
                method: 'POST',
                query: { programs: 24 }
            });
            const playlistId = sync && (sync.playlistId || sync.PlaylistId);
            if (!playlistId) throw new Error('No playlist was returned for this channel.');

            window.location.hash = '!/details?id=' + encodeURIComponent(playlistId) + '&serverId=' + encodeURIComponent(apiClient().serverId());
        } catch (error) {
            console.warn('[Spectral TV] Could not open channel.', error);
            if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
                window.Dashboard.alert('Spectral TV could not open this channel.');
            }
        }
    }

    function bindChannelClicks(container) {
        container.querySelectorAll('.spectralTvChannelCard').forEach(function (card) {
            if (card.dataset.spectralBound) return;
            card.dataset.spectralBound = '1';
            card.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                void openChannel(card.getAttribute('data-spectral-channel'));
            });
        });
    }

    async function renderChannels() {
        const generation = ++renderGeneration;
        await loadSelection();
        if (!Number.isInteger(selectedIndex) || selectedIndex < 0 || selectedIndex >= MAX_SECTIONS) return false;

        const home = document.querySelector('.homeSectionsContainer');
        if (!home) return false;
        const slot = home.querySelector('.section' + selectedIndex);
        if (!slot) return false;

        const renderKey = String(currentUserId() || '') + ':' + selectedIndex;
        if (slot.dataset.spectralTvRendered === renderKey
            && (slot.querySelector('.spectralTvChannelsItems') || slot.querySelector('.spectralTvChannelsEmpty'))) {
            return true;
        }

        try {
            const channels = await apiJson(CHANNELS_ENDPOINT);
            if (generation !== renderGeneration || !document.body.contains(slot)) return false;
            ensureStyles();

            const list = Array.isArray(channels) ? channels : [];
            let html = '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left"><h2 class="sectionTitle sectionTitle-cards">Channels</h2></div>';
            if (!list.length) {
                html += '<div class="spectralTvChannelsEmpty">No enabled Spectral TV on-demand channels yet.</div>';
                slot.innerHTML = html;
                slot.dataset.spectralTvRendered = renderKey;
                return true;
            }

            html += '<div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale" data-centerfocus="true">' +
                '<div is="emby-itemscontainer" class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right spectralTvChannelsItems">' +
                list.map(channelCard).join('') +
                '</div></div>';
            slot.innerHTML = html;
            slot.dataset.spectralTvRendered = renderKey;
            bindChannelClicks(slot);
            return true;
        } catch (error) {
            console.debug('[Spectral TV] Could not render Channels home section.', error);
            return false;
        }
    }

    let scheduled = null;
    function schedule() {
        if (scheduled) window.clearTimeout(scheduled);
        scheduled = window.setTimeout(async function () {
            scheduled = null;
            await patchHomeSettings();
            await renderChannels();
        }, 250);
    }

    function resetForUserChange() {
        const userId = currentUserId();
        if (settingsLoadedForUser && userId && settingsLoadedForUser !== userId) {
            settingsLoadedForUser = null;
            selectedIndex = null;
        }
    }

    const observer = new MutationObserver(function () {
        resetForUserChange();
        schedule();
    });

    function start() {
        if (!document.body) {
            window.setTimeout(start, 100);
            return;
        }
        observer.observe(document.body, { childList: true, subtree: true });
        schedule();
    }

    start();
})();
