#!/usr/bin/env bash
set -euo pipefail

DEFAULT_PATH="/usr/local/bin:/usr/local/sbin:/usr/syno/bin:/usr/syno/sbin:/usr/bin:/usr/sbin:/bin:/sbin"
CURRENT_PATH="${PATH-}"
PATH="$DEFAULT_PATH"
if [[ -n "$CURRENT_PATH" ]]; then
  PATH="$PATH:$CURRENT_PATH"
fi
export PATH

OPS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GEORAEPLAN_ROOT="${GEORAEPLAN_ROOT:-$(cd "$OPS_DIR/.." && pwd)}"
ENV_FILE="${ENV_FILE:-$OPS_DIR/.env}"
COMPOSE_FILE="${COMPOSE_FILE:-$OPS_DIR/docker-compose.yml}"
COMPOSE_PROJECT_NAME_VALUE="${COMPOSE_PROJECT_NAME_VALUE:-georaeplan}"

if [[ -f "$ENV_FILE" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "$ENV_FILE"
  set +a
fi

APP_LIVE_PATH="${APP_LIVE_PATH:-$GEORAEPLAN_ROOT/app/live}"
RELEASES_PATH="${RELEASES_PATH:-$GEORAEPLAN_ROOT/releases}"
BACKUPS_PATH="${BACKUPS_PATH:-$GEORAEPLAN_ROOT/backups}"
POSTGRES_DATA_PATH="${POSTGRES_DATA_PATH:-$GEORAEPLAN_ROOT/data/postgres}"
STATE_DIR="${STATE_DIR:-$OPS_DIR/state}"
CURRENT_RELEASE_FILE="${CURRENT_RELEASE_FILE:-$STATE_DIR/current-release.txt}"
PREVIOUS_RELEASE_FILE="${PREVIOUS_RELEASE_FILE:-$STATE_DIR/previous-release.txt}"
API_HOST_PORT="${API_HOST_PORT:-18082}"
HEALTH_CHECK_RETRIES="${HEALTH_CHECK_RETRIES:-150}"
POSTGRES_DB="${POSTGRES_DB:-georaeplan}"
POSTGRES_USER="${POSTGRES_USER:-georaeplan}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-}"
FILE_EXPORT_ENABLED="${FILE_EXPORT_ENABLED:-true}"
EXTERNAL_REPLICA_ENABLED="${EXTERNAL_REPLICA_ENABLED:-true}"
EXTERNAL_REPLICA_PATH="${EXTERNAL_REPLICA_PATH:-/volume1/???????/???? ??}"
EXTERNAL_REPLICA_MIN_AGE_DAYS="${EXTERNAL_REPLICA_MIN_AGE_DAYS:-1}"
FILE_STORAGE_SOURCE_PATH="${FILE_STORAGE_SOURCE_PATH:-$GEORAEPLAN_ROOT/storage/files}"

ensure_state_dir() {
  mkdir -p "$STATE_DIR"
}

read_first_line_clean() {
  local file_path="$1"
  [[ -f "$file_path" ]] || return 1
  LC_ALL=C sed $'1s/^\xEF\xBB\xBF//' "$file_path" | head -n 1 | tr -d -- '\r' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//'
}

sanitize_release_id() {
  printf '%s' "$1" | LC_ALL=C tr -cd -- '0-9A-Za-z._-'
}

compose() {
  local args=()
  if [[ -f "$ENV_FILE" ]]; then
    args+=(--env-file "$ENV_FILE")
  fi
  args+=(-p "$COMPOSE_PROJECT_NAME_VALUE" -f "$COMPOSE_FILE")

  if docker compose version >/dev/null 2>&1; then
    COMPOSE_PROJECT_NAME="$COMPOSE_PROJECT_NAME_VALUE" docker compose "${args[@]}" "$@"
    return
  fi

  if command -v docker-compose >/dev/null 2>&1; then
    COMPOSE_PROJECT_NAME="$COMPOSE_PROJECT_NAME_VALUE" docker-compose "${args[@]}" "$@"
    return
  fi

  echo "docker compose command not found." >&2
  exit 1
}

mirror_dir() {
  local src="$1"
  local dst="$2"

  mkdir -p "$dst"
  find "$dst" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
  cp -a "$src"/. "$dst"/
}

wait_for_health() {
  local retries="${1:-$HEALTH_CHECK_RETRIES}"
  local url="http://127.0.0.1:${API_HOST_PORT}/healthz"

  for ((i = 1; i <= retries; i++)); do
    if command -v curl >/dev/null 2>&1 && curl -fsS "$url" >/dev/null 2>&1; then
      return 0
    fi

    if command -v wget >/dev/null 2>&1 && wget -qO- "$url" >/dev/null 2>&1; then
      return 0
    fi

    sleep 2
  done

  return 1
}
