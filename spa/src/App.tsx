import { useEffect, useLayoutEffect, useRef, useState, type FormEvent, type PointerEvent, type ReactNode } from 'react'
import { HashRouter, Link, NavLink, Route, Routes, useLocation, useNavigate, useParams } from 'react-router-dom'
import { Api, ApiError, img, type Match, type Page, type Player, type Region, type Team, type TeamInput } from './api'
import { AuthProvider, useAuth } from './auth'
import { MicrosoftPanel } from './msal'

export default function App() {
  return (
    <AuthProvider>
      <HashRouter>
        <Header />
        <main className="wrap">
          <PageFade>
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/teams" element={<Teams />} />
            <Route path="/teams/new" element={<TeamEdit />} />
            <Route path="/teams/:id" element={<TeamView />} />
            <Route path="/teams/:id/edit" element={<TeamEdit />} />
            <Route path="/players" element={<Players />} />
            <Route path="/login" element={<Login />} />
            <Route path="/microsoft" element={<MicrosoftPanel />} />
            <Route path="*" element={<p className="muted">Not found. <Link to="/">Home</Link></p>} />
          </Routes>
          </PageFade>
        </main>
        <footer className="wrap foot muted">
          React SPA over the VCT Hub REST API · <a href="/">server-rendered site</a> · <a href="/swagger">API docs</a>
        </footer>
      </HashRouter>
    </AuthProvider>
  )
}

function Header() {
  const { me, logout } = useAuth()
  return (
    <header className="top">
      <div className="wrap top-in">
        <Link to="/" className="brand"><span className="mark">V</span>VCT<b>HUB</b> <small>SPA</small></Link>
        <nav>
          <NavLink to="/" end>Matches</NavLink>
          <NavLink to="/teams">Teams</NavLink>
          <NavLink to="/players">Players</NavLink>
          <NavLink to="/microsoft">Microsoft</NavLink>
        </nav>
        <div className="me">
          {me ? <><span title={me.tenant}>{me.name}</span><button className="btn ghost" onClick={logout}>Sign out</button></>
              : <Link className="btn red" to="/login">Sign in</Link>}
        </div>
      </div>
    </header>
  )
}

/** Re-mounts on every route change so the page plays its enter animation. */
function PageFade({ children }: { children: ReactNode }) {
  const { pathname } = useLocation()
  return <div key={pathname} className="page">{children}</div>
}

/** Filter chips with one red pill that glides to the selected chip. */
function Chips<T extends string>({ options, value, onChange, label = o => o }: { options: readonly T[], value: T, onChange: (v: T) => void, label?: (o: T) => string }) {
  const box = useRef<HTMLDivElement>(null)
  const [pill, setPill] = useState<{ x: number, y: number, w: number, h: number } | null>(null)
  useLayoutEffect(() => {
    const measure = () => {
      const on = box.current?.querySelector<HTMLElement>('.chip.on')
      setPill(on ? { x: on.offsetLeft, y: on.offsetTop, w: on.offsetWidth, h: on.offsetHeight } : null)
    }
    measure()
    addEventListener('resize', measure)
    return () => removeEventListener('resize', measure)
  }, [value, options.length])
  return (
    <div className={pill ? 'chips measured' : 'chips'} ref={box}>
      {pill && <span className="pill" style={{ transform: `translate(${pill.x}px, ${pill.y}px)`, width: pill.w, height: pill.h }} />}
      {options.map(o => <button key={o} className={o === value ? 'chip on' : 'chip'} onClick={() => onChange(o)}>{label(o)}</button>)}
    </div>
  )
}

/** Moves the card spotlight to the cursor (CSS reads --mx / --my). */
function spotlight(e: PointerEvent<HTMLElement>) {
  const card = (e.target as HTMLElement).closest<HTMLElement>('.card')
  if (!card) return
  const r = card.getBoundingClientRect()
  card.style.setProperty('--mx', `${e.clientX - r.left}px`)
  card.style.setProperty('--my', `${e.clientY - r.top}px`)
}

const Skeleton = ({ n, kind }: { n: number, kind: 'row' | 'tile' }) => <>{Array.from({ length: n }, (_, i) => <div key={i} className={`skeleton ${kind}`} />)}</>

/** Hook: one page of a list + "load more" through nextLink (B1). */
function usePaged<T>(load: () => Promise<Page<T>>, deps: unknown[]) {
  const [items, setItems] = useState<T[]>([])
  const [next, setNext] = useState<string | null>(null)
  const [total, setTotal] = useState(0)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    setBusy(true); setError(null)
    load().then(p => { if (live) { setItems(p.value); setNext(p.nextLink); setTotal(p.count) } })
      .catch(e => live && setError(e.message)).finally(() => live && setBusy(false))
    return () => { live = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)
  const more = async () => {
    if (!next) return
    setBusy(true)
    const p = await Api.next<T>(next)
    setItems(x => [...x, ...p.value]); setNext(p.nextLink); setBusy(false)
  }
  return { items, next, total, busy, error, more }
}

