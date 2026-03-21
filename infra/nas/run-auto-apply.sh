#!/usr/bin/env bash
set -euo pipefail

OPS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STATE_DIR="${STATE_DIR:-$OPS_DIR/state}"
AUTO_APPLY_LOG_FILE="${AUTO_APPLY_LOG_FILE:-$STATE_DIR/auto-apply.log}"

mkdir -p "$STATE_DIR"

timestamp() {
  date '+%Y-%m-%d %H:%M:%S'
}

{
  printf '[%s] task start\n' "$(timestamp)"

  set +e
  /bin/bash "$OPS_DIR/auto-apply-release.sh"
  apply_rc=$?
  set -e
  printf '[%s] auto-apply exit=%s\n' "$(timestamp)" "$apply_rc"

  set +e
  /bin/bash "$OPS_DIR/routine-ops-check.sh"
  routine_rc=$?
  set -e
  printf '[%s] routine-check exit=%s\n' "$(timestamp)" "$routine_rc"

  final_rc=$apply_rc
  if [[ $final_rc -eq 0 && $routine_rc -ne 0 ]]; then
    final_rc=$routine_rc
  fi

  printf '[%s] task exit=%s\n' "$(timestamp)" "$final_rc"
  exit "$final_rc"
} >> "$AUTO_APPLY_LOG_FILE" 2>&1
