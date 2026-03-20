#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

if [[ $# -ne 1 ]]; then
  echo "usage: apply-release.sh <release-id>" >&2
  exit 1
fi

release_id="$1"
release_dir="$RELEASES_PATH/$release_id"

if [[ ! -d "$release_dir" ]]; then
  echo "릴리스 디렉터리를 찾을 수 없습니다: $release_dir" >&2
  exit 1
fi

if ! compgen -G "$release_dir/*.Server.Api.dll" >/dev/null; then
  echo "유효한 publish 결과물이 아닙니다: $release_dir" >&2
  exit 1
fi

ensure_state_dir
mkdir -p "$APP_LIVE_PATH"

if [[ -f "$CURRENT_RELEASE_FILE" ]]; then
  cp "$CURRENT_RELEASE_FILE" "$PREVIOUS_RELEASE_FILE"
fi

mirror_dir "$release_dir" "$APP_LIVE_PATH"
echo "$release_id" > "$CURRENT_RELEASE_FILE"

compose up -d postgres api

if ! wait_for_health 45; then
  echo "헬스체크 실패: http://127.0.0.1:${API_HOST_PORT}/healthz" >&2
  exit 1
fi

echo "apply_release_done release=$release_id url=http://127.0.0.1:${API_HOST_PORT}/healthz"
