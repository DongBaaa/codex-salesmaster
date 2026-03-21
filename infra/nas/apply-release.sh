#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

if [[ $# -ne 1 ]]; then
  echo "usage: apply-release.sh <release-id>" >&2
  exit 1
fi

release_id="$(sanitize_release_id "$1")"
release_dir="$RELEASES_PATH/$release_id"

if [[ -z "$release_id" ]]; then
  echo "release id is empty after sanitization" >&2
  exit 1
fi

if [[ ! -d "$release_dir" ]]; then
  echo "release directory not found: $release_dir" >&2
  exit 1
fi

if ! compgen -G "$release_dir/*.Server.Api.dll" >/dev/null; then
  echo "release directory does not contain published api binaries: $release_dir" >&2
  exit 1
fi

ensure_state_dir
mkdir -p "$APP_LIVE_PATH"

previous_release_id=""
if [[ -f "$CURRENT_RELEASE_FILE" ]]; then
  previous_release_id="$(read_first_line_clean "$CURRENT_RELEASE_FILE" || true)"
  if [[ -n "$previous_release_id" ]]; then
    cp "$CURRENT_RELEASE_FILE" "$PREVIOUS_RELEASE_FILE"
  fi
fi

mirror_dir "$release_dir" "$APP_LIVE_PATH"
compose up -d postgres api

if ! wait_for_health 45; then
  if [[ -n "$previous_release_id" && -d "$RELEASES_PATH/$previous_release_id" ]]; then
    echo "health check failed; rolling back to $previous_release_id" >&2
    mirror_dir "$RELEASES_PATH/$previous_release_id" "$APP_LIVE_PATH"
    compose up -d postgres api
    if wait_for_health 30; then
      printf '%s\n' "$previous_release_id" > "$CURRENT_RELEASE_FILE"
      echo "rollback_release_done release=$previous_release_id"
    else
      echo "health check still failing after rollback: http://127.0.0.1:${API_HOST_PORT}/healthz" >&2
    fi
  fi

  echo "health check failed: http://127.0.0.1:${API_HOST_PORT}/healthz" >&2
  exit 1
fi

printf '%s\n' "$release_id" > "$CURRENT_RELEASE_FILE"
echo "apply_release_done release=$release_id url=http://127.0.0.1:${API_HOST_PORT}/healthz"
