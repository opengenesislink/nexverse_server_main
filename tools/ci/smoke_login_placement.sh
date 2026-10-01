#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BIN="$ROOT/bin"
COMMON="$BIN/config-include/StandaloneCommon.ini"
COMMON_TEMPLATE="$ROOT/tools/ci/NexLoginPlacement/StandaloneCommon.ini"
DB="/tmp/nexverse-login-placement.db"
LOG="$ROOT/nexverse-login-placement-smoke.log"
REQUEST="/tmp/nexverse-login-placement-request.xml"
RESPONSE="/tmp/nexverse-login-placement-response.xml"
BACKUP="/tmp/nexverse-standalone-common-backup.ini"\nLOGIN_PASSWORD="NexVerse-CI-Placement-${GITHUB_RUN_ID:-local}"\nexport NEXVERSE_CI_LOGIN_PASSWORD="$LOGIN_PASSWORD"\n\nOPENSIM_PID=""

cleanup() {
  if [ -n "$OPENSIM_PID" ]; then
    kill "$OPENSIM_PID" 2>/dev/null || true
    wait "$OPENSIM_PID" 2>/dev/null || true
  fi

  if [ -f "$BACKUP" ]; then
    mv -f "$BACKUP" "$COMMON"
  else
    rm -f "$COMMON"
  fi

  rm -f "$DB" "$DB-shm" "$DB-wal"
}

on_exit() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "::group::NexVerse successful login placement OpenSim log"
    cat "$LOG" 2>/dev/null || true
    echo "::endgroup::"
    echo "::group::NexVerse successful login placement XML-RPC response"
    cat "$RESPONSE" 2>/dev/null || true
    echo "::endgroup::"
  fi
  cleanup
  exit "$status"
}
trap on_exit EXIT

rm -f "$DB" "$DB-shm" "$DB-wal" "$LOG" "$REQUEST" "$RESPONSE" "$BACKUP"

if [ -f "$COMMON" ]; then
  cp "$COMMON" "$BACKUP"
fi
cp "$COMMON_TEMPLATE" "$COMMON"

cd "$BIN"
dotnet run --configuration Release \
  --project ../tools/ci/NexLoginPlacementSeed/NexLoginPlacementSeed.csproj \
  -- "$DB"
dotnet OpenSim.dll \
  -inifile OpenSim.NexVerseLoginPlacement.Tests.ini \
  > "$LOG" 2>&1 &
OPENSIM_PID=$!

READY=0
for i in $(seq 1 120); do
  if ! kill -0 "$OPENSIM_PID" 2>/dev/null; then
    echo "::error::OpenSim exited before the CI region became ready."
    exit 1
  fi

  if grep -F "NexVerse CI Landing" "$LOG" >/dev/null 2>&1 &&
     (echo > /dev/tcp/127.0.0.1/19100) >/dev/null 2>&1; then
    READY=1
    break
  fi

  sleep 0.25
done

if [ "$READY" -ne 1 ]; then
  echo "::error::OpenSim CI region did not become ready."
  exit 1
fi

PASSWORD_HASH="$(printf '%s' "$LOGIN_PASSWORD" | md5sum | awk '{print $1}')"

cat > "$REQUEST" <<XML
<?xml version="1.0"?>
<methodCall>
  <methodName>login_to_simulator</methodName>
  <params>
    <param>
      <value>
        <struct>
          <member><name>first</name><value><string>NexVerseCI</string></value></member>
          <member><name>last</name><value><string></string></value></member>
          <member><name>passwd</name><value><string>\$1\$$PASSWORD_HASH</string></value></member>
          <member><name>start</name><value><string>home</string></value></member>
          <member><name>scope_id</name><value><string>00000000-0000-0000-0000-000000000000</string></value></member>
          <member><name>version</name><value><string>Firestorm-Release CI Placement</string></value></member>
          <member><name>channel</name><value><string>Firestorm-Releasex64</string></value></member>
          <member><name>mac</name><value><string>00:00:00:00:00:01</string></value></member>
          <member><name>id0</name><value><string>nexverse-ci-placement</string></value></member>
        </struct>
      </value>
    </param>
  </params>
</methodCall>
XML

LOGIN_OK=0
for i in $(seq 1 80); do
  if ! kill -0 "$OPENSIM_PID" 2>/dev/null; then
    echo "::error::OpenSim exited while waiting for successful login placement."
    exit 1
  fi

  curl --silent --show-error --max-time 5 \
    -H 'Content-Type: text/xml' \
    --data-binary @"$REQUEST" \
    http://127.0.0.1:19100/ > "$RESPONSE" || true

  if python3 - "$RESPONSE" <<'PY'
import sys
import xmlrpc.client

path = sys.argv[1]
try:
    with open(path, "rb") as handle:
        params, _ = xmlrpc.client.loads(handle.read())
    payload = params[0]
except Exception:
    raise SystemExit(1)

value = payload.get("login")
if value is True or str(value).lower() == "true":
    raise SystemExit(0)
raise SystemExit(1)
PY
  then
    LOGIN_OK=1
    break
  fi

  sleep 0.25
done

if [ "$LOGIN_OK" -ne 1 ]; then
  echo "::error::Firestorm-compatible login never reached successful simulator placement."
  exit 1
fi

python3 - "$RESPONSE" <<'PY'
import sys
import xmlrpc.client

with open(sys.argv[1], "rb") as handle:
    params, _ = xmlrpc.client.loads(handle.read())

payload = params[0]

assert str(payload["login"]).lower() == "true", payload
assert payload["first_name"] == "NexVerseCI", payload["first_name"]
assert payload["last_name"] == "Resident", payload["last_name"]
assert int(payload["sim_port"]) == 19101, payload["sim_port"]
assert payload["sim_ip"] == "127.0.0.1", payload["sim_ip"]
assert int(payload["region_x"]) == 1200 * 256, payload["region_x"]
assert int(payload["region_y"]) == 1200 * 256, payload["region_y"]
assert int(payload["region_size_x"]) == 256, payload["region_size_x"]
assert int(payload["region_size_y"]) == 256, payload["region_size_y"]

seed = payload.get("seed_capability", "")
assert seed.startswith("http://127.0.0.1:19100/"), seed
assert "/CAPS/" in seed, seed

print("NexVerse successful Firestorm-protocol simulator placement response: OK")
PY

grep -F "NexVerseCI Resident" "$LOG"
grep -F "Firestorm-Release CI Placement" "$LOG"
grep -F "Found destination NexVerse CI Landing" "$LOG"
grep -F "All clear. Sending login response to NexVerseCI Resident" "$LOG"

echo "NexVerse successful simulator login placement smoke: OK"
