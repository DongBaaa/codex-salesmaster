#!/usr/bin/env bash
set -Eeuo pipefail

umask 077

STATE_ROOT="${GEORAEPLAN_BACKUP_STATE_ROOT:-/srv/georaeplan/ops/state}"
REPLICA_ROOT="${GEORAEPLAN_REPLICA_ROOT:-/mnt/georaeplan-backup-replica}"
REPLICA_ID="${GEORAEPLAN_REPLICA_ID:-}"
IMAGE_ID="${GEORAEPLAN_RESTORE_DRILL_IMAGE_ID:-}"
LOCK_TIMEOUT_SECONDS="${GEORAEPLAN_RESTORE_DRILL_LOCK_TIMEOUT_SECONDS:-120}"
STEP_TIMEOUT_SECONDS="${GEORAEPLAN_RESTORE_DRILL_STEP_TIMEOUT_SECONDS:-300}"
RESTORE_WORK_RESERVE_BYTES=1073741824
ALLOW_LOCAL_TEST=false

if [[ "${1:-}" == "--test-allow-local-filesystem" && "$#" -eq 1 ]]; then
  ALLOW_LOCAL_TEST=true
elif (( $# != 0 )); then
  echo "restore_drill_configuration_invalid field=arguments" >&2
  exit 2
fi

DOCKER_BIN=/usr/bin/docker
TIMEOUT_BIN=/usr/bin/timeout
if [[ "$ALLOW_LOCAL_TEST" == true ]]; then
  DOCKER_BIN="${GEORAEPLAN_RESTORE_DRILL_DOCKER_BIN:-$DOCKER_BIN}"
  TIMEOUT_BIN="${GEORAEPLAN_RESTORE_DRILL_TIMEOUT_BIN:-$TIMEOUT_BIN}"
  RESTORE_WORK_RESERVE_BYTES="${GEORAEPLAN_RESTORE_DRILL_WORK_RESERVE_BYTES:-$RESTORE_WORK_RESERVE_BYTES}"
fi

require_absolute_path() {
  local value="$1"
  local label="$2"
  if [[ "$value" != /* || "$value" == "/" ||
        "$value" == *$'\n'* || "$value" == *$'\r'* ||
        "$value" =~ (^|/)(\.|\.\.)(/|$) ]]; then
    echo "restore_drill_configuration_invalid field=$label" >&2
    exit 2
  fi
}

reject_symlink_chain() {
  local value="$1"
  local current=""
  local segment
  local previous_ifs="$IFS"
  IFS='/'
  read -r -a segments <<< "${value#/}"
  IFS="$previous_ifs"
  for segment in "${segments[@]}"; do
    [[ -n "$segment" ]] || continue
    current="$current/$segment"
    if [[ -L "$current" ]]; then
      echo "restore_drill_path_invalid reason=symlink path=$current" >&2
      exit 2
    fi
  done
}

assert_external_replica_mount() {
  local replica_root="$1"
  local source_root="$2"
  local mount_target
  local mount_source
  local mount_fstype
  local replica_block_source
  local source_mount_source
  local source_block_source
  local replica_disk
  local source_disk

  mount_target="$(findmnt -T "$replica_root" -n -o TARGET)"
  mount_source="$(findmnt -T "$replica_root" -n -o SOURCE)"
  mount_fstype="$(findmnt -T "$replica_root" -n -o FSTYPE)"
  [[ -n "$mount_target" && "$mount_target" != "/" && -n "$mount_source" ]] || {
    echo "restore_drill_mount_invalid reason=not_external" >&2
    exit 2
  }
  case "$mount_fstype" in
    cifs|nfs|nfs4) ;;
    ext4)
      replica_block_source="${mount_source%%\[*}"
      source_mount_source="$(findmnt -T "$source_root" -n -o SOURCE)"
      source_block_source="${source_mount_source%%\[*}"
      if [[ ! -b "$replica_block_source" || ! -b "$source_block_source" ]]; then
        echo "restore_drill_mount_invalid reason=local_block_source_invalid" >&2
        exit 2
      fi
      replica_disk="$(lsblk -srno NAME "$replica_block_source" | awk 'NF { value=$1 } END { print value }')"
      source_disk="$(lsblk -srno NAME "$source_block_source" | awk 'NF { value=$1 } END { print value }')"
      if [[ -z "$replica_disk" || -z "$source_disk" || "$replica_disk" == "$source_disk" ]]; then
        echo "restore_drill_mount_invalid reason=same_physical_disk" >&2
        exit 2
      fi
      ;;
    *) echo "restore_drill_mount_invalid fstype=$mount_fstype" >&2; exit 2 ;;
  esac
  if [[ "$(stat -Lc '%d' "$source_root")" == "$(stat -Lc '%d' "$replica_root")" ]]; then
    echo "restore_drill_mount_invalid reason=same_device" >&2
    exit 2
  fi
}

assert_regular_single_link_file() {
  local file="$1"
  local label="$2"
  if [[ ! -f "$file" || -L "$file" || "$(stat -Lc '%h' "$file")" != "1" ]]; then
    echo "restore_drill_file_invalid field=$label" >&2
    exit 3
  fi
}

resolve_trusted_system_executable() {
  local requested="$1"
  local label="$2"
  local resolved
  local metadata
  local uid
  local mode

  require_absolute_path "$requested" "$label"
  resolved="$(realpath -e -- "$requested")" || {
    echo "restore_drill_executable_invalid path=$requested reason=unresolved" >&2
    exit 2
  }
  case "$resolved" in
    /usr/bin/*|/usr/sbin/*|/usr/lib/*|/usr/libexec/*|/bin/*|/sbin/*) ;;
    *) echo "restore_drill_executable_invalid path=$requested reason=untrusted_root" >&2; exit 2 ;;
  esac
  [[ -f "$resolved" && -x "$resolved" ]] || {
    echo "restore_drill_executable_invalid path=$requested reason=not_executable" >&2
    exit 2
  }
  metadata="$(stat -Lc '%u:%a' "$resolved")"
  uid="${metadata%%:*}"
  mode="${metadata#*:}"
  if [[ "$uid" != "0" || ! "$mode" =~ ^[0-7]{3,4}$ || $((8#$mode & 8#022)) -ne 0 ]]; then
    echo "restore_drill_executable_invalid path=$requested reason=unsafe_metadata" >&2
    exit 2
  fi
  printf '%s\n' "$resolved"
}

read_single_field() {
  local file="$1"
  local key="$2"
  local count
  local value
  count="$(awk -F= -v key="$key" '$1 == key { count += 1 } END { print count + 0 }' "$file")"
  if [[ "$count" != "1" ]]; then
    echo "restore_drill_status_invalid field=$key count=$count" >&2
    exit 3
  fi
  value="$(awk -F= -v key="$key" '$1 == key { print substr($0, index($0, "=") + 1) }' "$file")"
  if [[ -z "$value" || "$value" == *$'\n'* || "$value" == *$'\r'* ]]; then
    echo "restore_drill_status_invalid field=$key" >&2
    exit 3
  fi
  printf '%s' "$value"
}

assert_exact_keys() {
  local file="$1"
  local expected="$2"
  local label="$3"
  local actual
  actual="$(awk -F= 'NF >= 2 { print $1 }' "$file" | LC_ALL=C sort)"
  if [[ "$actual" != "$expected" ]]; then
    echo "restore_drill_key_set_invalid field=$label" >&2
    exit 3
  fi
}

assert_identity() {
  local path="$1"
  local expected="$2"
  local label="$3"
  if [[ "$(stat -Lc '%d:%i' "$path")" != "$expected" ]]; then
    echo "restore_drill_identity_changed field=$label" >&2
    exit 3
  fi
}

load_database_manifest() {
  local root="$1"
  local line_count=0
  local database_name
  local dump_name
  local digest
  local extra
  local computed_list_sha256
  local computed_digest_set_sha256
  local -A seen_databases=()
  local -A seen_dumps=()
  manifest_database_names=()
  manifest_dump_names=()
  manifest_database_digests=()

  assert_regular_single_link_file "$root/databases.txt" databases.txt
  while IFS=$'\t' read -r database_name dump_name digest extra; do
    ((line_count += 1))
    if [[ -n "$extra" ||
          ( "$database_name" != georaeplan &&
            "$database_name" != georaeplan_itworld &&
            "$database_name" != georaeplan_usenet &&
            ! "$database_name" =~ ^georaeplan_org_[a-z0-9_]+$ ) ||
          "$dump_name" != "${database_name}.dump" ||
          ! "$digest" =~ ^[0-9a-f]{64}$ ||
          -n "${seen_databases[$database_name]:-}" ||
          -n "${seen_dumps[$dump_name]:-}" ]]; then
      echo "restore_drill_database_manifest_invalid line=$line_count" >&2
      exit 3
    fi
    seen_databases[$database_name]=1
    seen_dumps[$dump_name]=1
    manifest_database_names+=("$database_name")
    manifest_dump_names+=("$dump_name")
    manifest_database_digests+=("$digest")
  done < "$root/databases.txt"
  if (( line_count < 2 || line_count > 256 )) ||
     [[ -z "${seen_databases[georaeplan]:-}" ||
        -z "${seen_databases[georaeplan_itworld]:-}" ]]; then
    echo "restore_drill_database_manifest_invalid reason=count_or_required_database" >&2
    exit 3
  fi
  computed_list_sha256="$(printf '%s\n' "${manifest_database_names[@]}" | sha256sum | awk '{print $1}')"
  computed_digest_set_sha256="$(
    for index in "${!manifest_database_names[@]}"; do
      printf '%s=%s\n' "${manifest_database_names[$index]}" "${manifest_database_digests[$index]}"
    done | sha256sum | awk '{print $1}'
  )"
  [[ "$(read_single_field "$root/metadata.txt" database_manifest)" == databases.txt &&
      "$(read_single_field "$root/metadata.txt" database_count)" == "$line_count" &&
      "$(read_single_field "$root/metadata.txt" database_list_sha256)" == "$computed_list_sha256" &&
      "$(read_single_field "$root/metadata.txt" database_manifest_sha256)" == "$(sha256sum "$root/databases.txt" | awk '{print $1}')" &&
      "$(read_single_field "$root/metadata.txt" database_digest_set_sha256)" == "$computed_digest_set_sha256" ]] || {
    echo "restore_drill_database_manifest_binding_invalid" >&2
    exit 3
  }
}

compute_replica_manifest_sha256() {
  local root="$1"
  load_database_manifest "$root"
  (
    cd "$root"
    sha256sum \
      COMPLETE \
      SHA256SUMS \
      data-protection-keys.tar.gz \
      databases.txt \
      files.tar.gz \
      "${manifest_dump_names[@]}" \
      metadata.txt |
      sha256sum |
      awk '{print $1}'
  )
}

assert_replica_set() {
  local root="$1"
  local source_run_id="$2"
  local source_manifest_sha256="$3"
  local replica_manifest_sha256="$4"
  local actual
  local expected
  load_database_manifest "$root"
  expected="$({
    printf '%s\n' COMPLETE REPLICA SHA256SUMS data-protection-keys.tar.gz databases.txt files.tar.gz metadata.txt
    printf '%s\n' "${manifest_dump_names[@]}"
  } | LC_ALL=C sort)"
  actual="$(find "$root" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
  [[ "$actual" == "$expected" ]] || {
    echo "restore_drill_replica_entry_set_invalid" >&2
    exit 3
  }
  if find "$root" -xdev -mindepth 1 ! -type f -print -quit | grep -q .; then
    echo "restore_drill_replica_entry_type_invalid" >&2
    exit 3
  fi
  for name in COMPLETE REPLICA SHA256SUMS data-protection-keys.tar.gz databases.txt files.tar.gz \
    metadata.txt "${manifest_dump_names[@]}"; do
    assert_regular_single_link_file "$root/$name" "$name"
  done
  assert_exact_keys \
    "$root/COMPLETE" \
    $'backup\nmanifest_sha256\nrun_id\nverified_at' \
    COMPLETE
  assert_exact_keys \
    "$root/REPLICA" \
    $'replica\nreplica_id\nreplica_manifest_sha256\nreplicated_at\nsource_manifest_sha256\nsource_run_id' \
    REPLICA
  assert_exact_keys \
    "$root/metadata.txt" \
    $'backup\nbusiness_business_count_sha256\nbusiness_database\ncentral_business_count_sha256\ncentral_database\ncreated_at\ndatabase_count\ndatabase_digest_set_sha256\ndatabase_list_sha256\ndatabase_manifest\ndatabase_manifest_sha256\ndatabase_snapshot_consistency\ndatabase_snapshot_sha256\nestimated_source_bytes\nfile_deletion_lease\nfiles_archive\nkeyring_archive\nreplica\nrequired_available_bytes\nrun_id' \
    metadata
  [[ "$(read_single_field "$root/COMPLETE" backup)" == complete &&
      "$(read_single_field "$root/COMPLETE" run_id)" == "$source_run_id" &&
      "$(read_single_field "$root/COMPLETE" manifest_sha256)" == "$source_manifest_sha256" &&
      "$(read_single_field "$root/REPLICA" replica)" == complete &&
      "$(read_single_field "$root/REPLICA" replica_id)" == "$REPLICA_ID" &&
      "$(read_single_field "$root/REPLICA" source_run_id)" == "$source_run_id" &&
      "$(read_single_field "$root/REPLICA" source_manifest_sha256)" == "$source_manifest_sha256" &&
      "$(read_single_field "$root/REPLICA" replica_manifest_sha256)" == "$replica_manifest_sha256" ]] || {
    echo "restore_drill_replica_binding_invalid" >&2
    exit 3
  }
  [[ "$(read_single_field "$root/metadata.txt" backup)" == georaeplan &&
      "$(read_single_field "$root/metadata.txt" run_id)" == "$source_run_id" &&
      "$(read_single_field "$root/metadata.txt" files_archive)" == files.tar.gz &&
      "$(read_single_field "$root/metadata.txt" keyring_archive)" == data-protection-keys.tar.gz &&
      "$(read_single_field "$root/metadata.txt" file_deletion_lease)" == exclusive_during_database_and_file_capture &&
      "$(read_single_field "$root/metadata.txt" database_snapshot_consistency)" == unchanged_across_all_dumps &&
      "$(read_single_field "$root/metadata.txt" replica)" == disabled &&
      "$(read_single_field "$root/metadata.txt" database_snapshot_sha256)" =~ ^[0-9a-f]{64}$ &&
      "$(read_single_field "$root/metadata.txt" central_business_count_sha256)" =~ ^[0-9a-f]{64}$ &&
      "$(read_single_field "$root/metadata.txt" business_business_count_sha256)" =~ ^[0-9a-f]{64}$ ]] || {
    echo "restore_drill_metadata_contract_invalid" >&2
    exit 3
  }
  [[ "$(sha256sum "$root/SHA256SUMS" | awk '{print $1}')" == "$source_manifest_sha256" ]] || {
    echo "restore_drill_source_manifest_hash_invalid" >&2
    exit 3
  }
  [[ "$(compute_replica_manifest_sha256 "$root")" == "$replica_manifest_sha256" ]] || {
    echo "restore_drill_replica_manifest_hash_invalid" >&2
    exit 3
  }
  (cd "$root" && sha256sum -c SHA256SUMS > /dev/null)
  for dump_name in "${manifest_dump_names[@]}"; do
    pg_restore -l "$root/$dump_name" > /dev/null
  done
}

write_status_atomically() {
  local target="$1"
  shift
  local temporary
  temporary="$(mktemp "$STATE_ROOT/.restore-drill-status.XXXXXX")"
  printf '%s\n' "$@" > "$temporary"
  chmod 0600 "$temporary" 2>/dev/null || true
  mv -T -- "$temporary" "$target"
}

docker_exec() {
  "$TIMEOUT_BIN" "${STEP_TIMEOUT_SECONDS}s" "$DOCKER_BIN" exec "$@"
}

query_business_count_digest() {
  local container_id="$1"
  local database="$2"
  local output
  local -a lines
  local -a expected_keys=(
    users customers items transactions rental_assets invoices payments)
  local index
  output="$(docker_exec -i "$container_id" psql --no-password -X -q -v ON_ERROR_STOP=1 -At -U postgres -d "$database" <<'SQL'
SELECT 'users=' || count(*) FROM "Users";
SELECT 'customers=' || count(*) FROM "Customers";
SELECT 'items=' || count(*) FROM "Items";
SELECT 'transactions=' || count(*) FROM "Transactions";
SELECT 'rental_assets=' || count(*) FROM "RentalAssets";
SELECT 'invoices=' || count(*) FROM "Invoices";
SELECT 'payments=' || count(*) FROM "Payments";
SQL
)"
  output="${output//$'\r'/}"
  mapfile -t lines <<< "$output"
  [[ "${#lines[@]}" -eq "${#expected_keys[@]}" ]] || {
    echo "restore_drill_business_query_invalid database=$database" >&2
    exit 5
  }
  for index in "${!expected_keys[@]}"; do
    [[ "${lines[$index]}" =~ ^${expected_keys[$index]}=[0-9]+$ ]] || {
      echo "restore_drill_business_query_invalid database=$database" >&2
      exit 5
    }
  done
  printf '%s' "$output" | sha256sum | awk '{print $1}'
}

for pair in "$STATE_ROOT:state_root" "$REPLICA_ROOT:replica_root"; do
  require_absolute_path "${pair%%:*}" "${pair#*:}"
done
if [[ ! "$REPLICA_ID" =~ ^[0-9a-f]{32}$ ||
      ! "$IMAGE_ID" =~ ^sha256:[0-9a-f]{64}$ ||
      ! "$LOCK_TIMEOUT_SECONDS" =~ ^[0-9]+$ ||
      ! "$STEP_TIMEOUT_SECONDS" =~ ^[0-9]+$ ||
      ! "$RESTORE_WORK_RESERVE_BYTES" =~ ^[0-9]{1,18}$ ||
      "$LOCK_TIMEOUT_SECONDS" -lt 5 || "$LOCK_TIMEOUT_SECONDS" -gt 600 ||
      "$STEP_TIMEOUT_SECONDS" -lt 30 || "$STEP_TIMEOUT_SECONDS" -gt 1800 ]]; then
  echo "restore_drill_configuration_invalid field=value" >&2
  exit 2
fi
if [[ "$ALLOW_LOCAL_TEST" == true ]]; then
  for executable in "$DOCKER_BIN" "$TIMEOUT_BIN"; do
    [[ -x "$executable" && ! -L "$executable" ]] || {
      echo "restore_drill_executable_invalid path=$executable" >&2
      exit 2
    }
  done
else
  DOCKER_BIN="$(resolve_trusted_system_executable "$DOCKER_BIN" docker_bin)"
  TIMEOUT_BIN="$(resolve_trusted_system_executable "$TIMEOUT_BIN" timeout_bin)"
fi
for root in "$STATE_ROOT" "$REPLICA_ROOT"; do
  reject_symlink_chain "$root"
  [[ -d "$root" ]] || { echo "restore_drill_path_missing path=$root" >&2; exit 2; }
done
STATE_ROOT="$(realpath -e -- "$STATE_ROOT")"
REPLICA_ROOT="$(realpath -e -- "$REPLICA_ROOT")"
state_root_identity="$(stat -Lc '%d:%i' "$STATE_ROOT")"
replica_root_identity="$(stat -Lc '%d:%i' "$REPLICA_ROOT")"

if [[ "$ALLOW_LOCAL_TEST" != true ]]; then
  [[ "$REPLICA_ROOT" == /mnt/georaeplan-backup-replica ]] || {
    echo "restore_drill_configuration_invalid field=replica_root" >&2
    exit 2
  }
  assert_external_replica_mount "$REPLICA_ROOT" /srv/georaeplan/backups/automatic
fi

BACKUP_STATUS="$STATE_ROOT/backup-status.txt"
REPLICA_STATUS="$STATE_ROOT/external-replica-status.txt"
SUCCESS_STATUS="$STATE_ROOT/backup-restore-drill-status.txt"
FAILURE_STATUS="$STATE_ROOT/backup-restore-drill-failure-status.txt"
DRILL_LOCK="$STATE_ROOT/backup-restore-drill.lock"
REPLICA_LOCK="$REPLICA_ROOT/.georaeplan-replica.lock"
ROOT_MARKER="$REPLICA_ROOT/.georaeplan-replica-root"

for required in "$BACKUP_STATUS" "$REPLICA_STATUS" "$DRILL_LOCK" "$REPLICA_LOCK" "$ROOT_MARKER"; do
  assert_regular_single_link_file "$required" "${required##*/}"
done
assert_exact_keys \
  "$ROOT_MARKER" \
  $'owner\nreplica_id\nschema_version' \
  replica-root-marker
[[ "$(read_single_field "$ROOT_MARKER" schema_version)" == 1 &&
    "$(read_single_field "$ROOT_MARKER" owner)" == georaeplan-external-backup-replica &&
    "$(read_single_field "$ROOT_MARKER" replica_id)" == "$REPLICA_ID" ]] || {
  echo "restore_drill_replica_root_marker_invalid" >&2
  exit 3
}
while IFS= read -r entry; do
  name="${entry##*/}"
  case "$name" in
    .georaeplan-replica-root|.georaeplan-replica.lock) [[ -f "$entry" && ! -L "$entry" ]] ;;
    sets|.staging) [[ -d "$entry" && ! -L "$entry" ]] ;;
    *) echo "restore_drill_replica_root_unknown name=$name" >&2; exit 3 ;;
  esac
done < <(find "$REPLICA_ROOT" -mindepth 1 -maxdepth 1 -print)
assert_exact_keys \
  "$REPLICA_STATUS" \
  $'archive_validation\ndatabase_count\ndatabase_digest_set_sha256\ndatabase_list_sha256\ndatabase_manifest_sha256\nreplica\nreplica_id\nreplica_manifest_sha256\nreplica_set_path\nrestore_catalog_validation\nsource_manifest_sha256\nsource_run_id\nverified_at' \
  external-replica-status

source_run_id="$(read_single_field "$BACKUP_STATUS" run_id)"
source_manifest_sha256="$(read_single_field "$BACKUP_STATUS" manifest_sha256)"
replica_run_id="$(read_single_field "$REPLICA_STATUS" source_run_id)"
replica_source_manifest_sha256="$(read_single_field "$REPLICA_STATUS" source_manifest_sha256)"
replica_manifest_sha256="$(read_single_field "$REPLICA_STATUS" replica_manifest_sha256)"
replica_set="$(read_single_field "$REPLICA_STATUS" replica_set_path)"
if [[ ! "$source_run_id" =~ ^[0-9]{8}T[0-9]{6}Z-[0-9]+$ ||
      ! "$source_manifest_sha256" =~ ^[0-9a-f]{64}$ ||
      ! "$replica_manifest_sha256" =~ ^[0-9a-f]{64}$ ||
      "$replica_run_id" != "$source_run_id" ||
      "$replica_source_manifest_sha256" != "$source_manifest_sha256" ||
      "$(read_single_field "$REPLICA_STATUS" replica)" != ok ||
      "$(read_single_field "$REPLICA_STATUS" replica_id)" != "$REPLICA_ID" ||
      ! "$(read_single_field "$REPLICA_STATUS" database_count)" =~ ^[0-9]{1,3}$ ||
      ! "$(read_single_field "$REPLICA_STATUS" database_list_sha256)" =~ ^[0-9a-f]{64}$ ||
      ! "$(read_single_field "$REPLICA_STATUS" database_manifest_sha256)" =~ ^[0-9a-f]{64}$ ||
      ! "$(read_single_field "$REPLICA_STATUS" database_digest_set_sha256)" =~ ^[0-9a-f]{64}$ ||
      "$(read_single_field "$REPLICA_STATUS" archive_validation)" != ok ||
      "$(read_single_field "$REPLICA_STATUS" restore_catalog_validation)" != ok ]]; then
  echo "restore_drill_replica_status_binding_invalid" >&2
  exit 3
fi
expected_replica_set="$REPLICA_ROOT/sets/replica_${source_run_id}.complete"
[[ "$replica_set" == "$expected_replica_set" && -d "$replica_set" && ! -L "$replica_set" ]] || {
  echo "restore_drill_replica_set_path_invalid" >&2
  exit 3
}
replica_set="$(realpath -e -- "$replica_set")"
[[ "$replica_set" == "$expected_replica_set" ]] || {
  echo "restore_drill_replica_set_identity_invalid" >&2
  exit 3
}
replica_set_identity="$(stat -Lc '%d:%i' "$replica_set")"

exec 8< "$REPLICA_LOCK"
flock -s -w "$LOCK_TIMEOUT_SECONDS" 8 || { echo "restore_drill_replica_lock_timeout" >&2; exit 4; }
exec 9<> "$DRILL_LOCK"
flock -w "$LOCK_TIMEOUT_SECONDS" 9 || { echo "restore_drill_lock_timeout" >&2; exit 4; }
assert_replica_set "$replica_set" "$source_run_id" "$source_manifest_sha256" "$replica_manifest_sha256"
load_database_manifest "$replica_set"
restore_database_names=("${manifest_database_names[@]}")
restore_dump_names=("${manifest_dump_names[@]}")
restore_expected_digests=("${manifest_database_digests[@]}")
restore_database_count="${#restore_database_names[@]}"
[[ "$(read_single_field "$REPLICA_STATUS" database_count)" == "$restore_database_count" &&
    "$(read_single_field "$REPLICA_STATUS" database_list_sha256)" == "$(read_single_field "$replica_set/metadata.txt" database_list_sha256)" &&
    "$(read_single_field "$REPLICA_STATUS" database_manifest_sha256)" == "$(read_single_field "$replica_set/metadata.txt" database_manifest_sha256)" &&
    "$(read_single_field "$REPLICA_STATUS" database_digest_set_sha256)" == "$(read_single_field "$replica_set/metadata.txt" database_digest_set_sha256)" ]] || {
  echo "restore_drill_replica_database_binding_invalid" >&2
  exit 3
}
assert_identity "$STATE_ROOT" "$state_root_identity" state_root
assert_identity "$REPLICA_ROOT" "$replica_root_identity" replica_root
assert_identity "$replica_set" "$replica_set_identity" replica_set

replica_bind_source="$replica_set"
if [[ "$ALLOW_LOCAL_TEST" != true ]]; then
  exec 7< "$replica_set"
  [[ "$(stat -Lc '%d:%i' "/proc/$$/fd/7")" == "$replica_set_identity" ]] || {
    echo "restore_drill_replica_handle_identity_invalid" >&2
    exit 3
  }
fi

container_id=""
restore_workdir=""
restore_workdir_identity=""
completed=false
failure_reason=restore_failed
cleanup_container() {
  if [[ -n "$container_id" ]]; then
    "$DOCKER_BIN" rm -f "$container_id" > /dev/null 2>&1 || return 1
    container_id=""
  fi
}
cleanup_restore_workdir() {
  [[ -n "$restore_workdir" ]] || return 0
  case "$restore_workdir" in
    "$REPLICA_ROOT"/.staging/restore-drill-"$source_run_id".*) ;;
    *) return 1 ;;
  esac
  [[ -d "$restore_workdir" && ! -L "$restore_workdir" ]] || return 1
  [[ "$(realpath -e -- "$restore_workdir")" == "$restore_workdir" ]] || return 1
  [[ "$(stat -Lc '%d:%i' "$restore_workdir")" == "$restore_workdir_identity" ]] || return 1
  [[ "$(stat -Lc '%d' "$restore_workdir")" == "$(stat -Lc '%d' "$REPLICA_ROOT")" ]] || return 1
  find "$restore_workdir" -xdev -depth -delete
  [[ ! -e "$restore_workdir" && ! -L "$restore_workdir" ]] || return 1
  restore_workdir=""
  restore_workdir_identity=""
}
on_exit() {
  local exit_code=$?
  if ! cleanup_container; then
    exit_code=91
    failure_reason=container_cleanup_failed
  fi
  if ! cleanup_restore_workdir; then
    exit_code=92
    failure_reason=restore_workdir_cleanup_failed
  fi
  if [[ "$completed" != true ]]; then
    write_status_atomically \
      "$FAILURE_STATUS" \
      "restore_drill=failed" \
      "replica_id=$REPLICA_ID" \
      "source_run_id=$source_run_id" \
      "source_manifest_sha256=$source_manifest_sha256" \
      "replica_manifest_sha256=$replica_manifest_sha256" \
      "failed_at=$(date -Iseconds)" \
      "reason=$failure_reason"
  fi
  exit "$exit_code"
}
trap on_exit EXIT

