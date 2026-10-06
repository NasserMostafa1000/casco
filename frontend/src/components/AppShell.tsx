import { useEffect } from 'react'
import { Link, NavLink, Navigate, Outlet, useNavigate } from 'react-router-dom'
import { CreditCard, Download, LayoutGrid, LogOut, Plus, Settings, Shield, Sparkles, Zap } from 'lucide-react'
import { useAuth } from '../lib/auth'
import { num, setLang, t } from '../lib/i18n'
import { LanguageSwitcher, Popover } from './LanguageSwitcher'
import { ThemeToggle } from './ThemeToggle'
import { Badge, Spinner } from './ui'

export function Logo({ className = '', full = false }: { className?: string; full?: boolean; dark?: boolean }) {
  if (full)
    return (
      <Link to="/" className={`flex ${className}`}>
        <img src="/logo.png" alt="Casco Studio" className="h-24 w-auto" width={320} height={331} />
      </Link>
    )
  return (
    <Link to="/" dir="ltr" className={`flex shrink-0 items-center gap-2 whitespace-nowrap text-base font-extrabold tracking-wide text-ink sm:text-lg ${className}`}>
      <img src="/logo-mark.png" alt="" className="h-9 w-9 object-contain" width={128} height={128} />
      Casco Studio
    </Link>
  )
}

export function RequireAuth() {
  const { me, loading } = useAuth()
  useEffect(() => {
    setLang('en')
  }, [])
  if (loading)
    return (
      <div className="grid min-h-screen place-items-center text-brand-600">
        <Spinner className="h-8 w-8" />
      </div>
    )
  if (!me) return <Navigate to="/login" replace />
  return <Outlet />
}

function initials(name: string) {
  return (
    name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((p) => p[0])
      .join('')
      .toUpperCase() || '?'
  )
}

export function AppShell() {
  const { me, logout } = useAuth()
  const navigate = useNavigate()
  if (!me) return null
  const isAdmin = me.user.role === 'Admin'

  const nav = [
    { to: '/app', end: true, icon: LayoutGrid, label: t('مواقعي') },
    { to: '/app/billing', end: false, icon: CreditCard, label: t('الاشتراك والنقاط') },
    { to: '/app/settings', end: false, icon: Settings, label: t('الإعدادات') },
    { to: '/download', end: false, icon: Download, label: t('تنزيل') },
    ...(isAdmin ? [{ to: '/app/admin', end: false, icon: Shield, label: t('لوحة الإدارة') }] : []),
  ]
  const signOut = () => {
    logout()
    navigate('/')
  }

  return (
    <div className="min-h-screen bg-slate-50/70 pb-20 md:pb-0">
      <header className="sticky top-0 z-30 border-b border-slate-200/70 bg-white/80 backdrop-blur-xl">
        <div className="mx-auto flex h-16 max-w-7xl items-center gap-3 px-4 sm:px-6">
          <Logo />
          <nav className="ms-6 hidden items-center gap-1 md:flex">
            {nav.map((n) => (
              <NavLink
                key={n.to}
                to={n.to}
                end={n.end}
                className={({ isActive }) =>
                  `inline-flex items-center gap-2 rounded-xl px-3 py-2 text-sm font-medium transition ${isActive ? 'bg-slate-100 text-slate-900' : 'text-slate-500 hover:text-slate-900'}`
                }
              >
                <n.icon className="h-4 w-4" />
                {n.label}
              </NavLink>
            ))}
          </nav>
          <div className="ms-auto flex items-center gap-1.5 sm:gap-2">
            <Link
              to="/app/billing"
              title={t('النقاط المتاحة')}
              className="inline-flex h-9 items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-3 text-sm font-semibold text-slate-800 shadow-sm transition hover:border-slate-300"
            >
              <Zap className="h-4 w-4 fill-amber-400 text-amber-500" />
              {num(me.credits.available)}
            </Link>
            {!me.plan.isPro && (
              <Link
                to="/app/billing"
                className="hidden h-9 items-center gap-1.5 rounded-xl bg-gradient-to-r from-brand-600 to-fuchsia-500 px-3.5 text-sm font-semibold text-white shadow-md shadow-brand-600/25 transition hover:brightness-110 sm:inline-flex"
              >
                <Sparkles className="h-4 w-4" />
                {t('ترقية')}
              </Link>
            )}
            <ThemeToggle />
            <LanguageSwitcher compact />
            <Popover
              trigger={() => (
                <button
                  type="button"
                  aria-label={t('الحساب')}
                  className="grid h-9 w-9 place-items-center rounded-full bg-gradient-to-br from-brand-500 to-fuchsia-500 text-xs font-bold text-white ring-2 ring-white transition hover:ring-brand-100"
                >
                  {initials(me.user.name)}
                </button>
              )}
            >
              {(close) => (
                <div className="w-64">
                  <div className="border-b border-slate-100 px-3 pb-3 pt-2">
                    <p className="truncate font-semibold">{me.user.name}</p>
                    <p className="truncate text-xs text-slate-500" dir="ltr">
                      {me.user.email}
                    </p>
                    <div className="mt-2">{me.plan.isPro ? <Badge color="brand">Pro</Badge> : <Badge>{t('مجاني')}</Badge>}</div>
                  </div>
                  <div className="py-1">
                    {nav.map((n) => (
                      <Link key={n.to} to={n.to} onClick={close} className="flex items-center gap-2.5 rounded-xl px-3 py-2 text-sm text-slate-700 hover:bg-slate-50">
                        <n.icon className="h-4 w-4 text-slate-400" />
                        {n.label}
                      </Link>
                    ))}
                  </div>
                  <button onClick={signOut} className="flex w-full items-center gap-2.5 rounded-xl px-3 py-2 text-sm text-red-600 hover:bg-red-50">
                    <LogOut className="flip-rtl h-4 w-4" />
                    {t('خروج')}
                  </button>
                </div>
              )}
            </Popover>
          </div>
        </div>
      </header>

      <Outlet />

      <nav className="fixed inset-x-0 bottom-0 z-30 border-t border-slate-200 bg-white/95 pb-[env(safe-area-inset-bottom)] backdrop-blur-xl md:hidden">
        <div className="mx-auto flex max-w-md items-stretch justify-around">
          {nav.slice(0, 1).map((n) => (
            <TabLink key={n.to} {...n} />
          ))}
          <Link to="/app/new" className="flex flex-1 flex-col items-center justify-center gap-0.5 py-2 text-[11px] font-medium text-slate-500">
            <span className="grid h-9 w-9 place-items-center rounded-xl bg-brand-600 text-white shadow-md shadow-brand-600/30">
              <Plus className="h-5 w-5" />
            </span>
          </Link>
          {nav.slice(1).map((n) => (
            <TabLink key={n.to} {...n} />
          ))}
        </div>
      </nav>
    </div>
  )
}

function TabLink({ to, end, icon: Icon, label }: { to: string; end: boolean; icon: typeof LayoutGrid; label: string }) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) => `flex flex-1 flex-col items-center justify-center gap-1 py-2.5 text-[11px] font-medium ${isActive ? 'text-brand-600' : 'text-slate-500'}`}
    >
      <Icon className="h-5 w-5" />
      <span className="max-w-[6rem] truncate">{label}</span>
    </NavLink>
  )
}
