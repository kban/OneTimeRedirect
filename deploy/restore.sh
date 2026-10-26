#!/usr/bin/env bash
# One-Time Redirect: restore the SQLite database from a backup WITHOUT resurrecting any link.
#
#   restore.sh [--yes] [--retry] /path/to/redirects-YYYYMMDDTHHMMSSZ.db     (the .sha256 file must sit next to it)
#
# Every link that is neither used nor revoked in the backup (expired ones included) is revoked, so a link that was
# opened after the backup was taken can never work again. The history of used/revoked links is kept.
#
# Order of operations (any failure after "maintenance ON" leaves the service DOWN and public access CLOSED):
#   1  checks, backup pre-verification (nothing is changed yet)
#   2  public maintenance gate ON  (/etc/one-time-redirect.maintenance, honoured by Caddy -> 503)
#   3  RESTORE_LOCK created, service stopped (the lock also blocks every automatic start)
#   4  `OneTimeRedirect.Web restore-db`: save current db/wal/shm, verify backup, restore to a temp file, revoke all
#      unused links in ONE transaction, verify, swap atomically with a way back, verify the working database
#   5  independent re-verification, RESTORE_LOCK removed, service started, /health checked
#   6  re-verification once more, maintenance gate OFF (only now is the public service reachable again)
#
# Installed as /usr/local/sbin/otr-restore (root:root 0755).
set -Eeuo pipefail
umask 077

STATE_DIR="${OTR_STATE_DIR:-/var/lib/one-time-redirect}"
DB="${OTR_DB:-$STATE_DIR/redirects.db}"
BIN="${OTR_BIN:-/opt/one-time-redirect/OneTimeRedirect.Web}"
SERVICE="${OTR_SERVICE:-one-time-redirect}"
SERVICE_USER="${OTR_USER:-redirect}"
SYSTEMCTL="${SYSTEMCTL:-systemctl}"
CURL="${CURL_BIN:-curl}"
GATE="${OTR_MAINTENANCE_FLAG:-/etc/one-time-redirect.maintenance}"
LOCK_MARKER="$(dirname "$DB")/RESTORE_LOCK"
SAVED_DIR="${OTR_SAVED_DIR:-$STATE_DIR/restore-saved}"
INCOMING="${OTR_INCOMING_DIR:-$STATE_DIR/incoming}"
HEALTH_URL="${OTR_HEALTH_URL:-http://127.0.0.1:5000/health}"
HEALTH_TIMEOUT="${OTR_HEALTH_TIMEOUT:-30}"
ENV_FILE="${OTR_ENV_FILE:-/etc/one-time-redirect.env}"
PUBLIC_HOST="${OTR_PUBLIC_HOST:-$(sed -n 's/^PUBLIC_BASE_URL=//p' "$ENV_FILE" 2>/dev/null | tr -d "\"'" | sed -E 's#^https?://##; s#[/:].*$##' | head -n1)}"

YES=0; RETRY=0; BACKUP=""
for a in "$@"; do
    case "$a" in
        --yes) YES=1 ;;
        --retry) RETRY=1 ;;
        -*) echo "unknown option $a" >&2; exit 2 ;;
        *) BACKUP="$a" ;;
    esac
done

PHASE=prepare   # prepare: nothing changed yet | closed: maintenance ON, service must stay down
log() { printf '%s restore: %s\n' "$(date -u +%FT%TZ)" "$*" >&2; }

fail() {
    local msg="$1"
    log "FAILED: $msg"
    if [[ $PHASE == closed ]]; then
        # Fail closed: stop the service, keep the lock and the maintenance gate. Never reopen from an error path.
        "$SYSTEMCTL" stop "$SERVICE" >/dev/null 2>&1 || true
        runuser -u "$SERVICE_USER" -- touch "$LOCK_MARKER" 2>/dev/null || : >"$LOCK_MARKER"
        : >"$GATE"; chmod 0644 "$GATE" 2>/dev/null || true
        log "RESTORE INCOMPLETE: the service is STOPPED and public access stays CLOSED (503)."
        log "Read the runbook (deploy/DISASTER_RECOVERY.md), fix the cause, then re-run with --retry."
    else
        log "nothing was changed."
    fi
    exit 1
}
trap 'fail "unexpected error at line $LINENO"' ERR

[[ $EUID -eq 0 ]] || { echo "run as root" >&2; exit 2; }
[[ -n "$BACKUP" && -f "$BACKUP" ]] || { echo "usage: $0 [--yes] [--retry] BACKUP_FILE" >&2; exit 2; }
[[ -f "$BACKUP.sha256" ]] || fail "checksum file not found: $BACKUP.sha256 (download it together with the backup)"
id "$SERVICE_USER" >/dev/null 2>&1 || fail "user $SERVICE_USER does not exist"

