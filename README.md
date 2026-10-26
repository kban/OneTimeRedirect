# One-Time Redirect Service

ASP.NET Core 10 (Minimal API + Razor Pages) + SQLite. Админ-панель: `/admin` (Cookie Authentication).

## Конфигурация (переменные окружения, не хранить в репозитории)

- `REDIRECT_ADMIN_PASSWORD_HASH` - обязательно. Хеш пароля (PBKDF2-SHA256). Получить:
  `dotnet run --project src/OneTimeRedirect.Web -- hash-password` (или `OneTimeRedirect.Web hash-password` на сервере; пароль минимум 12 символов).
- `REDIRECT_ADMIN_USER` - логин администратора, по умолчанию `admin`.
- `REDIRECT_DB_PATH` - путь к SQLite (рядом создаётся каталог `keys/` с ключами Data Protection - сохраняйте его между перезапусками, права только для пользователя сервиса).
- `PUBLIC_BASE_URL=https://ваш-домен` - HTTPS-origin без пути и query. Вне Development **обязателен** (иначе сервис не стартует); ссылки никогда не строятся из заголовка `Host`.
- `AllowedHosts` - список хостов через `;`. Вне Development, если не задан или `*`, разрешён только хост из `PUBLIC_BASE_URL` (запрос с чужим `Host` -> 400; локальная проверка: `curl -H "Host: ваш-домен" http://127.0.0.1:5000/health`).
- `ADMIN_LOGIN_MAX_FAILURES` - число попыток входа с одного клиента (IPv4-адрес или IPv6 /64) за 15 минут до блокировки (по умолчанию 5). Попытка резервируется атомарно до проверки пароля, поэтому параллельные POST не обходят лимит.
- `ADMIN_LOGIN_MAX_CONCURRENT_CHECKS` - сколько проверок PBKDF2 выполняется одновременно на весь сервис (по умолчанию 2). Ожидание слота не дольше 5 с, затем 503 (попытка не засчитывается).
- `PUBLIC_RATE_LIMIT_PER_MINUTE` - лимит запросов к `/r/{token}` с одного клиента в минуту (по умолчанию 60). Отклонённый запрос получает 429 и ссылку не гасит.

Cookie помечены `Secure`: админка работает только по HTTPS (за Caddy; приложение читает `X-Forwarded-*` только от loopback). Для локальной разработки используйте HTTPS-профиль (`dotnet dev-certs https`).

## Админ-панель

`/admin` (список, фильтр по статусу и поиск, пагинация по 20), `/admin/create`, `/admin/links/{id}/edit`, `/admin/login`.
Ссылка показывается один раз сразу после создания; в БД хранится только SHA-256 токена.

## Admin API (миграция с `X-Admin-Key`)

Заголовок `X-Admin-Key` и `REDIRECT_ADMIN_KEY` **удалены**: единственный способ доступа - сессионная cookie после входа в `/admin/login`.
Для скриптов: войти через форму (cookie-jar), получить `GET /admin/api/csrf` и передавать `X-CSRF-TOKEN` в POST/PUT.

- `GET /admin/api/csrf`, `POST /admin/api/links`, `GET /admin/api/links`, `PUT /admin/api/links/{id}`, `POST /admin/api/links/{id}/revoke`

Без cookie - `401`, без токена на изменяющем запросе - `400`.
Устаревший `tests/test_concurrency.py` (использовал `X-Admin-Key`) удалён; конкурентность покрыта тестом `ConcurrentGets_AtMostOneRedirect`.

## Публичный маршрут

`GET /r/{token}`: первый запрос - `302`, далее `404`. `HEAD` всегда `404` и не расходует токен.
Любая ошибка SQLite при погашении (BUSY, LOCKED, FULL, IOERR, READONLY, CANTOPEN, ...) -> `503` с `Retry-After`, без `Location`; в журнал пишется только код SQLite.
Известное ограничение: GET от сканеров (Telegram/email) тоже погашает ссылку.

Токены не попадают в журналы: категория `Microsoft.AspNetCore.Hosting.Diagnostics` отключена (её logging scope содержит `RequestPath`), Caddy вырезает `/r/{token}` из своих логов ошибок (`deploy/Caddyfile`).

## Тесты

`dotnet test` - интеграционные тесты на временных SQLite-базах.

## Публикация и развёртывание (Debian, systemd, Caddy)

```powershell
dotnet publish src/OneTimeRedirect.Web -p:PublishProfile=linux-x64   # -> deploy/publish/ (в .gitignore)
```

Framework-dependent linux-x64: на сервере нужен `aspnetcore-runtime-10.0` (пакеты packages.microsoft.com; если рантайм не в `/usr/share/dotnet`, добавьте в unit `Environment=DOTNET_ROOT=...`). Секретов в публикации нет: только `appsettings.json` без паролей.

