#!/bin/sh
set -eu
PATH=/usr/syno/bin:/usr/local/bin:/usr/bin:/bin
export PATH

umask 077

BASE_DIR="/volume1/workplan-certificate-automation/trade-http01"
NATIVE_CLI="$BASE_DIR/native/native_deploy.py"
DOMAIN="${WORKPLAN_ACME_DOMAIN:-trade.2884.kr}"
case "$DOMAIN" in
  trade.2884.kr) ;;
  *)
    printf '%s\n' "renew_failed reason=unsupported_domain domain=$DOMAIN" >&2
    exit 1
    ;;
esac
CHALLENGE_METHOD="${WORKPLAN_ACME_CHALLENGE_METHOD:-http01}"
HTTP_WEBROOT="${WORKPLAN_ACME_HTTP_WEBROOT:-}"
case "$CHALLENGE_METHOD" in
  http01) ;;
  *) printf '%s\n' 'renew_failed reason=unsupported_challenge_method' >&2; exit 1 ;;
esac
RENEW_BEFORE_DAYS="${WORKPLAN_ACME_RENEW_BEFORE_DAYS:-45}"
DSM_CERTIFICATE_DESCRIPTION="${WORKPLAN_ACME_DSM_CERTIFICATE:-trade.2884.kr}"
TLS_CONNECT_HOST="${WORKPLAN_ACME_TLS_CONNECT_HOST:-127.0.0.1}"
TLS_CONNECT_PORT="${WORKPLAN_ACME_TLS_CONNECT_PORT:-443}"
ACME_VERSION="3.1.4"
ACME_BIN="/volume1/workplan-certificate-automation/vendor/acme.sh-$ACME_VERSION/acme.sh"
CONFIG_HOME="$BASE_DIR/config-http01-$DOMAIN"
STAGING_CONFIG_HOME="$BASE_DIR/config-http01-staging-$DOMAIN"
STATE_DIR="$BASE_DIR/state"
LOG_DIR="$BASE_DIR/logs"
TMP_DIR="$BASE_DIR/tmp"
LOCK_DIR="$STATE_DIR/renew.lock"
LOG_FILE="$LOG_DIR/renew.log"
LAST_STATUS_FILE="$STATE_DIR/last-status-${DOMAIN}.txt"
MODE="${1:-scheduled}"

write_status() {
  status="$1"
  detail="$2"
  mkdir -p "$STATE_DIR" 2>/dev/null || return 0
  chmod 700 "$STATE_DIR" 2>/dev/null || true
  {
    printf 'timestamp_utc=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    printf 'status=%s\n' "$status"
    printf 'detail=%s\n' "$detail"
  } > "$LAST_STATUS_FILE.new" 2>/dev/null || return 0
  chmod 600 "$LAST_STATUS_FILE.new" 2>/dev/null || true
  mv "$LAST_STATUS_FILE.new" "$LAST_STATUS_FILE" 2>/dev/null || true
}

log() {
  message="$(date -u '+%Y-%m-%dT%H:%M:%SZ') $*"
  printf '%s\n' "$message"
  printf '%s\n' "$message" >> "$LOG_FILE"
}

fail() {
  write_status "failed" "$1"
  log "renew_failed reason=$1"
  exit 1
}

acquire_lock() {
  if mkdir "$LOCK_DIR" 2>/dev/null; then
    printf '%s\n' "$$" > "$LOCK_DIR/pid"
    return 0
  fi

  if [ -f "$LOCK_DIR/pid" ]; then
    lock_pid="$(cat "$LOCK_DIR/pid" 2>/dev/null || true)"
    case "$lock_pid" in
      ''|*[!0-9]*) ;;
      *)
        if kill -0 "$lock_pid" 2>/dev/null; then
          log "renew_skip reason=locked pid=$lock_pid"
          exit 0
        fi
        ;;
    esac
  fi

  if find "$LOCK_DIR" -maxdepth 0 -mmin +360 -print 2>/dev/null | grep -q .; then
    stale_lock="$STATE_DIR/renew.lock.stale.$$"
    mv "$LOCK_DIR" "$stale_lock" 2>/dev/null || fail "stale_lock_takeover_failed"
    mkdir "$LOCK_DIR" || fail "lock_create_failed"
    printf '%s\n' "$$" > "$LOCK_DIR/pid"
    rm -rf -- "$stale_lock"
    log "renew_warn reason=stale_lock_recovered"
    return 0
  fi

  fail "lock_unavailable"
}

release_lock() {
  rm -f -- "$LOCK_DIR/pid"
  rmdir "$LOCK_DIR" 2>/dev/null || true
}

