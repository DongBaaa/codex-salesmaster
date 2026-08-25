#!/usr/bin/env bash
set -Eeuo pipefail

umask 077

SOURCE_BACKUP_ROOT="${GEORAEPLAN_SOURCE_BACKUP_ROOT:-/srv/georaeplan/backups/automatic}"
STATE_ROOT="${GEORAEPLAN_BACKUP_STATE_ROOT:-/srv/georaeplan/ops/state}"
REPLICA_ROOT="${GEORAEPLAN_REPLICA_ROOT:-}"
REPLICA_ID="${GEORAEPLAN_REPLICA_ID:-}"
LOCK_TIMEOUT_SECONDS="${GEORAEPLAN_REPLICA_LOCK_TIMEOUT_SECONDS:-120}"
ALLOW_LOCAL_TEST=false

if [[ "${1:-}" == "--test-allow-local-filesystem" && "$#" -eq 1 ]]; then
  ALLOW_LOCAL_TEST=true
elif (( $# != 0 )); then
  echo "replica_configuration_invalid field=arguments" >&2
  exit 2
fi

require_absolute_path() {
  local value="$1"
  local label="$2"
  if [[ "$value" != /* ||
        "$value" == "/" ||
        "$value" == *$'\n'* ||
        "$value" == *$'\r'* ||
        "$value" =~ (^|/)(\.|\.\.)(/|$) ]]; then
    echo "replica_configuration_invalid field=$label" >&2
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
      echo "replica_path_invalid reason=symlink path=$current" >&2
      exit 2
    fi
  done
}

paths_overlap() {
  local first="$1"
  local second="$2"
  [[ "$first" == "$second" ||
     "$first" == "$second/"* ||
     "$second" == "$first/"* ]]
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
    echo "replica_mount_invalid reason=not_external" >&2
    exit 2
  }

  case "$mount_fstype" in
    cifs|nfs|nfs4) ;;
    ext4)
      replica_block_source="${mount_source%%\[*}"
      source_mount_source="$(findmnt -T "$source_root" -n -o SOURCE)"
      source_block_source="${source_mount_source%%\[*}"
      if [[ ! -b "$replica_block_source" || ! -b "$source_block_source" ]]; then
        echo "replica_mount_invalid reason=local_block_source_invalid" >&2
        exit 2
      fi
      replica_disk="$(lsblk -srno NAME "$replica_block_source" | awk 'NF { value=$1 } END { print value }')"
      source_disk="$(lsblk -srno NAME "$source_block_source" | awk 'NF { value=$1 } END { print value }')"
      if [[ -z "$replica_disk" || -z "$source_disk" || "$replica_disk" == "$source_disk" ]]; then
        echo "replica_mount_invalid reason=same_physical_disk" >&2
        exit 2
      fi
      ;;
    *) echo "replica_mount_invalid fstype=$mount_fstype" >&2; exit 2 ;;
  esac

  if [[ "$(stat -Lc '%d' "$source_root")" == "$(stat -Lc '%d' "$replica_root")" ]]; then
    echo "replica_mount_invalid reason=same_device" >&2
    exit 2
  fi
}

read_single_field() {
  local file="$1"
  local key="$2"
  local count
  local value
  count="$(awk -F= -v key="$key" '$1 == key { count += 1 } END { print count + 0 }' "$file")"
  if [[ "$count" != "1" ]]; then
    echo "replica_source_status_invalid field=$key count=$count" >&2
    exit 3
  fi
  value="$(awk -F= -v key="$key" '$1 == key { print substr($0, index($0, "=") + 1) }' "$file")"
  if [[ -z "$value" || "$value" == *$'\n'* || "$value" == *$'\r'* ]]; then
    echo "replica_source_status_invalid field=$key" >&2
    exit 3
  fi
  printf '%s' "$value"
}

assert_regular_single_link_file() {
  local file="$1"
  local label="$2"
  if [[ ! -f "$file" || -L "$file" || "$(stat -Lc '%h' "$file")" != "1" ]]; then
    echo "replica_file_invalid field=$label" >&2
    exit 3
  fi
}

assert_exact_keys() {
  local file="$1"
  local expected="$2"
  local label="$3"
  local actual
  actual="$(awk -F= 'NF >= 2 { print $1 }' "$file" | LC_ALL=C sort)"
  if [[ "$actual" != "$expected" ]]; then
    echo "replica_key_set_invalid field=$label" >&2
    exit 3
  fi
}

compute_replica_manifest_sha256() {
  local root="$1"
  (
    cd "$root"
    sha256sum \
      COMPLETE \
      SHA256SUMS \
      data-protection-keys.tar.gz \
      files.tar.gz \
      georaeplan.dump \
      georaeplan_itworld.dump \
      metadata.txt |
      sha256sum |
      awk '{print $1}'
  )
}

assert_exact_entry_set() {
  local root="$1"
  local include_replica_marker="$2"
  local expected
  local actual
  expected=$'COMPLETE\nSHA256SUMS\ndata-protection-keys.tar.gz\nfiles.tar.gz\ngeoraeplan.dump\ngeoraeplan_itworld.dump\nmetadata.txt'
  if [[ "$include_replica_marker" == "true" ]]; then
    expected+=$'\nREPLICA'
  fi
  actual="$(find "$root" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
  if [[ "$actual" != "$expected" ]]; then
    echo "replica_entry_set_invalid root=$root" >&2
    exit 3
  fi
  if find "$root" -xdev -mindepth 1 ! -type f -print -quit | grep -q .; then
    echo "replica_entry_type_invalid root=$root" >&2
    exit 3
  fi
}

remove_owned_staging_dir() {
  local root="$1"
  local root_identity
  local entry
  local name
  if [[ ! -d "$root" || -L "$root" ||
        "$(stat -Lc '%d' "$root")" != "$(stat -Lc '%d' "$REPLICA_STAGING_ROOT")" ]]; then
    echo "replica_staging_invalid path=$root" >&2
    return 1
  fi
  root_identity="$(stat -Lc '%d:%i' "$root")"
  while IFS= read -r -d '' entry; do
    name="${entry##*/}"
    case "$name" in
      COMPLETE|SHA256SUMS|data-protection-keys.tar.gz|files.tar.gz|georaeplan.dump|georaeplan_itworld.dump|metadata.txt|REPLICA)
        if [[ ! -f "$entry" || -L "$entry" || "$(stat -Lc '%h' "$entry")" != "1" ]]; then
          echo "replica_staging_entry_invalid name=$name" >&2
          return 1
        fi
        ;;
      *)
        echo "replica_staging_unknown name=$name" >&2
        return 1
        ;;
    esac
  done < <(find "$root" -mindepth 1 -maxdepth 1 -print0)
  if [[ "$(stat -Lc '%d:%i' "$root")" != "$root_identity" ]]; then
    echo "replica_staging_identity_changed" >&2
    return 1
  fi
  for name in \
    COMPLETE \
    SHA256SUMS \
    data-protection-keys.tar.gz \
    files.tar.gz \
    georaeplan.dump \
    georaeplan_itworld.dump \
    metadata.txt \
    REPLICA; do
    if [[ -e "$root/$name" || -L "$root/$name" ]]; then
      rm -f -- "$root/$name" || return 1
    fi
  done
  rmdir -- "$root"
}

assert_archive_set() {
  local root="$1"
  local expected_run_id="$2"
  local expected_manifest_sha256="$3"
  local complete_run_id
  local complete_manifest_sha256
  local actual_manifest_sha256

  assert_exact_entry_set "$root" false
  for name in \
    COMPLETE \
    SHA256SUMS \
    data-protection-keys.tar.gz \
    files.tar.gz \
    georaeplan.dump \
    georaeplan_itworld.dump \
    metadata.txt; do
    assert_regular_single_link_file "$root/$name" "$name"
  done

  complete_run_id="$(read_single_field "$root/COMPLETE" run_id)"
  complete_manifest_sha256="$(read_single_field "$root/COMPLETE" manifest_sha256)"
  assert_exact_keys \
    "$root/COMPLETE" \
    $'backup\nmanifest_sha256\nrun_id\nverified_at' \
    COMPLETE
  if [[ "$(read_single_field "$root/COMPLETE" backup)" != "complete" ||
        "$complete_run_id" != "$expected_run_id" ||
        "$complete_manifest_sha256" != "$expected_manifest_sha256" ||
        -z "$(date -d "$(read_single_field "$root/COMPLETE" verified_at)" -Iseconds 2>/dev/null)" ]]; then
    echo "replica_complete_marker_mismatch" >&2
    exit 3
  fi

  actual_manifest_sha256="$(sha256sum "$root/SHA256SUMS" | awk '{print $1}')"
  if [[ "$actual_manifest_sha256" != "$expected_manifest_sha256" ]]; then
    echo "replica_manifest_hash_mismatch" >&2
    exit 3
  fi
  (
    cd "$root"
    sha256sum -c SHA256SUMS > /dev/null
  )
  tar -tzf "$root/files.tar.gz" > /dev/null
  tar -tzf "$root/data-protection-keys.tar.gz" > /dev/null
  pg_restore -l "$root/georaeplan.dump" > /dev/null
  pg_restore -l "$root/georaeplan_itworld.dump" > /dev/null
}

assert_replicated_set() {
  local root="$1"
  local expected_run_id="$2"
  local expected_manifest_sha256="$3"
  local expected_root_id="$4"
  local actual
  local expected
  local replica_manifest_sha256
  local replicated_at

  expected=$'COMPLETE\nREPLICA\nSHA256SUMS\ndata-protection-keys.tar.gz\nfiles.tar.gz\ngeoraeplan.dump\ngeoraeplan_itworld.dump\nmetadata.txt'
  actual="$(find "$root" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
  if [[ "$actual" != "$expected" ]]; then
    echo "replica_entry_set_invalid root=$root" >&2
    exit 3
  fi
  if find "$root" -xdev -mindepth 1 ! -type f -print -quit | grep -q .; then
    echo "replica_entry_type_invalid root=$root" >&2
    exit 3
  fi
  assert_exact_keys \
    "$root/REPLICA" \
    $'replica\nreplica_id\nreplica_manifest_sha256\nreplicated_at\nsource_manifest_sha256\nsource_run_id' \
    REPLICA
  replicated_at="$(read_single_field "$root/REPLICA" replicated_at)"
  if [[ "$(read_single_field "$root/REPLICA" replica)" != "complete" ||
        "$(read_single_field "$root/REPLICA" replica_id)" != "$expected_root_id" ||
        "$(read_single_field "$root/REPLICA" source_run_id)" != "$expected_run_id" ||
        "$(read_single_field "$root/REPLICA" source_manifest_sha256)" != "$expected_manifest_sha256" ||
        -z "$(date -d "$replicated_at" -Iseconds 2>/dev/null)" ]]; then
    echo "replica_marker_mismatch" >&2
    exit 3
  fi
  assert_regular_single_link_file "$root/REPLICA" REPLICA
  replica_manifest_sha256="$(read_single_field "$root/REPLICA" replica_manifest_sha256)"
  if [[ ! "$replica_manifest_sha256" =~ ^[0-9a-f]{64}$ ||
        "$replica_manifest_sha256" != "$(compute_replica_manifest_sha256 "$root")" ]]; then
    echo "replica_marker_hash_invalid" >&2
    exit 3
  fi
  assert_archive_set_without_entry_check "$root" "$expected_run_id" "$expected_manifest_sha256"
}

assert_archive_set_without_entry_check() {
  local root="$1"
  local expected_run_id="$2"
  local expected_manifest_sha256="$3"
  local actual_manifest_sha256
  for name in \
    COMPLETE \
    SHA256SUMS \
    data-protection-keys.tar.gz \
    files.tar.gz \
    georaeplan.dump \
    georaeplan_itworld.dump \
    metadata.txt; do
    assert_regular_single_link_file "$root/$name" "$name"
  done
  assert_exact_keys \
    "$root/COMPLETE" \
    $'backup\nmanifest_sha256\nrun_id\nverified_at' \
    COMPLETE
  if [[ "$(read_single_field "$root/COMPLETE" backup)" != "complete" ||
        "$(read_single_field "$root/COMPLETE" run_id)" != "$expected_run_id" ||
        "$(read_single_field "$root/COMPLETE" manifest_sha256)" != "$expected_manifest_sha256" ||
        -z "$(date -d "$(read_single_field "$root/COMPLETE" verified_at)" -Iseconds 2>/dev/null)" ]]; then
    echo "replica_complete_marker_mismatch" >&2
    exit 3
  fi
  actual_manifest_sha256="$(sha256sum "$root/SHA256SUMS" | awk '{print $1}')"
  [[ "$actual_manifest_sha256" == "$expected_manifest_sha256" ]] || {
    echo "replica_manifest_hash_mismatch" >&2
    exit 3
  }
  (
    cd "$root"
    sha256sum -c SHA256SUMS > /dev/null
  )
  tar -tzf "$root/files.tar.gz" > /dev/null
  tar -tzf "$root/data-protection-keys.tar.gz" > /dev/null
  pg_restore -l "$root/georaeplan.dump" > /dev/null
  pg_restore -l "$root/georaeplan_itworld.dump" > /dev/null
}

for pair in \
  "$SOURCE_BACKUP_ROOT:source_backup_root" \
  "$STATE_ROOT:state_root" \
  "$REPLICA_ROOT:replica_root"; do
  require_absolute_path "${pair%%:*}" "${pair#*:}"
done
if [[ ! "$REPLICA_ID" =~ ^[0-9a-f]{32}$ ]]; then
  echo "replica_configuration_invalid field=replica_id" >&2
  exit 2
fi
if [[ ! "$LOCK_TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] ||
   (( LOCK_TIMEOUT_SECONDS < 5 || LOCK_TIMEOUT_SECONDS > 600 )); then
  echo "replica_configuration_invalid field=lock_timeout_seconds" >&2
  exit 2
fi

for root in "$SOURCE_BACKUP_ROOT" "$STATE_ROOT" "$REPLICA_ROOT"; do
  reject_symlink_chain "$root"
  [[ -d "$root" ]] || {
    echo "replica_path_missing path=$root" >&2
    exit 2
  }
done
SOURCE_BACKUP_ROOT="$(realpath -e -- "$SOURCE_BACKUP_ROOT")"
STATE_ROOT="$(realpath -e -- "$STATE_ROOT")"
REPLICA_ROOT="$(realpath -e -- "$REPLICA_ROOT")"
if paths_overlap "$SOURCE_BACKUP_ROOT" "$REPLICA_ROOT" ||
   paths_overlap "$STATE_ROOT" "$REPLICA_ROOT"; then
  echo "replica_configuration_invalid field=path_overlap" >&2
  exit 2
fi

SOURCE_SETS_ROOT="$SOURCE_BACKUP_ROOT/sets"
SOURCE_LOCK_FILE="$SOURCE_BACKUP_ROOT/georaeplan-backup.lock"
SOURCE_STATUS="$STATE_ROOT/backup-status.txt"
REPLICA_STATUS="$STATE_ROOT/external-replica-status.txt"
REPLICA_FAILURE_STATUS="$STATE_ROOT/external-replica-failure-status.txt"
REPLICA_ROOT_MARKER="$REPLICA_ROOT/.georaeplan-replica-root"
REPLICA_LOCK_FILE="$REPLICA_ROOT/.georaeplan-replica.lock"
REPLICA_SETS_ROOT="$REPLICA_ROOT/sets"
REPLICA_STAGING_ROOT="$REPLICA_ROOT/.staging"

for required in "$SOURCE_SETS_ROOT" "$SOURCE_LOCK_FILE" "$SOURCE_STATUS" "$REPLICA_ROOT_MARKER" "$REPLICA_LOCK_FILE"; do
  [[ -e "$required" ]] || {
    echo "replica_prerequisite_missing path=$required" >&2
    exit 2
  }
done
assert_regular_single_link_file "$SOURCE_LOCK_FILE" source_lock
assert_regular_single_link_file "$SOURCE_STATUS" source_status
assert_regular_single_link_file "$REPLICA_ROOT_MARKER" replica_root_marker
assert_regular_single_link_file "$REPLICA_LOCK_FILE" replica_lock

if [[ "$(read_single_field "$REPLICA_ROOT_MARKER" schema_version)" != "1" ||
      "$(read_single_field "$REPLICA_ROOT_MARKER" owner)" != "georaeplan-external-backup-replica" ||
      "$(read_single_field "$REPLICA_ROOT_MARKER" replica_id)" != "$REPLICA_ID" ||
      "$(wc -l < "$REPLICA_ROOT_MARKER" | tr -d ' ')" != "3" ]]; then
  echo "replica_root_marker_invalid" >&2
  exit 2
fi

if [[ "$ALLOW_LOCAL_TEST" != true ]]; then
  assert_external_replica_mount "$REPLICA_ROOT" "$SOURCE_BACKUP_ROOT"
fi

mkdir -p -- "$REPLICA_SETS_ROOT" "$REPLICA_STAGING_ROOT"
chmod 0700 "$REPLICA_SETS_ROOT" "$REPLICA_STAGING_ROOT" 2>/dev/null || true

staging_dir=""
record_failure() {
  local exit_code="$?"
  trap - EXIT
  if [[ -n "$staging_dir" && -d "$staging_dir" ]]; then
    case "$staging_dir" in
      "$REPLICA_STAGING_ROOT"/replica_"$REPLICA_ID"_*.staging)
        if ! remove_owned_staging_dir "$staging_dir"; then
          echo "replica_staging_cleanup_retained path=$staging_dir" >&2
        fi
        ;;
    esac
  fi
  if (( exit_code != 0 )); then
    temporary="$REPLICA_FAILURE_STATUS.tmp.$$"
    printf '%s\n' \
      'replica=failed' \
      "replica_id=$REPLICA_ID" \
      "failed_at=$(date -Iseconds)" \
      "exit_code=$exit_code" > "$temporary"
    chmod 0644 "$temporary"
    mv -f -- "$temporary" "$REPLICA_FAILURE_STATUS"
  fi
  exit "$exit_code"
}
trap record_failure EXIT

exec 8< "$SOURCE_LOCK_FILE"
flock -s -w "$LOCK_TIMEOUT_SECONDS" 8
exec 9<> "$REPLICA_LOCK_FILE"
flock -w "$LOCK_TIMEOUT_SECONDS" 9
source_root_identity="$(stat -Lc '%d:%i' "$SOURCE_BACKUP_ROOT")"
replica_root_identity="$(stat -Lc '%d:%i' "$REPLICA_ROOT")"

while IFS= read -r stale_stage; do
  [[ -n "$stale_stage" ]] || continue
  case "$stale_stage" in
    "$REPLICA_STAGING_ROOT"/replica_"$REPLICA_ID"_*.staging)
      remove_owned_staging_dir "$stale_stage" || exit 3
      ;;
    *) echo "replica_staging_unknown path=$stale_stage" >&2; exit 3 ;;
  esac