restore_work_root="$REPLICA_ROOT/.staging"
[[ -d "$restore_work_root" && ! -L "$restore_work_root" ]] || {
  failure_reason=restore_work_root_invalid
  exit 5
}
restore_work_root="$(realpath -e -- "$restore_work_root")"
[[ "$restore_work_root" == "$REPLICA_ROOT/.staging" &&
    "$(stat -Lc '%d' "$restore_work_root")" == "$(stat -Lc '%d' "$REPLICA_ROOT")" ]] || {
  failure_reason=restore_work_root_identity_invalid
  exit 5
}
restore_estimated_source_bytes="$(read_single_field "$replica_set/metadata.txt" estimated_source_bytes)"
restore_available_bytes="$(df -PB1 -- "$restore_work_root" | awk 'NR == 2 {print $4}')"
[[ "$restore_estimated_source_bytes" =~ ^[0-9]{1,18}$ &&
    "$restore_available_bytes" =~ ^[0-9]{1,18}$ ]] || {
  failure_reason=restore_work_capacity_measurement_invalid
  exit 5
}
restore_required_bytes=$((restore_estimated_source_bytes + restore_estimated_source_bytes / 2 + RESTORE_WORK_RESERVE_BYTES))
(( restore_available_bytes >= restore_required_bytes )) || {
  failure_reason=restore_work_capacity_insufficient
  exit 5
}
restore_workdir="$(mktemp -d "$restore_work_root/restore-drill-${source_run_id}.XXXXXX")"
chmod 0700 "$restore_workdir"
restore_workdir="$(realpath -e -- "$restore_workdir")"
case "$restore_workdir" in
  "$restore_work_root"/restore-drill-"$source_run_id".*) ;;
  *) failure_reason=restore_workdir_path_invalid; exit 5 ;;
