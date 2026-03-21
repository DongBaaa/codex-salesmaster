#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

BACKUP_CREATE_ENABLED="${BACKUP_CREATE_ENABLED:-true}"
BACKUP_RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"
ITWORLD_POSTGRES_DB="${ITWORLD_POSTGRES_DB:-}"

if [[ "${BACKUP_CREATE_ENABLED,,}" != "true" ]]; then
  echo "backup_skipped reason=disabled"
  exit 0
fi

ensure_state_dir

now_stamp="$(date '+%Y%m%d-%H%M%S')"
date_bucket="$(date '+%Y-%m-%d')"
db_backup_dir="$BACKUPS_PATH/db/$date_bucket"
file_backup_dir="$BACKUPS_PATH/files/$date_bucket"
mkdir -p "$db_backup_dir" "$file_backup_dir"

latest_release=""
if [[ -f "$CURRENT_RELEASE_FILE" ]]; then
  latest_release="$(read_first_line_clean "$CURRENT_RELEASE_FILE" || true)"
fi

run_pg_dump() {
  local database_name="$1"
  local output_path="$2"
  compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres \
    pg_dump -U "$POSTGRES_USER" -d "$database_name" | gzip -c > "$output_path"
}

database_exists() {
  local database_name="$1"
  local exists
  exists="$(compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres \
    psql -U "$POSTGRES_USER" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$database_name'" | tr -d '[:space:]')"
  [[ "$exists" == "1" ]]
}

export_blob_records() {
  local database_name="$1"
  local entity_folder="$2"
  local sql_query="$3"
  local target_root="$4"
  local tmp_jsonl="$STATE_DIR/export-${database_name}-${entity_folder}-${now_stamp}.jsonl"

  compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres \
    psql -U "$POSTGRES_USER" -d "$database_name" -qtAX -c "$sql_query" > "$tmp_jsonl"

  python3 - "$tmp_jsonl" "$target_root" "$entity_folder" <<'PY'
import json
import pathlib
import re
import sys
from datetime import datetime

def sanitize_fragment(value: str, fallback: str) -> str:
    cleaned = re.sub(r'[\\/:*?\"<>|\\r\\n\\t]+', '_', (value or '').strip())
    cleaned = re.sub(r'\s+', ' ', cleaned).strip(' ._')
    return cleaned or fallback

def normalize_timestamp(value: str) -> str:
    if not value:
        return 'unknown-time'
    text = value.replace('Z', '+00:00')
    try:
        parsed = datetime.fromisoformat(text)
        return parsed.strftime('%Y%m%d-%H%M%S')
    except Exception:
        return sanitize_fragment(value, 'unknown-time')

def ensure_extension(name: str, mime_type: str) -> str:
    if '.' in pathlib.Path(name).name:
        return name
    mime = (mime_type or '').lower()
    if mime == 'application/pdf':
        return f'{name}.pdf'
    if mime.startswith('image/jpeg'):
        return f'{name}.jpg'
    if mime.startswith('image/png'):
        return f'{name}.png'
    if mime.startswith('image/webp'):
        return f'{name}.webp'
    if mime.startswith('image/gif'):
        return f'{name}.gif'
    if mime.startswith('image/heic'):
        return f'{name}.heic'
    if mime.startswith('image/heif'):
        return f'{name}.heif'
    if mime.startswith('image/tiff'):
        return f'{name}.tiff'
    if mime.startswith('image/bmp'):
        return f'{name}.bmp'
    return name

tmp_jsonl = pathlib.Path(sys.argv[1])
target_root = pathlib.Path(sys.argv[2])
entity_folder = sys.argv[3]
entity_root = target_root / entity_folder
entity_root.mkdir(parents=True, exist_ok=True)
manifest_path = entity_root / '_manifest.jsonl'
count = 0

with tmp_jsonl.open('r', encoding='utf-8') as source, manifest_path.open('w', encoding='utf-8') as manifest:
    for raw_line in source:
        raw_line = raw_line.strip()
        if not raw_line:
            continue
        payload = json.loads(raw_line)
        parent_id = sanitize_fragment(str(payload.get('parent_id') or 'unassigned'), 'unassigned')
        file_name = sanitize_fragment(str(payload.get('file_name') or payload.get('id') or 'file'), 'file')
        file_name = ensure_extension(file_name, str(payload.get('mime_type') or ''))
        stamped_name = f"{normalize_timestamp(str(payload.get('uploaded_at') or ''))}__{sanitize_fragment(str(payload.get('id') or 'file'), 'file')}__{file_name}"
        output_dir = entity_root / parent_id
        output_dir.mkdir(parents=True, exist_ok=True)
        output_path = output_dir / stamped_name
        file_hex = str(payload.get('file_hex') or '')
        output_path.write_bytes(bytes.fromhex(file_hex))
        payload.pop('file_hex', None)
        payload['relative_path'] = str(output_path.relative_to(target_root)).replace('\\', '/')
        payload['size_bytes'] = output_path.stat().st_size
        manifest.write(json.dumps(payload, ensure_ascii=False) + '\n')
        count += 1

print(count)
PY

  rm -f "$tmp_jsonl"
}

