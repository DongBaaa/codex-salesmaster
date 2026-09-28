#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

if [[ "${1:-}" == '--capabilities' && "$#" == 1 ]]; then
  echo 'georaeplan-release-health-deadlines-v1'
  exit 0
fi

RELEASE_ID="${1:-}"
if [[ ! "$RELEASE_ID" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
  echo 'usage: apply-release.sh <releaseId>' >&2
  exit 2
fi

ROOT="/srv/georaeplan"
OPS="$ROOT/ops"
RELEASE="$ROOT/releases/$RELEASE_ID"
LIVE="$ROOT/app/live"
BACKUP_ROOT="$ROOT/app/backups"
BACKUP="$BACKUP_ROOT/live-$(date +%Y%m%d-%H%M%S)-$$"
COMPOSE_FILE="$OPS/docker-compose.yml"
ENV_FILE="$OPS/.env"
PROJECT="georaeplan"
HEALTH_URL="http://127.0.0.1:18082/healthz"
READY_URL="http://127.0.0.1:18082/readyz"
HEALTH_TIMEOUT="${HEALTH_CHECK_TIMEOUT_SECONDS:-120}"
ROLLBACK_HEALTH_TIMEOUT="${ROLLBACK_HEALTH_TIMEOUT_SECONDS:-$HEALTH_TIMEOUT}"
ROLLBACK_ARMED=0

if [[ -n "${HEALTH_CHECK_RETRIES+x}" ]]; then
  echo 'HEALTH_CHECK_RETRIES is no longer supported; use explicit health timeout seconds' >&2
  exit 4
fi

for budget in "$HEALTH_TIMEOUT" "$ROLLBACK_HEALTH_TIMEOUT"; do
  if [[ ! "$budget" =~ ^[1-9][0-9]{0,3}$ || "$budget" -gt 3600 ]]; then
    echo 'invalid health timeout seconds; expected 1..3600' >&2
    exit 4
  fi
done

require_file() {
  [[ -f "$1" ]] || { echo "required file missing: $1" >&2; exit 10; }
}

wait_health() {
  local started=$SECONDS deadline i=0
  deadline=$((started + $1))
  while (( SECONDS < deadline )); do
    i=$((i + 1))
    if check_http_200 "$HEALTH_URL" "$deadline" &&
       check_http_200 "$READY_URL" "$deadline" && (( SECONDS < deadline )); then
      echo "health_and_readiness_ok attempt=$i elapsed_seconds=$((SECONDS - started))"
      return 0
    fi
    if (( SECONDS < deadline )); then sleep 1; fi
  done
  echo "health_or_readiness_failed timeout_seconds=$1 elapsed_seconds=$((SECONDS - started))" >&2
  return 1
}

check_http_200() {
  local status remaining request_timeout connect_timeout
  remaining=$(($2 - SECONDS))
  (( remaining > 0 )) || return 1
  request_timeout=$((remaining < 10 ? remaining : 10))
  connect_timeout=$((request_timeout < 3 ? request_timeout : 3))
  status="$(curl -fsS --noproxy '*' --connect-timeout "$connect_timeout" --max-time "$request_timeout" \
    --output /dev/null --write-out '%{http_code}' "$1" 2>/dev/null)" || return 1
  [[ "$status" == 200 ]]
}

sync_dir() {
  # Require rsync before touching live; do not delete live before a tar fallback.
  # Same-size files can share an mtime (reproducible builds or rapid rollback).
  rsync -a --checksum --delete "$1/" "$2/"
}

recreate_api() {
  if docker info >/dev/null 2>&1; then
    docker compose --env-file "$ENV_FILE" -p "$PROJECT" -f "$COMPOSE_FILE" \
      up -d --no-deps --force-recreate api
  else
    sg docker -c 'cd /srv/georaeplan/ops && docker compose --env-file /srv/georaeplan/ops/.env -p georaeplan -f /srv/georaeplan/ops/docker-compose.yml up -d --no-deps --force-recreate api'
  fi
}

handle_failure() {
  local cause="$1" original_status="$2"
  # Do not recurse or interrupt the bounded rollback with another handled signal.
  trap - ERR
  trap '' INT TERM
  if [[ "$ROLLBACK_ARMED" != 1 ]]; then
    echo "apply_failed_before_live_change cause=$cause status=$original_status" >&2
    exit "$original_status"
  fi

  echo "apply_failed cause=$cause status=$original_status backup=$BACKUP" >&2
  if ! sync_dir "$BACKUP" "$LIVE"; then
    echo "rollback_copy_failed backup=$BACKUP" >&2
    exit 31
  fi
  if ! recreate_api; then
    echo "rollback_api_recreate_failed backup=$BACKUP" >&2
    exit 31
  fi
  if ! wait_health "$ROLLBACK_HEALTH_TIMEOUT"; then
    echo "rollback_health_failed backup=$BACKUP" >&2
    exit 31
  fi
  ROLLBACK_ARMED=0
  echo "rollback_done backup=$BACKUP" >&2
  # Deployment failed even though the prior app recovered; callers must stop.
  exit 30
}

trap 'handle_failure command "$?"' ERR
trap 'handle_failure interrupt 130' INT
trap 'handle_failure terminate 143' TERM

[[ -d "$RELEASE" ]] || { echo "release directory missing: $RELEASE" >&2; exit 21; }
[[ -d "$LIVE" ]] || { echo "live directory missing: $LIVE" >&2; exit 22; }
require_file "$COMPOSE_FILE"
require_file "$ENV_FILE"
for name in '거래플랜.Server.Api.dll' appsettings.json updates/manifest/stable.json release-info.txt; do
  require_file "$RELEASE/$name"
done
command -v rsync >/dev/null
command -v flock >/dev/null

# The inode stays in place. Concurrent invocations of this version cannot interleave.
exec 9>"$OPS/.apply-release.lock"
if ! flock -n 9; then
  echo 'another_release_apply_is_running' >&2
  exit 75
fi

mkdir -p "$BACKUP_ROOT"
[[ ! -e "$BACKUP" ]] || { echo "backup path already exists: $BACKUP" >&2; exit 25; }
cp -a "$LIVE" "$BACKUP"
[[ -d "$BACKUP" ]] || { echo "backup creation failed: $BACKUP" >&2; exit 26; }
echo "backup=$BACKUP"
echo "applying_release=$RELEASE_ID"

# Arm before the first write: rsync can partially modify live before failing.
ROLLBACK_ARMED=1
sync_dir "$RELEASE" "$LIVE"
recreate_api
wait_health "$HEALTH_TIMEOUT"
ROLLBACK_ARMED=0
echo "apply_release_done release_id=$RELEASE_ID"
