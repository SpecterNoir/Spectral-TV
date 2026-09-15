#!/usr/bin/env python3
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

# Jellyfin web can retain plugin controller modules across plugin upgrades because the
# resource URL is unchanged. Give this auth fix fresh resource names so an installed
# 0.0.3.108 controller cannot remain in the browser module cache after updating.
controller_names = {
    "SpectralTV_channelStudio.js": "SpectralTV_channelStudio_auth2.js",
    "SpectralTV_admin.js": "SpectralTV_admin_auth2.js",
    "SpectralTV_livetvConnect.js": "SpectralTV_livetvConnect_auth2.js",
}

plugin_path = Path("Jellyfin.Plugin.SpectralTV/Plugin.cs")
plugin_text = plugin_path.read_text(encoding="utf-8")
for old, new in controller_names.items():
    plugin_text = plugin_text.replace(f'Name = "{old}"', f'Name = "{new}"')

plugin_text = plugin_text.replace(
    "Name = Name,\n                DisplayName = Name,\n                EnableInMainMenu = true,",
    "Name = \"SpectralTV_ChannelStudio_auth2\",\n                DisplayName = Name,\n                EnableInMainMenu = true,",
    1,
)
plugin_path.write_text(plugin_text, encoding="utf-8")

page_replacements = {
    Path("Jellyfin.Plugin.SpectralTV/Configuration/channelStudioPage.html"): [
        ("__plugin/SpectralTV_channelStudio.js", "__plugin/SpectralTV_channelStudio_auth2.js"),
    ],
    Path("Jellyfin.Plugin.SpectralTV/Configuration/configPage.html"): [
        ("__plugin/SpectralTV_admin.js", "__plugin/SpectralTV_admin_auth2.js"),
    ],
    Path("Jellyfin.Plugin.SpectralTV/Configuration/liveTvConnectPage.html"): [
        ("__plugin/SpectralTV_livetvConnect.js", "__plugin/SpectralTV_livetvConnect_auth2.js"),
        ("#!/configurationpage?name=Spectral%20TV", "#!/configurationpage?name=SpectralTV_ChannelStudio_auth2"),
    ],
}
for path, replacements in page_replacements.items():
    text = path.read_text(encoding="utf-8")
    for old, new in replacements:
        if old not in text and new not in text:
            raise SystemExit(f"Could not find expected controller/page reference {old!r} in {path}")
        text = text.replace(old, new)
    path.write_text(text, encoding="utf-8")

for path in JS_FILES:
    text = path.read_text(encoding="utf-8")
    if "ApiClient.setRequestHeaders(headers)" not in text or "ApiClient.fetch(request, true)" not in text:
        raise SystemExit(f"Jellyfin native authorization transport was not installed in {path}")
    if "const response = await fetch(resolveUrl(" in text:
        raise SystemExit(f"Direct unauthenticated Spectral API transport remains in {path}")

plugin_text = plugin_path.read_text(encoding="utf-8")
for new in controller_names.values():
    if f'Name = "{new}"' not in plugin_text:
        raise SystemExit(f"Fresh controller resource name {new} was not registered")
if 'Name = "SpectralTV_ChannelStudio_auth2"' not in plugin_text:
    raise SystemExit("Fresh Channel Studio page route was not registered")

print("Prepared Jellyfin-native authorization, authenticated transport, and fresh admin resource URLs.")
