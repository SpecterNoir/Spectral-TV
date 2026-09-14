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
base_url="http://127.0.0.1:18096"
auth_identity='MediaBrowser Client="Spectral TV CI", DeviceId="spectral-tv-ci", Device="GitHub Actions", Version="1.0"'

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

fail_with_logs() {
  echo "$1" >&2
  if [[ -n "${2:-}" && -f "$2" ]]; then
    cat "$2" >&2 || true
  fi
  docker logs "$container" >&2 || true
  exit 1
}

json_field() {
  python3 - "$1" "$2" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    value = json.load(handle)
for part in sys.argv[2].split('.'):
    value = value[part]
if isinstance(value, bool):
    print("true" if value else "false")
elif value is None:
    print("")
else:
    print(value)
PY
}

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
  if curl -fsS --max-time 2 "$base_url/System/Info/Public" >/dev/null 2>&1; then
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
  fail_with_logs "Jellyfin failed to become healthy with Spectral TV installed." "$logfile"
fi

if ! grep -Eiq 'Loaded plugin:.*Spectral TV|Loaded plugin.*"Spectral TV"' "$logfile"; then
  fail_with_logs "Jellyfin started, but Spectral TV was not reported as a loaded plugin." "$logfile"
fi

if grep -Eiq 'BadImageFormatException|Disabling plugin.*Spectral|Spectral TV.*Disabling plugin|SpectralTV.*Unhandled|SpectralTV.*fatal' "$logfile"; then
  fail_with_logs "Spectral TV produced a startup-fatal signature." "$logfile"
fi

# Capture the ephemeral first user's name while first-time-setup access is still allowed.
startup_user="$workdir/startup-user.json"
startup_user_code=$(curl -sS -o "$startup_user" -w '%{http_code}' --max-time 5 \
  "$base_url/Startup/User" || true)
if [[ "$startup_user_code" != "200" ]]; then
  fail_with_logs "Could not read the ephemeral Jellyfin startup user (HTTP $startup_user_code)." "$startup_user"
fi
startup_username=$(json_field "$startup_user" "Name")
if [[ -z "$startup_username" ]]; then
  fail_with_logs "The ephemeral Jellyfin startup user had no name." "$startup_user"
fi

# A brand-new CI server is still behind Jellyfin's first-run middleware. Complete only
# this disposable container's wizard before exercising ordinary plugin API routes.
wizard_code=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 \
  -X POST "$base_url/Startup/Complete" || true)
if [[ "$wizard_code" != "204" ]]; then
  fail_with_logs "Could not complete the ephemeral Jellyfin startup wizard (HTTP $wizard_code)."
fi

# Activate the anonymous setup-URL action so CI verifies that Jellyfin can construct the
# setup controller with its native ITunerHostManager/IListingsManager dependencies.
setup_body="$workdir/setup.json"
setup_code=$(curl -sS -o "$setup_body" -w '%{http_code}' --max-time 5 \
  "$base_url/SpectralTV/api/setup/urls" || true)
if [[ "$setup_code" != "200" ]]; then
  fail_with_logs "Spectral TV setup controller returned HTTP $setup_code instead of 200." "$setup_body"
fi
setup_json=$(cat "$setup_body")
if [[ "$setup_json" != *"/SpectralTV/iptv/channels.m3u"* || "$setup_json" != *"/SpectralTV/iptv/epg.xml"* ]]; then
  fail_with_logs "Spectral TV setup controller did not return the expected native Live TV URLs." "$setup_body"
fi

# The mutation/status actions require an elevated Jellyfin session. An unauthenticated
# request must not be able to inspect or change the server's Live TV configuration.
status_code=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 \
  "$base_url/SpectralTV/api/setup/livetv-status" || true)
if [[ "$status_code" != "401" && "$status_code" != "403" ]]; then
  fail_with_logs "Spectral TV Live TV status endpoint was not protected by Jellyfin admin authorization (HTTP $status_code)."
fi

# Authenticate the disposable first user exactly as Jellyfin's own integration tests do.
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
  fail_with_logs "Could not authenticate the ephemeral Jellyfin administrator (HTTP $auth_code)." "$auth_response"
fi
access_token=$(json_field "$auth_response" "AccessToken")
if [[ -z "$access_token" ]]; then
  fail_with_logs "Jellyfin authentication returned no access token." "$auth_response"
fi
auth_header="$auth_identity, Token=\"$access_token\""

# Force generated URLs to an address reachable from inside the Jellyfin container. This
# is only CI configuration and never touches a user's server.
settings_body="$workdir/setup-settings.json"
settings_code=$(curl -sS -o "$settings_body" -w '%{http_code}' --max-time 8 \
  -X PUT "$base_url/SpectralTV/api/setup/settings" \
  -H "Authorization: $auth_header" \
  -H 'Content-Type: application/json' \
  --data '{"publicBaseUrl":"http://127.0.0.1:8096"}' || true)
if [[ "$settings_code" != "200" ]]; then
  fail_with_logs "Could not set the ephemeral Spectral TV server address (HTTP $settings_code)." "$settings_body"
fi

# Exercise the actual one-click mutation. This validates Jellyfin's M3U tuner manager,
# XMLTV listings manager, Spectral ownership logic, plugin configuration persistence, and
# the generated M3U endpoint together instead of merely proving the controller resolves.
connect_one="$workdir/connect-one.json"
connect_one_code=$(curl -sS -o "$connect_one" -w '%{http_code}' --max-time 20 \
  -X POST "$base_url/SpectralTV/api/setup/livetv" \
  -H "Authorization: $auth_header" || true)
