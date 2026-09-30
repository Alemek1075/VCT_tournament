# E2E tests (QA2)

Playwright tests that drive a real browser through the MVC site and the React SPA.

```
docker compose up -d --build      # from the repo root
cd e2e && npm ci && npx playwright install chromium
npx playwright test               # BASE_URL=https://... to test another host
npx monocart show-report report/index.html
```

On Windows without the Playwright chromium download: `PW_CHANNEL=msedge npx playwright test`.

## What is covered

| Area | Flow |
|---|---|
| C1 MVC | navbar pages open, team filter by region + search, team landing page |
| C1 CRUD | admin signs in, server validation errors, create → edit → delete a team |
| Auth | anonymous user is redirected to sign in, wrong password shows an error |
| F2 | Select2 team picker asks for 3 letters, then loads options from `/api/lookup/teams` |
| Search | Elasticsearch full-text search, typo tolerance ("fnatik" → FNATIC) |
| C8 | Leaflet map renders team markers |
| F5 SPA | match tabs, teams paging through `nextLink`, live search, players sort |
| F5 CRUD | JWT sign in, API validation (problem+json) shown per field, create → edit → delete |

## Coverage

Each test records V8 JS coverage in Chromium; monocart-reporter merges it and maps the SPA bundle
back to the `.tsx` sources through the Vite source maps. Last local run, 15/15 tests:

| File | Lines | Functions |
|---|---|---|
| spa/src/App.tsx | 100% | 85% |
| spa/src/api.ts | 100% | 100% |
| spa/src/auth.tsx | 89% | 100% |
| spa/src/msal.tsx | 0% | 8% |
| wwwroot/js/autocomplete.js | 100% | 88% |
| wwwroot/js/site.js | 100% | 100% |
| wwwroot/js/map.js | 66% | 48% |
| **total** | **71%** | **64%** |

Not covered on purpose: `msal.tsx` needs a real Microsoft login (can't be automated without test
tenants). The realtime pages (live, docs, board, aim) are not in this suite yet. Server-side C#
coverage comes from the xUnit job in CI.

The E2E run found one real bug: the SPA "New team" form sent `""` for empty URL fields, which the
API's `[Url]` validation rejected. Empty optional fields now start as `null`.