function Home() {
  const [status, setStatus] = useState<'Live' | 'Upcoming' | 'Completed'>('Upcoming')
  const { items, next, busy, more } = usePaged<Match>(() => Api.matches(status), [status])
  return (
    <>
      <h1>Matches</h1>
      <Chips options={['Live', 'Upcoming', 'Completed'] as const} value={status} onChange={setStatus} label={s => s === 'Completed' ? 'Results' : s} />
      <div className="list">
        {busy && items.length === 0 && <Skeleton n={6} kind="row" />}
        {items.map((m, i) => (
          <a key={m.id} className="match" href={`/Matches/Details/${m.id}`} style={{ '--i': i % 20 } as React.CSSProperties}>
            <span className="when">{new Date(m.scheduledAt).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}</span>
            <span className="t"><img src={img(m.teamA?.logoUrl)} alt="" />{m.teamA?.name}</span>
            <span className="score">{m.status === 'Upcoming' ? 'vs' : `${m.scoreA} : ${m.scoreB}`}</span>
            <span className="t r">{m.teamB?.name}<img src={img(m.teamB?.logoUrl)} alt="" /></span>
            <span className="muted small">{m.tournament}</span>
          </a>
        ))}
        {!busy && items.length === 0 && <p className="muted pad">Nothing here right now.</p>}
      </div>
      {next && <button className="btn ghost more" disabled={busy} onClick={more}>{busy ? 'Loading…' : 'Load more'}</button>}
    </>
  )
}

function Teams() {
  const [q, setQ] = useState('')
  const [region, setRegion] = useState('')
  const [regions, setRegions] = useState<Region[]>([])
  const { me } = useAuth()
  useEffect(() => { Api.regions().then(setRegions) }, [])
  const [debounced, setDebounced] = useState('')
  useEffect(() => { const t = setTimeout(() => setDebounced(q), 250); return () => clearTimeout(t) }, [q])
  const { items, next, total, busy, more, error } = usePaged<Team>(() => Api.teams(debounced, region), [debounced, region])
  return (
    <>
      <div className="head-row">
        <h1>Teams <small className="muted">{total}</small></h1>
        {me && <Link className="btn red" to="/teams/new">+ New team</Link>}
      </div>
      <div className="filters">
        <input placeholder="Search name or tag" value={q} onChange={e => setQ(e.target.value)} />
        <Chips options={['', ...regions.filter(r => r.code !== 'international').map(r => r.code)]} value={region} onChange={setRegion}
          label={c => regions.find(r => r.code === c)?.name ?? 'All'} />
      </div>
      {error && <p className="err">{error}</p>}
      <div className="grid" onPointerMove={spotlight}>
        {busy && items.length === 0 && <Skeleton n={8} kind="tile" />}
        {items.map(t => (
          <Link key={t.id} to={`/teams/${t.id}`} className="card" data-tag={t.tag}>
            <img src={img(t.logoUrl)} alt="" />
            <div><b>{t.name}</b><div className="muted small">{t.tag} · {t.region}</div></div>
          </Link>
        ))}
      </div>
      {next && <button className="btn ghost more" disabled={busy} onClick={more}>{busy ? 'Loading…' : `Load more (${items.length}/${total})`}</button>}
    </>
  )
}

function TeamView() {
  const { id } = useParams()
  const nav = useNavigate()
  const { me, isAdmin } = useAuth()
  const [team, setTeam] = useState<Team | null>(null)
  const [players, setPlayers] = useState<Player[]>([])
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    Api.team(+id!).then(setTeam).catch(e => setError(e.message))
    Api.teamPlayers(+id!).then(setPlayers).catch(() => {})
  }, [id])
  if (error) return <p className="err">{error}</p>
  if (!team) return <p className="muted">Loading…</p>
  const remove = async () => {
    if (!confirm(`Delete ${team.name}?`)) return
    try { await Api.deleteTeam(team.id); nav('/teams') } catch (e) { setError((e as ApiError).status === 409 ? 'This team still has matches.' : (e as Error).message) }
  }
  return (
    <>
      <div className="team-head">
        <img src={img(team.logoUrl)} alt="" />
        <div>
          <div className="kicker">{team.region} · {team.country}</div>
          <h1>{team.name}</h1>
          <div className="muted">{team.tag}{team.city ? ` · ${team.city}` : ''}</div>
        </div>
        <div className="actions">
          <a className="btn ghost" href={`/team/${team.slug}`}>Landing page</a>
          {me && <Link className="btn ghost" to={`/teams/${team.id}/edit`}>Edit</Link>}
          {isAdmin && <button className="btn ghost" onClick={remove}>Delete</button>}
        </div>
      </div>
      <h2>Roster</h2>
      <table>
        <thead><tr><th>Player</th><th>Role</th><th className="n">Rating</th><th className="n">ACS</th><th className="n">K:D</th><th className="n">ADR</th></tr></thead>
        <tbody>{players.map(p => (
          <tr key={p.id}><td><b>{p.nickname}</b> <span className="muted small">{p.realName}</span></td><td className="muted">{p.role}</td>
            <td className="n"><b>{p.rating}</b></td><td className="n">{p.acs}</td><td className="n">{p.kd}</td><td className="n">{p.adr}</td></tr>))}
        </tbody>
      </table>
    </>
  )
}

