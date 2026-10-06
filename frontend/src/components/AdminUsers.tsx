import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { del, errorMessage, get, post } from '../lib/api'
import { usd, usePlans } from '../lib/plans'
import { Alert, Badge, Button, Card, Input, Modal, Spinner, Textarea, formatDate, formatDateTime } from './ui'

interface UserRow {
  id: string
  email: string
  name: string
  role: string
  createdAt: string
  google: boolean
  apple: boolean
  plan: string
  periodEnd: string | null
  credits: number
  projects: number
  publishedSites: number
  stoppedSites: number
  suspendedSites: number
  paid: number
  payments: number
  lastPaymentAt: string | null
  lastActivityAt: string | null
  aiCost: number
}

interface UsersPage {
  total: number
  page: number
  pageSize: number
  counts: Record<string, number>
  items: UserRow[]
}

interface SiteHosting {
  requiredTier: 'static' | 'backend'
  tier: string | null
  paidUntil: string | null
  active: boolean
  enough: boolean
  inGrace: boolean
  daysLeft: number
}

interface AdminSite {
  id: string
  name: string
  description: string
  templateKey: string
  template: string | null
  slug: string
  createdAt: string
  updatedAt: string
  publishedAt: string | null
  customDomain: string | null
  customDomainVerified: boolean
  siteUrl: string | null
  previewUrl: string | null
  pages: number
  features: string[]
  hosting: SiteHosting
  suspension: { reason: string | null; at: string | null; by: string | null } | null
  versions: number
  tasks: number
  aiCost: number
}

interface AdminPayment {
  id: string
  kind: string
  interval: string | null
  topupPackId: string | null
  topupCredits: number | null
  hostingTier: string | null
  projectName: string | null
  amount: number
  currency: string
  provider: string
  status: string
  isTest: boolean
  createdAt: string
  completedAt: string | null
}

interface UserDetail {
  user: { id: string; email: string; name: string; role: string; createdAt: string; freeSiteUsed: boolean; password: boolean; google: boolean; apple: boolean }
  plan: { plan: string; interval: string | null; periodEnd: string | null }
  credits: number
  totals: { paid: number; payments: number; aiCost: number; creditsUsed: number; tasks: number }
  sites: AdminSite[]
  payments: AdminPayment[]
}

const filters: { key: string; label: string }[] = [
  { key: 'all', label: 'الكل' },
  { key: 'built', label: 'بنوا موقع' },
  { key: 'paid', label: 'دفعوا' },
  { key: 'built_unpaid', label: 'بنوا ولم يدفعوا' },
  { key: 'unpaid', label: 'لم يدفعوا' },
  { key: 'no_site', label: 'بدون موقع' },
  { key: 'pro', label: 'مشتركين Pro' },
  { key: 'stopped', label: 'مواقعهم متوقفة' },
  { key: 'suspended', label: 'موقوفين إدارياً' },
]

const sorts: { key: string; label: string }[] = [
  { key: 'newest', label: 'الأحدث تسجيلاً' },
  { key: 'activity', label: 'آخر نشاط' },
  { key: 'paid', label: 'الأكثر دفعاً' },
  { key: 'sites', label: 'الأكثر مواقع' },
  { key: 'cost', label: 'الأعلى تكلفة AI' },
]

const paymentKinds: Record<string, string> = { subscription: 'اشتراك Pro', hosting: 'استضافة', topup: 'شحن نقاط' }
const paymentStatuses: Record<string, { label: string; color: 'green' | 'amber' | 'red' | 'slate' }> = {
  completed: { label: 'مكتمل', color: 'green' },
  pending: { label: 'لم يكتمل', color: 'amber' },
  failed: { label: 'فشل', color: 'red' },
  canceled: { label: 'ألغي', color: 'slate' },
}
const featureNames: Record<string, string> = { auth: 'حسابات', db: 'قاعدة بيانات', store: 'متجر', bookings: 'حجوزات', courses: 'كورسات', ads: 'إعلانات' }

