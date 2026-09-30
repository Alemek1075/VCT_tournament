// B7: serverless API on Supabase Edge Functions (Deno, runs on demand, no server of ours).
//
//   GET /functions/v1/h2h?a=sentinels&b=fnatic   head-to-head, recent form and Elo win chance
//   GET /functions/v1/h2h?top=10                 Elo power ranking of all teams
//
// Reads the same Postgres the ASP.NET app writes to, in a read-only transaction.
// Public data only, so there is no auth; responses are cached by the CDN for a minute.
import postgres from 'npm:postgres@3.4.5'

const sql = postgres(Deno.env.get('SUPABASE_DB_URL')!, { max: 1, prepare: false })

const cors = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, OPTIONS',
  'Access-Control-Allow-Headers': 'authorization, apikey, content-type',
}

type Team = { id: number; name: string; tag: string; slug: string; logoUrl: string | null }
type Match = { id: number; a: number; b: number; sa: number; sb: number; at: Date; event: string }

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { ...cors, 'Content-Type': 'application/json', 'Cache-Control': 'public, max-age=60' },
  })

// Plain Elo over every finished series in date order. K is bigger for a clean sweep.
function elo(matches: Match[]) {
  const r = new Map<number, number>()
  const get = (id: number) => r.get(id) ?? 1500
  for (const m of matches) {
    const ra = get(m.a), rb = get(m.b)
    const expA = 1 / (1 + 10 ** ((rb - ra) / 400))
    const winA = m.sa > m.sb ? 1 : 0
    const k = 24 * (1 + Math.abs(m.sa - m.sb) / 4)
    r.set(m.a, ra + k * (winA - expA))
    r.set(m.b, rb + k * (expA - winA))
  }
  return get
}

const chance = (ra: number, rb: number) => 1 / (1 + 10 ** ((rb - ra) / 400))

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') return new Response(null, { headers: cors })
  if (req.method !== 'GET') return json({ title: 'Method not allowed' }, 405)

  const url = new URL(req.url)
  const a = url.searchParams.get('a')?.toLowerCase()
  const b = url.searchParams.get('b')?.toLowerCase()
  const top = Math.min(Math.max(Number(url.searchParams.get('top') ?? 0) || 0, 0), 60)
  if (!top && (!a || !b)) return json({ title: 'Pass ?a=<slug>&b=<slug> or ?top=10' }, 400)

  try {
    const [teams, matches] = await sql.begin('read only', async (tx) => [
      await tx<Team[]>`select "Id" as id, "Name" as name, "Tag" as tag, "Slug" as slug, "LogoUrl" as "logoUrl" from "Teams"`,
      await tx<Match[]>`
        select m."Id" as id, m."TeamAId" as a, m."TeamBId" as b, m."ScoreA" as sa, m."ScoreB" as sb,
               m."ScheduledAt" as at, t."Name" as event
        from "Matches" m join "Tournaments" t on t."Id" = m."TournamentId"
        where m."Status" = 'Completed' and m."ScoreA" <> m."ScoreB"
        order by m."ScheduledAt"`,
    ])
    const rating = elo(matches)

    if (top) {
      const played = new Set(matches.flatMap((m) => [m.a, m.b]))
      const ranking = teams
        .filter((t) => played.has(t.id))
        .map((t) => ({ ...t, elo: Math.round(rating(t.id)) }))
        .sort((x, y) => y.elo - x.elo)
        .slice(0, top)
        .map((t, i) => ({ rank: i + 1, ...t }))
      return json({ matchesRated: matches.length, ranking })
    }

    const ta = teams.find((t) => t.slug === a), tb = teams.find((t) => t.slug === b)
    if (!ta || !tb) return json({ title: 'Team not found', detail: `Unknown slug: ${!ta ? a : b}` }, 404)

    const form = (id: number) =>
      matches.filter((m) => m.a === id || m.b === id).slice(-5).reverse()
        .map((m) => ((m.a === id) === (m.sa > m.sb) ? 'W' : 'L'))

    const meetings = matches
      .filter((m) => (m.a === ta.id && m.b === tb.id) || (m.a === tb.id && m.b === ta.id))
      .reverse()
      .map((m) => {
        const aFirst = m.a === ta.id
        return { matchId: m.id, date: m.at, event: m.event, scoreA: aFirst ? m.sa : m.sb, scoreB: aFirst ? m.sb : m.sa }
      })

    const ea = rating(ta.id), eb = rating(tb.id)
    return json({
      teamA: { ...ta, elo: Math.round(ea), form: form(ta.id) },
      teamB: { ...tb, elo: Math.round(eb), form: form(tb.id) },
      record: {
        a: meetings.filter((m) => m.scoreA > m.scoreB).length,
        b: meetings.filter((m) => m.scoreB > m.scoreA).length,
      },
      winChance: { a: +chance(ea, eb).toFixed(3), b: +chance(eb, ea).toFixed(3) },
      meetings,
    })
  } catch (e) {
    console.error(e)
    return json({ title: 'Database error' }, 500)
  }
})