capture_public_certificate() {
  output_file="$1"
  timeout 20 openssl s_client -servername "$DOMAIN" -connect "$TLS_CONNECT_HOST:$TLS_CONNECT_PORT" \
    -verify_return_error -verify_hostname "$DOMAIN" </dev/null > "$output_file.tls" 2>/dev/null || return 1
  openssl x509 -in "$output_file.tls" -outform PEM > "$output_file" 2>/dev/null || return 1
  rm -f -- "$output_file.tls"
  openssl x509 -in "$output_file" -noout -subject >/dev/null 2>&1
}

certificate_has_domain() {
  certificate_file="$1"
  certificate_san="$(openssl x509 -in "$certificate_file" -noout -ext subjectAltName 2>/dev/null)" || return 1
  printf '%s\n' "$certificate_san" \
    | awk -v expected="DNS:$DOMAIN" 'BEGIN { FS="[[:space:],]+" } { for (i=1; i<=NF; i++) if ($i==expected) found=1 } END { exit !found }'
}

certificate_fingerprint() {
  certificate_file="$1"
  openssl x509 -in "$certificate_file" -noout -fingerprint -sha256 2>/dev/null \
    | sed 's/.*=//;s/://g' | tr '[:upper:]' '[:lower:]'
}

certificate_and_key_match() {
  certificate_file="$1"
  key_file="$2"
  # Check each parser before comparing public keys; failed pipelines can hash empty input.
  certificate_public_key="$(openssl x509 -in "$certificate_file" -pubkey -noout 2>/dev/null)" || return 1
  private_public_key="$(openssl pkey -in "$key_file" -pubout 2>/dev/null)" || return 1
  [ -n "$certificate_public_key" ] && [ "$certificate_public_key" = "$private_public_key" ]
}

