#!/usr/bin/env bash
# End-to-end disaster-recovery check on a throw-away Debian 13 container (see run.sh).
# Real: deploy/backup.sh, deploy/restore.sh, the published Linux binary, SQLite, rclone (crypt remote), runuser/flock,
#       file owners and modes. Simulated: systemd (a tiny `systemctl` stub starts/stops the process and honours
#       the same ConditionPathExists=!RESTORE_LOCK) and Caddy (checked separately by run.sh).
set -uo pipefail

PASS=0; FAILN=0
ok()   { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad()  { FAILN=$((FAILN + 1)); printf '  FAIL %s\n' "$1"; }
expect() { if [[ "$2" == "$3" ]]; then ok "$1 (= $3)"; else bad "$1: expected '$3', got '$2'"; fi; }
section() { printf '\n== %s\n' "$1"; }

# ---------------------------------------------------------------- environment
useradd --system --home-dir /nonexistent --shell /usr/sbin/nologin redirect
install -d -m 0755 /opt/one-time-redirect && cp -r /publish/. /opt/one-time-redirect/ && chmod 0755 /opt/one-time-redirect/OneTimeRedirect.Web
install -d -m 0700 -o redirect -g redirect /var/lib/one-time-redirect
install -m 0755 /src/deploy/backup.sh /usr/local/sbin/otr-backup
install -m 0755 /src/deploy/restore.sh /usr/local/sbin/otr-restore
BIN=/opt/one-time-redirect/OneTimeRedirect.Web
STATE=/var/lib/one-time-redirect
HOST=short.test
PASSWORD='e2e-password-0123456789'

HASH="$(printf '%s\n' "$PASSWORD" | "$BIN" hash-password 2>/dev/null)"
umask 077
printf 'REDIRECT_ADMIN_PASSWORD_HASH=%s\nPUBLIC_BASE_URL=https://%s\nREDIRECT_DB_PATH=%s/redirects.db\n' "$HASH" "$HOST" "$STATE" >/etc/one-time-redirect.env
install -d -m 0755 /etc/one-time-redirect
OBS="$(rclone obscure 'e2e-crypt-passphrase')"
cat >/etc/one-time-redirect/rclone.conf <<EOF
[offsite]
type = local
[otr-crypt]
type = crypt
remote = offsite:/srv/offsite
password = $OBS
EOF
printf 'RCLONE_REMOTE=otr-crypt:\n' >/etc/one-time-redirect-backup.env
ls -l /etc/one-time-redirect/rclone.conf | cut -c1-10 | xargs -I{} echo "rclone.conf mode {}"

# systemd stand-in
cat >/usr/local/bin/systemctl <<'EOF'
#!/usr/bin/env bash
cmd="$1"; shift; quiet=0; [[ "${1:-}" == --quiet ]] && { quiet=1; shift; }; unit="${1:-}"
pidf=/run/otr.pid
alive() { [[ -f $pidf ]] && kill -0 "$(cat $pidf)" 2>/dev/null; }
case "$cmd:$unit" in
  start:one-time-redirect)
    if [[ -e /var/lib/one-time-redirect/RESTORE_LOCK ]]; then echo "(condition failed: RESTORE_LOCK exists, unit not started)" >&2; exit 0; fi
    alive && exit 0
    set -a; . /etc/one-time-redirect.env; set +a
    ( umask 077; export ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5000
      cd /opt/one-time-redirect; exec setpriv --reuid=redirect --regid=redirect --clear-groups ./OneTimeRedirect.Web >>/tmp/app.log 2>&1 ) &
    echo $! >$pidf ;;
  stop:one-time-redirect) alive && { kill "$(cat $pidf)"; for _ in $(seq 50); do alive || break; sleep 0.1; done; } ; rm -f $pidf; exit 0 ;;
  is-active:one-time-redirect) alive && exit 0 || exit 3 ;;
  is-active:*) exit 3 ;;
  *) exit 0 ;;
esac
EOF
chmod 0755 /usr/local/bin/systemctl
export PATH=/usr/local/bin:$PATH

