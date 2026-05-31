#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

EXTERNAL_REPLICA_ENABLED="${EXTERNAL_REPLICA_ENABLED:-true}"
EXTERNAL_REPLICA_MIN_AGE_DAYS="${EXTERNAL_REPLICA_MIN_AGE_DAYS:-1}"
EXTERNAL_REPLICA_PATH="${EXTERNAL_REPLICA_PATH:-/volume1/??? ???????/??? ???? ??}"

if [[ "${EXTERNAL_REPLICA_ENABLED,,}" != "true" ]]; then
  echo "replica_skipped reason=disabled"
  exit 0
fi

if [[ -z "$EXTERNAL_REPLICA_PATH" ]]; then
  echo "replica_skipped reason=path_missing"
  exit 0
fi

mkdir -p "$EXTERNAL_REPLICA_PATH/db" "$EXTERNAL_REPLICA_PATH/files"

gather_candidates() {
  local source_root="$1"
  python3 - "$source_root" "$EXTERNAL_REPLICA_MIN_AGE_DAYS" <<'PY'
from datetime import date, timedelta
from pathlib import Path
import sys

source_root = Path(sys.argv[1])
min_age = int(sys.argv[2])
cutoff = date.today() - timedelta(days=min_age)
if source_root.exists():
    for child in sorted(source_root.iterdir()):
        if not child.is_dir():
            continue
        try:
            bucket = date.fromisoformat(child.name)
        except Exception:
            continue
        if bucket <= cutoff:
            print(child.name)
PY
}

sync_directory() {
  local src_dir="$1"
  local dst_dir="$2"
  mkdir -p "$dst_dir"
  if command -v rsync >/dev/null 2>&1; then
    rsync -a "$src_dir/" "$dst_dir/"
  else
    cp -a "$src_dir/." "$dst_dir/"
  fi
}

replicated=0
for category in db files; do
  source_root="$BACKUPS_PATH/$category"
  target_root="$EXTERNAL_REPLICA_PATH/$category"
  while IFS= read -r bucket; do
    [[ -n "$bucket" ]] || continue
    sync_directory "$source_root/$bucket" "$target_root/$bucket"
    replicated=$((replicated + 1))
  done < <(gather_candidates "$source_root")
done

printf 'replica_ok target=%s replicated_dirs=%s\n' "$EXTERNAL_REPLICA_PATH" "$replicated"
