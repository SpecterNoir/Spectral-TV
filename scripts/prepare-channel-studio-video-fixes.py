#!/usr/bin/env python3
from pathlib import Path

HTML_PATH = Path("Jellyfin.Plugin.SpectralTV/Configuration/channelStudioPage.html")
JS_PATH = Path("Jellyfin.Plugin.SpectralTV/Configuration/channelStudio.js")
CHANNELS_HOME_PATH = Path("Jellyfin.Plugin.SpectralTV/Configuration/nativeChannelsHome.js")

html = HTML_PATH.read_text(encoding="utf-8")
js = JS_PATH.read_text(encoding="utf-8")

# Search results should participate in document flow. The previous absolute dropdown could remain
# open while the page scrolled and cover the promo and preview sections underneath it.
old_results_css = "#SpectralTVChannelStudioPage .cs-results{display:none;position:absolute;z-index:20;top:calc(100% + .3rem);left:0;right:0;max-height:320px;overflow:auto;padding:.35rem;border:1px solid var(--cs-border);border-radius:10px;background:#17191d;box-shadow:0 20px 45px rgba(0,0,0,.45)}"
new_results_css = "#SpectralTVChannelStudioPage .cs-results{display:none;position:static;max-height:260px;overflow:auto;margin-top:.3rem;padding:.35rem;border:1px solid var(--cs-border);border-radius:10px;background:#17191d;box-shadow:0 12px 28px rgba(0,0,0,.3)}"
if old_results_css not in html and new_results_css not in html:
    raise SystemExit("Could not locate Channel Studio search-result CSS")
html = html.replace(old_results_css, new_results_css, 1)

# The normal product UI should expose a harmless preview, not internal progress-key test controls.
old_preview = """            <div class=\"cs-card\">\n                <div class=\"cs-card-head\"><div><h2>Sequence preview</h2><p>Preview the recipe without changing anyone's progress. The test controls below also exercise the resumable progress engine.</p></div></div>\n                <div class=\"cs-actions\"><button id=\"cs-od-preview\" type=\"button\" class=\"cs-button cs-button-primary\">Preview next 12 programs</button><input id=\"cs-od-progress-key\" class=\"emby-input\" type=\"text\" value=\"test\" style=\"max-width:180px\" aria-label=\"Test progress key\"><button id=\"cs-od-resume\" type=\"button\" class=\"cs-button\">Start / resume test</button><button id=\"cs-od-next\" type=\"button\" class=\"cs-button\">Complete &amp; next</button><button id=\"cs-od-reset\" type=\"button\" class=\"cs-button cs-button-danger\">Reset test progress</button></div>\n                <div id=\"cs-od-preview-list\" class=\"cs-preview\"></div>\n            </div>"""
new_preview = """            <div class=\"cs-card\">\n                <div class=\"cs-card-head\"><div><h2>Sequence preview</h2><p>See what this recipe would generate next without changing any viewer's progress.</p></div></div>\n                <div class=\"cs-actions\"><button id=\"cs-od-preview\" type=\"button\" class=\"cs-button cs-button-primary\">Preview next 12 programs</button></div>\n                <div id=\"cs-od-preview-list\" class=\"cs-preview\"></div>\n            </div>"""
if old_preview not in html and new_preview not in html:
    raise SystemExit("Could not locate Channel Studio sequence-preview controls")
html = html.replace(old_preview, new_preview, 1)

# Surface database health directly in Channel Studio. Opening the page will re-run only idempotent,
# additive repairs and then report whether the real upgraded database is connectable and writable.
health_anchor = """        </header>\n\n        <div class=\"cs-mode-tabs\" role=\"tablist\">"""
health_markup = """        </header>\n\n        <div id=\"cs-health\" class=\"cs-note cs-hidden\" role=\"status\" aria-live=\"polite\"></div>\n\n        <div class=\"cs-mode-tabs\" role=\"tablist\">"""
if health_markup not in html:
    if health_anchor not in html:
        raise SystemExit("Could not locate Channel Studio header for database health panel")
    html = html.replace(health_anchor, health_markup, 1)

HTML_PATH.write_text(html, encoding="utf-8")

# Make request errors actionable. Jellyfin's production exception page can legitimately return only
# "Error processing request"; preserving method, route, and status means the next report identifies
# the failing API immediately instead of every backend problem looking identical.
old_throw = "            throw new Error(message);"
new_throw = "            throw new Error(`${method} ${String(path).replace(/^\\/+/, '')} failed (HTTP ${response.status}): ${message}`);"
if old_throw not in js and new_throw not in js:
    raise SystemExit("Could not locate Channel Studio request error handling")
