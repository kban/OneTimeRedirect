#!/usr/bin/env bash
# One-Time Redirect: consistent SQLite backup + confirmed off-site upload.
#
#   backup.sh snapshot   (as user "redirect") online snapshot via the SQLite Online Backup API, verified, published atomically
#   backup.sh upload     (as root)            upload every snapshot that is not yet confirmed off-site, verify, mark
#   backup.sh all        (as root)            snapshot (as "redirect") followed by upload
#
# A local snapshot is NOT reported as a successful backup: the script exits non-zero until the off-site copy has been
# uploaded and verified (rclone check --download). Secrets live outside the repository:
#   /etc/one-time-redirect-backup.env   root:root 0600   (non-secret settings, optional BACKUP_PING_URL)
#   /etc/one-time-redirect/rclone.conf  root:root 0600   (remote credentials, ideally a `crypt` remote)
#
# Installed as /usr/local/sbin/otr-backup (root:root 0755) - it must not be writable by the "redirect" user.
set -Eeuo pipefail
umask 077

CONF="${OTR_BACKUP_CONF:-/etc/one-time-redirect-backup.env}"
# shellcheck disable=SC1090
[[ -r "$CONF" ]] && . "$CONF"

STATE_DIR="${OTR_STATE_DIR:-/var/lib/one-time-redirect}"
DB="${OTR_DB:-$STATE_DIR/redirects.db}"
BACKUP_DIR="${OTR_BACKUP_DIR:-$STATE_DIR/backups}"
BIN="${OTR_BIN:-/opt/one-time-redirect/OneTimeRedirect.Web}"
SERVICE_USER="${OTR_USER:-redirect}"
RCLONE="${RCLONE_BIN:-rclone}"
RCLONE_REMOTE="${RCLONE_REMOTE:-}"                       # e.g. otr-offsite:one-time-redirect
export RCLONE_CONFIG="${RCLONE_CONFIG:-/etc/one-time-redirect/rclone.conf}"
KEEP_DAYS="${RETENTION_LOCAL_DAYS:-14}"
KEEP_MIN="${RETENTION_LOCAL_MIN_COUNT:-3}"

log()  { printf '%s backup: %s\n' "$(date -u +%FT%TZ)" "$*" >&2; }
fail() { log "FAILED: $*"; exit 1; }
trap 'fail "unexpected error at line $LINENO"' ERR

snapshot() {
    [[ -f "$DB" ]] || fail "database not found: $DB"
    local out file
    out="$("$BIN" backup --db "$DB" --out "$BACKUP_DIR")" || fail "snapshot failed (see messages above)"
    file="$(sed -n 's/^BACKUP_FILE=//p' <<<"$out")"
    [[ -f "$file" && -f "$file.sha256" ]] || fail "snapshot did not produce a file with a checksum"
    sync -f "$file" 2>/dev/null || sync
    log "local snapshot ready: $(basename "$file") ($(sed -n 's/^LINKS=/links=/p' <<<"$out")) - NOT yet off-site"
}

upload() {
    [[ $EUID -eq 0 ]] || fail "upload must run as root (it reads the off-site credentials)"
    [[ -n "$RCLONE_REMOTE" ]] || fail "RCLONE_REMOTE is not set: off-site upload is not configured, only local snapshots exist"
    command -v "$RCLONE" >/dev/null || fail "rclone is not installed"
    [[ -r "$RCLONE_CONFIG" ]] || fail "rclone config not readable: $RCLONE_CONFIG"

    local f name pending=0 uploaded=0
    shopt -s nullglob
    for f in "$BACKUP_DIR"/redirects-*.db; do
        name="$(basename "$f")"
        [[ -e "$f.uploaded" ]] && continue
        pending=$((pending + 1))
        [[ -f "$f.sha256" ]] || { log "skipping $name: checksum file missing"; continue; }
        (cd "$BACKUP_DIR" && sha256sum --check --status "$name.sha256") || fail "local checksum mismatch for $name"

        "$RCLONE" copyto "$f.sha256" "$RCLONE_REMOTE/$name.sha256" --immutable
        "$RCLONE" copyto "$f" "$RCLONE_REMOTE/$name" --immutable
        # Confirmation: the remote copy is downloaded and compared byte-for-byte (the files are small).
        "$RCLONE" check "$BACKUP_DIR" "$RCLONE_REMOTE" --one-way --download \
            --include "$name" --include "$name.sha256" >/dev/null 2>&1 || fail "off-site verification failed for $name"

        printf 'confirmed %s remote=%s\n' "$(date -u +%FT%TZ)" "${RCLONE_REMOTE%%:*}" >"$f.uploaded"
        chown --reference="$f" "$f.uploaded" 2>/dev/null || true
        uploaded=$((uploaded + 1))
        log "off-site copy confirmed: $name"
    done
    [[ $pending -eq 0 ]] && log "nothing to upload"
    prune_local
    [[ -z "${BACKUP_PING_URL:-}" ]] || curl -fsS -m 10 -o /dev/null "$BACKUP_PING_URL" || log "warning: monitoring ping failed"
    log "OK: $uploaded snapshot(s) confirmed off-site"
}

# Only snapshots that are confirmed off-site are ever deleted locally; the newest KEEP_MIN are always kept.
prune_local() {
    local files f i=0
    mapfile -t files < <(ls -1t "$BACKUP_DIR"/redirects-*.db 2>/dev/null || true)
    for f in "${files[@]}"; do
        i=$((i + 1))
        [[ $i -le $KEEP_MIN || ! -e "$f.uploaded" ]] && continue
        if [[ -n "$(find "$f" -mtime +"$KEEP_DAYS" -print 2>/dev/null)" ]]; then
            rm -f -- "$f" "$f.sha256" "$f.uploaded"
            log "pruned local $(basename "$f")"
        fi
    done
}

case "${1:-all}" in
    snapshot) snapshot ;;
    upload)   upload ;;
    all)
        [[ $EUID -eq 0 ]] || fail "'all' must run as root; run 'snapshot' as $SERVICE_USER and 'upload' as root separately otherwise"
        runuser -u "$SERVICE_USER" -- env OTR_BACKUP_CONF=/dev/null OTR_STATE_DIR="$STATE_DIR" OTR_DB="$DB" \
            OTR_BACKUP_DIR="$BACKUP_DIR" OTR_BIN="$BIN" "$0" snapshot
        upload
        ;;
    *) echo "usage: $0 snapshot|upload|all" >&2; exit 2 ;;
esac
