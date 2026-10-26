# Резервное копирование и восстановление (Debian 13, systemd, Caddy, один VPS)

**Главный инвариант:** восстановление *никогда* не оживляет ссылки. После восстановления старой копии все ссылки, которые в копии ещё не использованы и не отозваны (включая просроченные), получают `RevokedAt`. Ссылка, открытая после снимка, не откроется снова. Платой за это является потеря ссылок, созданных после снимка (их нужно выдать заново).

## 1. Что и как копируется

| Что | Как | Где |
|---|---|---|
| SQLite | `OneTimeRedirect.Web backup` - SQLite Online Backup API, без остановки приложения, не копирование файла. Результат проверяется (`integrity_check`, схема), пишется во временный файл, публикуется атомарным `rename`, права `0600`, рядом `.sha256` | `/var/lib/one-time-redirect/backups/redirects-YYYYMMDDTHHMMSSZ.db` |
| Внешнее хранилище | `rclone` (рекомендуется `crypt`-remote), загрузка + проверка `rclone check --download`; только после этого создаётся `.uploaded` | см. §2 |
| Data Protection keys (`keys/`) | **не входят** в снимок БД. С версии с `EncryptedToken` ключи нужны ещё и для показа/копирования токенов активных ссылок (без них админка покажет «Недоступна для копирования», сами ссылки продолжат работать); после восстановления все ссылки и так отзываются и не трогаются восстановлением. Делайте отдельную копию (§2.4): без них после потери VPS придётся заново входить в админку (данные ссылок это не затрагивает) | `/var/lib/one-time-redirect/keys` |

В БД нет открытых токенов (только SHA-256), но есть `TargetUrl` - копии считаются конфиденциальными: права `0600`, на внешнее хранилище только в зашифрованном виде.

## 2. Установка

### 2.1 Файлы

```bash
sudo install -m 0755 deploy/backup.sh  /usr/local/sbin/otr-backup     # root:root, не доступны на запись пользователю redirect
sudo install -m 0755 deploy/restore.sh /usr/local/sbin/otr-restore
sudo install -m 0644 deploy/one-time-redirect.service deploy/one-time-redirect-backup.service \
                     deploy/one-time-redirect-backup.timer /etc/systemd/system/
sudo apt install rclone curl
```

Caddyfile (`deploy/Caddyfile`) уже содержит блок `@maintenance`; после копирования: `sudo caddy validate --config /etc/caddy/Caddyfile && sudo systemctl reload caddy`.

### 2.2 Внешнее хранилище (секреты - только вне репозитория)

Подойдёт любое S3-совместимое (Selectel Object Storage), SFTP на другом сервере, Backblaze B2 и т.д. Хранилище должно быть **на другом хосте и под другой учётной записью**, чем VPS.

```bash
sudo install -d -m 0755 /etc/one-time-redirect
sudo -e /etc/one-time-redirect/rclone.conf          # создайте через `sudo rclone config --config /etc/one-time-redirect/rclone.conf`
sudo chown root:root /etc/one-time-redirect/rclone.conf && sudo chmod 0600 /etc/one-time-redirect/rclone.conf
```

Минимальная схема: `[selectel]` (type=s3, ключ только на запись/чтение своего бакета) и `[otr-crypt]` (type=crypt, `remote = selectel:ИМЯ-БАКЕТА/otr`, пароль и salt через `rclone obscure`). **Храните пароль crypt вне VPS (менеджер паролей): без него копии не расшифровать.** Включите на бакете версионирование или object-lock/неизменяемость (скрипт использует `--immutable` и не перезаписывает файлы, но компрометированный VPS мог бы удалять объекты, если ключ это позволяет - выдайте ключ без права удаления, если провайдер это поддерживает).

```bash
sudo tee /etc/one-time-redirect-backup.env >/dev/null <<'EOF'
RCLONE_REMOTE=otr-crypt:
RETENTION_LOCAL_DAYS=14
# BACKUP_PING_URL=https://hc-ping.com/<uuid>     # необязательно: сторожевой пинг после ПОДТВЕРЖДЁННОЙ выгрузки
EOF
sudo chmod 0600 /etc/one-time-redirect-backup.env
```

Проверка вручную (должно завершиться кодом 0 и строкой `off-site copy confirmed`):

