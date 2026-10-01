# VCT Hub

Сайт про Valorant Champions Tour 2026 у дусі valorantesports.com: розклад і результати матчів, команди, гравці, івенти, live-рахунки. Курсовий проєкт з "Web-технологій" (КНУ).

- Сайт: https://vct-hub.onrender.com
- React SPA: https://vct-hub.onrender.com/spa/
- API (Swagger): https://vct-hub.onrender.com/swagger

Стек: ASP.NET Core 10 MVC + Web API (C#), EF Core, PostgreSQL (Supabase), Redis, Elasticsearch, RabbitMQ, SignalR, React + Vite, Solidity (Hardhat), Docker, GitHub Actions, Render. Дані зібрані з vlr.gg скриптом `tools/scrape_vlr.py`.

## Запуск локально

```
cp .env.example .env    # заповни те, що треба
docker compose up -d --build
```

Сайт на http://localhost:8080, адмін для тестів `admin@vct.local` / `local-admin-123`. Без зовнішніх ключів усе теж запускається: Google-вхід, HubSpot, Gemini, Telegram, GA просто вимикаються.

Тести:

```
dotnet test                          # xUnit, автентифікація та авторизація
cd e2e && npm ci && npx playwright test   # E2E, звіт з покриттям у e2e/report
cd contracts && npx hardhat test     # смарт-контракт
```

## Де що зроблено

| Завдання | Що зроблено | Де дивитись |
|---|---|---|
| C1 MVC | Сутності Region, Team, Player, Tournament, Match, PlayerStat зі зв'язками, повний CRUD, логотипи й фото через файл або посилання | `Controllers/`, `Views/Teams` тощо, `Models/Domain.cs` |
| C2 Cloud | Render (web + Redis), база в Supabase | https://vct-hub.onrender.com |
| C3 CI/CD | GitHub Actions: build, unit, Postman, E2E, потім deploy hook на Render тільки якщо все зелене | `.github/workflows/ci.yml` |
| C4 Docker | Dockerfile (SPA + .NET у одному образі), compose зі всім стеком | `Dockerfile`, `docker-compose.yml` |
| C5 REST API | CRUD на JSON для всіх сутностей, коди 200/201/204/400/404/409, ProblemDetails | `Api/`, `/swagger` |
| QA1 API тести | Postman-колекція (38 запитів з перевірками), Newman у CI | `tests/postman` |
| C6 Landing | Сторінка команди `/team/{slug}`: один скрол, sticky-навігація, ростер, матчі, контакти завжди внизу екрана | `Views/Teams/Details.cshtml`, `css/landing.css` |
| C7 SEO | meta/OG теги, JSON-LD (SportsTeam), `sitemap.xml`, `robots.txt`, canonical | `SeoController.cs`, `Teams/Details.cshtml` |
| QA2 E2E | 15 Playwright-тестів для MVC і SPA, покриття JS через V8 (≈71% рядків) | `e2e/` |
| B1 Pagination | `skip`/`limit`/`nextLink`/`count` у всіх списках API | `Api/ApiBase.cs` |
| C8 Мапа | Leaflet: всі команди на мапі, фільтр за регіонами, кластери, лінії до місць івентів | `/Map`, `wwwroot/js/map.js` |
| F1 Графіки | Chart.js: рейтинг гравця по івентах, статистика на сторінках | `Players/Details` |
| B2 Сторонній API | Telegram-бот: підписка на команду, сповіщення про старт і кінець матчу | `Messaging/Telegram.cs` |
| B3 CRM | HubSpot: кожен новий користувач стає контактом, заявка на Team plan з `/premium` стає лідом. Йде через чергу | `Messaging/Crm.cs` |
| C9 Аналітика | Google Analytics 4 + власні події (`search`, `login`, `pickem_pick`, ...) | `Views/Shared/_Analytics.cshtml` |
| F2 Autocomplete | Select2 у формах, від 3 символів із затримкою, пошук по мільйону ranked-акаунтів (pg_trgm індекс) | `wwwroot/js/autocomplete.js`, `Api/LookupApiController.cs` |
| B4 Пошук | Elasticsearch з виправленням опечаток (спробуй "fnatik"), fallback на Postgres | `Services/SearchService.cs`, `/search`, Ctrl+K |
| C10 Connectivity | Live-рахунки п'ятьма способами: polling, adaptive polling з ETag, long poll, SSE, WebSocket, плюс сторінка порівняння з цифрами | `Live/`, `/Live/Compare/{id}` |
| C11 SignalR | Live-хаб для матчів | `Live/LiveHub`, `/hubs/live` |
| C12 Markdown editor | Блоковий редактор як у Notion, автозбереження в localStorage | `/docs/local`, `wwwroot/js/editor.js` |
| F3 Drag-n-drop | Перетягування блоків з анімацією, порядок зберігається | там само |
| C13 Синхронізація | Документ у двох браузерах оновлюється майже миттєво (SignalR) | `/docs/{id}`, `Docs/` |
| C14 Візуалізація дій | Курсори й підсвітка чужих змін | там само |
| C15 Низькорівневі API | File API (картинки на дошку), WebRTC (голос між учасниками дошки) | `wwwroot/js/board.js` |
| C16 OAuth2 | Вхід через Google, реєстрація з паролем, JWT для API і SPA | `AccountController.cs`, `Api/AuthApiController.cs` |
| F4 MSAL.js | SPA входить через Microsoft (Entra ID) і читає профіль і фото з Microsoft Graph | `spa/src/msal.tsx`, `/spa/#/microsoft` |
| B5 Тенанти | Воркспейси з інвайт-кодами, документи й дошки видно лише своєму воркспейсу | `Services/Accounts.cs`, `/account/workspace` |
| B6 Feature flags | Плани Free/Premium через Microsoft.FeatureManagement, сторінка `/premium` і CSV-експорт лише для Premium | `PremiumController.cs` |
| QA3 Auth тести | 21 xUnit-тест: логін, lockout, JWT, ролі, ізоляція тенантів, флаги | `tests/VctHub.Tests/AuthTests.cs` |
| F5 SPA | React + Vite над тим самим API: матчі, команди (CRUD через JWT), гравці | `spa/` |
| B7 Serverless | Supabase Edge Function `h2h`: Elo, форма, шанс на перемогу; показується на сторінці матчу | `serverless/` |
| C18 Shared board | Спільна дошка для малювання й схем з кімнатами | `/board`, `Board/` |
| F6 Canvas | Aim-тренер на canvas з таблицею рекордів | `/aim`, `wwwroot/js/aim.js` |
| B8 Черги | RabbitMQ: події матчів для Telegram, синхронізація з CRM | `Messaging/MessageBus.cs` |
| B9 Кеш API | Output cache у Redis для GET-запитів API | `Services/ApiCache.cs` |
| C19 Статика | Фото й логотипи в Supabase Storage (CDN) | `Services/FileStorage.cs` |
| C20 Web3 | Pick'em на Solidity: прогнози на матчі, очки за правильні, таблиця лідерів; сторінка на web3.js + MetaMask | `contracts/`, `/pickem` |
| C21 ШІ-агент | Чат з Gemini, відповідь стрімиться через SSE, видно виклики інструментів | `/agent`, `Agent/` |
| C22 MCP | MCP-сервер на `/mcp`: 5 інструментів на читання (`search`, `get_team`, `get_player`, `list_matches`, `top_players`) і 1 на запис (`create_strategy_doc`) | `Agent/VctMcpServer.cs` |
| C17, QA4 | Окремо, не в цьому репозиторії | |

## Дизайн

Своя дизайн-система в дусі сайтів Riot: токени, типографіка й анімації описані в `DESIGN.md`. MVC і SPA мають однаковий вигляд.

## Змінні середовища

Основні: `ConnectionStrings__Default`, `JWT_KEY`, `REDIS_URL`, `ELASTIC_URL`, `RABBITMQ_URL`. Опційні інтеграції: `GOOGLE_CLIENT_ID`/`GOOGLE_CLIENT_SECRET`, `MSAL_CLIENT_ID`, `GEMINI_API_KEY`, `TELEGRAM_BOT_TOKEN`, `HUBSPOT_TOKEN`, `GA_MEASUREMENT_ID`, `SUPABASE_PROJECT_REF`/`SUPABASE_SECRET_KEY`, `PICKEM_ADDRESS`/`PICKEM_CHAIN_ID`.

Дані про матчі взяті з vlr.gg. VCT Hub не пов'язаний з Riot Games.