function TeamEdit() {
  const { id } = useParams()
  const nav = useNavigate()
  const { me } = useAuth()
  const [regions, setRegions] = useState<Region[]>([])
  const [form, setForm] = useState<TeamInput>({ name: '', tag: '', regionId: 1, country: null, city: null, logoUrl: null, website: null, twitter: null, description: null })
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [msg, setMsg] = useState<string | null>(null)
  useEffect(() => {
    Api.regions().then(setRegions)
    if (id) Api.team(+id).then(t => setForm({ name: t.name, tag: t.tag, regionId: t.regionId, country: t.country, city: t.city, logoUrl: t.logoUrl, website: t.website, twitter: t.twitter, description: t.description }))
  }, [id])
  if (!me) return <p>You need to <Link to="/login">sign in</Link> to edit teams.</p>
  const set = (k: keyof TeamInput) => (e: { target: { value: string } }) => setForm(f => ({ ...f, [k]: k === 'regionId' ? +e.target.value : e.target.value || null }))
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setErrors({}); setMsg(null)
    try {
      const t = id ? await Api.updateTeam(+id, form) : await Api.createTeam(form)
      nav(`/teams/${t.id}`)
    } catch (err) {
      const ae = err as ApiError
      setErrors(ae.errors ?? {}); setMsg(`${ae.status}: ${ae.message}`)
    }
  }
  const field = (k: keyof TeamInput, label: string, type = 'text') => (
    <label>{label}<input type={type} value={(form[k] as string) ?? ''} onChange={set(k)} />
      {Object.entries(errors).filter(([ek]) => ek.toLowerCase() === k.toLowerCase()).flatMap(([, v]) => v).map(x => <span key={x} className="err">{x}</span>)}</label>)
  return (
    <form className="form" onSubmit={submit}>
      <h1>{id ? 'Edit team' : 'New team'}</h1>
      {msg && <p className="err">{msg}</p>}
      {field('name', 'Name')}{field('tag', 'Tag')}
      <label>Region<select value={form.regionId} onChange={set('regionId')}>{regions.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}</select></label>
      {field('country', 'Country')}{field('city', 'City')}{field('logoUrl', 'Logo URL', 'url')}{field('website', 'Website', 'url')}{field('twitter', 'Twitter / X', 'url')}
      <label>Description<textarea rows={4} value={form.description ?? ''} onChange={set('description')} /></label>
      <div className="actions"><button className="btn red">Save</button><button type="button" className="btn ghost" onClick={() => nav(-1)}>Cancel</button></div>
    </form>
  )
}

function Players() {
  const [sort, setSort] = useState('rating')
  const [role, setRole] = useState('')
  const { items, next, busy, more } = usePaged<Player>(() => Api.players(sort, role), [sort, role])
  const th = (k: string, label: string) => <th className={'n sortable' + (sort === k ? ' on' : '')} onClick={() => setSort(k)}>{label}</th>
  return (
    <>
      <h1>Players</h1>
      <Chips options={['', 'Duelist', 'Initiator', 'Controller', 'Sentinel']} value={role} onChange={setRole} label={r => r || 'All roles'} />
      <table>
        <thead><tr><th>#</th><th>Player</th><th>Team</th>{th('rating', 'Rating')}{th('acs', 'ACS')}{th('kd', 'K:D')}{th('adr', 'ADR')}</tr></thead>
        <tbody>{items.map((p, i) => (
          <tr key={p.id}><td className="muted">{i + 1}</td><td><b>{p.nickname}</b> <span className="muted small">{p.role}</span></td><td>{p.team}</td>
            <td className="n"><b>{p.rating}</b></td><td className="n">{p.acs}</td><td className="n">{p.kd}</td><td className="n">{p.adr}</td></tr>))}
        </tbody>
      </table>
      {next && <button className="btn ghost more" disabled={busy} onClick={more}>{busy ? 'Loading…' : 'Load more'}</button>}
    </>
  )
}

function Login() {
  const { login, me } = useAuth()
  const nav = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  if (me) return <p>Signed in as <b>{me.name}</b> ({me.tenant}, {me.plan}). <Link to="/teams">Go to teams</Link></p>
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setError(null)
    try { await login(email, password); nav('/teams') } catch (err) { setError((err as Error).message) }
  }
  return (
    <form className="form narrow" onSubmit={submit}>
      <h1>Sign in</h1>
      <p className="muted">Same account as the main site. The SPA gets a JWT and sends it as a Bearer token.</p>
      {error && <p className="err">{error}</p>}
      <label>Email<input type="email" autoComplete="email" value={email} onChange={e => setEmail(e.target.value)} required /></label>
      <label>Password<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required /></label>
      <button className="btn red">Sign in</button>
      <p className="muted small">Signed up with Google on the main site? Set a password there first, or use the <Link to="/microsoft">Microsoft</Link> tab.</p>
    </form>
  )
}
