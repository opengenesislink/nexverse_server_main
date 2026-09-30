#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../../bin"

LOG="../nexverse-login-smoke.log"
RESPONSE="/tmp/nexverse-login-response.xml"

dotnet Robust.dll -inifile Robust.NexVerseLogin.Tests.ini > "$LOG" 2>&1 &
ROBUST_PID=$!

cleanup() {
  kill "$ROBUST_PID" 2>/dev/null || true
  wait "$ROBUST_PID" 2>/dev/null || true
}
trap cleanup EXIT

READY=0
for i in $(seq 1 40); do
  if kill -0 "$ROBUST_PID" 2>/dev/null; then
    if grep -F '[LLLOGIN IN CONNECTOR]: Starting' "$LOG" >/dev/null 2>&1; then
      READY=1
      break
    fi
  else
    echo "::error::Robust exited before LLLogin became ready."
    cat "$LOG"
    exit 1
  fi
  sleep 0.25
done

if [ "$READY" -ne 1 ]; then
  echo "::error::LLLogin connector did not become ready."
  cat "$LOG"
  exit 1
fi

cat > /tmp/nexverse-login-request.xml <<'XML'
<?xml version="1.0"?>
<methodCall>
  <methodName>login_to_simulator</methodName>
  <params>
    <param>
      <value>
        <struct>
          <member><name>first</name><value><string>NexVerseCiMissing</string></value></member>
          <member><name>last</name><value><string>Resident</string></value></member>
          <member><name>passwd</name><value><string>$1$00000000000000000000000000000000</string></value></member>
          <member><name>start</name><value><string>last</string></value></member>
          <member><name>scope_id</name><value><string>00000000-0000-0000-0000-000000000000</string></value></member>
          <member><name>version</name><value><string>Firestorm-Release CI</string></value></member>
          <member><name>channel</name><value><string>Firestorm-Releasex64</string></value></member>
          <member><name>mac</name><value><string>00:00:00:00:00:00</string></value></member>
          <member><name>id0</name><value><string>nexverse-ci</string></value></member>
        </struct>
      </value>
    </param>
  </params>
</methodCall>
XML

curl --silent --show-error --fail --max-time 5   -H 'Content-Type: text/xml'   --data-binary @/tmp/nexverse-login-request.xml   http://127.0.0.1:19092/ > "$RESPONSE"

grep -F '<methodResponse>' "$RESPONSE"
grep -F '<name>login</name>' "$RESPONSE"
grep -F 'false' "$RESPONSE"
grep -F 'NexVerseCiMissing Resident' "$LOG"
grep -F 'Firestorm-Release CI' "$LOG"
grep -F 'reason: user not found' "$LOG"

echo "NexVerse Firestorm-compatible LLLogin XML-RPC runtime smoke: OK"