```bash
sudo useradd -r -M -s /usr/sbin/nologin redirect
sudo mkdir -p /opt/one-time-redirect && sudo cp -r deploy/publish/. /opt/one-time-redirect/
sudo chmod 755 /opt/one-time-redirect/OneTimeRedirect.Web        # после копирования с Windows бит x теряется
sudo install -m 600 -o root -g root /dev/null /etc/one-time-redirect.env
sudoedit /etc/one-time-redirect.env   # REDIRECT_ADMIN_PASSWORD_HASH=..., PUBLIC_BASE_URL=https://ваш-домен
sudo install -m 644 deploy/one-time-redirect.service /etc/systemd/system/
sudo chmod -R go-rwx /var/lib/one-time-redirect 2>/dev/null       # для данных, созданных до UMask=0077
sudo systemctl daemon-reload && sudo systemctl enable --now one-time-redirect
sudo cp deploy/Caddyfile /etc/caddy/Caddyfile && sudo caddy validate --config /etc/caddy/Caddyfile && sudo systemctl reload caddy
```

`StateDirectory=` создаёт `/var/lib/one-time-redirect` (0700, владелец `redirect`), `UMask=0077` делает БД, `-wal`/`-shm` и ключи Data Protection доступными только пользователю сервиса. Хеш пароля на сервере: `/opt/one-time-redirect/OneTimeRedirect.Web hash-password` (скрытый ввод; не передавайте пароль аргументом или через `echo` - он останется в истории shell).

## Локальный запуск на Windows (HTTPS)

Секреты хранятся вне репозитория в user-secrets (`UserSecretsId` в csproj), профиль запуска - `Properties/launchSettings.json`.

```powershell
dotnet dev-certs https --trust            # один раз; без этого браузер покажет предупреждение
cd src/OneTimeRedirect.Web
$hash = (Read-Host "Admin password (min 12)" -AsSecureString | ConvertFrom-SecureString -AsPlainText | dotnet run --no-launch-profile -- hash-password)
dotnet user-secrets set REDIRECT_ADMIN_PASSWORD_HASH $hash
dotnet user-secrets set REDIRECT_DB_PATH "$env:LOCALAPPDATA\OneTimeRedirect\dev\redirects.db"
dotnet user-secrets set PUBLIC_BASE_URL "https://localhost:7443"
dotnet run --launch-profile https         # https://localhost:7443/admin  (логин: admin)
```

## Резервное копирование и восстановление

См. [deploy/DISASTER_RECOVERY.md](deploy/DISASTER_RECOVERY.md): `deploy/backup.sh`, `deploy/restore.sh`, systemd timer, внешнее хранилище, runbook и проверка на тестовом окружении. Восстановление всегда отзывает все не использованные ссылки.

## Целевые URL и отображение одноразовых ссылок

- Целевой URL: `http://` или `https://`; без схемы добавляется `https://`; остальные схемы, логин/пароль в URL, управляющие символы и обратные слеши отклоняются. Публичная ссылка `/r/{token}` всегда строится из HTTPS `PUBLIC_BASE_URL`.
- В `/admin` у активных ссылок есть столбец «Одноразовая ссылка» с кнопкой «Копировать». Токен хранится зашифрованным (`EncryptedToken`, Data Protection, purpose `OneTimeRedirect.LinkTokens.v1`) рядом с SHA-256 для погашения. Для старых записей, при потерянных ключах Data Protection или повреждённом значении показывается «Недоступна для копирования»; для Used/Revoked/Expired - «Недействительна».
- Схема SQLite мигрируется автоматически при старте (`ALTER TABLE ... ADD COLUMN EncryptedToken`), данные не меняются. **Каталог `keys/` теперь нужно сохранять вместе с БД** (см. deploy/DISASTER_RECOVERY.md).

## Удаление ссылок и создание

- В `/admin` у ссылок Used/Revoked/Expired есть «Удалить»: страница подтверждения (GET только показывает вопрос), удаление - POST с antiforgery. Условие статуса проверяется в самом SQL `DELETE ... RETURNING Id`, активную ссылку удалить нельзя. Строка удаляется полностью (включая TokenHash и EncryptedToken); старые резервные копии могут её содержать, но восстановление всё равно отзывает все активные ссылки.
- Создание: Post/Redirect/Get (303 на `/admin`), новая ссылка первой в списке с кнопкой «Копировать». Форма несёт одноразовый `SubmissionId`: повторная отправка той же формы не создаёт дубликат (в памяти процесса; после ошибки БД та же форма отправляется повторно).