if [[ "$connect_one_code" != "200" ]]; then
  fail_with_logs "Spectral TV could not register its native Jellyfin Live TV sources (HTTP $connect_one_code)." "$connect_one"
fi
if [[ "$(json_field "$connect_one" "connected")" != "true" ]]; then
  fail_with_logs "Spectral TV registration completed without reporting a connected Live TV state." "$connect_one"
fi
first_tuner_id=$(json_field "$connect_one" "tunerId")
first_provider_id=$(json_field "$connect_one" "listingsProviderId")
if [[ -z "$first_tuner_id" || -z "$first_provider_id" ]]; then
  fail_with_logs "Spectral TV registration did not return native tuner/provider IDs." "$connect_one"
fi

# Call Connect a second time. Idempotency requires the exact same Jellyfin objects to be
# updated rather than a duplicate tuner or XMLTV provider being created.
connect_two="$workdir/connect-two.json"
connect_two_code=$(curl -sS -o "$connect_two" -w '%{http_code}' --max-time 20 \
  -X POST "$base_url/SpectralTV/api/setup/livetv" \
  -H "Authorization: $auth_header" || true)
if [[ "$connect_two_code" != "200" ]]; then
  fail_with_logs "A repeated Spectral TV Live TV connection failed (HTTP $connect_two_code)." "$connect_two"
fi
if [[ "$(json_field "$connect_two" "tunerId")" != "$first_tuner_id" \
   || "$(json_field "$connect_two" "listingsProviderId")" != "$first_provider_id" ]]; then
  fail_with_logs "Repeated Live TV connection created different Jellyfin tuner/provider IDs instead of reusing the existing entries." "$connect_two"
fi

# Change the public base URL to another address that resolves inside the same container,
# then repair. The saved IDs must survive the URL change and both native entries must move
# together to the new Spectral endpoints.
settings_two="$workdir/setup-settings-two.json"
settings_two_code=$(curl -sS -o "$settings_two" -w '%{http_code}' --max-time 8 \
  -X PUT "$base_url/SpectralTV/api/setup/settings" \
  -H "Authorization: $auth_header" \
  -H 'Content-Type: application/json' \
  --data '{"publicBaseUrl":"http://localhost:8096"}' || true)
if [[ "$settings_two_code" != "200" ]]; then
  fail_with_logs "Could not change the ephemeral Spectral TV server address (HTTP $settings_two_code)." "$settings_two"
fi

connect_three="$workdir/connect-three.json"
connect_three_code=$(curl -sS -o "$connect_three" -w '%{http_code}' --max-time 20 \
  -X POST "$base_url/SpectralTV/api/setup/livetv" \
  -H "Authorization: $auth_header" || true)
if [[ "$connect_three_code" != "200" ]]; then
  fail_with_logs "Spectral TV could not repair Live TV after a server-address change (HTTP $connect_three_code)." "$connect_three"
fi
if [[ "$(json_field "$connect_three" "connected")" != "true" \
   || "$(json_field "$connect_three" "tunerId")" != "$first_tuner_id" \
   || "$(json_field "$connect_three" "listingsProviderId")" != "$first_provider_id" ]]; then
  fail_with_logs "Spectral TV did not preserve its Jellyfin Live TV identities during URL repair." "$connect_three"
fi
if [[ "$(json_field "$connect_three" "m3uUrl")" != "http://localhost:8096/SpectralTV/iptv/channels.m3u" \
   || "$(json_field "$connect_three" "xmlTvUrl")" != "http://localhost:8096/SpectralTV/iptv/epg.xml" ]]; then
  fail_with_logs "Spectral TV did not update both native Live TV URLs during repair." "$connect_three"
fi

status_body="$workdir/livetv-status.json"
status_auth_code=$(curl -sS -o "$status_body" -w '%{http_code}' --max-time 8 \
  "$base_url/SpectralTV/api/setup/livetv-status" \
  -H "Authorization: $auth_header" || true)
if [[ "$status_auth_code" != "200" || "$(json_field "$status_body" "connected")" != "true" ]]; then
  fail_with_logs "Authenticated Live TV status did not confirm the repaired Spectral connection (HTTP $status_auth_code)." "$status_body"
fi

echo "Spectral TV native Live TV registration smoke test passed."

# Keep the host alive briefly after the integration exercise so hosted-service and guide
# refresh failures have time to surface. A plugin that destabilizes Jellyfin must fail CI.
sleep 15
if ! docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null | grep -qx true; then
  fail_with_logs "Jellyfin exited after Spectral TV's Live TV integration smoke test."
fi

if ! curl -fsS --max-time 3 "$base_url/System/Info/Public" >/dev/null 2>&1; then
  fail_with_logs "Jellyfin stopped responding after Spectral TV's Live TV integration smoke test."
fi

docker logs "$container" >"$logfile" 2>&1 || true
if grep -Eiq 'BadImageFormatException|Disabling plugin.*Spectral|Spectral TV.*Disabling plugin|SpectralTV.*Unhandled|SpectralTV.*fatal' "$logfile"; then
  fail_with_logs "Spectral TV produced a fatal signature after its Live TV integration exercise." "$logfile"
fi

echo "Spectral TV Jellyfin 12 Linux smoke test passed."
