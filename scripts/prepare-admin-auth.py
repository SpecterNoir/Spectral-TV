#!/usr/bin/env python3
import os
import re
from pathlib import Path

JS_FILES = [
    Path("Jellyfin.Plugin.SpectralTV/Configuration/channelStudio.js"),
    Path("Jellyfin.Plugin.SpectralTV/Configuration/admin.js"),
    Path("Jellyfin.Plugin.SpectralTV/Configuration/liveTvConnect.js"),
]

OLD_AUTH = """    function authHeaders() {\n        const headers = {};\n        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {\n            const token = ApiClient.accessToken();\n            if (token) headers['X-Emby-Token'] = token;\n        }\n        return headers;\n    }\n"""

NEW_AUTH = """    function authHeaders() {\n        const headers = {};\n\n        // Jellyfin 12 no longer accepts X-Emby-Token unless legacy authorization is\n        // explicitly enabled. Build the same MediaBrowser Authorization header that\n        // Jellyfin's own dashboard uses.\n        if (typeof ApiClient !== 'undefined' && typeof ApiClient.setRequestHeaders === 'function') {\n            ApiClient.setRequestHeaders(headers);\n            return headers;\n        }\n\n        // Older clients may still support the legacy token header.\n        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {\n            const token = ApiClient.accessToken();\n            if (token) headers['X-Emby-Token'] = token;\n        }\n        return headers;\n    }\n\n    async function authenticatedFetch(url, init = {}) {\n        // Use Jellyfin's own authenticated transport instead of window.fetch. This is\n        // the same request path Jellyfin's dashboard uses and guarantees the active\n        // MediaBrowser identity is attached immediately before the request is sent.\n        if (typeof ApiClient !== 'undefined' && typeof ApiClient.fetch === 'function') {\n            const request = {\n                url,\n                type: init.method || 'GET',\n                headers: { ...(init.headers || {}) }\n            };\n            if (init.body !== undefined && init.body !== null) request.data = init.body;\n            try {\n                return await ApiClient.fetch(request, true);\n            } catch (error) {\n                if (error && typeof error.status === 'number' && typeof error.text === 'function') return error;\n                throw error;\n            }\n        }\n        return fetch(url, init);\n    }\n"""

for path in JS_FILES:
    text = path.read_text(encoding="utf-8")
    if NEW_AUTH not in text:
        if OLD_AUTH not in text:
            raise SystemExit(f"Could not find the expected authHeaders helper in {path}")
        text = text.replace(OLD_AUTH, NEW_AUTH, 1)

    old_fetch = "const response = await fetch(resolveUrl("
    new_fetch = "const response = await authenticatedFetch(resolveUrl("
    if new_fetch not in text:
        if old_fetch not in text:
            raise SystemExit(f"Could not find the expected Spectral request transport in {path}")
        text = text.replace(old_fetch, new_fetch, 1)
    path.write_text(text, encoding="utf-8")

# Jellyfin Web caches plugin pages/modules aggressively. Reusing the same embedded-resource
# name across releases caused 0.0.3.116 to display the older 0.0.3.111 Channel Studio even
# after the server plugin was updated. Every CI build now gets a unique page and module name.
raw_build = os.environ.get("GITHUB_RUN_NUMBER", "dev")
build_id = re.sub(r"[^A-Za-z0-9]", "", raw_build) or "dev"
suffix = f"run{build_id}"
page_name = f"SpectralTV_ChannelStudio_{suffix}"
controller_names = {
    "SpectralTV_channelStudio.js": f"SpectralTV_channelStudio_{suffix}.js",
    "SpectralTV_admin.js": f"SpectralTV_admin_{suffix}.js",
    "SpectralTV_livetvConnect.js": f"SpectralTV_livetvConnect_{suffix}.js",
}

plugin_path = Path("Jellyfin.Plugin.SpectralTV/Plugin.cs")
plugin_text = plugin_path.read_text(encoding="utf-8")
for old, new in controller_names.items():
    plugin_text = plugin_text.replace(f'Name = "{old}"', f'Name = "{new}"')