esac
restore_workdir_identity="$(stat -Lc '%d:%i' "$restore_workdir")"

container_name="georaeplan-restore-drill-${source_run_id//[^0-9A-Za-z]/-}-$$"
container_id="$(
  "$TIMEOUT_BIN" "${STEP_TIMEOUT_SECONDS}s" "$DOCKER_BIN" create \
    --name "$container_name" \
    --network none \
    --read-only \
    --mount "type=bind,src=$restore_workdir,dst=/var/lib/postgresql/data" \
    --tmpfs /run/postgresql:rw,noexec,nosuid,size=64m \
    --tmpfs /tmp:rw,noexec,nosuid,size=64m \
    --env POSTGRES_HOST_AUTH_METHOD=trust \
    --env POSTGRES_DB=postgres \
    --mount "type=bind,src=$replica_bind_source,dst=/restore,readonly" \
    "$IMAGE_ID"
)"
[[ "$container_id" =~ ^[0-9a-f]{64}$ ]] || {
  failure_reason=container_create_invalid
  exit 5
}
assert_replica_set "$replica_set" "$source_run_id" "$source_manifest_sha256" "$replica_manifest_sha256"
assert_identity "$STATE_ROOT" "$state_root_identity" state_root
assert_identity "$REPLICA_ROOT" "$replica_root_identity" replica_root
assert_identity "$replica_set" "$replica_set_identity" replica_set
if [[ "$ALLOW_LOCAL_TEST" != true ]]; then
  assert_identity "/proc/$$/fd/7" "$replica_set_identity" replica_set_handle
