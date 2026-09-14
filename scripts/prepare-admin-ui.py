#!/usr/bin/env python3
"""Prepare the embedded Jellyfin admin page for the simplified Spectral TV UI.

The legacy admin page is intentionally kept source-compatible with the recovered plugin,
while the installable package exposes only the current TV/movie workflow.  This build step:
- inlines the plugin stylesheet so Jellyfin 12 cannot drop the styling;
- removes Weather and music channel choices from the visible interface;
- hides legacy Weather controls while retaining their DOM ids for old admin.js code paths;
- removes background-music choices from EBS/off-air controls;
- renames several tabs so their purpose is clearer.
"""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "Jellyfin.Plugin.SpectralTV" / "Configuration"
PAGE = CONFIG / "configPage.html"
CSS = CONFIG / "admin.css"

page = PAGE.read_text(encoding="utf-8")
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
    background: linear-gradient(135deg, rgba(59,130,246,.14), rgba(139,92,246,.08));
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
    padding: .55rem;
    margin: .8rem 0 1.4rem;
    background: rgba(11,15,20,.94);
    border: 1px solid var(--spectraltv-border);
    border-radius: 14px;
    backdrop-filter: blur(10px);
    box-shadow: 0 10px 28px rgba(0,0,0,.18);
}

.spectraltv-tabs .tab {
    font-weight: 600;
    padding: .58rem .9rem;
}

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

.split-layout.has-panel {
    align-items: start;
}

.split-side {
    border-radius: 14px;
}

.data-table-wrap {
    background: rgba(255,255,255,.015);
}

#channel-form-panel .actions {
    grid-column: 1 / -1;
    padding-top: .7rem;
    border-top: 1px solid var(--spectraltv-border);
}

/* Weather and music channel creation are intentionally not part of Spectral TV. */
.spectraltv-tabs .tab[data-tab="weather"],
#tab-weather,
#weather-fields,
#ebs-music-source-field,
#ebs-library-field {
    display: none !important;
}

@media (max-width: 760px) {
    #spectraltv-app { padding: .8rem .65rem 3rem; }
    .spectraltv-tabs {
        position: static;
        overflow-x: auto;
        flex-wrap: nowrap;
        justify-content: flex-start;
    }
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

# Remove obsolete channel types from the visible selector while preserving enum values in
# the backend for database compatibility with old installations.
for option in (
    '<option value="2">Music Video</option>',
    '<option value="3">Music</option>',
    '<option value="4">Weather</option>',
):
    page = page.replace(option, "")

# Weather remains hidden legacy markup so old JavaScript references cannot null-deref.
page = page.replace(
    '<button type="button" class="tab" data-tab="weather" role="tab">Weather</button>',
    '',
)
page = page.replace(
    '<div id="weather-fields" class="field-group hidden">',
    '<div id="weather-fields" class="field-group hidden" hidden aria-hidden="true">',
)
page = page.replace(' Weather channels update the guide automatically.', '')

# Off-air playback keeps noise/silence/beep; Jellyfin music-library playback is removed.
page = page.replace(
    '<option value="0">Background music from Jellyfin library</option>',
    '<option value="0" hidden disabled>Background music (legacy, disabled)</option>',
)
page = page.replace(
    '<label class="field">\n                        <span>Background music source</span>\n                        <select id="ebs-music-source" class="emby-input">',
    '<label class="field hidden" id="ebs-music-source-field" hidden aria-hidden="true">\n                        <span>Background music source</span>\n                        <select id="ebs-music-source" class="emby-input">',
)
page = page.replace(
    '<label class="field" id="ebs-library-field">',
    '<label class="field hidden" id="ebs-library-field" hidden aria-hidden="true">',
)

# Clearer navigation labels without changing data-tab values or JavaScript behavior.
renames = {
    '>Presets</button>': '>Ready-made</button>',
    '>Lineups</button>': '>Schedule</button>',
    '>List</button>': '>Lists</button>',
    '>Special Presentation</button>': '>Specials</button>',
    '>Logo Sets</button>': '>Logos</button>',
    '>EBS</button>': '>Off-Air</button>',
    '>AI</button>': '>AI Assist</button>',
    '>Playwright</button>': '>Browser Engine</button>',
    '>General</button>': '>Settings</button>',
    '>Live TV Setup</button>': '>Jellyfin Setup</button>',
    '>Tasks</button>': '>Maintenance</button>',
}
for old, new in renames.items():
    page = page.replace(old, new)

PAGE.write_text(page, encoding="utf-8")
print("Prepared simplified Spectral TV admin UI")
