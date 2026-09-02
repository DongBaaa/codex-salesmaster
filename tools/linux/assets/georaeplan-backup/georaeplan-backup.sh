#!/usr/bin/env bash
set -Eeuo pipefail

umask 077

GEORAEPLAN_ROOT="${GEORAEPLAN_ROOT:-/srv/georaeplan}"
OPS_ROOT="${GEORAEPLAN_OPS_ROOT:-$GEORAEPLAN_ROOT/ops}"
BACKUP_ROOT="${GEORAEPLAN_BACKUP_ROOT:-$GEORAEPLAN_ROOT/backups/automatic}"
STATE_ROOT="${GEORAEPLAN_BACKUP_STATE_ROOT:-$OPS_ROOT/state}"
FILES_ROOT="${GEORAEPLAN_FILES_ROOT:-$GEORAEPLAN_ROOT/storage/files}"
KEYRING_ROOT="${GEORAEPLAN_KEYRING_ROOT:-$GEORAEPLAN_ROOT/storage/data-protection-keys}"
RETENTION_DAYS="${GEORAEPLAN_BACKUP_RETENTION_DAYS:-14}"
MIN_FREE_BYTES="${GEORAEPLAN_BACKUP_MIN_FREE_BYTES:-2147483648}"
MIN_FREE_INODES="${GEORAEPLAN_BACKUP_MIN_FREE_INODES:-1024}"
DELETE_LOCK_TIMEOUT_SECONDS="${GEORAEPLAN_BACKUP_DELETE_LOCK_TIMEOUT_SECONDS:-60}"

SETS_ROOT="$BACKUP_ROOT/sets"
STAGING_ROOT="$BACKUP_ROOT/.staging"
LOG_ROOT="$BACKUP_ROOT/logs"
LOCK_FILE="$BACKUP_ROOT/georaeplan-backup.lock"
FILE_DELETION_LOCK="$FILES_ROOT/.georaeplan-backup-delete.lock"
SUCCESS_STATUS="$STATE_ROOT/backup-status.txt"
FAILURE_STATUS="$STATE_ROOT/backup-failure-status.txt"

