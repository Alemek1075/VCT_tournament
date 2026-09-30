import { useEffect, useState } from 'react'
import { PublicClientApplication, InteractionRequiredAuthError, type AccountInfo } from '@azure/msal-browser'
import { Api, type Match } from './api'

// F4: MSAL.js sign-in with a Microsoft account + calls to Microsoft Graph.
// The client id comes from the server at runtime (MSAL_CLIENT_ID env var), so one build works everywhere.
const scopes = ['User.Read', 'Calendars.ReadWrite']
let pca: PublicClientApplication | null = null

const ready: Promise<PublicClientApplication | null> = fetch('/api/spa-config')
  .then(r => r.json())
  .then(async (c: { msalClientId?: string }) => {
    if (!c.msalClientId) return null
    pca = new PublicClientApplication({
      auth: { clientId: c.msalClientId, authority: 'https://login.microsoftonline.com/common', redirectUri: location.origin + '/spa/' },
      cache: { cacheLocation: 'localStorage' },
    })
    await pca.initialize()
    return pca
  })
  .catch(() => null)

async function graph<T>(account: AccountInfo, path: string, init: RequestInit = {}): Promise<T> {
  let token: string
  try {
    token = (await pca!.acquireTokenSilent({ scopes, account })).accessToken
  } catch (e) {
    if (!(e instanceof InteractionRequiredAuthError)) throw e
    token = (await pca!.acquireTokenPopup({ scopes, account })).accessToken
  }
  const res = await fetch('https://graph.microsoft.com/v1.0' + path, {
    ...init, headers: { ...init.headers, Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
  })
  if (!res.ok) throw new Error(`Graph ${res.status}: ${(await res.json().catch(() => ({})))?.error?.message ?? ''}`)
  return res.status === 204 ? (undefined as T) : res.json()
}

interface GraphUser { displayName: string; mail: string | null; userPrincipalName: string; jobTitle: string | null }
interface GraphEvent { id: string; subject: string; start: { dateTime: string }; webLink: string }

export function MicrosoftPanel() {
  const [account, setAccount] = useState<AccountInfo | null>(null)
  const [user, setUser] = useState<GraphUser | null>(null)
  const [photo, setPhoto] = useState<string | null>(null)
  const [events, setEvents] = useState<GraphEvent[]>([])
  const [upcoming, setUpcoming] = useState<Match[]>([])
  const [msg, setMsg] = useState<string | null>(null)
  const [configured, setConfigured] = useState<boolean | null>(null)

  useEffect(() => {
    ready.then(p => { setConfigured(!!p); const a = p?.getAllAccounts()[0]; if (a) setAccount(a) })
    Api.matches('Upcoming').then(p => setUpcoming(p.value.slice(0, 6)))
  }, [])

  useEffect(() => {
    if (!account) return
    graph<GraphUser>(account, '/me').then(setUser).catch(e => setMsg(e.message))
    loadEvents(account)
    // profile photo is binary; personal accounts often have none
    pca!.acquireTokenSilent({ scopes, account }).then(t =>
      fetch('https://graph.microsoft.com/v1.0/me/photo/$value', { headers: { Authorization: `Bearer ${t.accessToken}` } })
        .then(r => r.ok ? r.blob() : null).then(b => b && setPhoto(URL.createObjectURL(b)))).catch(() => {})
  }, [account])

  const loadEvents = (a: AccountInfo) =>
    graph<{ value: GraphEvent[] }>(a, `/me/events?$select=subject,start,webLink&$orderby=start/dateTime desc&$top=5`)
      .then(r => setEvents(r.value)).catch(e => setMsg(e.message))

  if (configured === null) return <p className="muted">Loading…</p>
  if (!pca) return (
    <div className="form narrow">
      <h1>Microsoft account</h1>
      <p className="muted">MSAL isn't configured: set the <code>MSAL_CLIENT_ID</code> environment variable on the server to the Application (client) ID of an Entra ID app registration (SPA platform, redirect URI <code>{location.origin}/spa/</code>).</p>
    </div>
  )

  const signIn = async () => {
    try { const r = await pca!.loginPopup({ scopes, prompt: 'select_account' }); setAccount(r.account) }
    catch (e) { setMsg((e as Error).message) }
  }
  const signOut = async () => { await pca!.logoutPopup({ account: account! }); setAccount(null); setUser(null); setEvents([]) }

  const addToCalendar = async (m: Match) => {
    const start = new Date(m.scheduledAt), end = new Date(start.getTime() + m.bestOf * 60 * 60 * 1000)
    try {
      await graph(account!, '/me/events', {
        method: 'POST',
        body: JSON.stringify({
          subject: `VCT: ${m.teamA?.name} vs ${m.teamB?.name}`,
          body: { contentType: 'HTML', content: `${m.tournament} · ${m.series ?? ''}<br><a href="${location.origin}/Matches/Details/${m.id}">Open on VCT Hub</a>` },
          start: { dateTime: start.toISOString().slice(0, 19), timeZone: 'UTC' },
          end: { dateTime: end.toISOString().slice(0, 19), timeZone: 'UTC' },
          location: { displayName: m.tournament ?? 'VCT' },
          isReminderOn: true, reminderMinutesBeforeStart: 15,
        }),
      })
      setMsg(`Added "${m.teamA?.tag} vs ${m.teamB?.tag}" to your Outlook calendar.`)
      loadEvents(account!)
    } catch (e) { setMsg((e as Error).message) }
  }

  return (
    <>
      <div className="head-row">
        <h1>Microsoft account</h1>
        {account ? <button className="btn ghost" onClick={signOut}>Sign out of Microsoft</button>
                 : <button className="btn red" onClick={signIn}>Sign in with Microsoft</button>}
      </div>
      <p className="muted">MSAL.js signs you in (OAuth 2.0 + PKCE, no server involved), then the SPA calls Microsoft Graph with your token.</p>
      {msg && <p className="note">{msg}</p>}
      {user && (
        <div className="ms-card">
          {photo ? <img src={photo} alt="" /> : <span className="mark big">{user.displayName[0]}</span>}
          <div><b>{user.displayName}</b><div className="muted">{user.mail ?? user.userPrincipalName}</div>{user.jobTitle && <div className="muted small">{user.jobTitle}</div>}</div>
        </div>
      )}
      {account && (
        <div className="two">
          <section>
            <h2>Add a match to Outlook</h2>
            <div className="list">{upcoming.map(m => (
              <div key={m.id} className="match compact">
                <span>{m.teamA?.tag} vs {m.teamB?.tag}</span>
                <span className="muted small">{new Date(m.scheduledAt).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}</span>
                <button className="btn ghost" onClick={() => addToCalendar(m)}>+ Calendar</button>
              </div>))}
            </div>
          </section>
          <section>
            <h2>Your latest calendar events</h2>
            <ul className="events">{events.map(e => <li key={e.id}><a href={e.webLink} target="_blank" rel="noreferrer">{e.subject}</a> <span className="muted small">{new Date(e.start.dateTime + 'Z').toLocaleString()}</span></li>)}</ul>
            {events.length === 0 && <p className="muted">No events yet.</p>}
          </section>
        </div>
      )}
    </>
  )
}
