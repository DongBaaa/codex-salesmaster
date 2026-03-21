#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

CERT_RENEW_ENABLED="${CERT_RENEW_ENABLED:-true}"
CERT_RENEW_BEFORE_DAYS="${CERT_RENEW_BEFORE_DAYS:-30}"
base_url="${PUBLIC_BASE_URL:-https://api.example.invalid}"
trimmed_url="${base_url#*://}"
host_port="${trimmed_url%%/*}"
host_name="${host_port%%:*}"
port_number="${host_port##*:}"
if [[ "$host_name" == "$port_number" ]]; then
  port_number=443
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
cert_file="$work_dir/remote-cert.pem"

if ! echo | openssl s_client -servername "$host_name" -connect "$host_name:$port_number" 2>/dev/null | openssl x509 -outform PEM > "$cert_file" 2>/dev/null; then
  echo "cert_check_failed reason=fetch_failed host=$host_name port=$port_number" >&2
  exit 1
fi

issuer="$(openssl x509 -in "$cert_file" -noout -issuer | sed 's/^issuer=//')"
subject="$(openssl x509 -in "$cert_file" -noout -subject | sed 's/^subject=//')"
end_date="$(openssl x509 -in "$cert_file" -noout -enddate | cut -d= -f2-)"
seconds_window=$(( CERT_RENEW_BEFORE_DAYS * 86400 ))

if openssl x509 -in "$cert_file" -checkend "$seconds_window" -noout >/dev/null 2>&1; then
  echo "cert_ok host=$host_name issuer=$issuer end=$end_date"
  exit 0
fi

renew_attempted=false
renew_result="not_attempted"
if [[ "${CERT_RENEW_ENABLED,,}" == "true" ]]; then
  if [[ -x /usr/syno/sbin/syno-letsencrypt ]]; then
    renew_attempted=true
    if /usr/syno/sbin/syno-letsencrypt renew-all >/dev/null 2>&1; then
      renew_result="renew_command_ok"
    else
      renew_result="renew_command_failed"
    fi
  else
    renew_result="renew_command_missing"
  fi
else
  renew_result="disabled"
fi

sleep 5
if echo | openssl s_client -servername "$host_name" -connect "$host_name:$port_number" 2>/dev/null | openssl x509 -outform PEM > "$cert_file" 2>/dev/null; then
  end_date="$(openssl x509 -in "$cert_file" -noout -enddate | cut -d= -f2-)"
  issuer="$(openssl x509 -in "$cert_file" -noout -issuer | sed 's/^issuer=//')"
fi

if openssl x509 -in "$cert_file" -checkend "$seconds_window" -noout >/dev/null 2>&1; then
  echo "cert_renew_ok host=$host_name issuer=$issuer end=$end_date attempted=$renew_attempted result=$renew_result"
  exit 0
fi

echo "cert_renew_pending host=$host_name issuer=$issuer subject=$subject end=$end_date attempted=$renew_attempted result=$renew_result" >&2
exit 1
