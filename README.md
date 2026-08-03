# FluxVisuals AuthServer

Сервер для клиента FluxVisuals: авторизация + HWID + админка + сайт + раздача лоадера и мода.
Разворачивается на Render (бесплатно) одной кнопкой из этого репозитория.

## Как задеплоить на Render (2 минуты)

1. **Залить этот код в GitHub** (создать репозиторий):
   - Сайт github.com → New repository → имя напр. `fluxvisuals-server` → Create
   - В разделе *«…or push an existing repository»* скопировать команды
   - Выполнить их в этой папке (открой её в терминале):
     ```
     git init
     git add .
     git commit -m "init"
     git remote add origin https://github.com/ТВОЙ_ЛОГИН/fluxvisuals-server.git
     git push -u origin main
     ```

2. **Создать базу данных (обязательно!)** — на бесплатном Render диск эфемерный,
   без внешней БД все пользователи сотрутся при перезапуске:
   - render.com → New → **PostgreSQL**
   - Выбрать Free plan (1 ГБ, бесплатно)
   - После создания скопировать **Internal Database URL** (строка `postgres://...`)

3. **Подключить Web Service**:
   - render.com → New → **Web Service**
   - Connect → выбрать репозиторий `fluxvisuals-server`
   - Render сам увидит `Dockerfile`
   - В разделе **Environment** добавить переменную:
     - Key: `DATABASE_URL`
     - Value: (вставь Internal Database URL из шага 2)
   - Нажми **Create Web Service**
   - Через ~5 минут сервер поднимется

4. **Готово!** Render даст адрес вида:
   ```
   https://fluxvisuals-server.onrender.com
   ```
   - Сайт: `https://fluxvisuals-server.onrender.com/`
   - Админка: `https://fluxvisuals-server.onrender.com/admin/` (логин `admin`, пароль `admin123`)
   - Этот же адрес клиенты вводят в поле «Сервер» лоадера

## ⚠️ Про бесплатный тариф Render
Бесплатные Web-сервисы **засыпают после 15 минут без запросов** и просыпаются ~30 сек.
Если клиент при входе получил «таймаут» — это он разбудил сервер, надо нажать вход ещё раз.
Можно держать сервер «тёплым» бесплатным аптайм-монитором (напр. UptimeRobot, пинг каждые 5 мин).

## Что внутри
- `/` — страница скачивания клиента
- `/admin/` — админ-панель (юзеры, логи, статистика, URL мода)
- `/loader/FluxVisualsLoader.exe` — клиент для Windows
- `/mods/fluxvisuals-mod-1.21.11.jar` — мод (клиенты качают сами)
- `/api/...` — авторизация (JWT + HWID)

## Обновление мода
Замени `AuthServer/wwwroot/mods/fluxvisuals-mod-1.21.11.jar` на новую сборку и сделай `git push` — Render пересоберёт и обновит автоматически.

## Обновление лоадера
Замени `AuthServer/wwwroot/loader/FluxVisualsLoader.exe` на новую сборку и сделай `git push`.

## Данные пользователей
С PostgreSQL (шаг 2) база живёт отдельно от сервиса — **юзеры не теряются при перезапусках**.
Если используешь сервер без `DATABASE_URL` (например Oracle Cloud) — база лежит в файле `auth.db` рядом с сервером (постоянный диск, бэкап = копия файла).
