#!/usr/bin/env python3
"""Fail CI when Spectral TV admin pages regress into dead controls, invalid JavaScript, or legacy UI."""

from __future__ import annotations

from html.parser import HTMLParser
from pathlib import Path
import re
import shutil
import subprocess


ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "Jellyfin.Plugin.SpectralTV" / "Configuration"
HTML_PATH = CONFIG / "configPage.html"
JS_PATH = CONFIG / "admin.js"
STUDIO_HTML_PATH = CONFIG / "channelStudioPage.html"
STUDIO_JS_PATH = CONFIG / "channelStudio.js"
CONNECT_HTML_PATH = CONFIG / "liveTvConnectPage.html"
CONNECT_JS_PATH = CONFIG / "liveTvConnect.js"
PLUGIN_PATH = ROOT / "Jellyfin.Plugin.SpectralTV" / "Plugin.cs"
API_PATH = ROOT / "Jellyfin.Plugin.SpectralTV" / "Api"


class AuditParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.ids: list[str] = []
        self.buttons: list[dict[str, str]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {key: value or "" for key, value in attrs}
        if values.get("id"):
            self.ids.append(values["id"])
        if tag == "button":
            self.buttons.append(values)


def audit_page(
    label: str,
    html: str,
    javascript: str,
    errors: list[str],
    allowed_delegated_keys: tuple[str, ...],
) -> tuple[set[str], set[str], int]:
    parser = AuditParser()
    parser.feed(html)

    duplicates = sorted({item for item in parser.ids if parser.ids.count(item) > 1})
    if duplicates:
        errors.append(f"{label}: duplicate HTML ids: " + ", ".join(duplicates))

    html_ids = set(parser.ids)
    referenced_ids = set(re.findall(r"byId\(['\"]([^'\"]+)['\"]\)", javascript))
    missing_ids = sorted(referenced_ids - html_ids)
    if missing_ids:
        errors.append(f"{label}: JavaScript references missing HTML ids: " + ", ".join(missing_ids))

    for index, button in enumerate(parser.buttons, start=1):
        if not button.get("type"):
            errors.append(f"{label}: button #{index} has no explicit type")
        button_id = button.get("id")
        if button_id and button_id not in referenced_ids:
            errors.append(f"{label}: button #{button_id} has no JavaScript binding")
        if (
            not button_id
            and not any(key in button for key in allowed_delegated_keys)
            and button.get("type") != "submit"
        ):
            errors.append(f"{label}: button #{index} has no id, submit behavior, or delegated action")

    return html_ids, referenced_ids, len(parser.buttons)


def endpoint_families(javascript: str) -> set[str]:
    return set(re.findall(r"request\((?:`|['\"])/?([a-z-]+)", javascript))


def check_javascript_syntax(label: str, javascript: str, errors: list[str]) -> None:
    node = shutil.which("node")
    if node is None:
        errors.append(f"{label}: Node.js is required to validate admin JavaScript syntax")
        return

    result = subprocess.run(
        [node, "--input-type=module", "--check"],
        input=javascript,
        text=True,
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        errors.append(f"{label}: JavaScript syntax check failed: {detail}")


def main() -> int:
    html = HTML_PATH.read_text(encoding="utf-8")
    javascript = JS_PATH.read_text(encoding="utf-8")
    studio_html = STUDIO_HTML_PATH.read_text(encoding="utf-8")
    studio_javascript = STUDIO_JS_PATH.read_text(encoding="utf-8")
    connect_html = CONNECT_HTML_PATH.read_text(encoding="utf-8")
    connect_javascript = CONNECT_JS_PATH.read_text(encoding="utf-8")
    plugin = PLUGIN_PATH.read_text(encoding="utf-8")
    errors: list[str] = []

    check_javascript_syntax("main admin", javascript, errors)
    check_javascript_syntax("Channel Studio", studio_javascript, errors)
    check_javascript_syntax("Live TV connect", connect_javascript, errors)

    html_ids, referenced_ids, button_count = audit_page(
        "main admin",
        html,
        javascript,
        errors,
        ("data-step", "data-copy"),
    )
    studio_ids, studio_referenced_ids, studio_button_count = audit_page(
        "Channel Studio",
        studio_html,
        studio_javascript,
        errors,
        ("data-cs-mode", "data-cs-pick", "data-live-remove", "data-od-source-remove", "data-od-filler-remove"),
    )
    connect_ids, connect_referenced_ids, connect_button_count = audit_page(
        "Live TV connect",
        connect_html,
        connect_javascript,
        errors,
        ("data-copy",),
    )

    forbidden = (
        "Binarygeek119",
        "Ready-made Channels",
        "Legacy #",
        "WeatherStar",
        "Playwright",
        ">Weather<",
        ">Music<",
        ">AI<",
        ">EBS<",
    )
    for value in forbidden:
        if value.lower() in html.lower() or value.lower() in studio_html.lower() or value.lower() in connect_html.lower():
            errors.append(f"retired UI concept is visible: {value}")

    required_steps = {"channel", "programming", "breaks", "finish"}
    actual_steps = set(re.findall(r'data-step="([^"]+)"', html))
    actual_panels = set(re.findall(r'data-panel="([^"]+)"', html))
    if actual_steps != required_steps or actual_panels != required_steps:
        errors.append(f"main admin workflow mismatch: steps={sorted(actual_steps)} panels={sorted(actual_panels)}")

    studio_modes = set(re.findall(r'data-cs-mode="([^"]+)"', studio_html))
    if studio_modes != {"live", "ondemand"}:
        errors.append(f"Channel Studio mode mismatch: modes={sorted(studio_modes)}")

    controller_routes = set()
    for controller in API_PATH.glob("*.cs"):
        controller_routes.update(
            route.lower()
            for route in re.findall(r'\[Route\("SpectralTV/api/([^"/{]+)', controller.read_text(encoding="utf-8"))
        )

    all_endpoint_families = endpoint_families(javascript) | endpoint_families(studio_javascript)
    missing_routes = sorted(all_endpoint_families - controller_routes)
    if missing_routes:
        errors.append("admin calls API families without controllers: " + ", ".join(missing_routes))

    setup_controller = (API_PATH / "SetupController.cs").read_text(encoding="utf-8")
    for route in ('[HttpGet("livetv-status")]', '[HttpPost("livetv")]'):
        if route not in setup_controller:
            errors.append(f"Live TV connect UI requires missing setup endpoint: {route}")
    if "SpectralTV/api/setup/" not in connect_javascript:
        errors.append("Live TV connect JavaScript is not scoped to the setup API")

    retired_controllers = {
        "AiController.cs", "ChannelPresetsController.cs", "CommercialsController.cs",
        "EbsController.cs", "LineupsController.cs", "ListsController.cs",
        "PlaywrightController.cs", "SpecialPresentationsController.cs", "WeatherController.cs",
    }
    present_retired = sorted(path.name for path in API_PATH.glob("*.cs") if path.name in retired_controllers)
    if present_retired:
        errors.append("retired controllers still present: " + ", ".join(present_retired))

    if plugin.count("EnableInMainMenu = true") != 1:
        errors.append("plugin must expose exactly one dashboard entry")
    if "channelStudioPage.html" not in plugin or "SpectralTV_channelStudio.js" not in plugin:
        errors.append("Channel Studio resources are not registered by the plugin")
    if "liveTvConnectPage.html" not in plugin or "SpectralTV_livetvConnect.js" not in plugin:
        errors.append("one-click Live TV connection resources are not registered by the plugin")
    if "SpectralTV_ConnectLiveTv" not in studio_html:
        errors.append("Channel Studio does not link to the one-click Live TV connection page")

    if errors:
        print("Spectral TV admin UI validation FAILED:")
        for error in errors:
            print(f"  - {error}")
        return 1

    print("Spectral TV admin UI validation passed.")
    print("  JavaScript syntax: valid")
    print(f"  Main HTML ids: {len(html_ids)}")
    print(f"  Main buttons audited: {button_count}")
    print(f"  Main JavaScript-bound ids: {len(referenced_ids)}")
    print(f"  Studio HTML ids: {len(studio_ids)}")
    print(f"  Studio buttons audited: {studio_button_count}")
    print(f"  Studio JavaScript-bound ids: {len(studio_referenced_ids)}")
    print(f"  Live TV connect HTML ids: {len(connect_ids)}")
    print(f"  Live TV connect buttons audited: {connect_button_count}")
    print(f"  Live TV connect JavaScript-bound ids: {len(connect_referenced_ids)}")
    print(f"  API families checked: {len(all_endpoint_families)}")
    print("  Retired UI concepts: none")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