done < <(find "$REPLICA_STAGING_ROOT" -mindepth 1 -maxdepth 1 -print)

if [[ "$(read_single_field "$SOURCE_STATUS" backup)" != "ok" ||
      "$(read_single_field "$SOURCE_STATUS" replica)" != "disabled" ||
      "$(read_single_field "$SOURCE_STATUS" database_snapshot_consistency)" != "unchanged_across_both_dumps" ]]; then
  echo "replica_source_status_not_eligible" >&2
  exit 3
fi
assert_exact_keys \
  "$SOURCE_STATUS" \
  $'backup\ncompleted_at\ndatabase_snapshot_consistency\ndatabase_snapshot_sha256\nestimated_source_bytes\nfile_deletion_lease\nmanifest_sha256\nreplica\nrequired_available_bytes\nretention_days\nrun_id\nset_path' \
  source_status
completed_at="$(read_single_field "$SOURCE_STATUS" completed_at)"
database_snapshot_sha256="$(read_single_field "$SOURCE_STATUS" database_snapshot_sha256)"
estimated_source_bytes="$(read_single_field "$SOURCE_STATUS" estimated_source_bytes)"
required_available_bytes="$(read_single_field "$SOURCE_STATUS" required_available_bytes)"
retention_days="$(read_single_field "$SOURCE_STATUS" retention_days)"
if ! date -d "$completed_at" -Iseconds >/dev/null 2>&1 ||
   [[ ! "$database_snapshot_sha256" =~ ^[0-9a-f]{64}$ ||
      ! "$estimated_source_bytes" =~ ^[0-9]+$ ||
      ! "$required_available_bytes" =~ ^[0-9]+$ ||
      ! "$retention_days" =~ ^[0-9]+$ ||
      "$(read_single_field "$SOURCE_STATUS" file_deletion_lease)" != "exclusive_during_database_and_file_capture" ]]; then
  echo "replica_source_status_invalid field=typed_contract" >&2
  exit 3
