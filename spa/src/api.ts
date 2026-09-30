// Tiny typed client for the VCT Hub JSON API (C5). The token comes from /api/auth/token or MSAL sign-in.

export interface Page<T> { value: T[]; count: number; skip: number; limit: number; nextLink: string | null }
export interface Region { id: number; code: string; name: string }
export interface Team {
  id: number; name: string; tag: string; slug: string; region: string; regionId: number;
  country: string | null; countryCode: string | null; city: string | null; latitude: number | null; longitude: number | null;
  logoUrl: string | null; website: string | null; twitter: string | null; description: string | null; playerCount: number;
}
export interface Player {
  id: number; nickname: string; realName: string | null; countryCode: string | null; role: string | null; mainAgent: string | null;
  photoUrl: string | null; teamId: number | null; team: string | null; isCaptain: boolean;
  rating: number; acs: number; kd: number; adr: number; kast: number; hs: number; maps: number;
}
export interface MatchTeam { id: number; name: string; tag: string; logoUrl: string | null }
export interface Match {
  id: number; tournamentId: number; tournament: string | null; teamA: MatchTeam | null; teamB: MatchTeam | null;
  scheduledAt: string; status: 'Upcoming' | 'Live' | 'Completed'; scoreA: number; scoreB: number; bestOf: number; series: string | null;
}
export interface Me { id: string; email: string; name: string; tenant: string; plan: string; roles: string[] }
export type TeamInput = Pick<Team, 'name' | 'tag' | 'regionId' | 'country' | 'city' | 'logoUrl' | 'website' | 'twitter' | 'description'>

const TOKEN_KEY = 'vct.spa.token';
export const token = {
  get: () => { try { return localStorage.getItem(TOKEN_KEY); } catch { return null; } },
  set: (t: string | null) => { try { t ? localStorage.setItem(TOKEN_KEY, t) : localStorage.removeItem(TOKEN_KEY); } catch { /* private mode */ } },
};

export class ApiError extends Error {
  status: number; errors?: Record<string, string[]>;
  constructor(status: number, message: string, errors?: Record<string, string[]>) { super(message); this.status = status; this.errors = errors; }
}

// images seeded as "/seed-img/..." live on the API host
export const img = (url: string | null | undefined) => url ?? '';

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const t = token.get();
  if (t) headers.set('Authorization', `Bearer ${t}`);
  // nextLink is absolute; everything else is relative to the site
  const url = path.startsWith('http') ? new URL(path).pathname + new URL(path).search : path;
  const res = await fetch(url, { ...init, headers });
  if (res.status === 204) return undefined as T;
  const body = res.headers.get('Content-Type')?.includes('json') ? await res.json() : null;
  if (!res.ok) {
    if (res.status === 401) token.set(null);
    throw new ApiError(res.status, body?.detail ?? body?.title ?? `HTTP ${res.status}`, body?.errors);
  }
  return body as T;
}

export const Api = {
  regions: () => api<Region[]>('/api/regions'),
  teams: (q = '', region = '') => api<Page<Team>>(`/api/teams?limit=24&q=${encodeURIComponent(q)}&region=${region}`),
  team: (id: number) => api<Team>(`/api/teams/${id}`),
  teamPlayers: (id: number) => api<Player[]>(`/api/teams/${id}/players`),
  createTeam: (t: TeamInput) => api<Team>('/api/teams', { method: 'POST', body: JSON.stringify(t) }),
  updateTeam: (id: number, t: TeamInput) => api<Team>(`/api/teams/${id}`, { method: 'PUT', body: JSON.stringify(t) }),
  deleteTeam: (id: number) => api<void>(`/api/teams/${id}`, { method: 'DELETE' }),
  players: (sort: string, role: string) => api<Page<Player>>(`/api/players?limit=25&sort=${sort}&role=${role}`),
  matches: (status: string) => api<Page<Match>>(`/api/matches?limit=12&status=${status}`),
  next: <T,>(link: string) => api<Page<T>>(link),
  login: (email: string, password: string) =>
    api<{ accessToken: string }>('/api/auth/token', { method: 'POST', body: JSON.stringify({ email, password }) }),
  me: () => api<Me>('/api/auth/me'),
};