export default function AdminUsers() {
  const [params, setParams] = useSearchParams()
  const userId = params.get('user')

  const update = (patch: Record<string, string | null>) =>
    setParams(
      (p) => {
        const next = new URLSearchParams(p)
        for (const [k, v] of Object.entries(patch)) {
          if (v === null || v === '') next.delete(k)
          else next.set(k, v)
        }
        return next
      },
      { replace: false },
    )

  return userId ? <UserView id={userId} onBack={() => update({ user: null })} /> : <UserList params={params} update={update} />
}

function UserList({ params, update }: { params: URLSearchParams; update: (patch: Record<string, string | null>) => void }) {
  const filter = params.get('filter') ?? 'all'
  const sort = params.get('sort') ?? 'newest'
  const page = Number(params.get('page') ?? '1')
  const q = params.get('q') ?? ''
  const [search, setSearch] = useState(q)
  const [data, setData] = useState<UsersPage | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    setError('')
    const qs = new URLSearchParams({ filter, sort, page: String(page), search: q })
    get<UsersPage>(`/api/admin/users?${qs}`)
      .then(setData)
      .catch((e) => setError(errorMessage(e)))
  }, [filter, sort, page, q])

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2">
        {filters.map((f) => (
          <button
            key={f.key}
            onClick={() => update({ filter: f.key === 'all' ? null : f.key, page: null })}
            className={`rounded-full px-3 py-1.5 text-sm font-semibold ${filter === f.key ? 'bg-brand-600 text-white' : 'bg-white text-slate-600 ring-1 ring-slate-200 hover:bg-slate-50'}`}
          >
            {f.label} <span className={filter === f.key ? 'text-white/80' : 'text-slate-400'}>{data?.counts[f.key] ?? '·'}</span>
          </button>
        ))}
      </div>

      <div className="flex flex-wrap items-end gap-2">
        <form
          className="flex flex-1 gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            update({ q: search.trim() || null, page: null })
          }}
        >
          <Input className="min-w-64" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="بحث بالبريد أو الاسم أو اسم الموقع أو الدومين" />
          <Button type="submit" variant="secondary">
            بحث
          </Button>
        </form>
        <select value={sort} onChange={(e) => update({ sort: e.target.value === 'newest' ? null : e.target.value, page: null })} className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm">
          {sorts.map((s) => (
            <option key={s.key} value={s.key}>
              {s.label}
            </option>
          ))}
        </select>
      </div>

      {error && <Alert tone="error">{error}</Alert>}
      {!data ? (
        <Spinner className="h-6 w-6 text-brand-600" />
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-slate-500">
              <tr>
                <th className="p-3 text-start">المستخدم</th>
                <th className="p-3 text-start">المواقع</th>
                <th className="p-3 text-start">الدفع</th>
                <th className="p-3 text-start">الخطة</th>
                <th className="p-3 text-start">النقاط</th>
                <th className="p-3 text-start">تكلفة AI</th>
                <th className="p-3 text-start">آخر نشاط</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 && (
                <tr>
                  <td colSpan={7} className="p-6 text-center text-slate-500">
                    لا يوجد مستخدمون هنا
                  </td>
                </tr>
              )}
              {data.items.map((u) => (
                <tr key={u.id} onClick={() => update({ user: u.id })} className="cursor-pointer border-t border-slate-100 hover:bg-brand-50/40">
                  <td className="p-3">
                    <div className="font-semibold">
                      {u.name} {u.role === 'Admin' && <Badge color="brand">Admin</Badge>}
                    </div>
                    <div className="text-xs text-slate-400" dir="ltr">
                      {u.email}
                    </div>
                    <div className="text-xs text-slate-400">سجل {formatDate(u.createdAt)}</div>
                  </td>
                  <td className="p-3">
                    {u.projects === 0 ? (
                      <span className="text-slate-400">لم يبنِ</span>
                    ) : (
                      <div className="flex flex-wrap gap-1">
                        <Badge>{u.projects} موقع</Badge>
                        {u.publishedSites > 0 && <Badge color="green">{u.publishedSites} منشور</Badge>}
                        {u.stoppedSites > 0 && <Badge color="red">{u.stoppedSites} متوقف</Badge>}
                        {u.suspendedSites > 0 && <Badge color="red">{u.suspendedSites} موقوف إدارياً</Badge>}
                      </div>
                    )}
                  </td>
                  <td className="p-3">
                    {u.paid > 0 ? (
                      <>
                        <Badge color="green">دفع {usd(u.paid)}</Badge>
                        <div className="text-xs text-slate-400">
                          {u.payments} عملية • {formatDate(u.lastPaymentAt)}
                        </div>
                      </>
                    ) : (
                      <Badge color={u.projects > 0 ? 'amber' : 'slate'}>لم يدفع</Badge>
                    )}
                  </td>
                  <td className="p-3">
                    {u.plan === 'pro' ? <Badge color="brand">Pro</Badge> : <Badge>مجاني</Badge>}
                    {u.plan === 'pro' && u.periodEnd && <div className="text-xs text-slate-400">حتى {formatDate(u.periodEnd)}</div>}
                  </td>
                  <td className="p-3">{u.credits.toLocaleString('ar')}</td>
                  <td className="p-3" dir="ltr">
                    ${Number(u.aiCost).toFixed(4)}
                  </td>
                  <td className="p-3 text-xs text-slate-500">{formatDate(u.lastActivityAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {data && pages > 1 && (
        <div className="flex items-center justify-center gap-2 text-sm">
          <Button variant="secondary" disabled={page <= 1} onClick={() => update({ page: String(page - 1) })}>
            السابق
          </Button>
          <span>
            صفحة {page} من {pages} ({data.total} مستخدم)
          </span>
          <Button variant="secondary" disabled={page >= pages} onClick={() => update({ page: String(page + 1) })}>
            التالي
          </Button>
        </div>
      )}
    </div>
  )
}

type Dialog = { type: 'pro' } | { type: 'credits' } | { type: 'hosting'; site: AdminSite } | { type: 'suspend'; site: AdminSite } | null

function UserView({ id, onBack }: { id: string; onBack: () => void }) {
  const [data, setData] = useState<UserDetail | null>(null)
  const [error, setError] = useState('')
  const [msg, setMsg] = useState('')
  const [dialog, setDialog] = useState<Dialog>(null)

  const load = useCallback(
    () =>
      get<UserDetail>(`/api/admin/users/${id}`)
        .then(setData)
        .catch((e) => setError(errorMessage(e))),
    [id],
  )

  useEffect(() => {
    void load()
  }, [load])

  const done = (text: string) => {
    setDialog(null)
    setMsg(text)
    void load()
  }

  if (error) return <Alert tone="error">{error}</Alert>
  if (!data) return <Spinner className="h-6 w-6 text-brand-600" />

  const { user, plan, totals } = data
  const logins = [user.password && 'بريد وكلمة مرور', user.google && 'Google', user.apple && 'Apple'].filter(Boolean).join(' • ')

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <button onClick={onBack} className="text-sm text-brand-600 hover:underline">
            → رجوع لقائمة المستخدمين
          </button>
          <h2 className="mt-1 text-xl font-extrabold">
            {user.name} {user.role === 'Admin' && <Badge color="brand">Admin</Badge>}
          </h2>
          <p className="text-sm text-slate-500" dir="ltr">
            {user.email}
          </p>
          <p className="text-xs text-slate-400">
            سجل {formatDateTime(user.createdAt)} • {logins}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" onClick={() => setDialog({ type: 'credits' })}>
            إضافة نقاط
          </Button>
          <Button onClick={() => setDialog({ type: 'pro' })}>{plan.plan === 'pro' ? 'تمديد Pro' : 'تفعيل Pro'}</Button>
        </div>
      </div>

      {msg && <Alert tone="success">{msg}</Alert>}

      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        <Mini label="الخطة" value={plan.plan === 'pro' ? 'Pro' : 'مجاني'} sub={plan.plan === 'pro' ? `${plan.interval === 'yearly' ? 'سنوي' : 'شهري'} حتى ${formatDate(plan.periodEnd)}` : undefined} />
        <Mini label="إجمالي ما دفعه" value={usd(totals.paid)} sub={`${totals.payments} عملية`} tone={totals.paid > 0 ? 'good' : data.sites.length > 0 ? 'warn' : undefined} />
        <Mini label="رصيد النقاط" value={data.credits.toLocaleString('ar')} sub={`استخدم ${totals.creditsUsed.toLocaleString('ar')}`} />
        <Mini label="تكلفة AI عليك" value={`$${totals.aiCost.toFixed(4)}`} sub={`${totals.tasks} طلب`} />
        <Mini label="المواقع" value={data.sites.length} sub={`${data.sites.filter((s) => s.publishedAt).length} منشور`} />
      </div>

      <div>
        <h3 className="text-lg font-bold">مواقعه</h3>
        {data.sites.length === 0 ? (
          <p className="mt-2 text-sm text-slate-500">لم يبنِ أي موقع بعد{user.freeSiteUsed ? ' (بنى موقعه المجاني ثم حذفه)' : ''}.</p>
        ) : (
          <div className="mt-3 grid gap-4 lg:grid-cols-2">
            {data.sites.map((s) => (
              <SiteCard
                key={s.id}
                site={s}
                onHosting={() => setDialog({ type: 'hosting', site: s })}
                onSuspend={() => setDialog({ type: 'suspend', site: s })}
                onUnsuspend={async () => {
                  if (!confirm(`إلغاء إيقاف "${s.name}"؟ سيعود الموقع للعمل حسب حالة استضافته.`)) return
                  try {
                    await post(`/api/admin/projects/${s.id}/unsuspend`)
                    done('تم إلغاء الإيقاف')
                  } catch (e) {
                    alert(errorMessage(e))
                  }
                }}
                onDelete={async () => {
                  if (!confirm(`حذف "${s.name}" نهائياً مع كل بياناته من السيرفر؟ لا يمكن التراجع.`)) return
                  try {
                    await del(`/api/admin/projects/${s.id}`)
                    done('تم حذف الموقع وكل بياناته')
                  } catch (e) {
                    alert(errorMessage(e))
                  }
                }}
              />
            ))}
          </div>
        )}
      </div>

      <div>
        <h3 className="text-lg font-bold">المدفوعات</h3>
        {data.payments.length === 0 ? (
          <p className="mt-2 text-sm text-slate-500">لم يحاول الدفع أبداً.</p>
        ) : (
          <Card className="mt-3 overflow-x-auto p-0">
            <table className="w-full text-sm">
              <thead className="bg-slate-50 text-slate-500">
                <tr>
                  <th className="p-3 text-start">التاريخ</th>
                  <th className="p-3 text-start">النوع</th>
                  <th className="p-3 text-start">المبلغ</th>
                  <th className="p-3 text-start">الحالة</th>
                  <th className="p-3 text-start">الطريقة</th>
                </tr>
              </thead>
              <tbody>
                {data.payments.map((p) => (
                  <tr key={p.id} className="border-t border-slate-100">
                    <td className="p-3 text-xs">{formatDateTime(p.createdAt)}</td>
                    <td className="p-3">
                      {paymentKinds[p.kind] ?? p.kind}
                      <div className="text-xs text-slate-400">
                        {p.kind === 'hosting' && `${p.hostingTier === 'backend' ? 'باك إند' : 'عادي'} • ${p.projectName ?? 'موقع محذوف'}`}
                        {p.kind === 'topup' && p.topupCredits != null && `${p.topupCredits.toLocaleString('ar')} نقطة`}
                        {p.interval && p.kind !== 'topup' && ` • ${p.interval === 'yearly' ? 'سنوي' : 'شهري'}`}
                      </div>
                    </td>
                    <td className="p-3 font-semibold" dir="ltr">
                      {usd(p.amount)}
                    </td>
                    <td className="p-3">
                      <Badge color={paymentStatuses[p.status]?.color ?? 'slate'}>{paymentStatuses[p.status]?.label ?? p.status}</Badge>
                      {p.isTest && <Badge color="amber">تجريبي</Badge>}
                    </td>
                    <td className="p-3 text-xs">{p.provider === 'manual' ? 'يدوي (تحويل/كاش)' : p.provider === 'ziina' ? 'Ziina' : p.provider}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        )}
      </div>

      {dialog?.type === 'pro' && <ProDialog userId={user.id} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.type === 'credits' && <CreditsDialog userId={user.id} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.type === 'hosting' && <HostingDialog site={dialog.site} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.type === 'suspend' && <SuspendDialog site={dialog.site} onClose={() => setDialog(null)} onDone={done} />}
    </div>
  )
}

function SiteCard({ site, onHosting, onSuspend, onUnsuspend, onDelete }: { site: AdminSite; onHosting: () => void; onSuspend: () => void; onUnsuspend: () => void; onDelete: () => void }) {
  const h = site.hosting
  const s = site.suspension
  const hostingBadge = !h.paidUntil ? (
    <Badge>بدون استضافة</Badge>
  ) : h.active ? (
    h.inGrace ? (
      <Badge color="amber">فترة سماح</Badge>
    ) : (
      <Badge color="green">
        استضافة {h.tier === 'backend' ? 'باك إند' : 'عادية'} حتى {formatDate(h.paidUntil)}
      </Badge>
    )
  ) : (
    <Badge color="red">الاستضافة انتهت {formatDate(h.paidUntil)}</Badge>
  )

  return (
    <Card>
      <div className="flex items-start justify-between gap-2">
        <div>
          <h4 className="font-bold">{site.name}</h4>
          <p className="text-xs text-slate-500">
            {site.template ?? site.templateKey} • {site.pages} صفحة • {site.versions} نسخة • أنشئ {formatDate(site.createdAt)}
          </p>
        </div>
        {s ? (
          <Badge color="red">موقوف إدارياً</Badge>
        ) : site.publishedAt ? (
          h.active ? (
            <Badge color="green">منشور ويعمل</Badge>
          ) : (
            <Badge color="red">منشور ومتوقف</Badge>
          )
        ) : (
          <Badge>غير منشور</Badge>
        )}
      </div>
      {site.description && <p className="mt-2 line-clamp-2 text-sm text-slate-600">{site.description}</p>}
      {s && (
        <div className="mt-3 rounded-xl border border-red-100 bg-red-50 p-3 text-sm text-red-800">
          <p className="font-semibold">السبب: {s.reason}</p>
          <p className="mt-1 text-xs text-red-700/80">
            {s.at && formatDateTime(s.at)}
            {s.by && <> • بواسطة <span dir="ltr">{s.by}</span></>}
          </p>
        </div>
      )}
      <div className="mt-3 flex flex-wrap gap-1">
        {hostingBadge}
        <Badge color={h.requiredTier === 'backend' ? 'brand' : 'slate'}>يحتاج {h.requiredTier === 'backend' ? 'باك إند' : 'استضافة عادية'}</Badge>
        {h.active && !h.enough && <Badge color="red">الاستضافة أقل من المطلوب</Badge>}
        {site.features.map((f) => (
          <Badge key={f}>{featureNames[f] ?? f}</Badge>
        ))}
      </div>
      {site.siteUrl && (
        <p className="mt-2 break-all text-xs text-slate-500" dir="ltr">
          {site.siteUrl}
          {site.customDomain && !site.customDomainVerified && ' (domain not verified)'}
        </p>
      )}
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {site.siteUrl && (
          <a href={site.siteUrl} target="_blank" rel="noreferrer" className="rounded-xl bg-brand-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-brand-700">
            فتح الموقع
          </a>
        )}
        {site.previewUrl && (
          <a href={site.previewUrl} target="_blank" rel="noreferrer" className="rounded-xl border border-slate-200 px-3 py-1.5 text-sm font-semibold hover:bg-slate-50">
            معاينة آخر نسخة
          </a>
        )}
        <Button variant="secondary" onClick={onHosting}>
          تفعيل استضافة
        </Button>
        {s ? (
          <Button variant="success" onClick={onUnsuspend}>
            إلغاء الإيقاف
          </Button>
        ) : (
          <Button variant="danger" onClick={onSuspend}>
            إيقاف الموقع
          </Button>
        )}
        <Button variant="danger" onClick={onDelete}>
          حذف الموقع بالكامل
        </Button>
        <span className="ms-auto text-xs text-slate-400" dir="ltr">
          AI ${site.aiCost.toFixed(4)} • {site.tasks} tasks
        </span>
      </div>
    </Card>
  )
}

function Mini({ label, value, sub, tone }: { label: string; value: ReactNode; sub?: string; tone?: 'good' | 'warn' }) {
  const ring = tone === 'good' ? 'ring-emerald-200 bg-emerald-50/50' : tone === 'warn' ? 'ring-amber-200 bg-amber-50/50' : 'ring-slate-200 bg-white'
  return (
    <div className={`rounded-2xl p-4 ring-1 ${ring}`}>
      <p className="text-xs text-slate-500">{label}</p>
      <p className="mt-1 text-xl font-extrabold">{value}</p>
      {sub && <p className="text-xs text-slate-400">{sub}</p>}
    </div>
  )
}

function useSubmit(onDone: (text: string) => void) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const submit = async (fn: () => Promise<unknown>, success: string) => {
    setBusy(true)
    setError('')
    try {
      await fn()
      onDone(success)
    } catch (e) {
      setError(errorMessage(e))
      setBusy(false)
    }
  }
  return { busy, error, submit }
}

const PAID_NOTE = 'اكتب المبلغ الذي استلمته (تحويل بنكي أو كاش) ليُسجَّل كدفعة ويظهر في الإيراد. اكتب 0 لو التفعيل هدية مجانية.'

function ProDialog({ userId, onClose, onDone }: { userId: string; onClose: () => void; onDone: (text: string) => void }) {
  const plans = usePlans()
  const [interval, setInterval] = useState<'monthly' | 'yearly'>('monthly')
  const [amount, setAmount] = useState<string | null>(null)
  const { busy, error, submit } = useSubmit(onDone)
  const price = plans ? (interval === 'yearly' ? plans.pro.yearlyPrice : plans.pro.monthlyPrice) : 0
  const value = amount ?? String(price)

  return (
    <Modal open onClose={onClose} title="تفعيل Pro يدوياً">
      <div className="space-y-4">
        {error && <Alert tone="error">{error}</Alert>}
        <Toggle value={interval} onChange={(v) => { setInterval(v); setAmount(null) }} />
        <Input label="المبلغ المستلم ($)" type="number" min="0" step="0.01" dir="ltr" value={value} onChange={(e) => setAmount(e.target.value)} />
        <p className="text-xs text-slate-500">{PAID_NOTE}</p>
        <Button className="w-full" loading={busy} onClick={() => submit(() => post(`/api/admin/users/${userId}/activate`, { interval, amount: Number(value) || null }), 'تم تفعيل Pro')}>
          تفعيل {interval === 'yearly' ? 'سنة' : 'شهر'}
        </Button>
      </div>
    </Modal>
  )
}

function HostingDialog({ site, onClose, onDone }: { site: AdminSite; onClose: () => void; onDone: (text: string) => void }) {
  const plans = usePlans()
  const [tier, setTier] = useState<'static' | 'backend'>(site.hosting.requiredTier)
  const [interval, setInterval] = useState<'monthly' | 'yearly'>('monthly')
  const [amount, setAmount] = useState<string | null>(null)
  const { busy, error, submit } = useSubmit(onDone)
  const price = plans
    ? tier === 'backend'
      ? interval === 'yearly' ? plans.hosting.backendYearly : plans.hosting.backendMonthly
      : interval === 'yearly' ? plans.hosting.staticYearly : plans.hosting.staticMonthly
    : 0
  const value = amount ?? String(price)

  return (
    <Modal open onClose={onClose} title={`استضافة: ${site.name}`}>
      <div className="space-y-4">
        {error && <Alert tone="error">{error}</Alert>}
        <div className="grid grid-cols-2 gap-2">
          {(['static', 'backend'] as const).map((t) => (
            <button
              key={t}
              disabled={t === 'static' && site.hosting.requiredTier === 'backend'}
              onClick={() => { setTier(t); setAmount(null) }}
              className={`rounded-xl border p-3 text-sm font-semibold disabled:opacity-40 ${tier === t ? 'border-brand-500 bg-brand-50' : 'border-slate-200'}`}
            >
              {t === 'backend' ? 'موقع + باك إند' : 'موقع عادي'}
            </button>
          ))}
        </div>
        <Toggle value={interval} onChange={(v) => { setInterval(v); setAmount(null) }} />
        <Input label="المبلغ المستلم ($)" type="number" min="0" step="0.01" dir="ltr" value={value} onChange={(e) => setAmount(e.target.value)} />
        <p className="text-xs text-slate-500">{PAID_NOTE}</p>
        <Button
          className="w-full"
          loading={busy}
          onClick={() =>
            submit(
              () => post(`/api/admin/projects/${site.id}/hosting`, { tier, months: interval === 'yearly' ? 12 : 1, amount: Number(value) || null }),
              'تم تفعيل الاستضافة',
            )
          }
        >
          تفعيل {interval === 'yearly' ? 'سنة' : 'شهر'}
        </Button>
      </div>
    </Modal>
  )
}

function SuspendDialog({ site, onClose, onDone }: { site: AdminSite; onClose: () => void; onDone: (text: string) => void }) {
  const [reason, setReason] = useState('')
  const { busy, error, submit } = useSubmit(onDone)
  return (
    <Modal open onClose={onClose} title={`إيقاف: ${site.name}`}>
      <div className="space-y-4">
        {error && <Alert tone="error">{error}</Alert>}
        <p className="text-sm text-slate-600">
          الزوار سيرون صفحة "الموقع متوقف مؤقتاً" وتتوقف كل خدماته (الطلبات، الحجوزات، الحسابات) حتى لو استضافته مدفوعة. صاحب الموقع يرى السبب في لوحته ولا يستطيع النشر أو دفع الاستضافة حتى تلغي الإيقاف. مدة الاستضافة
          المدفوعة لا تتغير.
        </p>
        <Textarea label="سبب الإيقاف (يظهر لصاحب الموقع فقط)" rows={3} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
        <Button
          variant="danger"
          className="w-full"
          loading={busy}
          disabled={reason.trim().length < 3}
          onClick={() => submit(() => post(`/api/admin/projects/${site.id}/suspend`, { reason: reason.trim() }), 'تم إيقاف الموقع')}
        >
          إيقاف الموقع
        </Button>
      </div>
    </Modal>
  )
}

function CreditsDialog({ userId, onClose, onDone }: { userId: string; onClose: () => void; onDone: (text: string) => void }) {
  const [credits, setCredits] = useState('1000')
  const [paid, setPaid] = useState('0')
  const { busy, error, submit } = useSubmit(onDone)
  return (
    <Modal open onClose={onClose} title="إضافة أو خصم نقاط">
      <div className="space-y-4">
        {error && <Alert tone="error">{error}</Alert>}
        <Input label="عدد النقاط (سالب للخصم)" type="number" dir="ltr" value={credits} onChange={(e) => setCredits(e.target.value)} />
        <Input label="المبلغ المستلم ($) — اختياري" type="number" min="0" step="0.01" dir="ltr" value={paid} onChange={(e) => setPaid(e.target.value)} />
        <Button
          className="w-full"
          loading={busy}
          onClick={() => submit(() => post(`/api/admin/users/${userId}/credits`, { amount: Number(credits), note: 'admin', paidAmount: Number(paid) || null }), 'تم تعديل النقاط')}
        >
          حفظ
        </Button>
      </div>
    </Modal>
  )
}

function Toggle({ value, onChange }: { value: 'monthly' | 'yearly'; onChange: (v: 'monthly' | 'yearly') => void }) {
  return (
    <div className="flex gap-1 rounded-xl bg-slate-100 p-1 text-sm font-semibold">
      {(['monthly', 'yearly'] as const).map((i) => (
        <button key={i} onClick={() => onChange(i)} className={`flex-1 rounded-lg py-1.5 ${value === i ? 'bg-white shadow' : 'text-slate-500'}`}>
          {i === 'monthly' ? 'شهر' : 'سنة'}
        </button>
      ))}
    </div>
  )
}