js = js.replace(old_throw, new_throw, 1)

# Ask the server to verify/repair the exact database this Jellyfin instance is using. This is
# deliberately separate from CI's synthetic database so upgraded Synology installs can self-report
# missing tables, read-only files, or a specific failed migration instead of a generic HTTP 500.
health_function = """
    async function repairDatabase() {
        const result = await request('/diagnostics/repair', { method: 'POST' });
        const el = byId('cs-health');
        if (!el) return result;
        const status = result?.status || {};
        const issues = Array.isArray(status.issues) ? status.issues : [];
        const repairs = Array.isArray(result?.repairs) ? result.repairs : [];
        const failedRepairs = repairs.filter((step) => step.success === false);
        el.classList.remove('cs-hidden');
        if (result?.healthy === true) {
            el.style.borderColor = 'rgba(74,222,128,.55)';
            el.textContent = 'Database check passed — Spectral TV can read and write its channel database.';
        } else {
            el.style.borderColor = 'rgba(239,68,68,.65)';
            const details = [
                ...issues,
                ...failedRepairs.map((step) => `${step.name}: ${step.error || 'repair failed'}`)
            ];
            el.textContent = `Database repair still has a problem: ${details.join(' • ') || 'unknown database error'}`;
        }
        return result;
    }
"""
if "async function repairDatabase()" not in js:
    safely_end = """    async function safely(work) {\n        try { return await work; } catch (error) { toast(error?.message || 'Something went wrong.', 'error'); return null; }\n    }\n"""
    if safely_end not in js:
        raise SystemExit("Could not locate Channel Studio safety helper for database diagnostics")
    js = js.replace(safely_end, safely_end + health_function, 1)

# Channel Studio now asks the catalog for only useful item classes. Dynamic sources are whole shows,
# seasons, or movies; fixed sequences are exact episodes/movies; promos are individual playable clips.
old_search = "            const items = await request(`/catalog/search?q=${encodeURIComponent(q)}&limit=30`);"
new_search = """            const catalogPurpose = purpose === 'od-filler'\n                ? 'filler'\n                : (purpose === 'od' && Number(byId('cs-od-programming-mode')?.value || 0) === 1 ? 'fixed' : 'dynamic');\n            const items = await request(`/catalog/search?q=${encodeURIComponent(q)}&purpose=${encodeURIComponent(catalogPurpose)}&limit=30`);"""
if old_search not in js and new_search not in js:
    raise SystemExit("Could not locate Channel Studio catalog search request")
js = js.replace(old_search, new_search, 1)

# A placeholder must not look like an already-entered channel name. The video showed native browser
# validation firing because "Cartoon Network" was only placeholder text.
js = js.replace(
    '<label class="cs-field"><span>Channel name</span><input data-live-create-name class="emby-input" type="text" required placeholder="Cartoon Network"></label>',
    '<label class="cs-field"><span>Channel name (required)</span><input data-live-create-name class="emby-input" type="text" required placeholder="e.g. Cartoon Network"></label>',
    1,
)

# Remove internal test-only controls from the normal render/enable path.
js = js.replace(
    "'cs-od-pattern','cs-od-save','cs-od-delete','cs-od-preview','cs-od-resume','cs-od-next','cs-od-reset','cs-od-search','cs-od-filler-search'",
    "'cs-od-pattern','cs-od-save','cs-od-delete','cs-od-preview','cs-od-search','cs-od-filler-search'",
    1,
)

start = js.find("    function testProgressKey() {")
end = js.find("    function bindEvents() {", start)
if start >= 0 and end > start:
    js = js[:start] + js[end:]
elif "function testProgressKey()" in js:
    raise SystemExit("Could not safely remove Channel Studio test-progress helpers")

for line in (
    "        byId('cs-od-resume').addEventListener('click', () => safely(testResume(false)));\n",
    "        byId('cs-od-next').addEventListener('click', () => safely(testResume(true)));\n",
    "        byId('cs-od-reset').addEventListener('click', () => safely(resetTestProgress()));\n",
):
    js = js.replace(line, "")

