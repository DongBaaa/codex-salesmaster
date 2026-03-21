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
  rc=$?
  set -e
  printf '[%s] task exit=%s\n' "$(timestamp)" "$rc"
  exit "$rc"
} >> "$AUTO_APPLY_LOG_FILE" 2>&1