fi
run_id="$(read_single_field "$SOURCE_STATUS" run_id)"
source_set="$(read_single_field "$SOURCE_STATUS" set_path)"
source_manifest_sha256="$(read_single_field "$SOURCE_STATUS" manifest_sha256)"
if [[ ! "$run_id" =~ ^[0-9]{8}T[0-9]{6}Z-[0-9]+$ ||
      ! "$source_manifest_sha256" =~ ^[0-9a-f]{64}$ ||
      "$source_set" != "$SOURCE_SETS_ROOT/backup_${run_id}.complete" ]]; then
  echo "replica_source_binding_invalid" >&2
  exit 3
fi
reject_symlink_chain "$source_set"
[[ -d "$source_set" ]] || {
  echo "replica_source_set_missing" >&2
  exit 3
}
source_identity_before="$(stat -Lc '%d:%i' "$source_set")"
assert_archive_set "$source_set" "$run_id" "$source_manifest_sha256"

final_dir="$REPLICA_SETS_ROOT/replica_${run_id}.complete"
if [[ -e "$final_dir" ]]; then
  reject_symlink_chain "$final_dir"
  [[ -d "$final_dir" ]] || {
    echo "replica_final_invalid" >&2
    exit 3
  }
  assert_replicated_set "$final_dir" "$run_id" "$source_manifest_sha256" "$REPLICA_ID"
