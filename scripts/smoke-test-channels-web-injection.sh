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
web_html="$workdir/index.html"
logfile="$workdir/jellyfin.log"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  if [[ -n "${workdir:-}" && "$workdir" == /tmp/* ]]; then
    sudo rm -rf -- "$workdir" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

fail_with_logs() {
  echo "$1" >&2
  if [[ -f "$web_html" ]]; then
    echo "--- Jellyfin web response (first 80 lines) ---" >&2
    sed -n '1,80p' "$web_html" >&2 || true
  fi
  docker logs "$container" >&2 || true
  exit 1
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

if ! grep -Fq "const SECTION_VALUE = 'spectraltvchannels';" "$web_html"; then
  fail_with_logs "The injected Jellyfin Web shell did not contain the Spectral TV Channels bridge code."
fi

docker logs "$container" >"$logfile" 2>&1 || true
if ! grep -Fq 'Spectral TV injected the Channels Home bridge directly into Jellyfin Web' "$logfile"; then
  fail_with_logs "Spectral TV injected markup was present, but the expected middleware success log was missing."
fi

if grep -Eiq 'BadImageFormatException|Disabling plugin.*Spectral|Spectral TV.*Disabling plugin|SpectralTV.*Unhandled|SpectralTV.*fatal' "$logfile"; then
  fail_with_logs "Spectral TV produced a fatal signature during the Channels web-injection smoke test."
fi

echo "Spectral TV Channels web injection smoke test passed."
