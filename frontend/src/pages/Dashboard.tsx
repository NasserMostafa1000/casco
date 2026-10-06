import { useEffect, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { ArrowUpRight, Crown, Database, Globe, LayoutGrid, PenLine, Plus, Sparkles, Trash2, Upload, Zap } from 'lucide-react'
import { Alert, Badge, Empty, Spinner } from '../components/ui'
import { del, errorMessage, get, type ProjectSummary } from '../lib/api'
import { useAuth } from '../lib/auth'
import { formatDate, num, t } from '../lib/i18n'

const templateNames = (): Record<string, string> => ({
  landing: t('موقع تعريفي'),
  courses: t('منصة كورسات'),
  ads: t('منصة إعلانات'),
  store: t('متجر إلكتروني'),
  booking: t('حجز مواعيد'),
})

const gradients = ['from-brand-500 to-violet-500', 'from-fuchsia-500 to-rose-500', 'from-sky-500 to-cyan-400', 'from-amber-500 to-orange-500', 'from-emerald-500 to-teal-400']

export default function Dashboard() {
  const { me, refresh } = useAuth()
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null)
  const [error, setError] = useState('')
  const [deleting, setDeleting] = useState<string | null>(null)

  const remove = async (id: string) => {
    if (!confirm(t('حذف الموقع نهائياً مع كل بياناته؟ لا يمكن التراجع.'))) return
    setError('')
    setDeleting(id)
    try {
      await del(`/api/projects/${id}`)
      setProjects((list) => list?.filter((p) => p.id !== id) ?? null)
      await refresh()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setDeleting(null)
    }
  }

  useEffect(() => {
    get<ProjectSummary[]>('/api/projects').then(setProjects).catch((e) => setError(errorMessage(e)))
  }, [])

  if (!me) return null
  const canCreate = me.limits.projectsCount < me.limits.maxProjects
  const names = templateNames()

  return (
    <main className="mx-auto max-w-7xl px-4 py-8 sm:px-6 sm:py-10">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <p className="text-sm text-slate-500">{t('أهلاً {name} 👋', { name: me.user.name })}</p>
          <h1 className="mt-1 text-3xl font-extrabold tracking-tight text-ink">{t('مواقعك')}</h1>
        </div>
        {canCreate ? (
          <div className="flex flex-wrap gap-2">
            <Link
              to="/app/react/new"
              className="inline-flex h-11 items-center justify-center gap-2 rounded-xl border border-slate-200 bg-white px-5 text-sm font-semibold text-ink shadow-sm transition hover:bg-slate-50"
            >
              <Upload className="h-4 w-4" />
              {t('رفع تطبيق React')}
            </Link>
            <Link
              to="/app/new"
              className="inline-flex h-11 items-center justify-center gap-2 rounded-xl bg-ink px-5 text-sm font-semibold text-white shadow-sm transition hover:bg-slate-800"
            >
              <Plus className="h-4 w-4" />
              {t('موقع جديد')}
            </Link>
          </div>
        ) : (
          <Link to="/app/billing" className="inline-flex h-11 items-center justify-center gap-2 rounded-xl bg-gradient-to-r from-brand-600 to-fuchsia-500 px-5 text-sm font-semibold text-white">
            <Sparkles className="h-4 w-4" />
            {t('ترقية لإنشاء مواقع أكثر')}
          </Link>
        )}
      </div>

      {me.plan.isPro && me.plan.daysLeft !== null && me.plan.daysLeft <= 5 && (
        <div className="mt-6">
          <Alert tone="warning">
            {t('اشتراكك ينتهي خلال {n} يوم.', { n: me.plan.daysLeft })}{' '}
            <Link to="/app/billing" className="font-semibold underline">
              {t('جدّد الآن')}
            </Link>
          </Alert>
        </div>
      )}

      <UsageBoard />

      <div className="mt-6 grid grid-cols-3 gap-2 sm:mt-8 sm:gap-4">
        <Stat icon={<Zap className="h-5 w-5 fill-amber-400 text-amber-500" />} label={t('النقاط المتاحة')} value={num(me.credits.available)}>
          <Link to="/app/billing" className="text-xs font-semibold text-brand-600 hover:underline">
            {me.plan.isPro ? t('شحن نقاط إضافية') : t('اشترك في Pro')}
          </Link>
        </Stat>
        <Stat icon={<Crown className="h-5 w-5 text-brand-600" />} label={t('خطتك')} value={me.plan.isPro ? 'Pro' : t('مجاني')}>
          <span className="text-xs text-slate-500">
            {me.plan.isPro ? (me.plan.periodEnd ? t('حتى {date}', { date: formatDate(me.plan.periodEnd) }) : '') : t('موقع صفحة واحدة مضمون')}
          </span>
        </Stat>
        <Stat icon={<LayoutGrid className="h-5 w-5 text-sky-600" />} label={t('المواقع')} value={`${me.limits.projectsCount} / ${me.limits.maxProjects}`}>
          <div className="h-1.5 w-full overflow-hidden rounded-full bg-slate-100">
            <div className="h-full rounded-full bg-sky-500" style={{ width: `${Math.min(100, (me.limits.projectsCount / Math.max(1, me.limits.maxProjects)) * 100)}%` }} />
          </div>
        </Stat>
      </div>

      {error && (
        <div className="mt-6">
          <Alert tone="error">{error}</Alert>
        </div>
      )}
      {!projects && !error && (
        <div className="mt-16 grid place-items-center text-brand-600">
          <Spinner className="h-8 w-8" />
        </div>
      )}
      {projects && projects.length === 0 && (
        <div className="mt-8">
          <Empty title={t('لا توجد مواقع بعد')} icon={<Sparkles className="h-6 w-6" />}>
            <p>{t('اكتب وصف موقعك وسنجهزه لك خلال دقيقة.')}</p>
            <Link to="/app/new" className="mt-5 inline-flex h-10 items-center gap-2 rounded-xl bg-brand-600 px-4 text-sm font-semibold text-white hover:bg-brand-700">
              <Plus className="h-4 w-4" />
              {t('ابدأ موقعك الأول')}
            </Link>
          </Empty>
        </div>
      )}

      {projects && projects.length > 0 && (
        <div className="mt-8 grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          {projects.map((p, i) => {
            const react = p.templateKey === 'react'
            const href = react ? `/app/react/${p.id}` : `/app/p/${p.id}`
            const days = hostingLine(p)
            return (
            <div key={p.id} className="group flex flex-col overflow-hidden rounded-2xl border border-slate-200/80 bg-white shadow-sm transition hover:-translate-y-0.5 hover:shadow-xl hover:shadow-slate-900/5">
              <Link to={href} className={`relative grid h-36 place-items-center bg-gradient-to-br ${gradients[i % gradients.length]}`}>
                <div className="bg-grid absolute inset-0 opacity-30 invert" />
                <span className="relative text-5xl font-extrabold text-white/90 drop-shadow">{p.name.trim().charAt(0).toUpperCase()}</span>
                <span className="absolute end-3 top-3">
                  {p.suspended ? <Badge color="red">{t('موقوف')}</Badge> : react && p.publishedAt && p.hostingOnline === false ? <Badge color="red">{t('متوقف')}</Badge> : p.publishedAt ? <Badge color="green">{t('منشور')}</Badge> : <Badge>{t('مسودة')}</Badge>}
                </span>
              </Link>
              <div className="flex flex-1 flex-col p-4">
                <h3 className="truncate text-base font-semibold text-ink">{p.name}</h3>
                <p className="mt-0.5 text-xs text-slate-500">
                  {(react ? t('تطبيق React') : names[p.templateKey]) ?? p.templateKey} • {t('آخر تعديل: {date}', { date: formatDate(p.updatedAt) })}
                </p>
                {days && <p className="mt-1 text-xs font-semibold text-slate-600">{days}</p>}
                {p.siteUrl && p.hostingOnline !== false && (
                  <a href={p.siteUrl} target="_blank" rel="noreferrer" className="mt-2 inline-flex items-center gap-1 truncate text-xs text-brand-600 hover:underline" dir="ltr">
                    <Globe className="h-3.5 w-3.5 shrink-0" />
                    <span className="truncate">{p.siteUrl.replace(/^https?:\/\//, '')}</span>
                    <ArrowUpRight className="h-3 w-3 shrink-0" />
                  </a>
                )}
                <div className="mt-4 flex flex-wrap gap-2">
                  <Link to={href} className="inline-flex h-9 flex-1 items-center justify-center gap-1.5 rounded-xl bg-ink text-sm font-semibold text-white transition hover:bg-slate-800">
                    <PenLine className="h-4 w-4" />
                    {react ? t('إدارة التطبيق') : t('تعديل')}
                  </Link>
                  {!react && (
                  <Link
                    to={`/app/p/${p.id}/data`}
                    className="inline-flex h-9 flex-1 items-center justify-center gap-1.5 rounded-xl border border-slate-200 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
                  >
                    <Database className="h-4 w-4" />
                    {t('البيانات')}
                  </Link>
                  )}
                  <button
                    type="button"
                    disabled={deleting === p.id}
                    onClick={() => void remove(p.id)}
                    className="inline-flex h-9 items-center justify-center gap-1.5 rounded-xl border border-red-200 px-3 text-sm font-semibold text-red-600 transition hover:bg-red-50 disabled:opacity-50"
                  >
                    <Trash2 className="h-4 w-4" />
                    {t('حذف')}
                  </button>
                </div>
              </div>
            </div>
            )
          })}
          {canCreate && (
            <Link
              to="/app/new"
              className="grid min-h-64 place-items-center rounded-2xl border-2 border-dashed border-slate-200 text-slate-400 transition hover:border-brand-300 hover:bg-brand-50/40 hover:text-brand-600"
            >
              <span className="flex flex-col items-center gap-2 text-sm font-semibold">
                <Plus className="h-8 w-8" />
                {t('موقع جديد')}
              </span>
            </Link>
          )}
        </div>
      )}
    </main>
  )
}