# Only one restore at a time (the kernel drops the lock if this script dies).
mkdir -p /run/lock
exec 9>/run/lock/one-time-redirect-restore.lock
flock -n 9 || fail "another restore is already running"

if [[ -e "$LOCK_MARKER" && $RETRY -ne 1 ]]; then
    fail "$LOCK_MARKER exists: an earlier restore did not finish. Check why, then re-run with --retry."
fi

if [[ $YES -ne 1 ]]; then
    echo "This will REPLACE the working database with: $BACKUP"
    echo "All links that are still active in that backup will be REVOKED; links created after it are lost."
    read -r -p "Type RESTORE to continue: " answer
    [[ "$answer" == "RESTORE" ]] || fail "aborted by operator"
fi

# 1. Stage a private copy and pre-verify it (cheap, nothing is stopped yet).
install -d -m 0700 -o "$SERVICE_USER" -g "$SERVICE_USER" "$INCOMING"
STAGED="$INCOMING/$(basename "$BACKUP")"
install -m 0600 -o "$SERVICE_USER" -g "$SERVICE_USER" "$BACKUP" "$STAGED"
install -m 0600 -o "$SERVICE_USER" -g "$SERVICE_USER" "$BACKUP.sha256" "$STAGED.sha256"
(cd "$INCOMING" && sha256sum --check --status "$(basename "$STAGED").sha256") || fail "backup checksum mismatch"
runuser -u "$SERVICE_USER" -- "$BIN" verify-db --db "$STAGED" >/dev/null || fail "backup failed verification"
log "backup verified: $(basename "$BACKUP")"

# 2. Close the public side first.
PHASE=closed
: >"$GATE"; chmod 0644 "$GATE"
log "maintenance gate ON: $GATE"
if "$SYSTEMCTL" is-active --quiet caddy 2>/dev/null && [[ -n "$PUBLIC_HOST" && "${OTR_SKIP_GATE_CHECK:-0}" != 1 ]]; then
    code="$("$CURL" -sk -o /dev/null -w '%{http_code}' --max-time 10 --resolve "$PUBLIC_HOST:443:127.0.0.1" "https://$PUBLIC_HOST/health" || true)"
    [[ "$code" == 503 ]] || fail "Caddy did not answer 503 in maintenance mode (got '$code'); check the maintenance block in the Caddyfile"
    log "public gate confirmed (503)"
fi

# 3. Block automatic starts, then stop the service.
runuser -u "$SERVICE_USER" -- touch "$LOCK_MARKER"
"$SYSTEMCTL" stop "$SERVICE"
for _ in $(seq 1 30); do "$SYSTEMCTL" is-active --quiet "$SERVICE" || break; sleep 1; done
if "$SYSTEMCTL" is-active --quiet "$SERVICE"; then fail "service did not stop"; fi
log "service stopped, RESTORE_LOCK in place"

# 4. Database restore (runs as the service user so files get the right owner and umask 077).
runuser -u "$SERVICE_USER" -- "$BIN" restore-db --db "$DB" --backup "$STAGED" --saved-dir "$SAVED_DIR" --retry >&2 \
    || fail "restore-db failed (the working database was not replaced, or was rolled back)"

# 5. Independent verification of the working database, then (and only then) allow the service to start.
verify_live() {
    runuser -u "$SERVICE_USER" -- "$BIN" verify-db --db "$DB" --expect-no-active >&2 || return 1
    [[ "$(stat -c '%U:%a' "$DB")" == "$SERVICE_USER:600" ]] || { log "bad owner/mode on $DB: $(stat -c '%U:%a' "$DB")"; return 1; }
    if [[ "${1:-}" == stopped ]]; then
        [[ ! -e "$DB-wal" && ! -e "$DB-shm" ]] || { log "unexpected WAL/SHM next to the new database"; return 1; }
    fi
}
verify_live stopped || fail "working database verification failed"
rm -f -- "$LOCK_MARKER" "$STAGED" "$STAGED.sha256"
"$SYSTEMCTL" start "$SERVICE"

ok=0
HOST_HDR=(); [[ -n "$PUBLIC_HOST" ]] && HOST_HDR=(-H "Host: $PUBLIC_HOST")
for _ in $(seq 1 "$HEALTH_TIMEOUT"); do
    if body="$("$CURL" -fsS --max-time 3 "${HOST_HDR[@]}" "$HEALTH_URL" 2>/dev/null)" && [[ "$body" == *'"status":"ok"'* ]]; then ok=1; break; fi
    sleep 1
done
[[ $ok -eq 1 ]] || fail "health check failed after start ($HEALTH_URL)"
log "service healthy"

# 6. Re-verify with the service running, then reopen public access.
verify_live || fail "working database verification failed after start"
rm -f -- "$GATE"
PHASE=done
log "maintenance gate OFF: public access restored"
log "DONE. Diagnostic copies: $SAVED_DIR ; *.replaced-* next to $DB (delete after the verification period)."