```bash
sudo systemctl start one-time-redirect-backup.service ; journalctl -u one-time-redirect-backup -n 20 --no-pager
sudo rclone --config /etc/one-time-redirect/rclone.conf ls otr-crypt:
```

Если `RCLONE_REMOTE` не задан или выгрузка не прошла - сервис завершается ошибкой (`systemctl --failed`), локальный снимок остаётся без `.uploaded` и **не считается резервной копией**.

### 2.3 Таймер

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now one-time-redirect-backup.timer
systemctl list-timers one-time-redirect-backup.timer
```

По умолчанию каждые 6 часов (`OnCalendar=*-*-* 00,06,12,18:17:00`, `Persistent=true`). Это ваше окно потери данных: ссылки, созданные после последней копии, после восстановления не будут работать. Уменьшайте интервал при необходимости (снимок маленький и не блокирует приложение).

### 2.4 Ключи Data Protection (отдельно, редко)

`sudo tar -C /var/lib/one-time-redirect -czf - keys | rclone --config /etc/one-time-redirect/rclone.conf rcat otr-crypt:keys-$(date +%F).tgz` после первого запуска и после ротации ключей (раз в ~90 дней ASP.NET создаёт новый ключ).

### 2.5 Мониторинг

Пинг (`BACKUP_PING_URL`) приходит только после подтверждённой выгрузки: отсутствие пинга = нет внешней копии. Дополнительно: `systemctl --failed` и `journalctl -u one-time-redirect-backup`.

## 3. Disaster Recovery Runbook

Подготовьте заранее: доступ к хранилищу (rclone.conf, пароль crypt), этот документ, root-доступ.

### Вариант A. База повреждена / откат на более раннее состояние (тот же VPS)

1. Скачайте нужную копию **вместе с `.sha256`** во временный каталог root:
   ```bash
   export RCLONE_CONFIG=/etc/one-time-redirect/rclone.conf
   sudo -E rclone lsf otr-crypt: | sort | tail          # выберите последнюю до инцидента
   sudo install -d -m 0700 /root/dr
   sudo -E rclone copyto otr-crypt:redirects-20261026T101705Z.db        /root/dr/redirects-20261026T101705Z.db
   sudo -E rclone copyto otr-crypt:redirects-20261026T101705Z.db.sha256 /root/dr/redirects-20261026T101705Z.db.sha256
   ```
2. Запустите восстановление:
   ```bash
   sudo otr-restore /root/dr/redirects-20261026T101705Z.db        # попросит ввести RESTORE
   ```
3. Скрипт сам (в таком порядке): проверяет копию (если не годится - **ничего не меняется**) → включает режим обслуживания (Caddy отвечает 503, флаг `/etc/one-time-redirect.maintenance`) → создаёт `RESTORE_LOCK` и останавливает сервис → сохраняет текущие `redirects.db`, `-wal`, `-shm` в `/var/lib/one-time-redirect/restore-saved/restore-<время>/` → восстанавливает во временный файл, **в одной транзакции отзывает все неиспользованные и неотозванные ссылки**, проверяет `active = 0` → атомарно подменяет базу (старые файлы остаются как `*.replaced-<время>`) → проверяет рабочую базу и права → снимает `RESTORE_LOCK`, запускает сервис, ждёт `/health` → повторная проверка → **только после этого** удаляет флаг обслуживания.
4. Проверьте: `curl -sS https://ВАШ-ДОМЕН/health`; войдите в `/admin`, статусы - Used/Revoked; создайте новую ссылку и откройте её один раз.
5. Через несколько дней, когда всё подтверждено, удалите `*.replaced-*` и каталоги `restore-saved/*` (в них боевые данные).
6. Сообщите заинтересованным: ссылки, выданные после времени копии, не работают и должны быть выпущены заново.

### Вариант B. VPS потерян (новый сервер)

1. Разверните сервис по инструкции README (публикация, `/etc/one-time-redirect.env` с `REDIRECT_ADMIN_PASSWORD_HASH`, `PUBLIC_BASE_URL`, юнит, Caddy, пользователь `redirect`, `/var/lib/one-time-redirect` `0700`), **не запуская** сервис. Если есть копия `keys/` - распакуйте её в `/var/lib/one-time-redirect/keys` владельцем `redirect`.
2. Установите `otr-restore`, `otr-backup`, `rclone.conf`, скачайте копию (Вариант A, п.1).
3. `sudo otr-restore --yes /root/dr/<файл>`. Отсутствие текущей БД - штатный случай; DNS переключайте на новый сервер **после** того, как скрипт сообщил `DONE`.