fi
container_security_contract="$(
  "$TIMEOUT_BIN" "${STEP_TIMEOUT_SECONDS}s" "$DOCKER_BIN" inspect \
    --format '{{.HostConfig.NetworkMode}}|{{.HostConfig.ReadonlyRootfs}}' \
    "$container_id"
)"
restore_mount_contract="$(
  "$TIMEOUT_BIN" "${STEP_TIMEOUT_SECONDS}s" "$DOCKER_BIN" inspect \
    --format '{{range .Mounts}}{{if eq .Destination "/restore"}}{{.Type}}|{{.Source}}|{{.RW}}{{end}}{{end}}' \
    "$container_id"
)"
data_mount_contract="$(
  "$TIMEOUT_BIN" "${STEP_TIMEOUT_SECONDS}s" "$DOCKER_BIN" inspect \
    --format '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Type}}|{{.Source}}|{{.RW}}{{end}}{{end}}' \
    "$container_id"
)"
[[ "$container_security_contract" == "none|true" &&
    "$restore_mount_contract" == "bind|$replica_set|false" &&
    "$data_mount_contract" == "bind|$restore_workdir|true" ]] || {
  failure_reason=container_mount_contract_invalid
  echo "restore_drill_container_mount_contract_invalid" >&2
  exit 5
}
"$TIMEOUT_BIN" "${STEP_TIMEOUT_SECONDS}s" "$DOCKER_BIN" start "$container_id" > /dev/null
for _ in $(seq 1 60); do
  if docker_exec "$container_id" pg_isready -U postgres -d postgres > /dev/null 2>&1; then
    break
  fi
  sleep 1
