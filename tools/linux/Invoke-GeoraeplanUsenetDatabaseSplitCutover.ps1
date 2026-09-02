[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ReleaseId,

    [switch]$Apply,

    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $LinuxSshKeyPath -PathType Leaf)) {
    throw "Linux SSH key was not found: $LinuxSshKeyPath"
}

$sshPath = 'C:\Windows\System32\OpenSSH\ssh.exe'
if (-not (Test-Path -LiteralPath $sshPath -PathType Leaf)) {
    $sshPath = (Get-Command ssh.exe -ErrorAction Stop).Source
}

if ($Apply -and $env:GEORAEPLAN_USENET_SPLIT_APPLY -cne '1') {
    throw 'Apply requires GEORAEPLAN_USENET_SPLIT_APPLY=1.'
}

$mode = if ($Apply) { 'apply' } else { 'plan' }
$remoteScript = @'
set -euo pipefail

MODE="__MODE__"
RELEASE_ID="__RELEASE_ID__"
ROOT=/srv/georaeplan
OPS="$ROOT/ops"
COMPOSE="$OPS/docker-compose.yml"
ENV_FILE="$OPS/.env"
RELEASE="$ROOT/releases/$RELEASE_ID"
PROJECT=georaeplan
POSTGRES_CONTAINER=georaeplan-postgres-1
SOURCE_DB=georaeplan
TARGET_DB=georaeplan_usenet
TARGET_LINE='      ConnectionStrings__USENET_GROUP: Host=postgres;Port=5432;Database=${USENET_POSTGRES_DB:-georaeplan_usenet};Username=${POSTGRES_USER:-georaeplan};Password=${POSTGRES_PASSWORD}'

cd "$OPS"
test -f "$COMPOSE"
test -f "$ENV_FILE"
test -x "$OPS/apply-release.sh"
test -d "$RELEASE"
docker inspect "$POSTGRES_CONTAINER" >/dev/null
PGUSER="$(docker exec "$POSTGRES_CONTAINER" sh -lc 'printf %s "$POSTGRES_USER"')"
test -n "$PGUSER"

psql_postgres() {
  docker exec "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PGUSER" -d postgres "$@"
}

database_exists() {
  local name="$1"
  [[ "$(psql_postgres -Atc "select count(*) from pg_database where datname='$name'")" == 1 ]]
}

table_count_digest() {
  local database="$1"
  local table
  while IFS= read -r table; do
    [[ -n "$table" ]] || continue
    local count
    count="$(docker exec "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PGUSER" -d "$database" -Atc "select count(*) from \"$table\"")"
    printf '%s|%s\n' "$table" "$count"
  done < <(docker exec "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PGUSER" -d "$database" -Atc "select tablename from pg_tables where schemaname='public' order by tablename") \
    | sha256sum | awk '{print $1}'
}

api_state="$(docker compose --env-file "$ENV_FILE" -p "$PROJECT" -f "$COMPOSE" ps api --format json)"
printf 'cutover_mode=%s\n' "$MODE"
printf 'release_id=%s\n' "$RELEASE_ID"
printf 'api_state_present=%s\n' "$([[ -n "$api_state" ]] && echo true || echo false)"
printf 'source_database_present=%s\n' "$(database_exists "$SOURCE_DB" && echo true || echo false)"
printf 'target_database_present=%s\n' "$(database_exists "$TARGET_DB" && echo true || echo false)"
printf 'compose_usenet_route_count=%s\n' "$(grep -Fxc "$TARGET_LINE" "$COMPOSE" || true)"
printf 'source_table_count_digest=%s\n' "$(table_count_digest "$SOURCE_DB")"

if [[ "$MODE" == plan ]]; then
  database_exists "$SOURCE_DB"
  if database_exists "$TARGET_DB"; then
    echo 'plan_blocked=target_database_already_exists' >&2
    exit 31
  fi
  [[ "$(grep -Fxc "$TARGET_LINE" "$COMPOSE" || true)" == 0 ]]
  docker compose --env-file "$ENV_FILE" -p "$PROJECT" -f "$COMPOSE" config --quiet
  echo 'cutover_plan_ready=true'
  exit 0
fi

database_exists "$SOURCE_DB"
if database_exists "$TARGET_DB"; then
  echo 'apply_blocked=target_database_already_exists' >&2
  exit 32
fi
if [[ "$(grep -Fxc "$TARGET_LINE" "$COMPOSE" || true)" != 0 ]]; then
  echo 'apply_blocked=compose_route_already_present' >&2
  exit 33
fi

compose_backup="$COMPOSE.before-usenet-split-$(date -u +%Y%m%dT%H%M%SZ)"
cp -a "$COMPOSE" "$compose_backup"
api_recovery_required=true
recover_api() {
  local status=$?
  if [[ "$status" != 0 && "$api_recovery_required" == true ]]; then
    echo "cutover_failed_status=$status" >&2
    docker compose --env-file "$ENV_FILE" -p "$PROJECT" -f "$COMPOSE" up -d --no-deps api >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap recover_api EXIT

docker compose --env-file "$ENV_FILE" -p "$PROJECT" -f "$COMPOSE" stop api
psql_postgres -Atc "select pg_terminate_backend(pid) from pg_stat_activity where datname='$SOURCE_DB' and pid <> pg_backend_pid()" >/dev/null
psql_postgres -c "create database $TARGET_DB with template $SOURCE_DB owner $PGUSER"

source_digest="$(table_count_digest "$SOURCE_DB")"
target_digest="$(table_count_digest "$TARGET_DB")"
if [[ "$source_digest" != "$target_digest" ]]; then
  echo "clone_digest_mismatch source=$source_digest target=$target_digest" >&2
  exit 34
fi

python3 - "$COMPOSE" "$TARGET_LINE" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
target = sys.argv[2]
text = path.read_text(encoding="utf-8")
needle = "      ConnectionStrings__Default: Host=postgres;Port=5432;Database=${POSTGRES_DB:-georaeplan};Username=${POSTGRES_USER:-georaeplan};Password=${POSTGRES_PASSWORD}"
if text.count(needle) != 1 or target in text:
    raise SystemExit("compose insertion precondition failed")
path.write_text(text.replace(needle, needle + "\n" + target), encoding="utf-8", newline="\n")
PY

[[ "$(grep -Fxc "$TARGET_LINE" "$COMPOSE")" == 1 ]]
docker compose --env-file "$ENV_FILE" -p "$PROJECT" -f "$COMPOSE" config --quiet
HEALTH_CHECK_RETRIES=900 /bin/bash "$OPS/apply-release.sh" "$RELEASE_ID"
curl -fsS http://127.0.0.1:18082/healthz >/dev/null

api_recovery_required=false
trap - EXIT
printf 'compose_backup=%s\n' "$compose_backup"
printf 'source_table_count_digest=%s\n' "$source_digest"
printf 'target_table_count_digest=%s\n' "$target_digest"
printf 'cutover_apply_succeeded=true\n'
'@

$remoteScript = $remoteScript.Replace('__MODE__', $mode).Replace('__RELEASE_ID__', $ReleaseId)
$encodedScript = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($remoteScript))
$arguments = @(
    '-p', $LinuxSshPort,
    '-i', $LinuxSshKeyPath,
    '-o', 'BatchMode=yes',
    '-o', 'IdentitiesOnly=yes',
    '-o', 'ConnectTimeout=10',
    "$LinuxSshUser@$LinuxSshHost",
    "echo $encodedScript | base64 -d | bash"
)

& $sshPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "USENET database split cutover failed with exit code $LASTEXITCODE."
}
