import { Link, useNavigate } from 'react-router-dom'
import { CreditCard, LogOut } from 'lucide-react'
import { Badge } from '../components/ui'
import { useAuth } from '../lib/auth'
import { t } from '../lib/i18n'

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

export default function Settings() {
  const { me, logout } = useAuth()
  const navigate = useNavigate()
  if (!me) return null

  return (
    <main className="mx-auto max-w-2xl px-4 py-8 sm:px-6 sm:py-10">
      <h1 className="text-3xl font-extrabold tracking-tight text-ink">{t('الإعدادات')}</h1>
      <section className="mt-6 rounded-3xl border border-slate-200/80 bg-white p-6 shadow-sm shadow-slate-900/[.03]">
        <p className="text-sm text-slate-500">{t('الملف الشخصي')}</p>
        <div className="mt-4 flex items-center gap-4">
          <span className="grid h-16 w-16 place-items-center rounded-2xl bg-gradient-to-br from-brand-500 to-fuchsia-500 text-lg font-bold text-white">
            {initials(me.user.name)}
          </span>
          <div className="min-w-0">
            <p className="truncate text-xl font-extrabold text-ink">{me.user.name}</p>
            <p className="truncate text-sm text-slate-500" dir="ltr">
              {me.user.email}
            </p>
            <div className="mt-2">{me.plan.isPro ? <Badge color="brand">Pro</Badge> : <Badge>{t('مجاني')}</Badge>}</div>
          </div>
        </div>
        <div className="mt-6 flex flex-wrap gap-2">
          <Link
            to="/app/billing"
            className="inline-flex h-10 items-center gap-2 rounded-xl border border-slate-200 px-4 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
          >
            <CreditCard className="h-4 w-4" />
            {t('الاشتراك والنقاط')}
          </Link>
          <button
            type="button"
            onClick={() => {
              logout()
              navigate('/')
            }}
            className="inline-flex h-10 items-center gap-2 rounded-xl border border-red-200 px-4 text-sm font-semibold text-red-600 transition hover:bg-red-50"
          >
            <LogOut className="h-4 w-4" />
            {t('خروج')}
          </button>
        </div>
      </section>
    </main>
  )
}
