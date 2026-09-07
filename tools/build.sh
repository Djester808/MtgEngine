#!/usr/bin/env sh
# Scoped build for one agent: one project, then that project's tests filtered to the
# agent's anchor. CARD_ENGINE_PLAN.md § "Build isolation" — a scoped build is ~8s where the
# solution build is ~3min, and shared compilation plus node reuse is what lets one
# worktree's build abort another worktree's test host.
#
#   tools/build.sh <project.csproj|project-dir> [<anchor>]
#
# With an anchor, the test project is derived from the project name:
#   MtgEngine.Rules/MtgEngine.Rules.csproj -> tests/MtgEngine.Rules.Tests/MtgEngine.Rules.Tests.csproj
#
# Exit code is the failing step's. 124 is the build lock, not a failure — run it once more.

set -u

if [ $# -lt 1 ]; then
  echo "usage: tools/build.sh <project.csproj|project-dir> [<anchor>]" >&2
  exit 64
fi

PROJECT="$1"
ANCHOR="${2:-}"

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT" || exit 1

MSBUILDDISABLENODEREUSE=1
export MSBUILDDISABLENODEREUSE

report_lock() {
  if [ "$1" -eq 124 ]; then
    echo "exit 124 = build lock, re-run once"
  fi
}

echo "== build $PROJECT =="
timeout 540 dotnet build "$PROJECT" -nologo -v q -p:UseSharedCompilation=false
STATUS=$?
echo "== build exit $STATUS =="
report_lock $STATUS
[ $STATUS -eq 0 ] || exit $STATUS

[ -n "$ANCHOR" ] || exit 0

# <path>/Foo.csproj or <path>/Foo -> Foo
NAME="$(basename "$PROJECT")"
NAME="${NAME%.csproj}"
TESTS="tests/${NAME}.Tests/${NAME}.Tests.csproj"

if [ ! -f "$TESTS" ]; then
  echo "no test project at $TESTS — nothing to filter for anchor '$ANCHOR'" >&2
  exit 66
fi

# The test project has to be built too; building only the project under test leaves the
# test assembly stale or absent and --no-build would then fail on a cold worktree.
echo "== build $TESTS =="
timeout 540 dotnet build "$TESTS" -nologo -v q -p:UseSharedCompilation=false
STATUS=$?
echo "== build exit $STATUS =="
report_lock $STATUS
[ $STATUS -eq 0 ] || exit $STATUS

echo "== test $TESTS --filter FullyQualifiedName~$ANCHOR =="
timeout 540 dotnet test "$TESTS" --no-build --filter "FullyQualifiedName~$ANCHOR" -v q
STATUS=$?
echo "== test exit $STATUS =="
report_lock $STATUS
exit $STATUS
