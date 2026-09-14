#!/usr/bin/env python3
"""Fail CI when the Spectral TV admin page regresses into dead controls or legacy UI."""

from __future__ import annotations

from html.parser import HTMLParser
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
HTML_PATH = ROOT / "Jellyfin.Plugin.SpectralTV" / "Configuration" / "configPage.html"
JS_PATH = ROOT / "Jellyfin.Plugin.SpectralTV" / "Configuration" / "admin.js"
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


def main() -> int:
    html = HTML_PATH.read_text(encoding="utf-8")
    javascript = JS_PATH.read_text(encoding="utf-8")
    plugin = PLUGIN_PATH.read_text(encoding="utf-8")
    parser = AuditParser()
    parser.feed(html)
    errors: list[str] = []

    duplicates = sorted({item for item in parser.ids if parser.ids.count(item) > 1})
    if duplicates:
        errors.append("duplicate HTML ids: " + ", ".join(duplicates))

    html_ids = set(parser.ids)
    referenced_ids = set(re.findall(r"byId\(['\"]([^'\"]+)['\"]\)", javascript))
    missing_ids = sorted(referenced_ids - html_ids)
    if missing_ids:
        errors.append("JavaScript references missing HTML ids: " + ", ".join(missing_ids))

    for index, button in enumerate(parser.buttons, start=1):
        if not button.get("type"):
            errors.append(f"button #{index} has no explicit type")
        button_id = button.get("id")
        if button_id and button_id not in referenced_ids:
            errors.append(f"button #{button_id} has no JavaScript binding")
        if not button_id and not any(key in button for key in ("data-step", "data-copy")) and button.get("type") != "submit":
            errors.append(f"button #{index} has no id, submit behavior, or delegated action")

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
        if value.lower() in html.lower():
            errors.append(f"retired UI concept is visible: {value}")

    required_steps = {"channel", "programming", "breaks", "finish"}
    actual_steps = set(re.findall(r'data-step="([^"]+)"', html))
    actual_panels = set(re.findall(r'data-panel="([^"]+)"', html))
    if actual_steps != required_steps or actual_panels != required_steps:
        errors.append(f"workflow mismatch: steps={sorted(actual_steps)} panels={sorted(actual_panels)}")

    endpoint_families = set(
        re.findall(r"request\((?:`|['\"])/?([a-z-]+)", javascript)
    )
    controller_routes = set()
    for controller in API_PATH.glob("*.cs"):
        controller_routes.update(
            route.lower()
            for route in re.findall(r'\[Route\("SpectralTV/api/([^"/{]+)', controller.read_text(encoding="utf-8"))
        )
    missing_routes = sorted(endpoint_families - controller_routes)
    if missing_routes:
        errors.append("admin calls API families without controllers: " + ", ".join(missing_routes))

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

    if errors:
        print("Spectral TV admin UI validation FAILED:")
        for error in errors:
            print(f"  - {error}")
        return 1

    print("Spectral TV admin UI validation passed.")
    print(f"  HTML ids: {len(html_ids)}")
    print(f"  Buttons audited: {len(parser.buttons)}")
    print(f"  JavaScript-bound ids: {len(referenced_ids)}")
    print(f"  API families checked: {len(endpoint_families)}")
    print("  Retired UI concepts: none")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
