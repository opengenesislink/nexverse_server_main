#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BIN="$ROOT/bin"
COMMON="$BIN/config-include/StandaloneCommon.ini"
COMMON_TEMPLATE="$ROOT/tools/ci/NexLoginPlacement/StandaloneCommon.ini"
DB="/tmp/nexverse-login-placement.db"
ESTATE_DB="/tmp/nexverse-login-placement-estate.db"
ASSET_DB="/tmp/nexverse-login-placement-assets.db"
SIM_DB="/tmp/nexverse-login-placement-sim.db"
LOG="$ROOT/nexverse-login-placement-smoke.log"
REQUEST="/tmp/nexverse-login-placement-request.xml"
RESPONSE="/tmp/nexverse-login-placement-response.xml"
BACKUP="/tmp/nexverse-standalone-common-backup.ini"
LOGIN_PASSWORD="NexVerse-CI-Placement-${GITHUB_RUN_ID:-local}"
export NEXVERSE_CI_LOGIN_PASSWORD="$LOGIN_PASSWORD"

OPENSIM_PID=""

cleanup() {
  if [ -n "$OPENSIM_PID" ]; then
    kill "$OPENSIM_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      if ! kill -0 "$OPENSIM_PID" 2>/dev/null; then
        break
      fi
      sleep 0.1
    done
    if kill -0 "$OPENSIM_PID" 2>/dev/null; then
      kill -9 "$OPENSIM_PID" 2>/dev/null || true
    fi
    wait "$OPENSIM_PID" 2>/dev/null || true
  fi

  if [ -f "$BACKUP" ]; then
    mv -f "$BACKUP" "$COMMON"
  else
    rm -f "$COMMON"
  fi

  for file in "$DB" "$ESTATE_DB" "$ASSET_DB" "$SIM_DB"; do
    rm -f "$file" "$file-shm" "$file-wal"
  done
}

on_exit() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "::group::NexVerse successful login placement OpenSim log"
    tail -n 400 "$LOG" 2>/dev/null || true
    echo "::endgroup::"
    echo "::group::NexVerse successful login placement XML-RPC response"
    tail -c 32768 "$RESPONSE" 2>/dev/null || true
    echo "::endgroup::"
  fi
  cleanup
  exit "$status"
}
trap on_exit EXIT

for file in "$DB" "$ESTATE_DB" "$ASSET_DB" "$SIM_DB"; do
  rm -f "$file" "$file-shm" "$file-wal"
done
rm -f "$LOG" "$REQUEST" "$RESPONSE" "$BACKUP"

if [ -f "$COMMON" ]; then
  cp "$COMMON" "$BACKUP"
fi
cp "$COMMON_TEMPLATE" "$COMMON"

cd "$BIN"
timeout 45s dotnet run --configuration Release \
  --project ../tools/ci/NexLoginPlacementSeed/NexLoginPlacementSeed.csproj \
  -- "$DB"
dotnet OpenSim.dll \
  -background=true \
  -inifile OpenSim.NexVerseLoginPlacement.Tests.ini \
  > "$LOG" 2>&1 &
OPENSIM_PID=$!

READY=0
for i in $(seq 1 120); do
  if ! kill -0 "$OPENSIM_PID" 2>/dev/null; then
    echo "::error::OpenSim exited before the CI region became ready."
    exit 1
  fi

  if grep -F 'INITIALIZATION COMPLETE FOR NexVerse CI Landing - LOGINS ENABLED' "$LOG" >/dev/null 2>&1 &&
     grep -F '[XML RPC MODULE]: RemoteData channel opened channel=' "$LOG" >/dev/null 2>&1 &&
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
          <member><name>last</name><value><string>Resident</string></value></member>
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
LOGIN_DEADLINE=$((SECONDS + 75))
while [ "$SECONDS" -lt "$LOGIN_DEADLINE" ]; do
  if ! kill -0 "$OPENSIM_PID" 2>/dev/null; then
    echo "::error::OpenSim exited while waiting for successful login placement."
    exit 1
  fi

  rm -f "$RESPONSE"
  curl --silent --show-error --connect-timeout 2 --max-time 30 \
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

REMOTE_CHANNEL=""
REMOTE_DEADLINE=$((SECONDS + 45))
while [ "$SECONDS" -lt "$REMOTE_DEADLINE" ]; do
  REMOTE_LINE="$(grep -m1 -F "[XML RPC MODULE]: RemoteData channel opened channel=" "$LOG" 2>/dev/null || true)"
  if [ -n "$REMOTE_LINE" ]; then
    REMOTE_CHANNEL="$(printf '%s\n' "$REMOTE_LINE" | sed -nE 's/.*channel=([0-9A-Fa-f-]{36}).*/\1/p')"
    if [ -n "$REMOTE_CHANNEL" ]; then
      break
    fi
  fi
  sleep 0.25
done

if [ -z "$REMOTE_CHANNEL" ]; then
  echo "::error::Running LSL RemoteData probe did not open an XML-RPC channel."
  exit 1
fi

python3 - "$REMOTE_CHANNEL" <<'PY'
import sys
import xmlrpc.client

channel = sys.argv[1]
proxy = xmlrpc.client.ServerProxy(
    "http://127.0.0.1:19102/",
    allow_none=True,
)

response = proxy.llRemoteData(
    {
        "Channel": channel,
        "IntValue": 41,
        "StringValue": "nexverse-e2e",
    }
)

if isinstance(response, (list, tuple)):
    assert len(response) == 1, response
    payload = response[0]
else:
    payload = response

assert payload["StringValue"] == "nexverse-ci-reply:nexverse-e2e", payload
assert int(payload["IntValue"]) == 42, payload

print(
    "NexVerse running-region LSL XML-RPC RemoteData request/reply: OK",
    channel,
)
PY

grep -F "NexVerseCI Resident" "$LOG"
grep -F "Firestorm-Release CI Placement" "$LOG"
grep -F "Found destination NexVerse CI Landing" "$LOG"
grep -F "All clear. Sending login response to NexVerseCI Resident" "$LOG"
grep -F "[XML RPC MODULE]: RemoteData channel opened channel=$REMOTE_CHANNEL" "$LOG"

echo "NexVerse successful simulator login placement + LSL RemoteData smoke: OK"
