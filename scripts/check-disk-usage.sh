#!/usr/bin/env bash

# Read-only disk growth audit for the GeoraePlan repository.
# This script never deletes, prunes, packs, or modifies files.

set -uo pipefail

GIB=$((1024 * 1024 * 1024))
PROJECT_ROOT=""
INCLUDE_HOST=0
WARNING_COUNT=0

usage() {
    cat <<'EOF'
Usage: scripts/check-disk-usage.sh [--project-root PATH] [--include-host]

  --project-root PATH  Inspect a specific GeoraePlan checkout.
  --include-host       Also show shared Codex/cache/handoff/Docker usage.

The command is read-only. It does not delete files or run Git/Docker cleanup.
EOF
}

while (($# > 0)); do
    case "$1" in
        --project-root)
            (($# >= 2)) || { echo "--project-root requires a path." >&2; exit 2; }
            PROJECT_ROOT=$2
            shift 2
            ;;
        --include-host)
            INCLUDE_HOST=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ -z "$PROJECT_ROOT" ]]; then
    SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
    PROJECT_ROOT=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)
fi

if [[ -z "$PROJECT_ROOT" || ! -d "$PROJECT_ROOT/.git" ]]; then
    echo "Git project root was not found: ${PROJECT_ROOT:-<empty>}" >&2
    exit 2
fi
PROJECT_ROOT=$(cd -- "$PROJECT_ROOT" && pwd -P)
GIT_DIR=$(git -C "$PROJECT_ROOT" rev-parse --absolute-git-dir)

human_bytes() {
    local bytes=${1:-0}
    if command -v numfmt >/dev/null 2>&1; then
        numfmt --to=iec-i --suffix=B "$bytes"
    else
        printf '%sB\n' "$bytes"
    fi
}

size_bytes() {
    local path=$1
    if [[ ! -e "$path" ]]; then
        printf '0\n'
        return
    fi
    du -sx -B1 -- "$path" 2>/dev/null | awk 'NR == 1 { print $1 + 0 }'
}

print_size() {
    local label=$1 path=$2 bytes
    bytes=$(size_bytes "$path")
    printf '%-28s %12s  %s\n' "$label" "$(human_bytes "$bytes")" "$path"
}

warn_over() {
    local label=$1 bytes=$2 threshold=$3
    if ((bytes > threshold)); then
        printf 'WARNING: %s is %s (threshold: %s).\n' \
            "$label" "$(human_bytes "$bytes")" "$(human_bytes "$threshold")"
        WARNING_COUNT=$((WARNING_COUNT + 1))
    fi
}

print_ranked_sizes() {
    local limit=$1
    while IFS=$'\t' read -r bytes path; do
        [[ -n "${bytes:-}" ]] || continue
        printf '%12s  %s\n' "$(human_bytes "$bytes")" "$path"
    done < <(head -n "$limit")
}

echo "=== GeoraePlan disk usage audit (read-only) ==="
printf 'Project: %s\n' "$PROJECT_ROOT"
printf 'Time:    %s\n\n' "$(date --iso-8601=seconds)"

echo "[Filesystem]"
df -hT -- "$PROJECT_ROOT" 2>/dev/null || df -h -- "$PROJECT_ROOT"

project_bytes=$(size_bytes "$PROJECT_ROOT")
git_bytes=$(size_bytes "$GIT_DIR")
objects_bytes=$(size_bytes "$GIT_DIR/objects")
packs_bytes=$(size_bytes "$GIT_DIR/objects/pack")
lfs_bytes=$(size_bytes "$GIT_DIR/lfs")

