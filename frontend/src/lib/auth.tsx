import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { get, post, revokeToken, tokenStore, type Me } from './api'
import { tiktokClick } from './tiktok'

interface AuthState {
  me: Me | null
  loading: boolean
  refresh: () => Promise<void>
  login: (email: string, password: string) => Promise<void>
  register: (name: string, email: string, password: string, turnstileToken?: string | null) => Promise<string>
  external: (provider: 'google' | 'apple', idToken: string, name?: string) => Promise<{ id: string; created: boolean }>
  logout: () => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null)
  const [loading, setLoading] = useState(true)

  const refresh = useCallback(async () => {
    if (!tokenStore.get()) {
      setMe(null)
      setLoading(false)
      return
    }
    try {
      setMe(await get<Me>('/api/me'))
    } catch {
      setMe(null)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const value = useMemo<AuthState>(
    () => ({
      me,
      loading,
      refresh,
      login: async (email, password) => {
        const previous = tokenStore.get()
        const r = await post<{ token: string }>('/api/auth/login', { email, password })
        tokenStore.set(r.token)
        if (previous && previous !== r.token) revokeToken(previous)
        await refresh()
      },
      register: async (name, email, password, turnstileToken) => {
        const previous = tokenStore.get()
        const click = tiktokClick()
        const r = await post<{ token: string; id: string }>('/api/auth/register', { name, email, password, turnstileToken, ttclid: click.ttclid, ttp: click.ttp, referralCode: storedReferral() })
        tokenStore.set(r.token)
        localStorage.removeItem('casco_ref')
        if (previous && previous !== r.token) revokeToken(previous)
        await refresh()
        return r.id
      },
      external: async (provider, idToken, name) => {
        const previous = tokenStore.get()
        const click = tiktokClick()
        const r = await post<{ token: string; id: string; created: boolean }>('/api/auth/external', { provider, idToken, name, ttclid: click.ttclid, ttp: click.ttp, referralCode: storedReferral() })
        tokenStore.set(r.token)
        if (r.created) localStorage.removeItem('casco_ref')
        if (previous && previous !== r.token) revokeToken(previous)
        await refresh()
        return { id: r.id, created: r.created }
      },
      logout: () => {
        const token = tokenStore.get()
        tokenStore.clear()
        setMe(null)
        revokeToken(token)
      },
    }),
    [me, loading, refresh],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

function storedReferral() {
  try {
    const ref = localStorage.getItem('casco_ref')
    return ref && /^[a-z0-9]{6,12}$/.test(ref) ? ref : undefined
  } catch {
    return undefined
  }
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
