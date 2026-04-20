#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

AUTO_APPLY_ENABLED="${NAS_SCHEDULED_APPLY_ENABLED:-true}"
PENDING_RELEASE_FILE="${PENDING_RELEASE_FILE:-$STATE_DIR/pending-release.txt}"
AUTO_APPLY_LOG_FILE="${AUTO_APPLY_LOG_FILE:-$STATE_DIR/auto-apply.log}"
AUTO_APPLY_LOCK_DIR="${AUTO_APPLY_LOCK_DIR:-$STATE_DIR/auto-apply.lock}"
FAILED_RELEASE_FILE="${FAILED_RELEASE_FILE:-$STATE_DIR/failed-release.txt}"
AUTO_APPLY_ENABLED_NORMALIZED="$(printf '%s' "$AUTO_APPLY_ENABLED" | tr -d -- '\r' | LC_ALL=C sed $'1s/^\xEF\xBB\xBF//' | tr '[:upper:]' '[:lower:]')"

ensure_state_dir

timestamp() {
  date '+%Y-%m-%dT%H:%M:%S%z'
}

log_auto_apply() {
  echo "$(timestamp) $*" >> "$AUTO_APPLY_LOG_FILE"
}

log_auto_apply "auto_apply_invoked enabled=$AUTO_APPLY_ENABLED pending=$PENDING_RELEASE_FILE current=$CURRENT_RELEASE_FILE"

case "$AUTO_APPLY_ENABLED_NORMALIZED" in
  true|1|yes|on) ;;
  *)
    log_auto_apply "auto_apply_disabled"
    exit 0
    ;;
esac

if [[ ! -f "$PENDING_RELEASE_FILE" ]]; then
  log_auto_apply "pending_missing"
  exit 0
fi

release_id="$(read_first_line_clean "$PENDING_RELEASE_FILE" || true)"
release_id="$(sanitize_release_id "$release_id")"
if [[ -z "$release_id" ]]; then
  log_auto_apply "pending_empty"
  rm -f "$PENDING_RELEASE_FILE"
  exit 0
fi

if ! mkdir "$AUTO_APPLY_LOCK_DIR" 2>/dev/null; then
  log_auto_apply "lock_busy release=$release_id"
  exit 0
fi
trap 'rmdir "$AUTO_APPLY_LOCK_DIR" >/dev/null 2>&1 || true' EXIT

current_release=""
if [[ -f "$CURRENT_RELEASE_FILE" ]]; then
  current_release="$(read_first_line_clean "$CURRENT_RELEASE_FILE" || true)"
  current_release="$(sanitize_release_id "$current_release")"
fi

if [[ "$current_release" == "$release_id" ]]; then
  if wait_for_health 3; then
    rm -f "$PENDING_RELEASE_FILE"
    log_auto_apply "already_current release=$release_id"
    exit 0
  fi

  log_auto_apply "reapply_unhealthy_current release=$release_id"
fi

log_auto_apply "apply_start release=$release_id"

if /bin/bash "$OPS_DIR/apply-release.sh" "$release_id" >> "$AUTO_APPLY_LOG_FILE" 2>&1; then
  rm -f "$PENDING_RELEASE_FILE"
  log_auto_apply "apply_ok release=$release_id"
  exit 0
fi

printf '%s\n' "$release_id" > "$FAILED_RELEASE_FILE"
rm -f "$PENDING_RELEASE_FILE"
log_auto_apply "apply_failed release=$release_id pending_cleared=1 failed_marker=$FAILED_RELEASE_FILE"
exit 1