created=()
file_export_summaries=()

default_backup="$db_backup_dir/${POSTGRES_DB}-${now_stamp}.sql.gz"
run_pg_dump "$POSTGRES_DB" "$default_backup"
created+=("$default_backup")

file_backup_roots=("$POSTGRES_DB")
if [[ -n "$ITWORLD_POSTGRES_DB" && "$ITWORLD_POSTGRES_DB" != "$POSTGRES_DB" ]]; then
  if database_exists "$ITWORLD_POSTGRES_DB"; then
    itworld_backup="$db_backup_dir/${ITWORLD_POSTGRES_DB}-${now_stamp}.sql.gz"
    run_pg_dump "$ITWORLD_POSTGRES_DB" "$itworld_backup"
    created+=("$itworld_backup")
    file_backup_roots+=("$ITWORLD_POSTGRES_DB")
  else
    echo "backup_warning database_missing=$ITWORLD_POSTGRES_DB" >&2
  fi
fi

if [[ "${FILE_EXPORT_ENABLED,,}" == "true" ]]; then
  for database_name in "${file_backup_roots[@]}"; do
    database_root="$file_backup_dir/$database_name"
    mkdir -p "$database_root"

    read -r -d '' contracts_sql <<'SQL' || true
SELECT json_build_object(
  'id', "Id",
  'parent_id', "CustomerId",
  'file_name', COALESCE(NULLIF("FileName", ''), CONCAT('contract-', "Id")),
  'mime_type', COALESCE("MimeType", 'application/octet-stream'),
  'file_hash', COALESCE("FileHash", ''),
  'uploaded_at', "UploadedAtUtc",
  'file_hex', encode("FileContent", 'hex')
)::text
FROM "CustomerContracts"
WHERE NOT "IsDeleted" AND octet_length("FileContent") > 0
ORDER BY "UploadedAtUtc", "Id";
SQL
    contract_count="$(export_blob_records "$database_name" "contracts" "$contracts_sql" "$database_root")"
    file_export_summaries+=("$database_name/contracts:$contract_count")

    read -r -d '' attachments_sql <<'SQL' || true
SELECT json_build_object(
  'id', "Id",
  'parent_id', "PaymentId",
  'file_name', COALESCE(NULLIF("FileName", ''), CONCAT('payment-attachment-', "Id")),
  'mime_type', COALESCE("MimeType", 'application/octet-stream'),
  'file_hash', COALESCE("FileHash", ''),
  'uploaded_at', "UploadedAtUtc",
  'file_hex', encode("FileContent", 'hex')
)::text
FROM "PaymentAttachments"
WHERE NOT "IsDeleted" AND octet_length("FileContent") > 0
ORDER BY "UploadedAtUtc", "Id";
SQL
    attachment_count="$(export_blob_records "$database_name" "payment-attachments" "$attachments_sql" "$database_root")"
    file_export_summaries+=("$database_name/payment-attachments:$attachment_count")
  done
fi

manifest_path="$db_backup_dir/backup-manifest-${now_stamp}.txt"
{
  echo "createdAt=$(date -Iseconds)"
  echo "release=$latest_release"
  for item in "${created[@]}"; do
    if [[ -f "$item" ]]; then
      size_bytes="$(wc -c < "$item" | tr -d '[:space:]')"
      echo "dbFile=$(basename "$item") size=$size_bytes"
    fi
  done
  if [[ "${FILE_EXPORT_ENABLED,,}" == "true" ]]; then
    echo "fileBackupRoot=$file_backup_dir"
    for summary in "${file_export_summaries[@]}"; do
      echo "fileExport=$summary"
    done
  fi
} > "$manifest_path"

if [[ "$BACKUP_RETENTION_DAYS" =~ ^[0-9]+$ ]]; then
  find "$BACKUPS_PATH/db" -mindepth 1 -maxdepth 1 -type d -mtime "+$BACKUP_RETENTION_DAYS" -exec rm -rf {} + 2>/dev/null || true
  find "$BACKUPS_PATH/files" -mindepth 1 -maxdepth 1 -type d -mtime "+$BACKUP_RETENTION_DAYS" -exec rm -rf {} + 2>/dev/null || true
fi

printf 'backup_ok db_path=%s file_path=%s db_files=%s file_exports=%s\n' "$db_backup_dir" "$file_backup_dir" "${#created[@]}" "${#file_export_summaries[@]}"