done
docker_exec "$container_id" pg_isready -U postgres -d postgres > /dev/null

declare -a restored_digest_records=()
for index in "${!restore_database_names[@]}"; do
  restore_database="$(printf 'restore_%03d' "$index")"
  source_database="${restore_database_names[$index]}"
  dump_name="${restore_dump_names[$index]}"
  expected_digest="${restore_expected_digests[$index]}"
  docker_exec "$container_id" createdb -U postgres "$restore_database"
  docker_exec "$container_id" \
    pg_restore --exit-on-error --no-owner --no-privileges \
    -U postgres -d "$restore_database" "/restore/$dump_name"
  restored_digest="$(query_business_count_digest "$container_id" "$restore_database")"
  if [[ ! "$restored_digest" =~ ^[0-9a-f]{64}$ ]]; then
    failure_reason=business_query_digest_invalid
    exit 5
  fi
  if [[ "$restored_digest" != "$expected_digest" ]]; then
    failure_reason=business_count_digest_mismatch
    echo "restore_drill_business_count_digest_mismatch database=$source_database expected_sha256=$expected_digest actual_sha256=$restored_digest" >&2
    exit 5
  fi
  restored_digest_records+=("$source_database=$restored_digest")
done
restored_database_set_sha256="$(printf '%s\n' "${restored_digest_records[@]}" | sha256sum | awk '{print $1}')"
[[ "$restored_database_set_sha256" == "$(read_single_field "$replica_set/metadata.txt" database_digest_set_sha256)" ]] || {
  failure_reason=business_count_digest_set_mismatch
  exit 5
}

