#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

ENSURE_RUNNING_LOG_FILE="${ENSURE_RUNNING_LOG_FILE:-$STATE_DIR/ensure-running.log}"
STARTUP_HEALTH_RETRIES="${STARTUP_HEALTH_RETRIES:-45}"

ensure_state_dir
mkdir -p "$(dirname "$ENSURE_RUNNING_LOG_FILE")"

timestamp() {
  date '+%Y-%m-%d %H:%M:%S'
}

log_line() {
  printf '[%s] %s\n' "$(timestamp)" "$1" | tee -a "$ENSURE_RUNNING_LOG_FILE"
}

if wait_for_health 2; then
  log_line "already_healthy"
  exit 0
fi

log_line "compose_up_start app_live=$APP_LIVE_PATH compose=$COMPOSE_FILE"
compose up -d postgres
compose up -d --force-recreate api

if wait_for_health "$STARTUP_HEALTH_RETRIES"; then
  log_line "compose_up_ok"
  exit 0
fi

log_line "compose_up_failed url=http://127.0.0.1:${API_HOST_PORT}/healthz"
exit 1
