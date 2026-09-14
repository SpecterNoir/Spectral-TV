#!/usr/bin/env python3
"""Prepare the embedded Jellyfin admin page for the simplified Spectral TV UI.

The recovered admin page keeps a few legacy DOM ids so old cached JavaScript/config data can
survive an upgrade, while the shipped interface is TV/movie focused:
- inline the stylesheet so Jellyfin 12 always applies it;
- remove weather, music, and music-video choices;
- remove weather-only and browser-capture navigation;
- remove weather-guide controls from AI;
- remove background-music controls from off-air settings;
- group the remaining tools into Build / Enhance / System navigation;
- add a short orientation panel so the workflow is understandable at a glance.
"""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "Jellyfin.Plugin.SpectralTV" / "Configuration"
PAGE = CONFIG / "configPage.html"
ADMIN_JS = CONFIG / "admin.js"
CSS = CONFIG / "admin.css"

page = PAGE.read_text(encoding="utf-8")
admin_js = ADMIN_JS.read_text(encoding="utf-8")
css = CSS.read_text(encoding="utf-8")

extra_css = r"""
/* Spectral TV simplified Jellyfin 12 admin shell */
#spectraltv-app {
    max-width: 1240px;
    padding: 1.5rem 1.25rem 4rem;
}

.spectraltv-header {
    padding: 1.15rem 1.25rem;
    margin-bottom: .85rem;
    background: linear-gradient(135deg, rgba(0,164,220,.16), rgba(139,92,246,.08));
    border: 1px solid var(--spectraltv-border);
    border-radius: 14px;
}

.spectraltv-header h1 {
    font-size: 2rem;
    letter-spacing: -.025em;
}

.spectraltv-tabs {
    position: sticky;
    top: .5rem;
    z-index: 20;
    display: grid;
    grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1fr);
    gap: .6rem;
    padding: .6rem;
    margin: .8rem 0 1rem;
    background: rgba(11,15,20,.94);
    border: 1px solid var(--spectraltv-border);
    border-radius: 14px;
    backdrop-filter: blur(10px);
    box-shadow: 0 10px 28px rgba(0,0,0,.18);
}

.spectraltv-tab-group {
    min-width: 0;
    padding: .45rem;
    border: 1px solid rgba(255,255,255,.055);
    border-radius: 10px;
    background: rgba(255,255,255,.018);
}

.spectraltv-tab-group-label {
    display: block;
    padding: 0 .35rem .35rem;
    color: var(--spectraltv-muted);
    font-size: .68rem;
    font-weight: 750;
    letter-spacing: .09em;
    text-transform: uppercase;
}

.spectraltv-tab-group-buttons {
    display: flex;
    gap: .35rem;
    flex-wrap: wrap;
}

.spectraltv-tabs .tab {
    flex: 0 1 auto;
    font-weight: 600;
    padding: .52rem .72rem;
    border-radius: 8px;
}

.spectraltv-overview {
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    gap: .55rem;
    margin: 0 0 1.25rem;
}

.spectraltv-overview-step {
    display: flex;
    align-items: flex-start;
    gap: .65rem;
    padding: .72rem .8rem;
    border: 1px solid var(--spectraltv-border);
    border-radius: 10px;
    background: rgba(255,255,255,.025);
}

.spectraltv-overview-step b {
    display: grid;
    place-items: center;
    flex: 0 0 1.55rem;
    height: 1.55rem;
    border-radius: 50%;
    background: rgba(0,164,220,.18);
    color: #8ddcff;
}

.spectraltv-overview-step strong { display: block; font-size: .86rem; }
.spectraltv-overview-step small { display: block; color: var(--spectraltv-muted); line-height: 1.35; margin-top: .12rem; }

.card, .section-card {
    border-radius: 14px;
    padding: 1.15rem 1.25rem;
    box-shadow: 0 10px 28px rgba(0,0,0,.10);
}

.toolbar {
    padding: .8rem;
    border: 1px solid var(--spectraltv-border);
    border-radius: 12px;
    background: rgba(255,255,255,.025);
}

.form-grid {
    grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    gap: 1rem;
}

.split-layout.has-panel { align-items: start; }
.split-side { border-radius: 14px; }
.data-table-wrap { background: rgba(255,255,255,.015); }

#channel-form-panel .actions {
    grid-column: 1 / -1;
    padding-top: .7rem;
    border-top: 1px solid var(--spectraltv-border);
}

/* Legacy compatibility nodes stay in the DOM but are not part of the product UI. */
#tab-weather,
#tab-playwright,
#weather-fields,
#ebs-music-source-field,
#ebs-library-field {
    display: none !important;
}

@media (max-width: 980px) {
    .spectraltv-tabs { grid-template-columns: 1fr; position: static; }
    .spectraltv-overview { grid-template-columns: repeat(2,1fr); }
}

@media (max-width: 600px) {
    #spectraltv-app { padding: .8rem .65rem 3rem; }
    .spectraltv-overview { grid-template-columns: 1fr; }
    .spectraltv-tab-group-buttons { flex-wrap: nowrap; overflow-x: auto; padding-bottom: .2rem; }
    .spectraltv-tabs .tab { white-space: nowrap; }
    .card, .section-card { padding: .9rem; }
}
"""

inline = f'<style id="spectraltv-admin-styles">\n{css}\n{extra_css}\n</style>'
page, replaced = re.subn(
    r'<link\s+rel="stylesheet"\s+href="/web/configurationpage\?name=SpectralTV_admin\.css"\s*>',
    inline,
    page,
    count=1,
)
if replaced != 1:
    raise SystemExit("Could not locate SpectralTV admin stylesheet link")