assert_replica_set "$replica_set" "$source_run_id" "$source_manifest_sha256" "$replica_manifest_sha256"
assert_identity "$STATE_ROOT" "$state_root_identity" state_root
assert_identity "$REPLICA_ROOT" "$replica_root_identity" replica_root
assert_identity "$replica_set" "$replica_set_identity" replica_set
if [[ "$ALLOW_LOCAL_TEST" != true ]]; then
  assert_identity "/proc/$$/fd/7" "$replica_set_identity" replica_set_handle
fi
cleanup_container || { failure_reason=container_cleanup_failed; exit 5; }
cleanup_restore_workdir || { failure_reason=restore_workdir_cleanup_failed; exit 5; }
write_status_atomically \
  "$SUCCESS_STATUS" \
  "restore_drill=ok" \
  "replica_id=$REPLICA_ID" \
  "source_run_id=$source_run_id" \
  "source_manifest_sha256=$source_manifest_sha256" \
  "replica_manifest_sha256=$replica_manifest_sha256" \
  "image_id=$IMAGE_ID" \
  "database_count=$restore_database_count" \
  "restored_database_set_sha256=$restored_database_set_sha256" \
  "business_count_digest_contract=source_metadata_match" \
  "network_mode=none" \
  "completed_at=$(date -Iseconds)"
rm -f -- "$FAILURE_STATUS"
completed=true
trap - EXIT
echo "restore_drill_completed source_run_id=$source_run_id replica_manifest_sha256=$replica_manifest_sha256"
