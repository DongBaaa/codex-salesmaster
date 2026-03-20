#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

if wait_for_health "${1:-15}"; then
  echo "health_ok url=http://127.0.0.1:${API_HOST_PORT}/healthz"
  exit 0
fi

echo "health_failed url=http://127.0.0.1:${API_HOST_PORT}/healthz" >&2
exit 1