echo
echo "[Core sizes]"
printf '%-28s %12s  %s\n' "Project total" "$(human_bytes "$project_bytes")" "$PROJECT_ROOT"
printf '%-28s %12s  %s\n' ".git" "$(human_bytes "$git_bytes")" "$GIT_DIR"
printf '%-28s %12s  %s\n' ".git/objects" "$(human_bytes "$objects_bytes")" "$GIT_DIR/objects"
printf '%-28s %12s  %s\n' ".git/objects/pack" "$(human_bytes "$packs_bytes")" "$GIT_DIR/objects/pack"
printf '%-28s %12s  %s\n' ".git/lfs" "$(human_bytes "$lfs_bytes")" "$GIT_DIR/lfs"

echo
echo "[Git object statistics]"
git -C "$PROJECT_ROOT" count-objects -vH

mapfile -t codex_refs < <(git -C "$PROJECT_ROOT" for-each-ref --format='%(refname)' refs/codex)
mapfile -t normal_refs < <(git -C "$PROJECT_ROOT" for-each-ref --format='%(refname)' refs/heads refs/remotes refs/tags)
codex_exclusive_count=0
codex_exclusive_bytes=0
if ((${#codex_refs[@]} > 0)); then
    codex_exclusive_count=$(
        git -C "$PROJECT_ROOT" rev-list --objects "${codex_refs[@]}" --not "${normal_refs[@]}" |
            awk 'NF { count++ } END { print count + 0 }'
    )
    codex_exclusive_bytes=$(
        git -C "$PROJECT_ROOT" rev-list --objects "${codex_refs[@]}" --not "${normal_refs[@]}" |
            awk '{ print $1 }' |
            git -C "$PROJECT_ROOT" cat-file --batch-check='%(objectsize:disk)' 2>/dev/null |
            awk '{ total += $1 } END { print total + 0 }'
    )
fi
printf 'Codex refs: %d, exclusive objects: %d, estimated disk: %s\n' \
    "${#codex_refs[@]}" "$codex_exclusive_count" "$(human_bytes "$codex_exclusive_bytes")"
git -C "$PROJECT_ROOT" for-each-ref --format='  %(refname) -> %(objectname:short)' refs/codex

echo
echo "[Top 20 directories, maximum depth 2]"
du -x -B1 --max-depth=2 -- "$PROJECT_ROOT" 2>/dev/null |
    awk -v root="$PROJECT_ROOT" '$2 != root { bytes=$1; $1=""; sub(/^ /, ""); print bytes "\t" $0 }' |
    sort -t $'\t' -k1,1nr |
    print_ranked_sizes 20

echo
echo "[Top 20 files]"
find "$PROJECT_ROOT" -xdev -type f -printf '%s\t%p\n' 2>/dev/null |
    sort -t $'\t' -k1,1nr |
    print_ranked_sizes 20

generated_bytes=$(
    find "$PROJECT_ROOT" -xdev \
        -path "$GIT_DIR" -prune -o \
        -type d \( \
            -name bin -o -name obj -o -name node_modules -o -name .next -o \
            -name dist -o -name build -o -name publish -o -name release-artifacts -o \
            -name TestResults -o -name coverage -o -name .cache -o -name cache -o \
            -name tmp -o -name temp -o -name logs -o -name __pycache__ \
        \) -prune -print0 2>/dev/null |
        while IFS= read -r -d '' directory; do size_bytes "$directory"; done |
        awk '{ total += $1 } END { print total + 0 }'
)
log_bytes=$(
    find "$PROJECT_ROOT" -xdev -type f -iname '*.log' -printf '%s\n' 2>/dev/null |
        awk '{ total += $1 } END { print total + 0 }'
)
database_bytes=$(
    find "$PROJECT_ROOT" -xdev -type f \( \
        -iname '*.db' -o -iname '*.sqlite' -o -iname '*.sqlite3' -o \
        -iname '*.db-wal' -o -iname '*.db-shm' -o -iname '*.db-journal' -o \
        -iname '*.sqlite-wal' -o -iname '*.sqlite-shm' -o -iname '*.sqlite-journal' \
    \) -printf '%s\n' 2>/dev/null |
        awk '{ total += $1 } END { print total + 0 }'
)

echo
echo "[Generated/runtime candidates inside the project]"
printf '%-28s %12s\n' "Generated directories" "$(human_bytes "$generated_bytes")"
printf '%-28s %12s\n' "*.log files" "$(human_bytes "$log_bytes")"
printf '%-28s %12s\n' "DB/WAL/SHM files" "$(human_bytes "$database_bytes")"

echo
echo "[Warnings]"
filesystem_percent=$(df -P -- "$PROJECT_ROOT" 2>/dev/null | awk 'NR == 2 { gsub(/%/, "", $5); print $5 + 0 }')
if ((filesystem_percent >= 85)); then
    printf 'WARNING: filesystem usage is %d%% (threshold: 85%%).\n' "$filesystem_percent"
    WARNING_COUNT=$((WARNING_COUNT + 1))
fi
warn_over ".git" "$git_bytes" $((5 * GIB))
warn_over ".git/objects" "$objects_bytes" $((3 * GIB))
warn_over "Codex-exclusive Git objects" "$codex_exclusive_bytes" "$GIB"
warn_over "generated directories" "$generated_bytes" "$GIB"
warn_over "project log files" "$log_bytes" "$GIB"
warn_over "project database files" "$database_bytes" "$GIB"

large_file_count=0
while IFS=$'\t' read -r bytes path; do
    [[ -n "${bytes:-}" ]] || continue
    printf 'WARNING: single file is larger than 1 GiB: %s (%s)\n' "$path" "$(human_bytes "$bytes")"
    large_file_count=$((large_file_count + 1))
done < <(find "$PROJECT_ROOT" -xdev -type f -size +1G -printf '%s\t%p\n' 2>/dev/null | sort -nr)
WARNING_COUNT=$((WARNING_COUNT + large_file_count))

if ((WARNING_COUNT == 0)); then
    echo "No project threshold warnings."
fi

if ((INCLUDE_HOST == 1)); then
    echo
    echo "[Shared host paths]"
    print_size "Codex home" "$HOME/.codex"
    print_size "Codex/runtime cache" "$HOME/.cache"
    print_size "GeoraePlan handoff" "$HOME/georaeplan-codex-handoff"
    print_size "Live GeoraePlan root" "/srv/georaeplan"
    print_size "Live update downloads" "/srv/georaeplan/app/live/updates/downloads"

    if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
        echo
        echo "[Docker disk usage (read-only)]"
        docker system df
        docker_build_cache_size=$(
            docker system df --format '{{.Type}}\t{{.Size}}' 2>/dev/null |
                awk -F '\t' '$1 == "Build Cache" { print $2; exit }'
        )
        if [[ -n "$docker_build_cache_size" && "${docker_build_cache_size%B}" != "$docker_build_cache_size" ]] &&
            command -v numfmt >/dev/null 2>&1; then
            docker_build_cache_bytes=$(numfmt --from=si "${docker_build_cache_size%B}" 2>/dev/null || printf '0')
            warn_over "Docker build cache" "$docker_build_cache_bytes" $((5 * GIB))
        fi
        for container in georaeplan-api-1 georaeplan-postgres-1; do
            if docker inspect "$container" >/dev/null 2>&1; then
                docker inspect --format \
                    "$container log={{json .HostConfig.LogConfig}} path={{.LogPath}}" \
                    "$container"
                log_config=$(docker inspect --format '{{json .HostConfig.LogConfig.Config}}' "$container")
                if [[ "$log_config" == "{}" || "$log_config" == "null" ]]; then
                    printf 'WARNING: %s does not yet have Docker log rotation limits.\n' "$container"
                    WARNING_COUNT=$((WARNING_COUNT + 1))
                fi
            fi
        done
    else
        echo "Docker disk usage unavailable (Docker missing or permission denied)."
    fi
fi

echo
printf 'Audit complete: %d warning(s). No files were changed.\n' "$WARNING_COUNT"