# ---------------------------------------------------------------- tiny HTTP client with a manual cookie jar
declare -A JAR
BODY=/tmp/body.txt
call() {
    local hf; hf="$(mktemp)"; local ck=""
    for k in "${!JAR[@]}"; do ck+="$k=${JAR[$k]}; "; done
    CODE="$(curl -sS -D "$hf" -o "$BODY" -w '%{http_code}' -H "Host: $HOST" -H 'X-Forwarded-Proto: https' ${ck:+-H "Cookie: $ck"} "$@")"
    LOCATION="$(sed -n 's/^[Ll]ocation: *//p' "$hf" | tr -d '\r')"
    while IFS= read -r line; do
        c="${line#*: }"; c="${c%%;*}"; JAR["${c%%=*}"]="${c#*=}"
    done < <(grep -i '^set-cookie:' "$hf" | tr -d '\r')
    rm -f "$hf"
}
form_token() { grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' "$BODY" | head -1 | sed 's/.*value="//; s/"$//'; }
admin_login() {
    JAR=(); call "http://127.0.0.1:5000/admin/login"; local t; t="$(form_token)"
    call -X POST "http://127.0.0.1:5000/admin/login" --data-urlencode "Username=admin" --data-urlencode "Password=$PASSWORD" --data-urlencode "__RequestVerificationToken=$t"
    expect "admin login" "$CODE" 302
}
create_link() { # name -> sets LINK_PATH, LINK_ID
    call "http://127.0.0.1:5000/admin/api/csrf"; local t; t="$(sed 's/.*"token":"\([^"]*\)".*/\1/' "$BODY")"
    call -X POST "http://127.0.0.1:5000/admin/api/links" -H "X-CSRF-TOKEN: $t" -H 'Content-Type: application/json' -d "{\"targetUrl\":\"https://example.com/$1\"}"
    [[ "$CODE" == 201 ]] || bad "create $1 -> $CODE"
    LINK_ID="$(sed 's/.*"id":\([0-9]*\).*/\1/' "$BODY")"
    LINK_PATH="/r/$(sed 's/.*\/r\/\([A-Za-z0-9_-]*\).*/\1/' "$BODY")"
}
open_link() { # path -> status code only (browser-like UA, no cookies)
    curl -sS -o /dev/null -w '%{http_code}' -H "Host: $HOST" -H 'X-Forwarded-Proto: https' -A 'Mozilla/5.0 (X11; Linux x86_64) Chrome/126.0 Safari/537.36' "http://127.0.0.1:5000$1"
}
wait_health() { for _ in $(seq 60); do curl -fsS -H "Host: $HOST" http://127.0.0.1:5000/health >/dev/null 2>&1 && return 0; sleep 0.5; done; return 1; }
mode() { stat -c '%U:%a' "$1"; }

# ---------------------------------------------------------------- 1. history
section "1. application running, links created"
systemctl start one-time-redirect; wait_health && ok "application healthy" || bad "application did not start"
admin_login
create_link a; A_PATH=$LINK_PATH
create_link d; D_PATH=$LINK_PATH
create_link e; E_PATH=$LINK_PATH
expect "E is used before the backup (first GET)" "$(open_link "$E_PATH")" 302
expect "E second GET" "$(open_link "$E_PATH")" 404
ls -l "$STATE/redirects.db-wal" >/dev/null 2>&1 && ok "WAL file is live while the app runs" || bad "no WAL present"
KEYS_BEFORE="$(cd $STATE/keys && ls | sort | tr '\n' ' ')"

# ---------------------------------------------------------------- 2. backup
section "2. backup while the application is running + off-site upload"
otr-backup all 2>/tmp/backup.log; expect "otr-backup all exit code" "$?" 0
tail -n 3 /tmp/backup.log | sed 's/^/       /'
B1="$(ls $STATE/backups/redirects-*.db | head -1)"
expect "backup dir owner:mode" "$(mode $STATE/backups)" redirect:700
expect "backup file owner:mode" "$(mode "$B1")" redirect:600
expect "checksum file owner:mode" "$(mode "$B1.sha256")" redirect:600
[[ -f "$B1.uploaded" ]] && ok "off-site confirmation marker present" || bad "no .uploaded marker"
(cd $STATE/backups && sha256sum --check --status "$(basename "$B1").sha256") && ok "sha256sum -c" || bad "sha256 mismatch"
[[ ! -e "$B1-wal" && ! -e "$B1-shm" && ! -e "$B1-journal" ]] && ok "backup is one self-contained file" || bad "sidecar files next to backup"
"$BIN" verify-db --db "$B1" 2>&1 | sed 's/^/       /'
N_REMOTE="$(find /srv/offsite -type f | wc -l)"; expect "files on the off-site remote (db + checksum)" "$N_REMOTE" 2
find /srv/offsite -type f | grep -q 'redirects-' && bad "remote file names are not encrypted" || ok "remote file names are encrypted (crypt)"
grep -q "$(basename "$B1")" /tmp/backup.log && ok "log names the file but contains no token" || bad "log content"
! grep -R "${A_PATH#/r/}" /tmp/backup.log && ok "no token in backup log" || bad "token leaked into backup log"

section "2b. upload failures are never reported as success"
sleep 1; runuser -u redirect -- env OTR_BACKUP_CONF=/dev/null "$BIN" backup --db $STATE/redirects.db --out $STATE/backups >/dev/null
B2="$(ls -t $STATE/backups/redirects-*.db | head -1)"; [[ "$B2" != "$B1" ]] && ok "second snapshot taken" || bad "no second snapshot"
OTR_BACKUP_CONF=/dev/null RCLONE_REMOTE= otr-backup upload 2>/tmp/up1.log; expect "no remote configured -> exit code" "$?" 1
OTR_BACKUP_CONF=/dev/null RCLONE_REMOTE=':local:/proc/definitely/not/writable' otr-backup upload 2>/tmp/up2.log; expect "unwritable remote -> exit code" "$?" 1
[[ ! -e "$B2.uploaded" ]] && ok "failed upload leaves the snapshot unconfirmed" || bad "snapshot wrongly marked uploaded"
otr-backup upload 2>/dev/null; expect "retry with the real remote" "$?" 0
[[ -e "$B2.uploaded" ]] && ok "snapshot confirmed after successful retry" || bad "not confirmed"

# ---------------------------------------------------------------- 3. history after the backup
section "3. after the backup: A is used, G is created"
expect "A first GET" "$(open_link "$A_PATH")" 302
expect "A second GET" "$(open_link "$A_PATH")" 404
create_link g; G_PATH=$LINK_PATH
expect "G works (before the disaster)" "$(open_link "$G_PATH")" 302
create_link h; H_PATH=$LINK_PATH   # created after the backup, never used before the disaster

# ---------------------------------------------------------------- 4. disaster recovery from off-site
section "4. restore from the OLD backup (downloaded from the encrypted off-site remote)"
export RCLONE_CONFIG=/etc/one-time-redirect/rclone.conf   # the operator needs the same config to download
install -d -m 0700 /root/dr
rclone copyto "otr-crypt:$(basename "$B1")" /root/dr/$(basename "$B1") && rclone copyto "otr-crypt:$(basename "$B1").sha256" /root/dr/$(basename "$B1").sha256 && ok "downloaded + decrypted from off-site" || bad "download failed"
(cd /root/dr && sha256sum --check --status "$(basename "$B1").sha256") && ok "downloaded copy matches its checksum" || bad "downloaded copy corrupt"

section "4a. corrupted backup is rejected before anything is touched"
cp /root/dr/$(basename "$B1") /root/dr/corrupt.db; cp /root/dr/$(basename "$B1").sha256 /root/dr/corrupt.db.sha256
printf 'X' | dd of=/root/dr/corrupt.db bs=1 seek=200 conv=notrunc 2>/dev/null
otr-restore --yes /root/dr/corrupt.db 2>/tmp/r0.log; expect "corrupt backup -> exit code" "$?" 1
grep -q 'nothing was changed' /tmp/r0.log && ok "reported: nothing was changed" || bad "unexpected log: $(tail -2 /tmp/r0.log)"
[[ ! -e /etc/one-time-redirect.maintenance && ! -e $STATE/RESTORE_LOCK ]] && ok "no gate, no lock, service untouched" || bad "restore state leaked"
systemctl is-active --quiet one-time-redirect && ok "service still running" || bad "service was stopped"
LIVE_ACTIVE="$("$BIN" verify-db --db $STATE/redirects.db | sed -n 's/.*active=//p')"; [[ "$LIVE_ACTIVE" -gt 0 ]] && ok "live DB untouched (still $LIVE_ACTIVE active links)" || bad "live DB changed"

section "4b. restore fails AFTER the service was closed: must stay down and closed"
# Failure injection: the DB swap succeeds, then the health check cannot succeed
OTR_HEALTH_URL=http://127.0.0.1:59999/health OTR_HEALTH_TIMEOUT=2 otr-restore --yes /root/dr/$(basename "$B1") 2>/tmp/r1.log; expect "failed restore -> exit code" "$?" 1
grep -q 'RESTORE INCOMPLETE' /tmp/r1.log && ok "operator is told the restore is incomplete" || bad "no incomplete notice"
[[ -e /etc/one-time-redirect.maintenance ]] && ok "maintenance gate stays ON" || bad "gate was removed on failure"
[[ -e $STATE/RESTORE_LOCK ]] && ok "RESTORE_LOCK stays in place" || bad "lock missing"
systemctl is-active --quiet one-time-redirect && bad "service is running after failed restore" || ok "service stays stopped"
systemctl start one-time-redirect 2>/dev/null; sleep 1
systemctl is-active --quiet one-time-redirect && bad "automatic/manual start was NOT blocked" || ok "systemctl start is blocked by the lock (ConditionPathExists)"
(cd /opt/one-time-redirect && REDIRECT_DB_PATH=$STATE/redirects.db PUBLIC_BASE_URL=https://$HOST REDIRECT_ADMIN_PASSWORD_HASH="$HASH" ASPNETCORE_URLS=http://127.0.0.1:5001 timeout 20 setpriv --reuid=redirect --regid=redirect --clear-groups ./OneTimeRedirect.Web >/tmp/refuse.log 2>&1); REFUSE_CODE=$?
[[ $REFUSE_CODE -ne 0 && $REFUSE_CODE -ne 124 ]] && grep -q RESTORE_LOCK /tmp/refuse.log && ok "the binary itself refuses to start while RESTORE_LOCK exists (exit $REFUSE_CODE)" || { bad "binary did not refuse (exit $REFUSE_CODE): $(head -c 300 /tmp/refuse.log)"; }
otr-restore --yes /root/dr/$(basename "$B1") 2>/tmp/r2.log; expect "re-run without --retry is refused" "$?" 1
grep -q -- '--retry' /tmp/r2.log && ok "refusal explains --retry" || bad "no --retry hint"

section "4c. concurrent restores"
( exec 8>/run/lock/one-time-redirect-restore.lock; flock -n 8; sleep 6 ) & sleep 1
otr-restore --yes --retry /root/dr/$(basename "$B1") 2>/tmp/r3.log; expect "second restore while one holds the lock -> exit code" "$?" 1
grep -q 'another restore is already running' /tmp/r3.log && ok "refused: another restore is running" || bad "unexpected: $(tail -1 /tmp/r3.log)"
wait

section "4d. retry completes the restore and only then reopens public access"
otr-restore --yes --retry /root/dr/$(basename "$B1") 2>/tmp/r4.log; expect "retry -> exit code" "$?" 0
tail -n 4 /tmp/r4.log | sed 's/^/       /'
[[ ! -e /etc/one-time-redirect.maintenance ]] && ok "maintenance gate OFF after success" || bad "gate still on"
[[ ! -e $STATE/RESTORE_LOCK ]] && ok "RESTORE_LOCK removed after success" || bad "lock still present"
systemctl is-active --quiet one-time-redirect && ok "service running" || bad "service not running"

# ---------------------------------------------------------------- 5. verification
section "5. result of restoring the OLD backup"
expect "A (used after the backup)" "$(open_link "$A_PATH")" 404
expect "D (active at backup time, never used)" "$(open_link "$D_PATH")" 404
expect "E (used before the backup)" "$(open_link "$E_PATH")" 404
expect "G (created after the backup)" "$(open_link "$G_PATH")" 404
expect "H (created after the backup)" "$(open_link "$H_PATH")" 404
expect "database owner:mode" "$(mode $STATE/redirects.db)" redirect:600
expect "state dir owner:mode" "$(mode $STATE)" redirect:700
expect "keys dir intact" "$(cd $STATE/keys && ls | sort | tr '\n' ' ')" "$KEYS_BEFORE"
"$BIN" verify-db --db $STATE/redirects.db --expect-no-active 2>&1 | sed 's/^/       /'
ls $STATE/restore-saved/*/ 2>/dev/null | sed 's/^/       saved: /' | head -4
find $STATE -maxdepth 2 \( -name '*.replaced-*' -o -name '*.restoring-*' \) | sed 's/^/       left: /'
JAR=(); admin_login; create_link b
expect "NEW link B works once" "$(open_link "$LINK_PATH")" 302
expect "NEW link B second GET" "$(open_link "$LINK_PATH")" 404
find $STATE -type f \( -perm /077 \) | grep -v '^$' | sed 's/^/       NOT owner-only: /' | head
[[ -z "$(find $STATE \( -type f -o -type d \) -perm /077 2>/dev/null)" ]] && ok "every file and directory under the state dir is owner-only" || bad "group/world accessible entries exist"
! grep -R -F -e "${A_PATH#/r/}" -e "${D_PATH#/r/}" /tmp/r*.log /tmp/backup.log /tmp/app.log 2>/dev/null | grep -q . && ok "no one-time token appears in any script or app log" || bad "token found in logs"

printf '\n%s passed, %s failed\n' "$PASS" "$FAILN"
[[ $FAILN -eq 0 ]]