### Если восстановление остановилось с ошибкой

Сервис остаётся **остановленным**, публичный доступ **закрыт** (503), `RESTORE_LOCK` на месте - это намеренно. Нельзя: удалять `RESTORE_LOCK` или `/etc/one-time-redirect.maintenance` вручную и запускать сервис, пока не понятна причина.

1. Прочитайте вывод скрипта; диагностические копии лежат в `restore-saved/`, откатные файлы - `*.replaced-*`.
2. Если база была заменена, а сбой произошёл на запуске/health (журнал `journalctl -u one-time-redirect`) - исправьте причину и запустите `sudo otr-restore --retry <файл>` (повторно проходит все проверки заново).
3. Если нужно вернуть состояние «до восстановления» (например, копия оказалась не той): остановленный сервис, затем вручную `mv redirects.db.replaced-<t> redirects.db` (и `-wal`/`-shm`, если есть), `OneTimeRedirect.Web verify-db --db ...`, после чего `rm /var/lib/one-time-redirect/RESTORE_LOCK`, `systemctl start`, проверка, `rm /etc/one-time-redirect.maintenance`. Учтите: это состояние содержит ссылки, активные «в реальности» - принимайте решение осознанно.

## 4. Проверка восстановления на тестовом окружении (делайте регулярно, например раз в квартал)

Автоматически, на Debian 13 в одноразовом контейнере (Docker используется только как песочница для проверки, в сам сервис он не входит):

```powershell
dotnet publish src/OneTimeRedirect.Web -c Release -r linux-x64 --self-contained true -p:PublishDir=$env:TEMP\otr-publish\
bash tests/e2e/run.sh "$env:TEMP\otr-publish"        # ожидается: «60 passed, 0 failed»
dotnet test                                           # тесты ядра backup/restore и статические проверки deploy/
```

Вручную на тестовой VM (копия production-окружения без DNS):

1. Установите сервис и скрипты как в §2, но с тестовыми `PUBLIC_BASE_URL`/паролем и **отдельным** (не боевым) хранилищем.
2. Создайте 3 ссылки (A, B, C), откройте C. Выполните `sudo systemctl start one-time-redirect-backup.service`; убедитесь, что в журнале «off-site copy confirmed».
3. Откройте A (302, затем 404). Создайте D.
4. Скачайте копию из хранилища (как в Варианте A) и выполните `sudo otr-restore`.
5. Ожидается: A, B, C, D → 404; `OneTimeRedirect.Web verify-db --db /var/lib/one-time-redirect/redirects.db --expect-no-active` → `active=0`; новая ссылка E работает один раз; `/etc/one-time-redirect.maintenance` и `RESTORE_LOCK` отсутствуют; `stat -c '%U:%a' redirects.db` → `redirect:600`.
6. Негативные проверки: повреждённая копия (`printf X | dd of=копия bs=1 seek=200 conv=notrunc`) → скрипт отказывает **до** остановки сервиса; проверка `/health` не проходит (временно `OTR_HEALTH_URL=http://127.0.0.1:1/health`) → сервис остановлен, 503 держится, повторный `systemctl start` ничего не запускает.

## 5. Справочник

- `OneTimeRedirect.Web backup --db P --out DIR` - снимок (используется `otr-backup snapshot`).
- `OneTimeRedirect.Web verify-db --db P [--expect-no-active]` - проверка целостности и схемы (только чтение).
- `OneTimeRedirect.Web restore-db --db P --backup F --saved-dir D [--retry]` - только работа с файлами БД; сервис не останавливает. **Не запускайте вручную при работающем сервисе**, используйте `otr-restore`.
- `OneTimeRedirect.Web restore-release --db P` - удалить `RESTORE_LOCK` (только после проверки базы).
- Переменные `OTR_*` в обоих скриптах переопределяют пути (используются тестовым контуром).
- Коды выхода CLI: 0 - успех, 1 - ошибка аргументов, 2 - ошибка операции.