else
  staging_dir="$REPLICA_STAGING_ROOT/replica_${REPLICA_ID}_${run_id}_$$.staging"
  mkdir -m 0700 -- "$staging_dir"
  for name in \
    COMPLETE \
    SHA256SUMS \
    data-protection-keys.tar.gz \
    files.tar.gz \
    georaeplan.dump \
    georaeplan_itworld.dump \
    metadata.txt; do
    cp --reflink=never --preserve=timestamps -- "$source_set/$name" "$staging_dir/$name"
  done
  assert_archive_set "$staging_dir" "$run_id" "$source_manifest_sha256"
  replica_manifest_sha256="$(compute_replica_manifest_sha256 "$staging_dir")"
  cat > "$staging_dir/REPLICA" <<EOF
replica=complete
replica_id=$REPLICA_ID
source_run_id=$run_id
source_manifest_sha256=$source_manifest_sha256
replica_manifest_sha256=$replica_manifest_sha256
replicated_at=$(date -Iseconds)
EOF
  chmod 0600 "$staging_dir/REPLICA"
  sync -f "$staging_dir" 2>/dev/null || sync
  assert_replicated_set "$staging_dir" "$run_id" "$source_manifest_sha256" "$REPLICA_ID"
  if [[ "$(stat -Lc '%d:%i' "$source_set")" != "$source_identity_before" ||
        "$(stat -Lc '%d:%i' "$SOURCE_BACKUP_ROOT")" != "$source_root_identity" ||
        "$(stat -Lc '%d:%i' "$REPLICA_ROOT")" != "$replica_root_identity" ]]; then
    echo "replica_source_changed_during_copy" >&2
    exit 3
  fi
  assert_archive_set "$source_set" "$run_id" "$source_manifest_sha256"
  [[ ! -e "$final_dir" ]] || {
    echo "replica_final_collision" >&2
    exit 3
  }
  mv -T -- "$staging_dir" "$final_dir"
  staging_dir=""