function hostingLine(p: ProjectSummary): string | null {
  if (p.templateKey === 'react') {
    if (p.hostingDaysLeft != null && p.hostingDaysLeft > 0) return t('فاضل {n} يوم من الاستضافة', { n: p.hostingDaysLeft })
    if (p.hostingTier) return t('الاستضافة انتهت والموقع متوقف')
    return t('بانتظار دفع 10$ في السنة')
  }
  if (p.hostingDaysLeft != null && p.hostingDaysLeft > 0) return t('فاضل {n} يوم من الاستضافة', { n: p.hostingDaysLeft })
  return null
}

type UsageDay = { date: string; requests: number; tokens: number; credits: number }

function UsageBoard() {
  const [days, setDays] = useState<UsageDay[] | null>(null)
  useEffect(() => {
    get<{ days: UsageDay[] }>('/api/me/usage')
      .then((body) => setDays(body.days))
      .catch(() => setDays([]))
  }, [])
  if (!days || days.length === 0) return null
  const max = Math.max(1, ...days.map((d) => d.credits))
  const today = days[days.length - 1]
  const totals = days.reduce(
    (sum, day) => ({
      requests: sum.requests + day.requests,
      tokens: sum.tokens + day.tokens,
      credits: sum.credits + day.credits,
    }),
    { requests: 0, tokens: 0, credits: 0 },
  )
  const radius = 42
  const ring = 2 * Math.PI * radius
  const filled = ring * (today.credits / max)
  return (
    <section className="mt-8 rounded-3xl border border-slate-200/80 bg-white p-5 shadow-sm shadow-slate-900/[.03] sm:p-6">
      <div>
        <p className="text-sm text-slate-500">{t('آخر 14 يوم')}</p>
        <h2 className="mt-1 text-xl font-extrabold tracking-tight text-ink">{t('الاستخدام اليومي')}</h2>
      </div>
      <div className="mt-5 grid items-center gap-6 md:grid-cols-[148px_1fr]">
        <svg viewBox="0 0 120 120" className="mx-auto h-36 w-36" role="img" aria-label={t('اليوم')}>
          <polygon points="60,6 108,33 108,87 60,114 12,87 12,33" fill="none" stroke="#e2e8f0" strokeWidth="1.5" />
          <circle cx="60" cy="60" r={radius} fill="none" stroke="#f1f5f9" strokeWidth="8" />
          <circle
            cx="60"
            cy="60"
            r={radius}
            fill="none"
            stroke="url(#usage-ring)"
            strokeWidth="8"
            strokeLinecap="round"
            strokeDasharray={`${filled} ${ring}`}
            transform="rotate(-90 60 60)"
          />
          <text x="60" y="57" textAnchor="middle" fill="#0f172a" fontSize="18" fontWeight="800">
            {num(today.credits)}
          </text>
          <text x="60" y="74" textAnchor="middle" fill="#94a3b8" fontSize="9">
            {t('اليوم')}
          </text>
          <defs>
            <linearGradient id="usage-ring" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stopColor="#7c3aed" />
              <stop offset="1" stopColor="#d946ef" />
            </linearGradient>
          </defs>
        </svg>
        <svg viewBox="0 0 420 148" className="h-40 w-full" role="img" aria-label={t('الاستخدام اليومي')}>
          {days.map((day, index) => {
            const height = day.credits > 0 ? Math.max(12, (day.credits / max) * 92) : 4
            const x = 10 + index * 29
            const y = 108 - height
            const width = 16
            return (
              <g key={day.date}>
                <polygon
                  points={`${x},${y + 8} ${x + width / 2},${y} ${x + width},${y + 8} ${x + width},${y + height} ${x},${y + height}`}
                  fill={index === days.length - 1 ? 'url(#usage-bar)' : '#ddd6fe'}
                />
                <text x={x + width / 2} y="124" textAnchor="middle" fill="#94a3b8" fontSize="8">
                  {day.date.slice(8)}
                </text>
              </g>
            )
          })}
          <defs>
            <linearGradient id="usage-bar" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0" stopColor="#7c3aed" />
              <stop offset="1" stopColor="#d946ef" />
            </linearGradient>
          </defs>
        </svg>
      </div>
      <div className="mt-4 grid grid-cols-3 gap-2">
        <UsageTotal label={t('عدد الطلبات')} value={num(totals.requests)} />
        <UsageTotal label={t('الرموز المستخدمة')} value={num(totals.tokens)} />
        <UsageTotal label={t('النقاط المستخدمة')} value={num(totals.credits)} />
      </div>
    </section>
  )
}

function UsageTotal({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-2xl bg-slate-50 px-3 py-3">
      <p className="truncate text-xs text-slate-500">{label}</p>
      <p className="mt-1 truncate text-lg font-extrabold tracking-tight text-ink">{value}</p>
    </div>
  )
}

function Stat({ icon, label, value, children }: { icon: ReactNode; label: string; value: string; children?: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col gap-1.5 rounded-2xl border border-slate-200/80 bg-white p-3 shadow-sm shadow-slate-900/[.03] sm:gap-3 sm:p-5">
      <div className="flex items-center justify-between gap-2">
        <span className="truncate text-xs text-slate-500 sm:text-sm">{label}</span>
        <span className="hidden h-9 w-9 shrink-0 place-items-center rounded-xl bg-slate-50 ring-1 ring-slate-100 sm:grid">{icon}</span>
      </div>
      <p className="truncate text-lg font-extrabold tracking-tight text-ink sm:text-2xl">{value}</p>
      <div className="hidden sm:block">{children}</div>
    </div>
  )
}
