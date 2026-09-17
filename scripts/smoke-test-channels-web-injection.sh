#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <plugin-zip>" >&2
  exit 2
fi

artifact=$(realpath "$1")
workdir=$(mktemp -d)
container="spectral-tv-web-smoke-${GITHUB_RUN_ID:-local}-$$"
base_url="http://127.0.0.1:18097"
auth_identity='MediaBrowser Client="Spectral TV Channels CI", DeviceId="spectral-tv-channels-ci", Device="GitHub Actions", Version="1.0"'
web_html="$workdir/index.html"
bridge_js="$workdir/channels-home.js"
browser_html="$workdir/channels-home-browser.html"
browser_dom="$workdir/channels-home-browser-dom.html"
web_browser_dom="$workdir/jellyfin-web-browser-dom.html"
logfile="$workdir/jellyfin.log"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  if [[ -n "${workdir:-}" && "$workdir" == /tmp/* ]]; then
    sudo rm -rf -- "$workdir" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

fail_with_logs() {
  local detail_file
  echo "$1" >&2
  detail_file="${2:-$web_html}"
  if [[ -f "$detail_file" ]]; then
    echo "--- Diagnostic response (first 80 lines) ---" >&2
    sed -n '1,80p' "$detail_file" >&2 || true
  fi
  docker logs "$container" >&2 || true
  exit 1
}

json_field() {
  python3 - "$1" "$2" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as handle:
    value = json.load(handle)
for part in sys.argv[2].split('.'):
    if not isinstance(value, dict):
        print('')
        raise SystemExit(0)
    if part in value:
        value = value[part]
        continue
    key = next((candidate for candidate in value if candidate.lower() == part.lower()), None)
    if key is None:
        print('')
        raise SystemExit(0)
    value = value[key]
if isinstance(value, bool):
    print('true' if value else 'false')
elif value is None:
    print('')
else:
    print(value)
PY
}

mkdir -p "$workdir/config/plugins/Spectral TV_test"
unzip -q "$artifact" -d "$workdir/config/plugins/Spectral TV_test"

docker pull jellyfin/jellyfin:12.0 >/dev/null

docker run -d \
  --name "$container" \
  -p 127.0.0.1:18097:8096 \
  -v "$workdir/config:/config" \
  jellyfin/jellyfin:12.0 >/dev/null

healthy=0
for _ in $(seq 1 60); do
  if curl -fsS --max-time 2 "$base_url/System/Info/Public" >/dev/null 2>&1; then
    healthy=1
    break
  fi

  if ! docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null | grep -qx true; then
    break
  fi

  sleep 2
done

if [[ "$healthy" -ne 1 ]]; then
  fail_with_logs "Jellyfin failed to become healthy for the Channels web-injection smoke test."
fi

web_code=""
for _ in $(seq 1 30); do
  web_code=$(curl -sS -o "$web_html" -w '%{http_code}' --max-time 5 \
    -H 'Accept-Encoding: identity' \
    "$base_url/web/index.html" || true)
  if [[ "$web_code" == "200" ]]; then
    break
  fi
  sleep 1
done

if [[ "$web_code" != "200" ]]; then
  fail_with_logs "Jellyfin Web returned HTTP $web_code instead of 200."
fi

if ! grep -Fq '<!-- BEGIN Spectral TV Channels Home -->' "$web_html"; then
  fail_with_logs "Spectral TV's Channels startup filter did not inject its marker into Jellyfin Web."
fi

if ! grep -Eq '<script defer src="\.\./SpectralTV/web/channels-home\.js\?v=[^"]+"></script>' "$web_html"; then
  fail_with_logs "The injected Jellyfin Web shell did not contain the external Spectral TV Channels bridge loader."
fi

docker logs "$container" >"$logfile" 2>&1 || true
if ! grep -Fq 'Spectral TV injected the external Channels Home bridge loader into Jellyfin Web' "$logfile"; then
  fail_with_logs "Spectral TV injected markup was present, but the expected middleware success log was missing."
fi

startup_user="$workdir/startup-user.json"
startup_user_code=""
for _ in $(seq 1 90); do
  startup_user_code=$(curl -sS -o "$startup_user" -w '%{http_code}' --max-time 5 \
    "$base_url/Startup/User" || true)
  if [[ "$startup_user_code" == "200" ]]; then
    break
  fi
  sleep 2
done
if [[ "$startup_user_code" != "200" ]]; then
  fail_with_logs "Jellyfin setup did not become ready for the Channels browser test (HTTP $startup_user_code)."
fi
startup_username=$(json_field "$startup_user" "Name")
if [[ -z "$startup_username" ]]; then
  fail_with_logs "The disposable Jellyfin startup user had no name." "$startup_user"
fi

wizard_code=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 \
  -X POST "$base_url/Startup/Complete" || true)
if [[ "$wizard_code" != "204" ]]; then
  fail_with_logs "Could not complete the disposable Jellyfin startup wizard for the Channels browser test (HTTP $wizard_code)."
fi

auth_request="$workdir/auth-request.json"
python3 - "$startup_username" >"$auth_request" <<'PY'
import json
import sys
print(json.dumps({"Username": sys.argv[1], "Pw": ""}))
PY
auth_response="$workdir/auth-response.json"
auth_code=$(curl -sS -o "$auth_response" -w '%{http_code}' --max-time 8 \
  -X POST "$base_url/Users/AuthenticateByName" \
  -H "Authorization: $auth_identity" \
  -H 'Content-Type: application/json' \
  --data-binary "@$auth_request" || true)
if [[ "$auth_code" != "200" ]]; then
  fail_with_logs "Could not authenticate the disposable Jellyfin administrator (HTTP $auth_code)." "$auth_response"
fi
access_token=$(json_field "$auth_response" "AccessToken")
if [[ -z "$access_token" ]]; then
  fail_with_logs "Jellyfin authentication returned no access token." "$auth_response"
fi
auth_header="$auth_identity, Token=\"$access_token\""

create_channel="$workdir/create-channel.json"
create_code=$(curl -sS -o "$create_channel" -w '%{http_code}' --max-time 8 \
  -X POST "$base_url/SpectralTV/api/channels" \
  -H "Authorization: $auth_header" \
  -H 'Content-Type: application/json' \
  --data '{"number":141,"name":"CI Spectral Channel","enabled":true,"aspectRatio":0,"scanlinesEnabled":false,"bugPlacement":0}' || true)
if [[ "$create_code" != "201" ]]; then
  fail_with_logs "Channel Studio's real channel API could not create the CI channel (HTTP $create_code)." "$create_channel"
fi
created_channel_id=$(json_field "$create_channel" "id")
if [[ -z "$created_channel_id" ]]; then
  fail_with_logs "The real channel API created a channel without returning its ID." "$create_channel"
fi

viewer_channels="$workdir/viewer-channels.json"
viewer_code=$(curl -sS -o "$viewer_channels" -w '%{http_code}' --max-time 8 \
  "$base_url/SpectralTV/api/viewer/channels" \
  -H "Authorization: $auth_header" || true)
if [[ "$viewer_code" != "200" ]]; then
  fail_with_logs "The viewer Channels endpoint returned HTTP $viewer_code instead of 200." "$viewer_channels"
fi
if ! python3 - "$viewer_channels" "$created_channel_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as handle:
    channels = json.load(handle)
channel_id = sys.argv[2].replace('-', '').lower()

def get_ci(obj, name):
    if name in obj:
        return obj[name]
    key = next((candidate for candidate in obj if candidate.lower() == name.lower()), None)
    return obj.get(key) if key is not None else None

match = next((c for c in channels if str(get_ci(c, 'id') or '').replace('-', '').lower() == channel_id), None)
if match is None:
    raise SystemExit('Created Channel Studio channel was absent from the viewer Channels endpoint')
if get_ci(match, 'name') != 'CI Spectral Channel':
    raise SystemExit(f"Viewer endpoint returned wrong channel name: {get_ci(match, 'name')!r}")
if str(get_ci(match, 'number')) != '141':
    raise SystemExit(f"Viewer endpoint returned wrong channel number: {get_ci(match, 'number')!r}")
PY
then
  fail_with_logs "The viewer Channels endpoint did not return the channel created through Channel Studio." "$viewer_channels"
fi

bridge_code=$(curl -sS -o "$bridge_js" -w '%{http_code}' --max-time 5 \
  "$base_url/SpectralTV/web/channels-home.js?v=smoke" || true)
if [[ "$bridge_code" != "200" ]]; then
  fail_with_logs "The Spectral TV Channels bridge endpoint returned HTTP $bridge_code instead of 200."
fi
if ! grep -Fq "const SECTION_VALUE = 'spectraltvchannels';" "$bridge_js" \
  || ! grep -Fq 'window.__spectralTvChannelsHomeBridge' "$bridge_js"; then
  fail_with_logs "The Spectral TV Channels bridge endpoint did not return the expected executable script."
fi

if ! grep -Fq "const CHANNELS_ENDPOINT = 'SpectralTV/api/viewer/channels';" "$bridge_js"; then
  fail_with_logs "The Channels Home bridge is not reading Spectral TV's real viewer channel endpoint." "$bridge_js"
fi
if ! grep -Fq "apiJson('LiveTv/Channels'" "$bridge_js"; then
  fail_with_logs "The Channels Home bridge is not resolving Spectral channels to Jellyfin native Live TV items." "$bridge_js"
fi
if grep -Fq 'SpectralTV/api/viewer/on-demand' "$bridge_js"; then
  fail_with_logs "The Channels Home bridge regressed to the obsolete on-demand playlist data source." "$bridge_js"
fi

browser_bin=$(command -v google-chrome || command -v chromium || command -v chromium-browser || true)
if [[ -z "$browser_bin" ]]; then
  fail_with_logs "No Chromium-compatible browser was available for the Channels DOM smoke test."
fi

"$browser_bin" \
  --headless=new \
  --no-sandbox \
  --disable-gpu \
  --user-data-dir="$workdir/jellyfin-web-chrome-profile" \
  --virtual-time-budget=5000 \
  --dump-dom "$base_url/web/index.html" >"$web_browser_dom" 2>"$workdir/jellyfin-web-chrome.log" || \
  fail_with_logs "Chromium could not load Jellyfin's real web shell."

if ! grep -Fq 'data-spectral-tv-channels-home-bridge="loaded"' "$web_browser_dom"; then
  fail_with_logs "The Spectral TV Channels bridge did not execute inside Jellyfin's real web shell." "$web_browser_dom"
fi

python3 - "$browser_html" "$base_url/SpectralTV/web/channels-home.js?v=browser-smoke" <<'PY'
import html
import sys
from pathlib import Path

target = Path(sys.argv[1])
script_url = html.escape(sys.argv[2], quote=True)
selects = "".join(
    f'<select id="selectHomeSection{index}"><option value="resume">Continue Watching</option><option value="none">None</option></select>'
    for index in range(1, 11)
)
target.write_text(
    '<!doctype html><html><head><meta charset="utf-8"></head><body>'
    f'<form>{selects}<button type="submit">Save</button></form>'
    '<div class="homeSectionsContainer"><div class="section0"></div></div>'
    '<script>'
    'window.ApiClient={'
    'getCurrentUserId:function(){return "smoke-user";},'
    'getUrl:function(path){return path;},'
    'setRequestHeaders:function(){},'
    'serverId:function(){return "smoke-server";}'
    '};'
    'window.fetch=async function(url){'
    'var value=String(url).indexOf("home-section")>=0'
    '?{sectionIndex:0}'
    ':[{id:"11111111-1111-1111-1111-111111111111",number:"101",name:"Spectral CI Channel",'
    'currentTitle:"CI Program",scheduleReady:true,liveTvItemId:"22222222-2222-2222-2222-222222222222"}];'
    'return {ok:true,status:200,statusText:"OK",text:async function(){return JSON.stringify(value);}};'
    '};'
    '</script>'
    f'<script defer src="{script_url}"></script>'
    '</body></html>',
    encoding='utf-8')
PY

"$browser_bin" \
  --headless=new \
  --no-sandbox \
  --disable-gpu \
  --allow-file-access-from-files \
  --user-data-dir="$workdir/chrome-profile" \
  --virtual-time-budget=3000 \
  --dump-dom "file://$browser_html" >"$browser_dom" 2>"$workdir/chrome.log" || \
  fail_with_logs "Chromium could not execute the Spectral TV Channels bridge."

option_count=$( (grep -o 'option value="spectraltvchannels"' "$browser_dom" || true) | wc -l | tr -d ' ')
if [[ "$option_count" != "10" ]]; then
  fail_with_logs "The real browser DOM contained $option_count Channels options instead of 10." "$browser_dom"
fi

if ! grep -Fq 'Spectral CI Channel' "$browser_dom" \
  || ! grep -Fq 'data-type="TvChannel"' "$browser_dom" \
  || ! grep -Fq 'data-action="play"' "$browser_dom"; then
  fail_with_logs "The real browser DOM did not render the configured channel as a playable Jellyfin TvChannel card." "$browser_dom"
fi

if grep -Fq 'Spectral On Demand' "$browser_dom"; then
  fail_with_logs "The Channels Home row regressed to the unrelated on-demand collection." "$browser_dom"
fi

docker logs "$container" >"$logfile" 2>&1 || true
if ! grep -Fq 'Jellyfin Web requested the Spectral TV Channels Home bridge' "$logfile"; then
  fail_with_logs "The external Channels script was not requested from Spectral TV during the browser test."
fi

if grep -Eiq 'BadImageFormatException|Disabling plugin.*Spectral|Spectral TV.*Disabling plugin|SpectralTV.*Unhandled|SpectralTV.*fatal' "$logfile"; then
  fail_with_logs "Spectral TV produced a fatal signature during the Channels web-injection smoke test."
fi

echo "Spectral TV Channels loader, configured-channel catalog, playable card, and real browser DOM smoke test passed."
