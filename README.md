
HEAD
Полноценный сайт FluxVisuals: лендинг с ценами + регистрация + личный кабинет + покупка доступа + админ-панель + раздача клиента. Сервер самодостаточный, деплой на Render одной кнопкой.

## Роли
- **admin** — доступ в админ-панель, управление юзерами и доступом
- **client** — купил доступ (по сроку или пожизненно), может скачать и запускать клиент
- **user** — обычный зарегистрированный, без доступа (не скачает и не запустит)

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
   - Кабинет: `/account.html` · Админка: `/admin/` (логин `admin`, пароль `admin123` — смени!)

## Настройка оплаты (CrystalPay)

В `appsettings.json` → `Payment`:
```json
"Payment": {
  "Provider": "crystalpay",
  "CrystalPay": { "MerchantId": "fluxvisuals", "Secret": "985dfd73b3692fd6a3d55fdbb418649195792914", "Salt": "43e84a591ef7d724b084606911a482051310320c" }
}
```
Пока ключи пустые — кнопки покупки показывают «Оплата не настроена». Как только впишешь ключи и задеплоишь — покупка заработает: юзер платит криптой, вебхук сам выдаёт доступ.

Тарифы — в `appsettings.json` → `Plans` (название, дни/пожизненно, цена).

## Где лежат файлы клиента

Большие файлы — в **GitHub Releases** (в git их нет, там лимит):
- **Лоадер** `FluxVisualsLoader.exe` → релиз `sours3s/FluxVisuals` (`Loader:DownloadUrl` в appsettings)
- **Мод** `fluxvisuals-mod-1.21.11.jar` → там же (`Mod:DownloadUrl`)

Клиент скачивается **с сайта**: сервер стримит файл с GitHub через `/api/download/loader` только авторизованным клиентам с доступом (без редиректа).

## Обновление лоадера/мода
1. Залей новый `.exe`/`.jar` в GitHub Release (перетащи поверх)
2. Если менялся код сервера — `git push`, Render пересоберёт сам

## ⚠️ Render Free
Сервис **засыпает после 15 мин без запросов** (~30 сек на пробуждение). Чтобы держать тёплым — аптайм-монитор (UptimeRobot) пингует каждые 5 мин.
=======
ea8448227a10e8f2959602572bd34cf3a4143c70
