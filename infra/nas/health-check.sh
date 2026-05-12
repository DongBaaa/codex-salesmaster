#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

if wait_for_health "${1:-15}"; then
  echo "readiness_ok url=http://127.0.0.1:${API_HOST_PORT}/readyz"
  exit 0
fi

echo "readiness_failed url=http://127.0.0.1:${API_HOST_PORT}/readyz" >&2
exit 1
