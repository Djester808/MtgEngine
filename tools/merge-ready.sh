#!/usr/bin/env sh
# The merge queue, CARD_ENGINE_PLAN.md § Orchestration. Run by the coordinator from the main
# checkout, on the integration branch. For every linked worktree whose agent wrote ready/<id>:
# merge its branch, run the gate steps (separately, never chained), keep the merge on green,
# reset to the first parent on red. One merge at a time; the ledger records each outcome.
#
#   tools/merge-ready.sh [--dry-run]
#
# Never edit tools/gate.sh or this file while it runs. If it is killed mid-gate, the integration
# tip may be an ungated merge: `git reset --hard HEAD~1` if `git log -1` shows a merge commit
# the ledger does not list as merged.

set -u
DRY=0
[ "${1:-}" = "--dry-run" ] && DRY=1

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT" || exit 1
BRANCH="$(git branch --show-current)"
if [ "$BRANCH" != "card-engine" ]; then
  echo "merge-ready: run on card-engine, not $BRANCH" >&2
  exit 64
fi
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  echo "merge-ready: the main checkout has uncommitted changes; commit or discard them first" >&2
  exit 65
fi

MERGED=0
BOUNCED=0
git worktree list --porcelain | awk '/^worktree /{wt=$2} /^branch /{print wt" "$2}' | while read -r WT REF; do
  [ "$WT" = "$ROOT" ] && continue
  [ -d "$WT/ready" ] || continue
  for MARKER in "$WT"/ready/*; do
    [ -f "$MARKER" ] || continue
    ID="$(basename "$MARKER")"
    AGENT_BRANCH="${REF#refs/heads/}"
    echo "== $ID: merging $AGENT_BRANCH from $WT =="
    if [ "$DRY" -eq 1 ]; then
      echo "   (dry run)"
      continue
    fi
    if [ -n "$(git -C "$WT" status --porcelain)" ]; then
      echo "   $ID has uncommitted changes in its worktree; skipping until it commits" >&2
      continue
    fi
    if ! git merge --no-ff --no-edit "$AGENT_BRANCH" -m "Merge $ID ($AGENT_BRANCH) into card-engine"; then
      git merge --abort 2>/dev/null
      echo "   merge conflict; bounced" >&2
      node tools/ledger.js set "$ID" blocked "merge conflict with card-engine" >/dev/null
      BOUNCED=$((BOUNCED + 1))
      continue
    fi
    # The gate's output goes to a log per merge so a red run can be read afterwards; a stopped
    # queue otherwise takes the reason with it. One retry, because a parallel worktree's build
    # can abort this checkout's test host, and that is contention, not a regression.
    mkdir -p .agents/merge-logs
    LOG=".agents/merge-logs/$ID-$(date -u +%Y%m%dT%H%M%SZ).log"
    if sh tools/gate.sh >"$LOG" 2>&1 || { echo "   gate red once; re-running alone" >&2; sh tools/gate.sh >>"$LOG" 2>&1; }; then
      node tools/ledger.js set "$ID" merged "$(git rev-parse --short HEAD)" >/dev/null
      rm -f "$MARKER"
      MERGED=$((MERGED + 1))
      echo "   merged as $(git rev-parse --short HEAD)  (gate log $LOG)"
    else
      git reset --hard HEAD~1 >/dev/null
      node tools/ledger.js set "$ID" blocked "gate red twice after merge; see $LOG" >/dev/null
      BOUNCED=$((BOUNCED + 1))
      echo "   gate red twice; reset to $(git rev-parse --short HEAD); bounced (see $LOG)" >&2
      grep -aE 'error|Failed!|FAIL|over 500' "$LOG" | head -12 >&2
    fi
  done
done
echo "merge-ready: done"