# Close a search result list as soon as the user chooses something or clicks away. If the subsequent
# mutation fails, the error toast remains visible without a large stale result list covering the page.
old_pick = """            const pick = event.target.closest('[data-cs-pick]');\n            if (pick) {\n                if (pick.dataset.csPick === 'live') return safely(addLiveSource(pick.dataset.itemId));\n                if (pick.dataset.csPick === 'od') return safely(addOdSource(pick.dataset.itemId));\n                if (pick.dataset.csPick === 'od-filler') return safely(addOdFiller(pick.dataset.itemId, pick.dataset.itemType));\n            }"""
new_pick = """            const pick = event.target.closest('[data-cs-pick]');\n            if (pick) {\n                if (pick.dataset.csPick === 'live') { hideResults('cs-live-results'); return safely(addLiveSource(pick.dataset.itemId)); }\n                if (pick.dataset.csPick === 'od') { hideResults('cs-od-results'); return safely(addOdSource(pick.dataset.itemId)); }\n                if (pick.dataset.csPick === 'od-filler') { hideResults('cs-od-filler-results'); return safely(addOdFiller(pick.dataset.itemId, pick.dataset.itemType)); }\n            }\n            if (!event.target.closest('.cs-search-wrap')) {\n                hideResults('cs-live-results');\n                hideResults('cs-od-results');\n                hideResults('cs-od-filler-results');\n            }"""
if old_pick not in js and new_pick not in js:
    raise SystemExit("Could not locate Channel Studio delegated search-result handler")
js = js.replace(old_pick, new_pick, 1)

# Switching modes should never leave results from the hidden mode hanging around.
old_set_mode = """    function setMode(mode) {\n        state.mode = mode;"""
new_set_mode = """    function setMode(mode) {\n        hideResults('cs-live-results');\n        hideResults('cs-od-results');\n        hideResults('cs-od-filler-results');\n        state.mode = mode;"""
if old_set_mode not in js and new_set_mode not in js:
    raise SystemExit("Could not locate Channel Studio mode switcher")
js = js.replace(old_set_mode, new_set_mode, 1)

# Changing dynamic/fixed mode changes the useful search result types; discard any stale results.
needle = "        byId('cs-od-search').addEventListener('input', () => queueSearch('cs-od-search', 'cs-od-results', 'od'));\n"
addition = needle + "        byId('cs-od-programming-mode').addEventListener('change', () => hideResults('cs-od-results'));\n"
if addition not in js:
    if needle not in js:
        raise SystemExit("Could not locate on-demand search binding")
    js = js.replace(needle, addition, 1)

# Run the real-server database check before loading any channel data. Even if it reports a failure,
# safely() keeps Channel Studio open so catalog search and unrelated diagnostics remain usable.
old_init = """        installLiveCreateUi();\n        bindEvents();\n        await safely(loadLive());"""
new_init = """        installLiveCreateUi();\n        bindEvents();\n        await safely(repairDatabase());\n        await safely(loadLive());"""
if old_init not in js and new_init not in js:
    raise SystemExit("Could not locate Channel Studio initialization sequence")
js = js.replace(old_init, new_init, 1)

JS_PATH.write_text(js, encoding="utf-8")

# Final build-time assertions for the exact regressions visible in the user's walkthrough.
html = HTML_PATH.read_text(encoding="utf-8")
js = JS_PATH.read_text(encoding="utf-8")
for retired in ("cs-od-progress-key", "cs-od-resume", "cs-od-next", "cs-od-reset", "Start / resume test", "Reset test progress"):
    if retired in html or retired in js:
        raise SystemExit(f"Internal on-demand test control remains visible/referenced: {retired}")
if "purpose=${encodeURIComponent(catalogPurpose)}" not in js:
    raise SystemExit("Purpose-specific Channel Studio search was not installed")
if "failed (HTTP ${response.status})" not in js:
    raise SystemExit("Actionable Channel Studio request errors were not installed")
if "request('/diagnostics/repair'" not in js or 'id="cs-health"' not in html:
    raise SystemExit("Real-server database diagnostics were not installed in Channel Studio")

channels_home = CHANNELS_HOME_PATH.read_text(encoding="utf-8")
if "#/livetv.html?tab=channels" in channels_home:
    raise SystemExit("Channels Home still contains the invalid Jellyfin 12 Live TV fallback route")
if "#/livetv?tab=2&serverId=" not in channels_home:
    raise SystemExit("Channels Home is missing Jellyfin 12's canonical Channels-tab fallback route")

print("Prepared Channel Studio fixes, fresh diagnostics, self-repair, and Channels navigation guards.")