# Serverless API (B7)

`h2h` is a Supabase Edge Function (Deno, runs on demand on Supabase's edge, we don't host it).
It reads the same Postgres the ASP.NET app writes to and computes an Elo rating from every
finished series.

Base URL: `https://ypuamnpalobfhbrllobg.supabase.co/functions/v1/h2h`

| Request | Result |
|---|---|
| `GET ?a=paper-rex&b=sentinels` | both teams with Elo and last 5 results, win chance, previous meetings |
| `GET ?top=10` | Elo power ranking |
| `GET` (no params) | `400` problem JSON |
| `GET ?a=nope&b=fnatic` | `404` problem JSON |

The match page (`/Matches/Details/{id}`) calls it from the browser (CORS is open) and shows the
"Head to head" block. If the function is down, the block just stays hidden.

Deploy (needs the Supabase CLI and a personal access token):

```
npx supabase functions deploy h2h --project-ref ypuamnpalobfhbrllobg --no-verify-jwt
```

`--no-verify-jwt` because it only serves public, read-only data. The SQL runs inside a
`read only` transaction, so the function can't change anything even if it had a bug.
`SUPABASE_DB_URL` is injected by Supabase, nothing secret lives in the code.
