#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

DAILY_CHECKS_ENABLED="${DAILY_CHECKS_ENABLED:-true}"
WEEKLY_CHECKS_ENABLED="${WEEKLY_CHECKS_ENABLED:-true}"
BACKUP_CREATE_ENABLED="${BACKUP_CREATE_ENABLED:-true}"
EXTERNAL_REPLICA_ENABLED="${EXTERNAL_REPLICA_ENABLED:-true}"
BACKUP_MAX_AGE_HOURS="${BACKUP_MAX_AGE_HOURS:-36}"
ROUTINE_LOG_FILE="${ROUTINE_LOG_FILE:-$STATE_DIR/routine-ops.log}"
DAILY_STAMP_FILE="${DAILY_STAMP_FILE:-$STATE_DIR/routine-daily.stamp}"
WEEKLY_STAMP_FILE="${WEEKLY_STAMP_FILE:-$STATE_DIR/routine-weekly.stamp}"
DAILY_STATUS_FILE="${DAILY_STATUS_FILE:-$STATE_DIR/daily-check-status.txt}"
WEEKLY_STATUS_FILE="${WEEKLY_STATUS_FILE:-$STATE_DIR/weekly-check-status.txt}"
BACKUP_STATUS_FILE="${BACKUP_STATUS_FILE:-$STATE_DIR/backup-status.txt}"
CERT_STATUS_FILE="${CERT_STATUS_FILE:-$STATE_DIR/cert-status.txt}"
REPLICA_STATUS_FILE="${REPLICA_STATUS_FILE:-$STATE_DIR/external-replica-status.txt}"
base_url="${PUBLIC_BASE_URL:-https://api.example.invalid}"

ensure_state_dir
mkdir -p "$(dirname "$ROUTINE_LOG_FILE")"

log_line() {
  printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1" | tee -a "$ROUTINE_LOG_FILE"
}

http_ok() {
  local url="$1"
  curl -fsS --max-time 20 "$url" >/dev/null
}

run_daily_checks() {
  local today="$1"
  local health_status="fail"
  local manifest_status="fail"
  local backup_status="missing"
  local backup_detail=""
  local replica_status="skipped"
  local replica_detail="disabled"

  if http_ok "$base_url/healthz"; then
    health_status="ok"
  fi

  if http_ok "$base_url/updates/manifest?channel=stable"; then
    manifest_status="ok"
  fi

  if [[ "${BACKUP_CREATE_ENABLED,,}" == "true" ]]; then
    if /bin/bash "$OPS_DIR/backup-now.sh" >> "$ROUTINE_LOG_FILE" 2>&1; then
      backup_status="ok"
    else
      backup_status="failed"
    fi
  fi

  latest_backup="$(find "$BACKUPS_PATH/db" -type f -name '*.sql.gz' -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -n 1 | cut -d' ' -f2- || true)"
  latest_file_backup="$(find "$BACKUPS_PATH/files" -type f \( -name '*.pdf' -o -name '*.png' -o -name '*.jpg' -o -name '*.jpeg' -o -name '*.gif' -o -name '*.webp' -o -name '*.bmp' -o -name '*.heic' -o -name '*.heif' -o -name '*.tif' -o -name '*.tiff' \) -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -n 1 | cut -d' ' -f2- || true)"
  if [[ -n "$latest_backup" ]]; then
    backup_detail="$latest_backup"
    if [[ -n "$latest_file_backup" ]]; then
      backup_detail="$backup_detail | files=$latest_file_backup"
    fi
    if command -v python3 >/dev/null 2>&1; then
      if python3 - "$latest_backup" "$BACKUP_MAX_AGE_HOURS" <<'PY'
import os, sys, time
path=sys.argv[1]
max_age_hours=float(sys.argv[2])
age=(time.time()-os.path.getmtime(path))/3600.0
sys.exit(0 if age <= max_age_hours else 1)
PY
      then
        if [[ "$backup_status" == "missing" ]]; then backup_status="ok"; fi
      else
        if [[ "$backup_status" == "ok" ]]; then backup_status="stale"; fi
      fi
    fi
  fi

  if [[ "${EXTERNAL_REPLICA_ENABLED,,}" == "true" ]]; then
    if replica_output=$(/bin/bash "$OPS_DIR/replicate-old-backups.sh" 2>&1); then
      replica_status="ok"
      replica_detail="$replica_output"
    else
      replica_status="failed"
      replica_detail="$replica_output"
    fi
  fi

  printf 'date=%s healthz=%s manifest=%s backup=%s latest_backup=%s replica=%s replica_detail=%s\n' "$today" "$health_status" "$manifest_status" "$backup_status" "$backup_detail" "$replica_status" "$replica_detail" | tee "$DAILY_STATUS_FILE" >/dev/null
  printf 'date=%s backup=%s latest_backup=%s\n' "$today" "$backup_status" "$backup_detail" | tee "$BACKUP_STATUS_FILE" >/dev/null
  printf 'date=%s replica=%s detail=%s\n' "$today" "$replica_status" "$replica_detail" | tee "$REPLICA_STATUS_FILE" >/dev/null
  log_line "daily_check date=$today healthz=$health_status manifest=$manifest_status backup=$backup_status latest_backup=$backup_detail replica=$replica_status detail=$replica_detail"
}

run_weekly_checks() {
  local week_token="$1"
  local cert_status="ok"
  local cert_detail=""
  local external_status="ok"

  if ! http_ok "$base_url/healthz" || ! http_ok "$base_url/updates/manifest?channel=stable"; then
    external_status="fail"
  fi

  if cert_output=$(/bin/bash "$OPS_DIR/certificate-check-renew.sh" 2>&1); then
    cert_detail="$cert_output"
  else
    cert_status="warning"
    cert_detail="$cert_output"
  fi

  printf 'week=%s external_access=%s cert=%s detail=%s\n' "$week_token" "$external_status" "$cert_status" "$cert_detail" | tee "$WEEKLY_STATUS_FILE" "$CERT_STATUS_FILE" >/dev/null
  log_line "weekly_check week=$week_token external_access=$external_status cert=$cert_status detail=$cert_detail"
}

current_day="$(date '+%F')"
current_week="$(date '+%G-W%V')"
last_day="$(read_first_line_clean "$DAILY_STAMP_FILE" 2>/dev/null || true)"
last_week="$(read_first_line_clean "$WEEKLY_STAMP_FILE" 2>/dev/null || true)"

if [[ "${DAILY_CHECKS_ENABLED,,}" == "true" && "$last_day" != "$current_day" ]]; then
  run_daily_checks "$current_day"
  printf '%s\n' "$current_day" > "$DAILY_STAMP_FILE"
fi

if [[ "${WEEKLY_CHECKS_ENABLED,,}" == "true" && "$last_week" != "$current_week" ]]; then
  run_weekly_checks "$current_week"
  printf '%s\n' "$current_week" > "$WEEKLY_STAMP_FILE"
fi
