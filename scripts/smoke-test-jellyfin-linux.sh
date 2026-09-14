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

  # Jellyfin writes its mounted config as the container user, which is not always the
  # GitHub runner UID. Restrict cleanup to the mktemp directory and use sudo so a
  # successful smoke test cannot be reported as failed just because of file ownership.
  if [[ -n "${workdir:-}" && "$workdir" == /tmp/* ]]; then
    sudo rm -rf -- "$workdir" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

mkdir -p "$workdir/config/plugins/Spectral TV_test"
unzip -q "$artifact" -d "$workdir/config/plugins/Spectral TV_test"

# Run the exact major/minor host targeted by the plugin. The official Jellyfin image
# publishes the stable 12.0 line as jellyfin/jellyfin:12.0.
docker pull jellyfin/jellyfin:12.0 >/dev/null

docker run -d \
  --name "$container" \
  -p 127.0.0.1:18096:8096 \
  -v "$workdir/config:/config" \
  jellyfin/jellyfin:12.0 >/dev/null

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

# Activate the anonymous setup-URL action so CI verifies that Jellyfin can construct the
# setup controller with its native ITunerHostManager/IListingsManager dependencies. This
# catches DI/API compatibility problems before a NAS administrator can press Connect.
setup_json=$(curl -fsS --max-time 5 http://127.0.0.1:18096/SpectralTV/api/setup/urls)
if [[ "$setup_json" != *"/SpectralTV/iptv/channels.m3u"* || "$setup_json" != *"/SpectralTV/iptv/epg.xml"* ]]; then
  echo "Spectral TV setup controller did not return the expected native Live TV URLs." >&2
  echo "$setup_json" >&2
  docker logs "$container" >&2 || true
  exit 1
fi

# The mutation/status actions require an elevated Jellyfin session. An unauthenticated
# request must not be able to inspect or change the server's Live TV configuration.
status_code=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 \
  http://127.0.0.1:18096/SpectralTV/api/setup/livetv-status || true)
if [[ "$status_code" != "401" && "$status_code" != "403" ]]; then
  echo "Spectral TV Live TV status endpoint was not protected by Jellyfin admin authorization (HTTP $status_code)." >&2
  docker logs "$container" >&2 || true
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
