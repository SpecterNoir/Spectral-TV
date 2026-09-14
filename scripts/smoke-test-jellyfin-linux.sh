#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <plugin-zip>" >&2
  exit 2
fi

artifact=$(realpath "$1")
workdir=$(mktemp -d)
container="spectral-tv-smoke-${GITHUB_RUN_ID:-local}-$$"
logfile="$workdir/jellyfin.log"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  rm -rf "$workdir"
}
trap cleanup EXIT

mkdir -p "$workdir/config/plugins/Spectral TV_test"
unzip -q "$artifact" -d "$workdir/config/plugins/Spectral TV_test"

# Run the exact major/minor host targeted by the plugin. The smoke test is intentionally
# Linux-based because the development target is the user's Synology Jellyfin 12 server.
docker pull jellyfin/jellyfin:12.0.0 >/dev/null

docker run -d \
  --name "$container" \
  -p 127.0.0.1:18096:8096 \
  -v "$workdir/config:/config" \
  jellyfin/jellyfin:12.0.0 >/dev/null

healthy=0
for _ in $(seq 1 60); do
  if curl -fsS --max-time 2 http://127.0.0.1:18096/System/Info/Public >/dev/null 2>&1; then
    healthy=1
    break
  fi

  if ! docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null | grep -qx true; then
    break
  fi

  sleep 2
done

docker logs "$container" >"$logfile" 2>&1 || true

if [[ "$healthy" -ne 1 ]]; then
  echo "Jellyfin failed to become healthy with Spectral TV installed." >&2
  cat "$logfile" >&2
  exit 1
fi

if ! grep -Eiq 'Loaded plugin:.*Spectral TV|Loaded plugin.*"Spectral TV"' "$logfile"; then
  echo "Jellyfin started, but Spectral TV was not reported as a loaded plugin." >&2
  cat "$logfile" >&2
  exit 1
fi

if grep -Eiq 'BadImageFormatException|Disabling plugin.*Spectral|Spectral TV.*Disabling plugin|SpectralTV.*Unhandled|SpectralTV.*fatal' "$logfile"; then
  echo "Spectral TV produced a startup-fatal signature." >&2
  cat "$logfile" >&2
  exit 1
fi

# Keep the host alive briefly after it first becomes reachable so hosted-service startup
# failures have time to surface. A plugin that kills Jellyfin seconds after boot must fail CI.
sleep 15
if ! docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null | grep -qx true; then
  echo "Jellyfin exited after initial startup with Spectral TV installed." >&2
  docker logs "$container" >&2 || true
  exit 1
fi

if ! curl -fsS --max-time 3 http://127.0.0.1:18096/System/Info/Public >/dev/null 2>&1; then
  echo "Jellyfin stopped responding after Spectral TV startup." >&2
  docker logs "$container" >&2 || true
  exit 1
fi

echo "Spectral TV Jellyfin 12 Linux smoke test passed."
