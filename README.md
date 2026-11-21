
HEAD
Полноценный сайт FluxVisuals: лендинг, регистрация, личный кабинет, бесплатный доступ к клиенту, админ-панель и раздача клиента. Сервер самодостаточный, деплой на Render одной кнопкой.

## Роли
- **admin** — доступ в админ-панель, управление юзерами и доступом
- **free** — бесплатный доступ, выдаётся сразу при регистрации; можно скачать и запустить клиент
- **client** — старый платный доступ (по сроку или пожизненно), работает так же
- **user** — без доступа, выставляется только снятием доступа в админке

## Как задеплоить на Render

1. **Залить в GitHub**: github.com → New repository (`fluxvisuals-server`) → команды из подсказки:
   ```
   git remote add origin https://github.com/ТВОЙ_ЛОГИН/fluxvisuals-server.git
   git push -u origin main
   ```
2. **База**: render.com → New → **PostgreSQL** (Free) → скопируй **Internal Database URL**
3. **Сервис**: render.com → New → **Web Service** → репозиторий → Render найдёт `Dockerfile` → в Environment добавь `DATABASE_URL` = (строка из шага 2) → Create Web Service
4. Готово, адрес вида `https://fluxvisuals-server.onrender.com`
   - Сайт: `/` · Логин: `/login.html` · Регистрация: `/register.html`
   - Кабинет: `/account.html` · Админка: `/admin/` (логин `admin`, пароль из БД)

## Оплата

Клиент бесплатный, приём денег отключён. Тарифы, кнопки покупки и модалка оплаты убраны с сайта. Код `PaymentController` и провайдер CrystalPay оставлены в бэкенде без изменений — если позже появятся документы и легальный мерчант, интерфейс можно вернуть.

## Где лежат файлы клиента

Большие файлы — в **GitHub Releases** (в git их нет, там лимит):
- **Лоадер** `FluxVisualsLoader.exe` → релиз `sours3s/FluxVisuals` (`Loader:DownloadUrl` в appsettings)
- **Мод** `fluxvisuals-mod-1.21.11.jar` → там же (`Mod:DownloadUrl`)

Клиент скачивается **с сайта**: сервер стримит файл с GitHub через `/api/download/loader` авторизованным аккаунтам с ролью `free`, `client` или `admin` (без редиректа).

## Обновление лоадера/мода
1. Залей новый `.exe`/`.jar` в GitHub Release (перетащи поверх)
2. Если менялся код сервера — `git push`, Render пересоберёт сам

## ⚠️ Render Free
Сервис **засыпает после 15 мин без запросов** (~30 сек на пробуждение). Чтобы держать тёплым — аптайм-монитор (UptimeRobot) пингует каждые 5 мин.
=======
ea8448227a10e8f2959602572bd34cf3a4143c70