for option in (
    '<option value="2">Music Video</option>',
    '<option value="3">Music</option>',
    '<option value="4">Weather</option>',
):
    page = page.replace(option, "")

# Keep the channel-weather fields hidden only because old cached admin.js still queries the ids.
page = page.replace(
    '<div id="weather-fields" class="field-group hidden">',
    '<div id="weather-fields" class="field-group hidden" hidden aria-hidden="true">',
)
page = page.replace(' Weather channels update the guide automatically.', '')

# Remove the visible weather-guide AI card. The next card begins with Channel auto-tagging.
page, weather_ai_removed = re.subn(
    r'\s*<div class="card section-card">\s*<div class="section-header-row">\s*<h3>Weather guide metadata</h3>.*?(?=\s*<div class="card section-card">\s*<div class="section-header-row">\s*<h3>Channel auto-tagging</h3>)',
    '\n',
    page,
    count=1,
    flags=re.S,
)
if weather_ai_removed != 1:
    raise SystemExit("Could not remove Weather guide metadata card")

# Remove music wording from the remaining logo instructions.
page = page.replace(
    'Channel bugs live under Shows, Movies, and Music Videos Channels.',
    'Channel bugs live under the Shows and Movies folders.',
)

# Off-air playback keeps white-noise/silence/beep options. Jellyfin music-library playback is retired.
page = page.replace(
    '<option value="0">Background music from Jellyfin library</option>',
    '<option value="0" hidden disabled>Legacy background music (disabled)</option>',
)
page = page.replace(
    '<label class="field">\n                        <span>Background music source</span>\n                        <select id="ebs-music-source" class="emby-input">',
    '<label class="field hidden" id="ebs-music-source-field" hidden aria-hidden="true">\n                        <span>Background music source</span>\n                        <select id="ebs-music-source" class="emby-input">',
)
page = page.replace(
    '<label class="field" id="ebs-library-field">',
    '<label class="field hidden" id="ebs-library-field" hidden aria-hidden="true">',
)

new_nav = '''<nav class="spectraltv-tabs" role="tablist" aria-label="Spectral TV settings">
                <div class="spectraltv-tab-group">
                    <span class="spectraltv-tab-group-label">Build</span>
                    <div class="spectraltv-tab-group-buttons">
                        <button type="button" class="tab active" data-tab="channels" role="tab">Channels</button>
                        <button type="button" class="tab" data-tab="presets" role="tab">Ready-made</button>
                        <button type="button" class="tab" data-tab="lineups" role="tab">Schedule</button>
                        <button type="button" class="tab" data-tab="list" role="tab">Lists</button>
                        <button type="button" class="tab" data-tab="special" role="tab">Specials</button>
                        <button type="button" class="tab" data-tab="commercials" role="tab">Commercials</button>
                        <button type="button" class="tab" data-tab="logos" role="tab">Logos</button>
                    </div>
                </div>
                <div class="spectraltv-tab-group">
                    <span class="spectraltv-tab-group-label">Enhance</span>
                    <div class="spectraltv-tab-group-buttons">
                        <button type="button" class="tab" data-tab="ebs" role="tab">Off-Air</button>
                        <button type="button" class="tab" data-tab="ai" role="tab">AI Assist</button>
                    </div>
                </div>
                <div class="spectraltv-tab-group">
                    <span class="spectraltv-tab-group-label">System</span>
                    <div class="spectraltv-tab-group-buttons">
                        <button type="button" class="tab" data-tab="general" role="tab">Settings</button>
                        <button type="button" class="tab" data-tab="setup" role="tab">Jellyfin Setup</button>
                        <button type="button" class="tab" data-tab="tasks" role="tab">Maintenance</button>
                    </div>
                </div>
            </nav>

            <div class="spectraltv-overview" aria-label="Spectral TV workflow">
                <div class="spectraltv-overview-step"><b>1</b><div><strong>Channels</strong><small>Create the station itself: number, name, aspect ratio, and logo.</small></div></div>
                <div class="spectraltv-overview-step"><b>2</b><div><strong>Programming</strong><small>Use the Programming page to choose shows/movies and airtime weights.</small></div></div>
                <div class="spectraltv-overview-step"><b>3</b><div><strong>Schedule</strong><small>Use only when you want fixed time blocks or special presentations.</small></div></div>
                <div class="spectraltv-overview-step"><b>4</b><div><strong>Jellyfin Setup</strong><small>Connect finished channels to Jellyfin Live TV with M3U/XMLTV.</small></div></div>
            </div>'''
page, nav_replaced = re.subn(
    r'<nav class="spectraltv-tabs" role="tablist">.*?</nav>',
    new_nav,
    page,
    count=1,
    flags=re.S,
)
if nav_replaced != 1:
    raise SystemExit("Could not replace Spectral TV tab navigation")

# Clean the one remaining user-facing music reference in a confirmation dialog.
admin_js = admin_js.replace(
    'Retag the entire library? This re-evaluates every movie, series, and music video.',
    'Retag the entire library? This re-evaluates every movie and series.',
)

# Build-time assertions make UI regressions fail CI instead of silently shipping.
for forbidden in (
    '<option value="2">Music Video</option>',
    '<option value="3">Music</option>',
    '<option value="4">Weather</option>',
    'data-tab="weather"',
    'data-tab="playwright"',
    '<h3>Weather guide metadata</h3>',
):
    if forbidden in page:
        raise SystemExit(f"Legacy UI element is still visible: {forbidden}")

PAGE.write_text(page, encoding="utf-8")
ADMIN_JS.write_text(admin_js, encoding="utf-8")
print("Prepared simplified Spectral TV admin UI")