fi

if [[ "$(stat -Lc '%d:%i' "$source_set")" != "$source_identity_before" ]]; then
  echo "replica_source_changed_during_copy" >&2
  exit 3
fi
if [[ "$(stat -Lc '%d:%i' "$SOURCE_BACKUP_ROOT")" != "$source_root_identity" ||
      "$(stat -Lc '%d:%i' "$REPLICA_ROOT")" != "$replica_root_identity" ]]; then
  echo "replica_root_identity_changed" >&2
  exit 3
fi
assert_archive_set "$source_set" "$run_id" "$source_manifest_sha256"
assert_replicated_set "$final_dir" "$run_id" "$source_manifest_sha256" "$REPLICA_ID"

replica_manifest_sha256="$(read_single_field "$final_dir/REPLICA" replica_manifest_sha256)"
status_temporary="$REPLICA_STATUS.tmp.$$"
printf '%s\n' \
  'replica=ok' \
  "replica_id=$REPLICA_ID" \
  "source_run_id=$run_id" \
  "source_manifest_sha256=$source_manifest_sha256" \
  "replica_set_path=$final_dir" \
  "replica_manifest_sha256=$replica_manifest_sha256" \
  "verified_at=$(date -Iseconds)" \
  'restore_catalog_validation=ok' \
  'archive_validation=ok' > "$status_temporary"
chmod 0644 "$status_temporary"
mv -f -- "$status_temporary" "$REPLICA_STATUS"
rm -f -- "$REPLICA_FAILURE_STATUS"
trap - EXIT
echo "replica_completed source_run_id=$run_id replica_manifest_sha256=$replica_manifest_sha256"
