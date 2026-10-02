#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BIN="$ROOT/bin"
COMMON="$BIN/config-include/StandaloneCommon.ini"
COMMON_TEMPLATE="$ROOT/tools/ci/NexHypergrid/StandaloneCommon.ini"
BACKUP="/tmp/nexverse-hg-standalone-common-backup.ini"
SOURCE_LOG="$ROOT/nexverse-hg-source-smoke.log"
DEST_LOG="$ROOT/nexverse-hg-destination-smoke.log"
SOURCE_DB="/tmp/nexverse-hg-source.db"
SOURCE_PID=""
DEST_PID=""
LOGIN_PASSWORD="NexVerse-HG-CI-${GITHUB_RUN_ID:-local}"
export NEXVERSE_CI_LOGIN_PASSWORD="$LOGIN_PASSWORD"

cleanup_db_family() {
  local base="$1"
  local stem="${base%.db}"
  for file in "$base" "$stem-estate.db" "$stem-assets.db" "$stem-sim.db"; do
    rm -f "$file" "$file-shm" "$file-wal"
  done
}

stop_pid() {
  local pid="$1"
  if [ -z "$pid" ]; then
    return
  fi
  kill "$pid" 2>/dev/null || true
  for _ in $(seq 1 20); do
    if ! kill -0 "$pid" 2>/dev/null; then
      break
    fi
    sleep 0.1
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill -9 "$pid" 2>/dev/null || true
  fi
  wait "$pid" 2>/dev/null || true
}

cleanup() {
  stop_pid "$DEST_PID"
  stop_pid "$SOURCE_PID"

  if [ -f "$BACKUP" ]; then
    mv -f "$BACKUP" "$COMMON"
  else
    rm -f "$COMMON"
  fi

  cleanup_db_family "/tmp/nexverse-hg-source.db"
  cleanup_db_family "/tmp/nexverse-hg-dest.db"
}

on_exit() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "::group::NexVerse Hypergrid source log"
    tail -n 350 "$SOURCE_LOG" 2>/dev/null || true
    echo "::endgroup::"
    echo "::group::NexVerse Hypergrid destination log"
    tail -n 450 "$DEST_LOG" 2>/dev/null || true
    echo "::endgroup::"
  fi
  cleanup
  exit "$status"
}
trap on_exit EXIT

cleanup_db_family "/tmp/nexverse-hg-source.db"
cleanup_db_family "/tmp/nexverse-hg-dest.db"
rm -f "$SOURCE_LOG" "$DEST_LOG" "$BACKUP"

if [ -f "$COMMON" ]; then
  cp "$COMMON" "$BACKUP"
fi
cp "$COMMON_TEMPLATE" "$COMMON"

cd "$BIN"

dotnet run --configuration Release   --project ../tools/ci/NexLoginPlacementSeed/NexLoginPlacementSeed.csproj   -- "$SOURCE_DB"

dotnet Robust.dll   -inifile Robust.NexVerseHypergridSource.Tests.ini   > "$SOURCE_LOG" 2>&1 &
SOURCE_PID=$!

SOURCE_READY=0
for _ in $(seq 1 120); do
  if ! kill -0 "$SOURCE_PID" 2>/dev/null; then
    echo "::error::Hypergrid source HomeAgent service exited during startup."
    exit 1
  fi
  if (echo > /dev/tcp/127.0.0.1/19200) >/dev/null 2>&1; then
    SOURCE_READY=1
    break
  fi
  sleep 0.25
done

if [ "$SOURCE_READY" -ne 1 ]; then
  echo "::error::Hypergrid source HomeAgent service did not become ready."
  exit 1
fi

dotnet OpenSim.dll   -background=true   -inifile OpenSim.NexVerseHypergridDestination.Tests.ini   > "$DEST_LOG" 2>&1 &
DEST_PID=$!

DEST_READY=0
for _ in $(seq 1 180); do
  if ! kill -0 "$DEST_PID" 2>/dev/null; then
    echo "::error::Hypergrid destination simulator exited during startup."
    exit 1
  fi

  if (echo > /dev/tcp/127.0.0.1/19300) >/dev/null 2>&1 &&
     grep -F "NexVerse HG Landing" "$DEST_LOG" >/dev/null 2>&1; then
    DEST_READY=1
    break
  fi
  sleep 0.25
done

if [ "$DEST_READY" -ne 1 ]; then
  echo "::error::Hypergrid destination simulator did not become ready."
  exit 1
fi

HG_OK=0
for _ in $(seq 1 40); do
  if ! kill -0 "$SOURCE_PID" 2>/dev/null || ! kill -0 "$DEST_PID" 2>/dev/null; then
    echo "::error::Hypergrid source or destination exited before login completed."
    exit 1
  fi

  if dotnet run --configuration Release        --project ../tools/ci/NexHypergridLoginSmoke/NexHypergridLoginSmoke.csproj
  then
    HG_OK=1
    break
  fi

  sleep 0.5
done

if [ "$HG_OK" -ne 1 ]; then
  echo "::error::Hypergrid HomeAgent -> Gatekeeper login did not succeed."
  exit 1
fi

grep -F "[USER AGENT SERVICE]: Request to login user NexVerseCI Resident" "$SOURCE_LOG"
grep -F "desired grid: http://127.0.0.1:19300/" "$SOURCE_LOG"
grep -F "[GATEKEEPER SERVICE]: Login request for NexVerseCI Resident" "$DEST_LOG"
grep -F "[GATEKEEPER SERVICE]: Identity verified for NexVerseCI Resident" "$DEST_LOG"
grep -F "NexVerseCI Resident" "$DEST_LOG"

echo "NexVerse Hypergrid HomeAgent/Gatekeeper end-to-end login smoke: OK"