validate_http_webroot() {
  case "$HTTP_WEBROOT" in /*) ;; *) return 1;; esac
  [ -d "$HTTP_WEBROOT" ] && [ ! -L "$HTTP_WEBROOT" ] || return 1
  [ "$(readlink -f "$HTTP_WEBROOT")" = "$HTTP_WEBROOT" ] || return 1
  for http_directory in "$HTTP_WEBROOT" "$HTTP_WEBROOT/.well-known" "$HTTP_WEBROOT/.well-known/acme-challenge"; do
    [ -d "$http_directory" ] && [ ! -L "$http_directory" ] || return 1
    [ "$(stat -c %u "$http_directory")" = 0 ] || return 1
    [ "$(stat -c %a "$http_directory")" = 755 ] || return 1
  done
}

run_http_acme() {
  http_config_home="$1"
  shift
  # Restrict inherited credentials/hooks and keep staging/production accounts separate.
  mkdir -p "$http_config_home"
  chmod 700 "$http_config_home"
  http_acme_log="$LOG_DIR/http01-$DOMAIN.log"
  touch "$http_acme_log"; chmod 600 "$http_acme_log"
  timeout 300 env -i PATH="$PATH" HOME="$http_config_home" LC_ALL=C \
    "$ACME_BIN" --home "$http_config_home" --config-home "$http_config_home" --cert-home "$http_config_home" \
      "$@" --webroot "$HTTP_WEBROOT" -d "$DOMAIN" >> "$http_acme_log" 2>&1
}

run_acme_issue() {
  config_home="$1"
  acme_server="$2"
  run_http_acme "$config_home" --issue --server "$acme_server" --keylength ec-256
}

run_acme_renew() {
  run_http_acme "$CONFIG_HOME" --renew --server letsencrypt --force --ecc
}

deploy_to_dsm() {
  certificate_file="$CONFIG_HOME/${DOMAIN}_ecc/fullchain.cer"
  key_file="$CONFIG_HOME/${DOMAIN}_ecc/${DOMAIN}.key"
  [ -f "$certificate_file" ] || fail "issued_certificate_missing"
  [ -f "$key_file" ] || fail "issued_private_key_missing"
  certificate_has_domain "$certificate_file" || fail "issued_certificate_san_invalid"
  certificate_and_key_match "$certificate_file" "$key_file" || fail "issued_key_mismatch"
  issued_issuer="$(openssl x509 -in "$certificate_file" -noout -issuer 2>/dev/null)" || fail "issued_certificate_issuer_invalid"
  case "$issued_issuer" in *'(STAGING)'*) fail "staging_certificate_deploy_refused";; esac
  openssl x509 -in "$certificate_file" -checkend 2592000 -noout >/dev/null 2>&1 \
    || fail "issued_certificate_validity_too_short"

  issued_fingerprint="$(certificate_fingerprint "$certificate_file")"
  [ -n "$issued_fingerprint" ] || fail "issued_fingerprint_unavailable"

  if env -i PATH="$PATH" LC_ALL=C python3 -I -B "$NATIVE_CLI" --apply-http01; then
    write_status "deployed" "domain=$DOMAIN fingerprint=$issued_fingerprint"
    log "deploy_ok domain=$DOMAIN fingerprint=$issued_fingerprint"
  else
    fail "native_deploy_requires_reconciliation"
  fi
}

[ "$(id -u)" = "0" ] || fail "root_required"
for command_name in openssl sed awk grep find; do
  command -v "$command_name" >/dev/null 2>&1 || fail "missing_command_$command_name"
done
[ -x "$ACME_BIN" ] || fail "acme_binary_missing"
for command_name in timeout env readlink stat; do
  command -v "$command_name" >/dev/null 2>&1 || fail "missing_command_$command_name"
done
validate_http_webroot || fail "http_webroot_invalid"

mkdir -p "$CONFIG_HOME" "$STATE_DIR" "$LOG_DIR" "$TMP_DIR"
chmod 700 "$BASE_DIR" "$CONFIG_HOME" "$STATE_DIR" "$LOG_DIR" "$TMP_DIR"
touch "$LOG_FILE"
chmod 600 "$LOG_FILE"
acquire_lock
trap release_lock EXIT
trap 'exit 130' HUP INT TERM

PUBLIC_CERT_FILE="$TMP_DIR/public-cert.$$.pem"
cleanup_files() {
  rm -f -- "$PUBLIC_CERT_FILE" "$PUBLIC_CERT_FILE.tls"
  release_lock
}
trap cleanup_files EXIT
trap 'exit 130' HUP INT TERM

case "$MODE" in
  --staging-test)
    STAGING_CONFIG_HOME="$(mktemp -d "$STAGING_CONFIG_HOME.XXXXXX")" || fail "staging_directory_failed"
    log "staging_test_start domain=$DOMAIN challenge=$CHALLENGE_METHOD"
    run_acme_issue "$STAGING_CONFIG_HOME" letsencrypt_test || fail "staging_issue_failed"
    staging_certificate="$STAGING_CONFIG_HOME/${DOMAIN}_ecc/fullchain.cer"
    [ -f "$staging_certificate" ] || fail "staging_certificate_missing"
    certificate_has_domain "$staging_certificate" || fail "staging_certificate_san_invalid"
    certificate_and_key_match "$staging_certificate" "$STAGING_CONFIG_HOME/${DOMAIN}_ecc/${DOMAIN}.key" || fail "staging_key_mismatch"
    openssl x509 -in "$staging_certificate" -noout -issuer | grep -F '(STAGING)' >/dev/null || fail "staging_issuer_invalid"
    openssl x509 -in "$staging_certificate" -checkend 86400 -noout >/dev/null 2>&1 || fail "staging_validity_too_short"
    write_status "staging_passed" "domain=$DOMAIN"
    log "staging_test_ok domain=$DOMAIN"
    exit 0
    ;;
  scheduled|--scheduled|--check-only) ;;
  *) fail "unsupported_mode" ;;
esac

# Fail closed before not_due, issue, renew, or reusing an issued certificate.
env -i PATH="$PATH" LC_ALL=C python3 -I -B "$NATIVE_CLI" --preflight \
  || fail "native_preflight_refused"

capture_public_certificate "$PUBLIC_CERT_FILE" || fail "public_certificate_unavailable"
certificate_has_domain "$PUBLIC_CERT_FILE" || fail "public_certificate_san_invalid"

renew_seconds=$((RENEW_BEFORE_DAYS * 86400))
if openssl x509 -in "$PUBLIC_CERT_FILE" -checkend "$renew_seconds" -noout >/dev/null 2>&1; then
  served_fingerprint="$(certificate_fingerprint "$PUBLIC_CERT_FILE")"
  write_status "not_due" "domain=$DOMAIN renew_before_days=$RENEW_BEFORE_DAYS fingerprint=$served_fingerprint"
  log "renew_not_due domain=$DOMAIN renew_before_days=$RENEW_BEFORE_DAYS fingerprint=$served_fingerprint"
  exit 0
fi

if [ "$MODE" = --check-only ]; then
  fail "renewal_due_check_only_no_issue"
fi

issued_certificate="$CONFIG_HOME/${DOMAIN}_ecc/fullchain.cer"
if [ -f "$issued_certificate" ] \
  && certificate_has_domain "$issued_certificate" \
  && openssl x509 -in "$issued_certificate" -checkend "$renew_seconds" -noout >/dev/null 2>&1; then
  log "deploy_existing_certificate_after_preflight domain=$DOMAIN"
  deploy_to_dsm
  exit 0
fi

if [ -f "$CONFIG_HOME/${DOMAIN}_ecc/${DOMAIN}.conf" ]; then
  log "renew_start domain=$DOMAIN"
  run_acme_renew || fail "acme_renew_failed"
else
  log "issue_start domain=$DOMAIN"
  run_acme_issue "$CONFIG_HOME" letsencrypt || fail "acme_issue_failed"
fi

deploy_to_dsm
