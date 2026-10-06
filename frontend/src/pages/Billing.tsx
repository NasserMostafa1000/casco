import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { Check, Crown, Globe, ImagePlus, Receipt, ShieldCheck, Zap } from 'lucide-react'
import { HostingModal } from '../components/HostingModal'
import { Alert, Badge, Button, Card, Segmented } from '../components/ui'
import { api, errorMessage, get, post, type HostingStatus } from '../lib/api'
import { useAuth } from '../lib/auth'
import { formatDate, formatDateTime, num, t } from '../lib/i18n'
import { usd, usePlans } from '../lib/plans'
import { rememberCheckout, tiktokClick } from '../lib/tiktok'

interface HostedSite {
  projectId: string
  name: string
  slug: string
  templateKey: string
  published: boolean
  status: HostingStatus
}

function HostedSites() {
  const plans = usePlans()
  const [sites, setSites] = useState<HostedSite[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  useEffect(() => {
    get<{ items: HostedSite[] }>('/api/billing/hosting')
      .then((r) => {
        setSites(r.items)
        const renew = params.get('renew')
        const site = renew ? r.items.find((s) => s.projectId === renew) : undefined
        if (site?.templateKey === 'react') navigate(`/app/react/${site.projectId}`, { replace: true })
        else if (site && !site.status.suspended) setOpen(site.projectId)
        if (renew) setParams({}, { replace: true })
      })
      .catch(() => {})
    // Only on first load: the e-mail link opens the renewal window once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  if (!sites || sites.length === 0) return null
  return (
    <section className="mt-12">
      <h2 className="flex items-center gap-2 text-xl font-bold text-ink">
        <Globe className="h-5 w-5 text-brand-600" />
        {t('استضافة مواقعك')}
      </h2>
      <p className="mt-1 text-sm text-slate-500">
        {t('كل موقع منشور له استضافة شهرية خاصة به: {static} للموقع العادي و {backend} للموقع الذي فيه باك إند. إذا لم تُجدَّد يتوقف الموقع.', {
          static: usd(plans?.hosting.staticMonthly),
          backend: usd(plans?.hosting.backendMonthly),
        })}
      </p>
      <Card className="mt-4 divide-y divide-slate-100 p-0">
        {sites.map((s) => (
          <div key={s.projectId} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <Link to={s.templateKey === 'react' ? `/app/react/${s.projectId}` : `/app/p/${s.projectId}`} className="font-semibold text-ink hover:text-brand-600">
                  {s.name}
                </Link>
                {s.status.suspended ? <Badge color="red">{t('موقوف من الإدارة')}</Badge> : s.templateKey === 'react' && s.published && !s.status.active ? <Badge color="red">{t('متوقف')}</Badge> : s.published && <Badge color="green">{t('منشور')}</Badge>}
              </div>
              <p className="mt-0.5 text-sm text-slate-500">
                {s.templateKey === 'react' ? (
                  <>
                    {t('تطبيق React')} • {usd(plans?.hosting.reactYearly)} {t('في السنة')} •{' '}
                    {s.status.daysLeft > 0 ? t('فاضل {n} يوم من الاستضافة', { n: s.status.daysLeft }) : s.status.paidUntil ? t('الاستضافة انتهت والموقع متوقف') : t('بدون استضافة')}
                  </>
                ) : (
                  <>
                    {t('يحتاج:')} {s.status.requiredTier === 'backend' ? t('موقع + باك إند') : t('موقع')} •{' '}
                    {s.status.paidUntil ? `${s.status.active ? t('فعّالة حتى') : t('انتهت في')} ${formatDate(s.status.paidUntil)}` : t('بدون استضافة')}
                    {s.status.inGrace && ` • ${t('فترة سماح')}`}
                  </>
                )}
              </p>
              {s.status.suspended && s.status.suspensionReason && (
                <p className="text-sm text-red-700">
                  {t('السبب:')} {s.status.suspensionReason}
                </p>
              )}
            </div>
            {!s.status.suspended && s.templateKey === 'react' && (
              <Link to={`/app/react/${s.projectId}`} className="inline-flex h-10 items-center justify-center rounded-xl bg-ink px-4 text-sm font-semibold text-white">
                {s.status.active ? t('جدّد الاستضافة') : t('ادفع {price} في السنة', { price: usd(plans?.hosting.reactYearly) })}
              </Link>
            )}
            {!s.status.suspended && s.templateKey !== 'react' && (
              <Button variant={s.status.active ? 'secondary' : 'primary'} onClick={() => setOpen(s.projectId)}>
                {s.status.active ? t('تجديد') : t('تفعيل الاستضافة')}
              </Button>
            )}
          </div>
        ))}
      </Card>
      {open && <HostingModal projectId={open} open onClose={() => setOpen(null)} />}
    </section>
  )
}

interface History {
  payments: {
    id: string
    kind: string
    interval: string | null
    hostingTier: string | null
    amount: number
    currency: string
    status: string
    createdAt: string
    isTest: boolean
  }[]
  credits: { id: number; amount: number; bucket: string; type: string; createdAt: string }[]
}

const creditTypes = (): Record<string, string> => ({
  signup_bonus: t('هدية التسجيل'),
  plan_grant: t('نقاط الاشتراك الشهرية'),
  plan_expire: t('انتهاء نقاط الشهر السابق'),
  usage: t('استخدام الذكاء الاصطناعي'),
  topup: t('شحن نقاط'),
  admin: t('إضافة من الإدارة'),
})

export default function Billing() {
  const { me } = useAuth()
  const plans = usePlans()
  const [history, setHistory] = useState<History | null>(null)
  const [interval, setInterval] = useState<'monthly' | 'yearly'>('monthly')
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    get<History>('/api/billing/history').then(setHistory).catch(() => {})
  }, [])

  const checkout = async (body: { kind: string; interval?: string; packId?: string }, key: string) => {
    setBusy(key)
    setError('')
    try {
      const click = tiktokClick()
      const r = await post<{ paymentId: string; redirectUrl: string }>('/api/billing/checkout', { ...body, ttclid: click.ttclid, ttp: click.ttp })
      if (plans && body.kind === 'subscription' && body.interval) {
        const yearly = body.interval === 'yearly'
        rememberCheckout({
          content_id: yearly ? 'pro-yearly' : 'pro-monthly',
          content_name: yearly ? 'Casco Pro yearly' : 'Casco Pro monthly',
          value: yearly ? plans.pro.yearlyPrice : plans.pro.monthlyPrice,
          currency: plans.currency || 'USD',
        }, r.paymentId)
      }
      if (plans && body.kind === 'topup' && body.packId) {
        const pack = plans.topups.find((p) => p.id === body.packId)
        if (pack) {
          rememberCheckout({
            content_id: pack.id,
            content_name: 'Casco credits',
            value: pack.price,
            currency: plans.currency || 'USD',
          }, r.paymentId)
        }
      }
      window.location.href = r.redirectUrl
    } catch (e) {
      setError(errorMessage(e))
      setBusy('')
    }
  }

  if (!me) return null
  const yearlySaving = plans ? plans.pro.monthlyPrice * 12 - plans.pro.yearlyPrice : 0
  const types = creditTypes()

  return (
    <main className="mx-auto max-w-5xl px-4 py-8 sm:px-6 sm:py-10">
      <h1 className="text-3xl font-extrabold tracking-tight text-ink">{t('الاشتراك والنقاط')}</h1>
      {error && (
        <div className="mt-4">
          <Alert tone="error">{error}</Alert>
        </div>
      )}

      <div className="mt-6 grid gap-4 md:grid-cols-2">
        <Card>
          <div className="flex items-center justify-between">
            <p className="text-sm text-slate-500">{t('خطتك الحالية')}</p>
            <Crown className="h-5 w-5 text-brand-600" />
          </div>
          <p className="mt-2 flex items-center gap-2 text-2xl font-extrabold text-ink">
            {me.plan.isPro ? 'Pro' : t('مجاني')} {me.plan.isPro && <Badge color="brand">{me.plan.interval === 'yearly' ? t('سنوي') : t('شهري')}</Badge>}
          </p>
          {me.plan.isPro && (
            <p className="mt-1 text-sm text-slate-600">
              {t('ينتهي في {date} (بعد {n} يوم). التجديد يضيف المدة على اشتراكك الحالي.', { date: formatDate(me.plan.periodEnd), n: me.plan.daysLeft ?? 0 })}
            </p>
          )}
        </Card>
        <Card>
          <div className="flex items-center justify-between">
            <p className="text-sm text-slate-500">{t('رصيد النقاط')}</p>
            <Zap className="h-5 w-5 fill-amber-400 text-amber-500" />
          </div>
          <p className="mt-2 text-2xl font-extrabold text-ink">{num(me.credits.available)}</p>
          <p className="mt-1 text-sm text-slate-600">
            {t('نقاط الخطة (تتجدد شهرياً): {plan} • مشحونة (لا تنتهي): {topup}', { plan: num(me.credits.plan), topup: num(me.credits.topup) })}
          </p>
          <p className="mt-2 text-xs text-slate-400">{t('تُخصم النقاط حسب التكلفة الفعلية لكل طلب؛ التعديلات الصغيرة تستهلك نقاطاً قليلة جداً.')}</p>
        </Card>
      </div>

      <div className="mt-12 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <h2 className="text-xl font-bold text-ink">{me.plan.isPro ? t('جدّد اشتراكك') : t('اشترك في Pro')}</h2>
        <Segmented
          value={interval}
          onChange={setInterval}
          options={[
            { value: 'monthly', label: t('شهري') },
            {
              value: 'yearly',
              label: (
                <>
                  {t('سنوي')}
                  {yearlySaving > 0 && <span className="text-xs font-semibold text-emerald-600">{t('وفّر ${n}', { n: yearlySaving })}</span>}
                </>
              ),
            },
          ]}
        />
      </div>

      {plans && (
        <div className="mt-4 grid gap-4 md:grid-cols-2">
          <div className="relative rounded-3xl bg-gradient-to-b from-brand-600 to-violet-700 p-6 text-white shadow-xl shadow-brand-600/25 sm:p-7">
            <p className="text-sm font-semibold text-white/80">Casco Pro</p>
            <div className="mt-2 flex items-baseline gap-1">
              <span className="text-5xl font-extrabold tracking-tight">${interval === 'monthly' ? plans.pro.monthlyPrice : plans.pro.yearlyPrice}</span>
              <span className="text-white/70">{t('مرة واحدة')}</span>
            </div>
            <p className="mt-4 text-sm leading-relaxed text-white">
              {t('المبلغ ده مرة واحدة بس، مقابل كود موقعك ومهمة بنائه. مش هيتدفع تاني عشان موقعك يفضل شغال. موقعك هيفضل شغال عادي، لكن مش هتقدر تحدّث الكود بالذكاء الاصطناعي.')}
            </p>
            <ul className="mt-6 space-y-2.5 text-sm">
              {[
                t('التعديل على مواقعك بالذكاء الاصطناعي'),
                t('{n} نقطة كل شهر', { n: num(plans.pro.monthlyCredits) }),
                t('مواقع متعددة الصفحات'),
                t('منصات كورسات وإعلانات ومتاجر كاملة'),
                t('تنزيل كود مواقعك كاملاً'),
                t('الوضع القوي للذكاء الاصطناعي'),
              ].map((x) => (
                <li key={x} className="flex gap-2.5">
                  <Check className="mt-0.5 h-4 w-4 shrink-0" />
                  {x}
                </li>
              ))}
            </ul>
            <button
              className="mt-6 inline-flex h-12 w-full items-center justify-center gap-2 rounded-xl bg-white font-semibold text-brand-700 transition hover:bg-slate-50 disabled:opacity-60"
              disabled={busy === 'sub'}
              onClick={() => checkout({ kind: 'subscription', interval }, 'sub')}
            >
              {busy === 'sub' ? '...' : t('ادفع عبر Ziina')}
            </button>
            <p className="mt-2 flex items-center justify-center gap-1.5 text-center text-xs text-white/70">
              <ShieldCheck className="h-3.5 w-3.5" />
              {t('دفع آمن بالبطاقة أو Apple Pay عبر Ziina')}
            </p>
            <p className="mt-2 text-center text-xs text-white/80">
              {t('الدفع مرة واحدة لبناء الموقع')}
            </p>
          </div>
          <Card className="p-6 sm:p-7">
            <h3 className="flex items-center gap-2 font-semibold text-ink">
              <Zap className="h-5 w-5 fill-amber-400 text-amber-500" />
              {t('شحن نقاط إضافية')}
            </h3>
            <p className="mt-1 text-sm text-slate-500">{t('النقاط المشحونة لا تنتهي وتُستخدم بعد نفاد نقاط الخطة.')}</p>
            <div className="mt-5 space-y-2">
              {plans.topups.map((p) => (
                <div key={p.id} className="flex items-center justify-between rounded-xl border border-slate-200 p-3">
                  <span className="font-semibold">{t('{n} نقطة', { n: num(p.credits) })}</span>
                  <Button variant="secondary" loading={busy === p.id} onClick={() => checkout({ kind: 'topup', packId: p.id }, p.id)}>
                    ${p.price}
                  </Button>
                </div>
              ))}
            </div>
          </Card>
        </div>
      )}

      <WalletRecharge />

      <HostedSites />

      {history && (
        <section className="mt-12 grid gap-4 md:grid-cols-2">
          <Card>
            <h3 className="mb-3 flex items-center gap-2 font-semibold text-ink">
              <Receipt className="h-4 w-4 text-slate-400" />
              {t('المدفوعات')}
            </h3>
            {history.payments.length === 0 ? (
              <p className="text-sm text-slate-500">{t('لا توجد مدفوعات بعد.')}</p>
            ) : (
              history.payments.map((p) => (
                <div key={p.id} className="flex items-center justify-between gap-3 border-b border-slate-100 py-2.5 text-sm last:border-0">
                  <span>
                    {p.kind === 'subscription'
                      ? `${t('اشتراك')} ${p.interval === 'yearly' ? t('سنوي') : t('شهري')}`
                      : p.kind === 'hosting'
                        ? `${t('استضافة')} ${p.hostingTier === 'backend' ? t('موقع + باك إند') : t('موقع')} (${p.interval === 'yearly' ? t('سنة') : t('شهر')})`
                        : t('شحن نقاط')}{' '}
                    {p.isTest && <Badge color="amber">{t('تجريبي')}</Badge>}
                  </span>
                  <span className="flex shrink-0 items-center gap-2">
                    ${p.amount} {p.status === 'completed' ? <Badge color="green">{t('مدفوع')}</Badge> : <Badge color="red">{p.status === 'canceled' ? t('ملغي') : t('فشل')}</Badge>}
                  </span>
                </div>
              ))
            )}
          </Card>
          <Card>
            <h3 className="mb-3 flex items-center gap-2 font-semibold text-ink">
              <Zap className="h-4 w-4 text-slate-400" />
              {t('حركة النقاط')}
            </h3>
            <div className="scrollbar-thin max-h-80 overflow-y-auto">
              {history.credits.map((c) => (
                <div key={c.id} className="flex justify-between border-b border-slate-100 py-2.5 text-sm last:border-0">
                  <span>
                    {types[c.type] ?? c.type}
                    <span className="block text-xs text-slate-400">{formatDateTime(c.createdAt)}</span>
                  </span>
                  <span className={c.amount >= 0 ? 'font-semibold text-emerald-600' : 'text-slate-600'} dir="ltr">
                    {c.amount > 0 ? '+' : ''}
                    {num(c.amount)}
                  </span>
                </div>
              ))}
            </div>
          </Card>
        </section>
      )}
    </main>
  )
}

interface WalletRow {
  id: string
  method: string
  amount: number
  currency: string
  status: string
  reviewNote: string | null
  createdAt: string
}

function WalletRecharge() {
  const { refresh } = useAuth()
  const plans = usePlans()
  const [method, setMethod] = useState<'vodafone_cash' | 'instapay'>('vodafone_cash')
  const [file, setFile] = useState<File | null>(null)
  const [rows, setRows] = useState<WalletRow[]>([])
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ tone: 'success' | 'error'; text: string } | null>(null)
  const phone = plans?.wallet?.phone ?? '01026159280'
  const amount = plans?.wallet?.amount ?? 680
  const pending = rows.some((r) => r.status === 'pending')

  const load = () =>
    get<{ items: WalletRow[] }>('/api/billing/wallet-transfers')
      .then((r) => setRows(r.items))
      .catch(() => {})

  useEffect(() => {
    void load()
  }, [])

  useEffect(() => {
    if (!pending) return
    const timer = window.setInterval(() => {
      void load().then(() => {})
    }, 15000)
    return () => window.clearInterval(timer)
  }, [pending])

  useEffect(() => {
    if (rows.some((r) => r.status === 'approved')) void refresh()
  }, [rows, refresh])

  const send = async () => {
    if (!file) {
      setMsg({ tone: 'error', text: t('ارفع صورة التحويل') })
      return
    }
    setBusy(true)
    setMsg(null)
    try {
      const fd = new FormData()
      fd.set('method', method)
      fd.set('file', file)
      await api('POST', '/api/billing/wallet-transfers', fd)
      setFile(null)
      setMsg({ tone: 'success', text: t('تم إرسال صورة التحويل وسيتم تفعيل حسابك خلال 24 ساعة.') })
      await load()
    } catch (e) {
      setMsg({ tone: 'error', text: errorMessage(e) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="mt-8 p-6 sm:p-7">
      <h2 className="text-xl font-bold text-ink">{t('فودافون كاش أو إنستا باي')}</h2>
      <p className="mt-2 text-sm leading-relaxed text-slate-600">
        {t('حوّل {amount} جنيه على {phone}. سعر الدولار في بوابة الدفع زي ما هو. ادخل بنفس حساب جوجل أو آبل، ومفيش خانة توكن.', {
          amount: num(amount),
          phone,
        })}
      </p>
      <p className="mt-3 font-mono text-2xl font-extrabold tracking-wide text-ink" dir="ltr">
        {phone}
      </p>
      <p className="mt-3 text-sm leading-relaxed text-slate-600">
        {t('يمكنك أيضاً التحويل على محفظة اتصالات كاش لهذا الرقم، ولكن التفعيل قد يكون من دقائق إلى 24 ساعة. أما الدفع بالفيزا فتحويل فوري.')}
      </p>
      <div className="mt-4 flex flex-wrap gap-2">
        <button
          type="button"
          onClick={() => setMethod('vodafone_cash')}
          className={`rounded-xl px-4 py-2 text-sm font-semibold ${method === 'vodafone_cash' ? 'bg-ink text-white' : 'bg-slate-100 text-slate-700'}`}
        >
          {t('فودافون كاش')}
        </button>
        <button
          type="button"
          onClick={() => setMethod('instapay')}
          className={`rounded-xl px-4 py-2 text-sm font-semibold ${method === 'instapay' ? 'bg-ink text-white' : 'bg-slate-100 text-slate-700'}`}
        >
          {t('إنستا باي')}
        </button>
      </div>
      <label className="mt-5 flex cursor-pointer items-center justify-center gap-3 rounded-2xl border-2 border-brand-500 bg-brand-50 px-5 py-4 text-base font-bold text-brand-800 shadow-sm transition hover:bg-brand-100">
        <ImagePlus className="h-6 w-6 shrink-0" />
        <span className="truncate">{file ? file.name : t('اختيار صورة التحويل')}</span>
        <input
          type="file"
          accept="image/jpeg,image/png,image/webp"
          className="sr-only"
          onChange={(e) => setFile(e.target.files?.[0] ?? null)}
        />
      </label>
      <Button className="mt-4" loading={busy} disabled={pending} onClick={() => void send()}>
        {pending ? t('في تحويل قيد المراجعة') : t('إرسال صورة التحويل')}
      </Button>
      {msg && (
        <div className="mt-4">
          <Alert tone={msg.tone}>{msg.text}</Alert>
        </div>
      )}
      {rows.length > 0 && (
        <ul className="mt-4 space-y-2 text-sm">
          {rows.map((r) => (
            <li key={r.id} className="flex items-center justify-between gap-3 border-t border-slate-100 pt-2">
              <span>{r.method === 'instapay' ? t('إنستا باي') : t('فودافون كاش')}</span>
              <span className="text-slate-500">
                {r.status === 'approved' ? t('تمت الموافقة') : r.status === 'rejected' ? t('مرفوض') : t('قيد المراجعة')}
                {r.reviewNote ? ` — ${r.reviewNote}` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}
