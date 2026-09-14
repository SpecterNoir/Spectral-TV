#!/usr/bin/env python3
from pathlib import Path

FILES = [
    Path("Jellyfin.Plugin.SpectralTV/Configuration/channelStudio.js"),
    Path("Jellyfin.Plugin.SpectralTV/Configuration/admin.js"),
    Path("Jellyfin.Plugin.SpectralTV/Configuration/liveTvConnect.js"),
]

OLD = """    function authHeaders() {\n        const headers = {};\n        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {\n            const token = ApiClient.accessToken();\n            if (token) headers['X-Emby-Token'] = token;\n        }\n        return headers;\n    }\n"""

NEW = """    function authHeaders() {\n        const headers = {};\n\n        // Use Jellyfin's own ApiClient header builder first. Jellyfin 12's elevated\n        // admin policies expect the full MediaBrowser Authorization header, not just\n        // a legacy X-Emby-Token header. This also carries the active client/device\n        // identity exactly the same way Jellyfin's own dashboard requests do.\n        if (typeof ApiClient !== 'undefined' && typeof ApiClient.setRequestHeaders === 'function') {\n            ApiClient.setRequestHeaders(headers);\n            return headers;\n        }\n\n        // Compatibility fallback for older Jellyfin web clients.\n        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {\n            const token = ApiClient.accessToken();\n            if (token) headers['X-Emby-Token'] = token;\n        }\n        return headers;\n    }\n"""

for path in FILES:
    text = path.read_text(encoding="utf-8")
    if NEW in text:
        continue
    if OLD not in text:
        raise SystemExit(f"Could not find the expected authHeaders helper in {path}")
    text = text.replace(OLD, NEW, 1)
    path.write_text(text, encoding="utf-8")

for path in FILES:
    text = path.read_text(encoding="utf-8")
    if "ApiClient.setRequestHeaders(headers)" not in text:
        raise SystemExit(f"Jellyfin native authorization was not installed in {path}")

print("Prepared Jellyfin-native authorization for Spectral TV admin pages.")
