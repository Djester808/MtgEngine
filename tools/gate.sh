#!/usr/bin/env sh
# The full gate, run by the merge queue and by the coordinator after a merge round — never
# by a builder. Four steps, each printed with its own exit code, stopping at the first
# failure. They are separate commands on purpose: a chained format/build/test hides a stall
# behind buffering, and then nobody knows which step is wedged.
#
# Stop the API first (it holds the SQLite db and the built dll) and kill stale test hosts:
#   Stop-Process -Name MtgEngine.Api -Force

set -u

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT" || exit 1

MSBUILDDISABLENODEREUSE=1
export MSBUILDDISABLENODEREUSE

step() {
  LABEL="$1"
  shift
  echo "== $LABEL =="
  "$@"
  STATUS=$?
  echo "== $LABEL exit $STATUS =="
  if [ $STATUS -eq 124 ]; then
    echo "exit 124 = build lock, re-run once"
  fi
  if [ $STATUS -ne 0 ]; then
    echo "gate failed at: $LABEL" >&2
    exit $STATUS
  fi
}

step format dotnet format MtgEngine.sln --verify-no-changes
step build dotnet build MtgEngine.sln -nologo -v q -p:UseSharedCompilation=false
step test dotnet test MtgEngine.sln --no-build -v q
step file-size node tools/check-file-size.js

echo "== gate green =="