plugin_text = plugin_text.replace(
    "Name = Name,\n                DisplayName = Name,\n                EnableInMainMenu = true,",
    f"Name = \"{page_name}\",\n                DisplayName = Name,\n                EnableInMainMenu = true,",
    1,
)
plugin_path.write_text(plugin_text, encoding="utf-8")

page_replacements = {
    Path("Jellyfin.Plugin.SpectralTV/Configuration/channelStudioPage.html"): [
        ("__plugin/SpectralTV_channelStudio.js", f"__plugin/{controller_names['SpectralTV_channelStudio.js']}"),
    ],
    Path("Jellyfin.Plugin.SpectralTV/Configuration/configPage.html"): [
        ("__plugin/SpectralTV_admin.js", f"__plugin/{controller_names['SpectralTV_admin.js']}"),
    ],
    Path("Jellyfin.Plugin.SpectralTV/Configuration/liveTvConnectPage.html"): [
        ("__plugin/SpectralTV_livetvConnect.js", f"__plugin/{controller_names['SpectralTV_livetvConnect.js']}"),
        ("#!/configurationpage?name=Spectral%20TV", f"#!/configurationpage?name={page_name}"),
    ],
}
for path, replacements in page_replacements.items():
    text = path.read_text(encoding="utf-8")
    for old, new in replacements:
        if old not in text and new not in text:
            raise SystemExit(f"Could not find expected controller/page reference {old!r} in {path}")
        text = text.replace(old, new)
    path.write_text(text, encoding="utf-8")

# Put an unmistakable build marker in Channel Studio. This makes a stale Jellyfin Web cache
# visible immediately in screenshots/videos instead of making us guess which UI code is running.
studio_page = Path("Jellyfin.Plugin.SpectralTV/Configuration/channelStudioPage.html")
studio_text = studio_page.read_text(encoding="utf-8")
hero_line = '<p class="cs-muted">Build channels around content rules instead of manually filling every time slot.</p>'
build_marker = hero_line + f'\n            <p class="cs-muted" style="margin-bottom:0;font-size:.78rem">Loaded UI build {build_id}</p>'
if build_marker not in studio_text:
    if hero_line not in studio_text:
        raise SystemExit("Could not locate Channel Studio hero for build marker")
    studio_text = studio_text.replace(hero_line, build_marker, 1)
studio_page.write_text(studio_text, encoding="utf-8")

# The UI validator runs after this preparation step. Teach it to accept the per-build resource names
# while still requiring the correct registered resource family.
validator_path = Path("scripts/validate-admin-ui.py")
validator_text = validator_path.read_text(encoding="utf-8")
validator_text = validator_text.replace(
    r"SpectralTV_channelStudio(?:_auth2)?\.js",
    r"SpectralTV_channelStudio(?:_[A-Za-z0-9]+)?\.js",
)
validator_text = validator_text.replace(
    r"SpectralTV_livetvConnect(?:_auth2)?\.js",
    r"SpectralTV_livetvConnect(?:_[A-Za-z0-9]+)?\.js",
)
validator_path.write_text(validator_text, encoding="utf-8")

for path in JS_FILES:
    text = path.read_text(encoding="utf-8")
    if "ApiClient.setRequestHeaders(headers)" not in text or "ApiClient.fetch(request, true)" not in text:
        raise SystemExit(f"Jellyfin native authorization transport was not installed in {path}")
    if "const response = await fetch(resolveUrl(" in text:
        raise SystemExit(f"Direct unauthenticated Spectral API transport remains in {path}")

plugin_text = plugin_path.read_text(encoding="utf-8")
for new in controller_names.values():
    if f'Name = "{new}"' not in plugin_text:
        raise SystemExit(f"Versioned controller resource name {new} was not registered")
if f'Name = "{page_name}"' not in plugin_text:
    raise SystemExit("Versioned Channel Studio page route was not registered")

print(f"Prepared Jellyfin-native authorization and cache-busted admin resources for build {build_id}.")
