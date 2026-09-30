import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { Api, token, type Me } from './api'

interface AuthState {
  me: Me | null
  loading: boolean
  login: (email: string, password: string) => Promise<void>
  logout: () => void
  isAdmin: boolean
}

const Ctx = createContext<AuthState>(null!)
export const useAuth = () => useContext(Ctx)

/** VCT Hub account (JWT from /api/auth/token). Microsoft sign-in (MSAL) lives in msal.tsx. */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null)
  const [loading, setLoading] = useState(!!token.get())

  const refresh = useCallback(async () => {
    if (!token.get()) { setMe(null); setLoading(false); return }
    try { setMe(await Api.me()) } catch { setMe(null) } finally { setLoading(false) }
  }, [])

  useEffect(() => { refresh() }, [refresh])

  const login = async (email: string, password: string) => {
    const r = await Api.login(email, password)
    token.set(r.accessToken)
    await refresh()
  }
  const logout = () => { token.set(null); setMe(null) }

  return <Ctx.Provider value={{ me, loading, login, logout, isAdmin: !!me?.roles.includes('Admin') }}>{children}</Ctx.Provider>
}