require_absolute_path() {
  local value="$1"
  local label="$2"
  if [[ "$value" != /* ||
        "$value" == *$'\n'* ||
        "$value" == *$'\r'* ||
        "$value" =~ (^|/)(\.|\.\.)(/|$) ]]; then
    echo "backup_configuration_invalid field=$label" >&2
    exit 2
  fi
}

for configured_path in \
  "$GEORAEPLAN_ROOT" \
  "$OPS_ROOT" \
  "$BACKUP_ROOT" \
  "$STATE_ROOT" \
  "$FILES_ROOT" \
  "$KEYRING_ROOT"; do
  require_absolute_path "$configured_path" "path"
done

GEORAEPLAN_ROOT="$(realpath -m -- "$GEORAEPLAN_ROOT")"
OPS_ROOT="$(realpath -m -- "$OPS_ROOT")"
BACKUP_ROOT="$(realpath -m -- "$BACKUP_ROOT")"
STATE_ROOT="$(realpath -m -- "$STATE_ROOT")"
FILES_ROOT="$(realpath -m -- "$FILES_ROOT")"
KEYRING_ROOT="$(realpath -m -- "$KEYRING_ROOT")"

if [[ ! "$RETENTION_DAYS" =~ ^[0-9]+$ ]] || (( RETENTION_DAYS < 1 )); then
  echo "backup_configuration_invalid field=retention_days" >&2
  exit 2
fi

if [[ ! "$MIN_FREE_BYTES" =~ ^[0-9]+$ ]] ||
   (( MIN_FREE_BYTES < 1073741824 )); then
  echo "backup_configuration_invalid field=min_free_bytes" >&2
  exit 2
fi

if [[ ! "$MIN_FREE_INODES" =~ ^[0-9]+$ ]] ||
   (( MIN_FREE_INODES < 128 )); then
  echo "backup_configuration_invalid field=min_free_inodes" >&2
  exit 2
fi

if [[ ! "$DELETE_LOCK_TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] ||
   (( DELETE_LOCK_TIMEOUT_SECONDS < 5 || DELETE_LOCK_TIMEOUT_SECONDS > 300 )); then
  echo "backup_configuration_invalid field=delete_lock_timeout_seconds" >&2
  exit 2
fi

if [[ "$GEORAEPLAN_ROOT" == "/" ]] ||
   [[ "$BACKUP_ROOT" != "$GEORAEPLAN_ROOT/"* ]] ||
   [[ "$STATE_ROOT" != "$GEORAEPLAN_ROOT/"* ]]; then
  echo "backup_configuration_invalid field=managed_root_boundary" >&2
  exit 2
fi

paths_overlap() {
  local first="$1"
  local second="$2"
  [[ "$first" == "$second" ||
     "$first" == "$second/"* ||
     "$second" == "$first/"* ]]
}

if paths_overlap "$BACKUP_ROOT" "$FILES_ROOT" ||
   paths_overlap "$BACKUP_ROOT" "$KEYRING_ROOT"; then
  echo "backup_configuration_invalid field=source_output_overlap" >&2
  exit 2
fi

COMPOSE_FILE="$OPS_ROOT/docker-compose.yml"
ENV_FILE="$OPS_ROOT/.env"
mkdir -p -- "$SETS_ROOT" "$STAGING_ROOT" "$LOG_ROOT" "$STATE_ROOT"
chmod 0700 "$BACKUP_ROOT" "$SETS_ROOT" "$STAGING_ROOT" "$LOG_ROOT"
chmod 0755 "$STATE_ROOT"

run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
log_file="$LOG_ROOT/backup_${run_id}.log"
staging_dir="$STAGING_ROOT/backup_${run_id}.staging"
final_dir="$SETS_ROOT/backup_${run_id}.complete"
completed=false

exec > >(tee -a "$log_file") 2>&1

write_status_atomically() {
  local target="$1"
  shift
  local temporary="${target}.tmp.$$"
  printf '%s\n' "$@" > "$temporary"
  chmod 0644 "$temporary"
  mv -f -- "$temporary" "$target"
}

cleanup_and_record_failure() {
  local exit_code="$?"
  trap - EXIT

  if [[ "$completed" != true && -d "$staging_dir" ]]; then
    case "$staging_dir" in
      "$STAGING_ROOT"/backup_*.staging)
        rm -rf --one-file-system -- "$staging_dir"
        ;;
    esac
  fi

  if (( exit_code != 0 )); then
    write_status_atomically \
      "$FAILURE_STATUS" \
      "backup=failed" \
      "replica=disabled" \
      "run_id=$run_id" \
      "failed_at=$(date -Iseconds)" \
      "exit_code=$exit_code" \
      "log_file=$log_file"
    echo "backup_failed run_id=$run_id exit_code=$exit_code log=$log_file" >&2
  fi

  exit "$exit_code"
}
trap cleanup_and_record_failure EXIT

exec 9> "$LOCK_FILE"
if ! flock -n 9; then
  echo "backup_skipped reason=lock_busy"
  trap - EXIT
  exit 0
fi

[[ -f "$COMPOSE_FILE" ]] || {
  echo "backup_prerequisite_missing file=docker-compose.yml" >&2
  exit 2
}
[[ -f "$ENV_FILE" ]] || {
  echo "backup_prerequisite_missing file=.env" >&2
  exit 2
}
[[ -d "$FILES_ROOT" ]] || {
  echo "backup_prerequisite_missing path=files" >&2
  exit 2
}
[[ -d "$KEYRING_ROOT" ]] || {
  echo "backup_prerequisite_missing path=keyring" >&2
  exit 2
}

compose=(
  docker compose
  --env-file "$ENV_FILE"
  -f "$COMPOSE_FILE"
  --project-directory "$OPS_ROOT"
)

configured_services="$("${compose[@]}" config --services)"
if ! grep -qx 'postgres' <<< "$configured_services" ||
   ! grep -qx 'api' <<< "$configured_services"; then
  echo "backup_prerequisite_failed reason=required_compose_service_missing" >&2
  exit 3
fi

running_postgres="$("${compose[@]}" ps --status running --services postgres)"
if ! grep -qx 'postgres' <<< "$running_postgres"; then
  echo "backup_prerequisite_failed service=postgres state=not_running" >&2
  exit 3
fi

if [[ ! -f "$FILE_DELETION_LOCK" || -L "$FILE_DELETION_LOCK" ]]; then
  echo "backup_prerequisite_failed reason=file_deletion_lock_missing_or_unsafe" >&2
  exit 3
fi
running_api="$("${compose[@]}" ps --status running --services api)"
if ! grep -qx 'api' <<< "$running_api"; then
  echo "backup_prerequisite_failed service=api state=not_running" >&2
  exit 3
fi
host_lock_identity="$(stat -Lc '%d:%i' "$FILE_DELETION_LOCK")"
api_lock_identity="$("${compose[@]}" exec -T api \
  stat -Lc '%d:%i' "/storage/files/$(basename "$FILE_DELETION_LOCK")")"
if [[ -z "$host_lock_identity" ||
      "$host_lock_identity" != "$api_lock_identity" ]]; then
  echo "backup_prerequisite_failed reason=file_deletion_lock_bind_identity_mismatch" >&2
  exit 3
fi
echo "backup_file_deletion_lock_bind_identity=ok"

database_user="$("${compose[@]}" exec -T postgres sh -ceu 'printf "%s" "${POSTGRES_USER:-georaeplan}"')"
central_database="$("${compose[@]}" exec -T postgres sh -ceu 'printf "%s" "${POSTGRES_DB:-georaeplan}"')"
business_database="$(
  "${compose[@]}" config --environment |
    awk -F= '
      $1 == "ITWORLD_POSTGRES_DB" {
        sub(/^[^=]*=/, "")
        print
        exit
      }'
)"
business_database="${business_database%$'\r'}"
business_database="${business_database:-georaeplan_itworld}"
api_database_identities="$("${compose[@]}" exec -T api sh -ceu '
extract_database_name() {
  connection_string="$1"
  previous_ifs="$IFS"
  IFS=";"
  set -f
  for segment in $connection_string; do
    key="${segment%%=*}"
    value="${segment#*=}"
    case "$key" in
      Database|database|DATABASE)
        case "$value" in
          ""|*[!A-Za-z0-9_.-]*) exit 12 ;;
        esac
        printf "%s\n" "$value"
        IFS="$previous_ifs"
        set +f
        return 0
        ;;
    esac
  done
  IFS="$previous_ifs"
  set +f
  return 11
}
printf "Default="
extract_database_name "${ConnectionStrings__Default:-}"
printf "ITWORLD="
extract_database_name "${ConnectionStrings__ITWORLD:-}"
if [ -n "${ConnectionStrings__USENET_GROUP:-}" ]; then
  printf "USENET_GROUP="
  extract_database_name "${ConnectionStrings__USENET_GROUP}"
fi
')"
mapfile -t api_database_lines <<< "$api_database_identities"
if (( ${#api_database_lines[@]} < 2 || ${#api_database_lines[@]} > 3 )); then
  echo "backup_prerequisite_failed reason=api_database_identity_unavailable" >&2
  exit 3
fi
if [[ "${api_database_lines[0]}" != Default=* ||
      "${api_database_lines[1]}" != ITWORLD=* ||
      ( ${#api_database_lines[@]} -eq 3 && "${api_database_lines[2]}" != USENET_GROUP=* ) ]]; then
  echo "backup_prerequisite_failed reason=api_database_identity_contract_invalid" >&2
  exit 3
fi
api_central_database="${api_database_lines[0]#Default=}"
api_central_database="${api_central_database%$'\r'}"
api_business_database="${api_database_lines[1]#ITWORLD=}"
api_business_database="${api_business_database%$'\r'}"
if [[ ! "$database_user" =~ ^[A-Za-z_][A-Za-z0-9_.-]*$ ]] ||
   [[ "$central_database" != georaeplan ]] ||
   [[ "$business_database" != georaeplan_itworld ]] ||
   [[ "$central_database" == "$business_database" ]]; then
  echo "backup_prerequisite_failed reason=invalid_database_identity" >&2
  exit 3
fi
if [[ "$api_central_database" != "$central_database" ]] ||
   [[ "$api_business_database" != "$business_database" ]]; then
  echo "backup_prerequisite_failed reason=api_database_identity_drift" >&2
  exit 3
fi

database_inventory="$(
  "${compose[@]}" exec -T postgres \
    psql --no-password -X -q -v ON_ERROR_STOP=1 \
    -U "$database_user" -d "$central_database" -At \
    -c "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate AND (datname = '$central_database' OR datname ~ '^georaeplan_(itworld|usenet|org_[a-z0-9_]+)$') ORDER BY datname;"
)"
database_inventory="${database_inventory//$'\r'/}"
mapfile -t databases <<< "$database_inventory"
if (( ${#databases[@]} < 2 || ${#databases[@]} > 256 )); then
  echo "backup_prerequisite_failed reason=database_inventory_count_invalid" >&2
  exit 3
fi
declare -A discovered_database=()
for database_name in "${databases[@]}"; do
  if [[ "$database_name" != georaeplan &&
        "$database_name" != georaeplan_itworld &&
        "$database_name" != georaeplan_usenet &&
        ! "$database_name" =~ ^georaeplan_org_[a-z0-9_]+$ ]] ||
     [[ -n "${discovered_database[$database_name]:-}" ]]; then
    echo "backup_prerequisite_failed reason=database_inventory_identity_invalid" >&2
    exit 3
  fi
  discovered_database[$database_name]=1
done
for required_database in "$central_database" "$business_database"; do
  if [[ -z "${discovered_database[$required_database]:-}" ]]; then
    echo "backup_prerequisite_failed reason=required_database_missing database=$required_database" >&2
    exit 3
  fi
done
for api_database_line in "${api_database_lines[@]}"; do
  api_database_name="${api_database_line#*=}"
  api_database_name="${api_database_name%$'\r'}"
  if [[ ! "$api_database_name" =~ ^georaeplan(_(itworld|usenet|org_[a-z0-9_]+))?$ ]] ||
     [[ -z "${discovered_database[$api_database_name]:-}" ]]; then
    echo "backup_prerequisite_failed reason=api_database_not_discovered database=$api_database_name" >&2
    exit 3
  fi
done
database_count="${#databases[@]}"
database_list_sha256="$(printf '%s\n' "${databases[@]}" | sha256sum | awk '{print $1}')"
echo "backup_api_database_identity=ok"
echo "backup_database_inventory=ok database_count=$database_count database_list_sha256=$database_list_sha256"

api_port_binding="$("${compose[@]}" port api 8080 | head -n 1 | tr -d '\r')"
case "$api_port_binding" in
  127.0.0.1:*) ;;
  *)
    echo "backup_prerequisite_failed reason=api_health_endpoint_not_loopback" >&2
    exit 3
    ;;
esac
api_host_port="${api_port_binding#127.0.0.1:}"
if [[ ! "$api_host_port" =~ ^[0-9]+$ ]] ||
   (( api_host_port < 1 || api_host_port > 65535 )); then
  echo "backup_prerequisite_failed reason=api_health_port_invalid" >&2
  exit 3
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "backup_prerequisite_failed reason=curl_unavailable" >&2
  exit 3
fi
read_api_process_start_ticks() {
  local start_ticks
  start_ticks="$(
    "${compose[@]}" exec -T api \
      sh -ceu 'awk "{ print \$22 }" /proc/1/stat'
  )"
  start_ticks="${start_ticks%$'\r'}"
  if [[ ! "$start_ticks" =~ ^[0-9]+$ ]]; then
    echo "backup_prerequisite_failed reason=api_process_identity_unavailable" >&2
    return 1
  fi
  printf '%s\n' "$start_ticks"
}

verify_api_ready_protocol() {
  local ready_payload
  if ! ready_payload="$(
    curl --fail --silent --show-error --max-time 10 \
      "http://127.0.0.1:${api_host_port}/readyz" \
      2>/dev/null
  )"; then
    echo "backup_prerequisite_failed reason=api_not_ready" >&2
    return 1
  fi
  if ! grep -Eq \
    '"status"[[:space:]]*:[[:space:]]*"ready"' \
    <<< "$ready_payload"; then
    echo "backup_prerequisite_failed reason=api_ready_contract_mismatch" >&2
    return 1
  fi
  if ! grep -Eq \
    '"fileDeletionLeaseProtocol"[[:space:]]*:[[:space:]]*"shared-flock-v1"' \
    <<< "$ready_payload"; then
    echo "backup_prerequisite_failed reason=api_file_deletion_lease_protocol_mismatch" >&2
    return 1
  fi
}

if ! initial_api_process_start_ticks="$(read_api_process_start_ticks)"; then
  exit 3
fi
if ! verify_api_ready_protocol; then
  exit 3
fi
echo "backup_api_ready=ok"
echo "backup_api_file_deletion_lease_protocol=shared-flock-v1"

read_database_size() {
  local database_name="$1"
  "${compose[@]}" exec -T postgres \
    psql --no-password -U "$database_user" -d "$central_database" -At \
    -c "SELECT pg_database_size('$database_name');" |
    tr -d '[:space:]'
}

read_database_snapshot() {
  local snapshot
  if ! snapshot="$(
    "${compose[@]}" exec -T postgres \
      psql --no-password -X -q -v ON_ERROR_STOP=1 \
      -U "$database_user" -d "$central_database" -At \
      -c 'SELECT pg_current_snapshot()::text;' |
      tr -d '\r\n'
  )"; then
    return 1
  fi

  if [[ ! "$snapshot" =~ ^[0-9]+:[0-9]+:([0-9]+(,[0-9]+)*)?$ ]]; then
    return 1
  fi

  printf '%s\n' "$snapshot"
}

read_database_business_count_digest() {
  local database_name="$1"
  local output
  local -a lines
  local -a expected_keys=(
    users customers items transactions rental_assets invoices payments)
  local index

  if ! output="$(
    "${compose[@]}" exec -T postgres \
      psql --no-password -X -q -v ON_ERROR_STOP=1 \
      -U "$database_user" -d "$database_name" -At \
      -c $'SELECT \'users=\' || count(*) FROM "Users";\nSELECT \'customers=\' || count(*) FROM "Customers";\nSELECT \'items=\' || count(*) FROM "Items";\nSELECT \'transactions=\' || count(*) FROM "Transactions";\nSELECT \'rental_assets=\' || count(*) FROM "RentalAssets";\nSELECT \'invoices=\' || count(*) FROM "Invoices";\nSELECT \'payments=\' || count(*) FROM "Payments";'
  )"; then
    return 1
  fi

  output="${output//$'\r'/}"
  mapfile -t lines <<< "$output"
  [[ "${#lines[@]}" -eq "${#expected_keys[@]}" ]] || return 1
  for index in "${!expected_keys[@]}"; do
    [[ "${lines[$index]}" =~ ^${expected_keys[$index]}=[0-9]+$ ]] || return 1
  done

  printf '%s' "$output" | sha256sum | awk '{print $1}'
}

database_bytes_total=0
for database_name in "${databases[@]}"; do
  database_bytes="$(read_database_size "$database_name")"
  if [[ ! "$database_bytes" =~ ^[0-9]+$ ]]; then
    echo "backup_prerequisite_failed reason=capacity_measurement_invalid database=$database_name" >&2
    exit 3
  fi
  database_bytes_total=$((database_bytes_total + database_bytes))
done
files_bytes="$(du -sb -- "$FILES_ROOT" | awk '{print $1}')"
keyring_bytes="$(du -sb -- "$KEYRING_ROOT" | awk '{print $1}')"
available_bytes="$(df -PB1 -- "$BACKUP_ROOT" | awk 'NR == 2 {print $4}')"
available_inodes="$(df -Pi -- "$BACKUP_ROOT" | awk 'NR == 2 {print $4}')"

for measured_value in \
  "$database_bytes_total" \
  "$files_bytes" \
  "$keyring_bytes" \
  "$available_bytes" \
  "$available_inodes"; do
  if [[ ! "$measured_value" =~ ^[0-9]+$ ]]; then
    echo "backup_prerequisite_failed reason=capacity_measurement_invalid" >&2
    exit 3
  fi
done

estimated_source_bytes=$((
  database_bytes_total +
  files_bytes +
  keyring_bytes
))
estimated_backup_bytes=$((
  estimated_source_bytes +
  estimated_source_bytes / 10 +
  10485760
))
required_available_bytes=$((estimated_backup_bytes + MIN_FREE_BYTES))

if (( available_bytes < required_available_bytes )); then
  echo \
    "backup_prerequisite_failed reason=insufficient_capacity available_bytes=$available_bytes required_bytes=$required_available_bytes" \
    >&2
  exit 4
fi
if (( available_inodes < MIN_FREE_INODES )); then
  echo \
    "backup_prerequisite_failed reason=insufficient_inodes available_inodes=$available_inodes required_inodes=$MIN_FREE_INODES" \
    >&2
  exit 4
fi

echo \
  "backup_capacity_ok available_bytes=$available_bytes required_bytes=$required_available_bytes available_inodes=$available_inodes"

exec 8< "$FILE_DELETION_LOCK"
if ! flock -w "$DELETE_LOCK_TIMEOUT_SECONDS" 8; then
  echo \
    "backup_prerequisite_failed reason=file_deletion_lock_timeout timeout_seconds=$DELETE_LOCK_TIMEOUT_SECONDS" \
    >&2
  exit 5
fi
echo "backup_file_deletion_lease=exclusive"
if ! locked_api_process_start_ticks="$(read_api_process_start_ticks)"; then
  exit 5
fi
if [[ "$locked_api_process_start_ticks" != "$initial_api_process_start_ticks" ]]; then
  echo "backup_prerequisite_failed reason=api_process_changed_before_capture" >&2
  exit 5
fi
if ! verify_api_ready_protocol; then
  exit 5
fi
echo "backup_api_runtime_stable=before_capture"

mkdir -- "$staging_dir"
files_archive="$staging_dir/files.tar.gz"
keyring_archive="$staging_dir/data-protection-keys.tar.gz"
database_manifest_file="$staging_dir/databases.txt"
metadata_file="$staging_dir/metadata.txt"
manifest_file="$staging_dir/SHA256SUMS"
complete_marker="$staging_dir/COMPLETE"
declare -a database_dump_names=()
declare -a database_digest_records=()

if ! database_snapshot_before="$(read_database_snapshot)"; then
  echo \
    "backup_prerequisite_failed reason=database_snapshot_unavailable phase=before" \
    >&2
  exit 5
fi
database_snapshot_before_sha256="$(
  printf '%s' "$database_snapshot_before" |
    sha256sum |
    awk '{print $1}'
)"
echo \
  "backup_database_snapshot phase=before sha256=$database_snapshot_before_sha256"

central_business_count_sha256=""
business_business_count_sha256=""
: > "$database_manifest_file"
for database_name in "${databases[@]}"; do
  dump_name="${database_name}.dump"
  database_dump="$staging_dir/$dump_name"
  if ! business_count_sha256_before="$(read_database_business_count_digest "$database_name")"; then
    echo "backup_prerequisite_failed reason=business_count_digest_unavailable database=$database_name phase=before" >&2
    exit 5
  fi
  echo "backup_database_start database=$database_name"
  "${compose[@]}" exec -T postgres \
    pg_dump --no-password -U "$database_user" -d "$database_name" -Fc \
    > "$database_dump"
  [[ -s "$database_dump" ]]
  "${compose[@]}" exec -T postgres pg_restore -l < "$database_dump" > /dev/null
  if ! business_count_sha256_after="$(read_database_business_count_digest "$database_name")"; then
    echo "backup_prerequisite_failed reason=business_count_digest_unavailable database=$database_name phase=after" >&2
    exit 5
  fi
  if [[ "$business_count_sha256_before" != "$business_count_sha256_after" ]]; then
    echo "backup_prerequisite_failed reason=business_count_digest_drift database=$database_name before_sha256=$business_count_sha256_before after_sha256=$business_count_sha256_after" >&2
    exit 5
  fi
  business_count_sha256="$business_count_sha256_before"
  printf '%s\t%s\t%s\n' "$database_name" "$dump_name" "$business_count_sha256" >> "$database_manifest_file"
  database_dump_names+=("$dump_name")
  database_digest_records+=("$database_name=$business_count_sha256")
  if [[ "$database_name" == "$central_database" ]]; then
    central_business_count_sha256="$business_count_sha256"
  elif [[ "$database_name" == "$business_database" ]]; then
    business_business_count_sha256="$business_count_sha256"
  fi
  echo "backup_business_count_digest_consistency=ok database=$database_name sha256=$business_count_sha256"
done
[[ -n "$central_business_count_sha256" && -n "$business_business_count_sha256" ]]

if ! database_snapshot_after="$(read_database_snapshot)"; then
  echo \
    "backup_prerequisite_failed reason=database_snapshot_unavailable phase=after" \
    >&2
  exit 5
fi
database_snapshot_after_sha256="$(
  printf '%s' "$database_snapshot_after" |
    sha256sum |
    awk '{print $1}'
)"
if [[ "$database_snapshot_before" != "$database_snapshot_after" ]]; then
  echo \
    "backup_prerequisite_failed reason=database_snapshot_drift before_sha256=$database_snapshot_before_sha256 after_sha256=$database_snapshot_after_sha256" \
    >&2
  exit 5
fi
database_snapshot_sha256="$database_snapshot_before_sha256"
database_manifest_sha256="$(sha256sum "$database_manifest_file" | awk '{print $1}')"
database_digest_set_sha256="$(printf '%s\n' "${database_digest_records[@]}" | sha256sum | awk '{print $1}')"
echo \
  "backup_database_snapshot_consistency=ok snapshot_sha256=$database_snapshot_sha256"

tar \
  --exclude="./$(basename "$FILE_DELETION_LOCK")" \
  --exclude='*/.*.tmp' \
  -czf "$files_archive" \
  -C "$FILES_ROOT" \
  .
tar -tzf "$files_archive" > /dev/null
tar -czf "$keyring_archive" -C "$KEYRING_ROOT" .
tar -tzf "$keyring_archive" > /dev/null
if ! final_api_process_start_ticks="$(read_api_process_start_ticks)"; then
  exit 5
fi
if [[ "$final_api_process_start_ticks" != "$initial_api_process_start_ticks" ]]; then
  echo "backup_prerequisite_failed reason=api_process_changed_during_capture" >&2
  exit 5
fi
if ! verify_api_ready_protocol; then
  exit 5
fi
echo "backup_api_runtime_stable=after_capture"
flock -u 8
echo "backup_file_deletion_lease=released"

cat > "$metadata_file" <<EOF
backup=georaeplan
run_id=$run_id
created_at=$(date -Iseconds)
central_database=$central_database
business_database=$business_database
database_manifest=$(basename "$database_manifest_file")
database_count=$database_count
database_list_sha256=$database_list_sha256
database_manifest_sha256=$database_manifest_sha256
database_digest_set_sha256=$database_digest_set_sha256
files_archive=$(basename "$files_archive")
keyring_archive=$(basename "$keyring_archive")
estimated_source_bytes=$estimated_source_bytes
required_available_bytes=$required_available_bytes
file_deletion_lease=exclusive_during_database_and_file_capture
database_snapshot_consistency=unchanged_across_all_dumps
database_snapshot_sha256=$database_snapshot_sha256
central_business_count_sha256=$central_business_count_sha256
business_business_count_sha256=$business_business_count_sha256
replica=disabled
EOF

(
  cd "$staging_dir"
  sha256sum \
    "${database_dump_names[@]}" \
    "$(basename "$database_manifest_file")" \
    "$(basename "$files_archive")" \
    "$(basename "$keyring_archive")" \
    "$(basename "$metadata_file")" \
    > "$(basename "$manifest_file")"
  sha256sum -c "$(basename "$manifest_file")" > /dev/null
)

cat > "$complete_marker" <<EOF
backup=complete
run_id=$run_id
verified_at=$(date -Iseconds)
manifest_sha256=$(sha256sum "$manifest_file" | awk '{print $1}')
EOF

[[ ! -e "$final_dir" ]]
mv -T -- "$staging_dir" "$final_dir"
completed=true

manifest_sha256="$(sha256sum "$final_dir/SHA256SUMS" | awk '{print $1}')"
write_status_atomically \
  "$SUCCESS_STATUS" \
  "backup=ok" \
  "replica=disabled" \
  "run_id=$run_id" \
  "completed_at=$(date -Iseconds)" \
  "set_path=$final_dir" \
  "manifest_sha256=$manifest_sha256" \
  "retention_days=$RETENTION_DAYS" \
  "estimated_source_bytes=$estimated_source_bytes" \
  "required_available_bytes=$required_available_bytes" \
  "file_deletion_lease=exclusive_during_database_and_file_capture" \
  "database_snapshot_consistency=unchanged_across_all_dumps" \
  "database_snapshot_sha256=$database_snapshot_sha256" \
  "database_count=$database_count" \
  "database_list_sha256=$database_list_sha256" \
  "database_manifest_sha256=$database_manifest_sha256" \
  "database_digest_set_sha256=$database_digest_set_sha256"

while IFS= read -r -d '' expired_set; do
  [[ "$expired_set" != "$final_dir" ]] || continue
  case "$expired_set" in
    "$SETS_ROOT"/backup_*.complete)
      echo "backup_retention_remove set=$expired_set"
      rm -rf --one-file-system -- "$expired_set"
      ;;
  esac
done < <(
  find "$SETS_ROOT" \
    -mindepth 1 \
    -maxdepth 1 \
    -type d \
    -name 'backup_*.complete' \
    -mtime "+$RETENTION_DAYS" \
    -print0
)

echo "backup_completed run_id=$run_id set=$final_dir manifest_sha256=$manifest_sha256"
