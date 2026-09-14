const SpectralTvLiveTvConnect = (() => {
    let root = null;
    let busy = false;

    const byId = (id) => root?.querySelector('#' + id) || null;

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
        if (body !== undefined && body !== null) {
            headers['Content-Type'] = 'application/json';
            body = JSON.stringify(body);
        }

        const response = await fetch(resolveUrl('SpectralTV/api/setup/' + String(path).replace(/^\/+/, '')), {
            method,
            credentials: 'same-origin',
            headers,
            body
        });
        const text = await response.text();
        if (!response.ok) {
            let message = text || response.statusText || 'Request failed';
            try {
                const parsed = JSON.parse(text);
                message = parsed.message || parsed.detail || message;
                if (parsed.detail && parsed.detail !== message) message += ' ' + parsed.detail;
            } catch { }
            throw new Error(message);
        }
        return text ? JSON.parse(text) : null;
    }

    function setBusy(value, message = '') {
        busy = value;
        ['stc-connect', 'stc-refresh', 'stc-save-base'].forEach((id) => {
            const element = byId(id);
            if (element) element.disabled = value;
        });
        if (message) showResult(message, '');
    }

    function showResult(message, kind) {
        const element = byId('stc-result');
        if (!element) return;
        element.textContent = message;
        element.className = 'stc-result' + (kind ? ' is-' + kind : '');
    }

    function setCheck(id, good) {
        byId(id)?.classList.toggle('is-good', !!good);
    }

    function renderStatus(status) {
        setCheck('stc-tuner-state', status?.tunerConfigured === true);
        setCheck('stc-guide-state', status?.guideConfigured === true);
        setCheck('stc-link-state', status?.guideLinkedToTuner === true);
        if (byId('stc-m3u')) byId('stc-m3u').textContent = status?.m3uUrl || 'Unavailable';
        if (byId('stc-xmltv')) byId('stc-xmltv').textContent = status?.xmlTvUrl || 'Unavailable';

        const summary = byId('stc-summary');
        if (summary) {
            summary.textContent = status?.connected
                ? 'Connected. Spectral TV is registered with Jellyfin Live TV.'
                : 'Not fully connected yet. Use the button below to create or repair only the Spectral TV entries.';
        }
        const connect = byId('stc-connect');
        if (connect) connect.textContent = status?.connected ? 'Repair / update connection' : 'Connect to Jellyfin Live TV';
    }

    async function load() {
        if (!root || busy) return;
        setBusy(true);
        try {
            const [settings, status] = await Promise.all([request('settings'), request('livetv-status')]);
            if (byId('stc-public-base')) byId('stc-public-base').value = settings?.publicBaseUrl || '';
            renderStatus(status);
        } catch (error) {
            showResult(error?.message || String(error), 'error');
        } finally {
            setBusy(false);
        }
    }

    async function saveBase() {
        if (busy) return;
        setBusy(true, 'Saving server address…');
        try {
            await request('settings', {
                method: 'PUT',
                body: { publicBaseUrl: byId('stc-public-base')?.value?.trim() || '' }
            });
            const status = await request('livetv-status');
            renderStatus(status);
            showResult('Server address saved. If the address changed, run Repair / update connection so Jellyfin uses the new M3U/XMLTV URLs.', 'good');
        } catch (error) {
            showResult(error?.message || String(error), 'error');
        } finally {
            setBusy(false);
        }
    }

    async function connect() {
        if (busy) return;
        setBusy(true, 'Connecting Spectral TV to Jellyfin Live TV…');
        try {
            const status = await request('livetv', { method: 'POST' });
            renderStatus(status);
            showResult(status?.connected
                ? 'Connected successfully. Jellyfin now has the Spectral TV M3U tuner and XMLTV guide.'
                : 'Jellyfin accepted the setup, but the connection is not yet complete. Refresh the status and check the server address.', status?.connected ? 'good' : 'error');
        } catch (error) {
            showResult(error?.message || String(error), 'error');
        } finally {
            setBusy(false);
        }
    }

    async function copyValue(id) {
        const text = byId(id)?.textContent?.trim();
        if (!text || text === 'Loading…' || text === 'Unavailable') return;
        try {
            await navigator.clipboard.writeText(text);
            showResult('Copied to clipboard.', 'good');
        } catch {
            showResult('Could not access the clipboard. You can still select and copy the URL manually.', 'error');
        }
    }

    function bind() {
        byId('stc-save-base')?.addEventListener('click', saveBase);
        byId('stc-connect')?.addEventListener('click', connect);
        byId('stc-refresh')?.addEventListener('click', load);
        root?.addEventListener('click', (event) => {
            const copy = event.target.closest('[data-copy]');
            if (copy) copyValue(copy.dataset.copy);
        });
    }

    async function init(view) {
        root = view || document.querySelector('#SpectralTVLiveTvConnectPage');
        if (!root || root.dataset.spectralLiveTvConnectBound === '1') return;
        root.dataset.spectralLiveTvConnectBound = '1';
        bind();
        await load();
    }

    function dispose() {
        if (root) delete root.dataset.spectralLiveTvConnectBound;
        root = null;
        busy = false;
    }

    return { init, dispose };
})();

export default function (view) {
    const boot = () => SpectralTvLiveTvConnect.init(view);
    view.addEventListener('viewshow', boot);
    view.addEventListener('viewdestroy', () => SpectralTvLiveTvConnect.dispose());
    boot();
}
