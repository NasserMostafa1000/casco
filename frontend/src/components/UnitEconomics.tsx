import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { Alert, Badge, Card, Spinner } from './ui'
import { errorMessage, get } from '../lib/api'

interface Report {
  month: string
  isCurrentMonth: boolean
  daysElapsed: number
  daysInMonth: number
  ai: {
    cost: number
    estimatedCost: number
    actualKnownShare: number
    projectedMonthCost: number
    calls: number
    tasks: number
    failures: number
    failedCost: number
    cacheHits: number
    retries: number
    retryCost: number
    avgDurationMs: number
    p90DurationMs: number
    inputTokens: number
    cachedTokens: number
    outputTokens: number
  }
  perUser: { activeUsers: number; average: number; median: number; p90: number; max: number; totalUsers: number; signups: number; costPerSignup: number }
  byPlan: { plan: string; users: number; cost: number; avgPerUser: number; median: number; p90: number; max: number }[]
  pro: {
    monthlyPrice: number
    subscribers: number
    aiCost: number
    aiCostPerSubscriber: number
    shareOfPrice: number
    freeUsersAiCost: number
    freeCostPerSubscriber: number
    paymentFee: number
    marginPerSubscriber: number
  }
  revenue: { total: number; subscriptions: number; hosting: number; topups: number; payingUsers: number; perPayingUser: number; aiCostShare: number }
  hosting: {
    staticPrice: number
    backendPrice: number
    staticSites: number
    backendSites: number
    infraMonthlyUsd: number
    domainMonthlyUsd: number
    emailMonthlyUsd: number
    platformMonthlyUsd: number
    expenses: { name: string; monthlyUsd: number }[]
    infraPerSite: number
    staticMargin: number
    backendMargin: number
    monthlyRunRate: number
  }
  byPurpose: { purpose: string; tasks: number; calls: number; cost: number; avgPerTask: number; callsPerTask: number }[]
  byModel: { provider: string; model: string; calls: number; failures: number; cacheHits: number; cost: number; avgDurationMs: number }[]
  byProvider: { provider: string; estimatedCost: number; cost: number }[]
  topUsers: { userId: string; email: string | null; name: string | null; plan: string; cost: number; calls: number; tasks: number; projects: number }[]
  daily: { date: string; cost: number; calls: number; users: number }[]
  requests: {
    cost: Distribution
    multiPart: number
    multiPartShare: number
    multiPartAvgCost: number
    avgParts: number
    maxParts: number
    bands: Band[]
    top: { taskId: string; project: string | null; email: string | null; kind: string | null; prompt: string | null; cost: number; parts: number; calls: number }[]
  }
  projects: { cost: Distribution; top: { projectId: string; name: string | null; email: string | null; cost: number; tasks: number }[] }
  proMargins: {
    users: number
    aiCostBands: Band[]
    margin: Distribution
    negative: number
    worst: { userId: string; email: string | null; revenue: number; aiCost: number; margin: number }[]
  }
}

interface Distribution {
  users: number
  total: number
  average: number
  median: number
  p90: number
  max: number
}
interface Band {
  from: number
  to: number | null
  count: number
}

const bandLabel = (b: Band, digits = 2) => (b.to == null ? `${usd(b.from, digits)}+` : `${usd(b.from, digits)} – ${usd(b.to, digits)}`)

const usd = (v: number, digits = 4) => `$${Number(v).toFixed(digits)}`
const pct = (v: number) => `${(Number(v) * 100).toFixed(1)}%`
const purposeNames: Record<string, string> = { generate: 'توليد موقع', edit: 'تعديل', fix: 'إصلاح' }
const planNames: Record<string, string> = { free: 'مجاني', pro: 'Pro' }

export default function UnitEconomics() {
  const [month, setMonth] = useState(() => new Date().toISOString().slice(0, 7))
  const [report, setReport] = useState<Report | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    setError(null)
    get<Report>(`/api/admin/economics?month=${month}`).then(setReport).catch((e) => setError(errorMessage(e)))
  }, [month])

  useEffect(load, [load])

  const maxDaily = Math.max(0.000001, ...(report?.daily.map((d) => d.cost) ?? [0]))
  const marginTone = (v: number) => (v >= 0 ? 'text-emerald-600' : 'text-red-600')

  return (
    <Card className="mt-8">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 className="text-lg font-bold">اقتصاديات المستخدم (Unit economics)</h2>
          <p className="text-sm text-slate-500">كل طلب ذكاء اصطناعي مسجّل: المستخدم، الموقع، المهمة، الموديل، التوكنز، التكلفة التقديرية والفعلية، المدة، وعدد المحاولات.</p>
        </div>
        <input type="month" value={month} onChange={(e) => setMonth(e.target.value)} className="rounded-lg border border-slate-200 p-2 text-sm" />
      </div>

      {error && (
        <div className="mt-4">
          <Alert tone="error">{error}</Alert>
        </div>
      )}

      {!report ? (
        <Spinner className="mt-6 h-6 w-6 text-brand-600" />
      ) : (
        <>
          <div className="mt-5 grid grid-cols-2 gap-3 md:grid-cols-4">
            <Metric
              label="متوسط تكلفة المستخدم النشط"
              value={usd(report.perUser.average)}
              sub={`الوسيط ${usd(report.perUser.median)} • أعلى 10% ${usd(report.perUser.p90)}`}
              highlight
            />
            <Metric label="تكلفة مشترك Pro من الذكاء الاصطناعي" value={usd(report.pro.aiCostPerSubscriber)} sub={`${pct(report.pro.shareOfPrice)} من $${report.pro.monthlyPrice}`} highlight />
            <Metric
              label="صافي مشترك Pro بعد كل التكاليف"
              value={<span className={marginTone(report.pro.marginPerSubscriber)}>{usd(report.pro.marginPerSubscriber, 2)}</span>}
              sub={`بعد دعم المجانيين ${usd(report.pro.freeCostPerSubscriber)} ورسوم الدفع ${usd(report.pro.paymentFee, 2)}`}
              highlight
            />
            <Metric label="تكلفة جلب مستخدم جديد" value={usd(report.perUser.costPerSignup)} sub={`${report.perUser.signups} تسجيل هذا الشهر`} />
            <Metric
              label="إجمالي تكلفة AI"
              value={usd(report.ai.cost, 2)}
              sub={report.isCurrentMonth ? `المتوقع للشهر ${usd(report.ai.projectedMonthCost, 2)}` : `${report.ai.calls} طلب`}
            />
            <Metric label="المستخدمون النشطون (AI)" value={report.perUser.activeUsers} sub={`من ${report.perUser.totalUsers} مستخدم • ${report.pro.subscribers} Pro`} />
            <Metric label="الإيراد الفعلي" value={usd(report.revenue.total, 2)} sub={`تكلفة AI = ${pct(report.revenue.aiCostShare)} من الإيراد`} />
            <Metric
              label="هدر المحاولات والفشل"
              value={usd(report.ai.retryCost + report.ai.failedCost)}
              sub={`${report.ai.retries} إعادة • ${report.ai.failures} فشل • ${report.ai.cacheHits} من الكاش`}
            />
          </div>

          <p className="mt-3 text-xs text-slate-400">التكلفة هنا تقدير من استخدام الموديلات. فواتير OpenAI والسيرفرات والبريد تتسجل في صفحة الحسابات.</p>

          <div className="mt-6 grid gap-4 lg:grid-cols-2">
            <Section title="حسب الخطة">
              <Table
                head={['الخطة', 'مستخدمون', 'الإجمالي', 'المتوسط', 'الوسيط', 'أعلى 10%', 'الأعلى']}
                rows={report.byPlan.map((p) => [planNames[p.plan] ?? p.plan, p.users, usd(p.cost), usd(p.avgPerUser), usd(p.median), usd(p.p90), usd(p.max)])}
              />
            </Section>
            <Section title="حسب نوع العملية">
              <Table
                head={['العملية', 'مهام', 'طلبات', 'طلبات/مهمة', 'التكلفة', 'متوسط المهمة']}
                rows={report.byPurpose.map((p) => [purposeNames[p.purpose] ?? p.purpose, p.tasks, p.calls, p.callsPerTask, usd(p.cost), usd(p.avgPerTask)])}
              />
            </Section>
            <Section title={`الاستضافة ($${report.hosting.staticPrice} / $${report.hosting.backendPrice})`}>
              <Table
                head={['', 'مواقع', 'السعر', 'نصيب السيرفر', 'الصافي']}
                rows={[
                  ['واجهة فقط', report.hosting.staticSites, usd(report.hosting.staticPrice, 2), usd(report.hosting.infraPerSite, 2), <span className={marginTone(report.hosting.staticMargin)}>{usd(report.hosting.staticMargin, 2)}</span>],
                  ['باك إند', report.hosting.backendSites, usd(report.hosting.backendPrice, 2), usd(report.hosting.infraPerSite, 2), <span className={marginTone(report.hosting.backendMargin)}>{usd(report.hosting.backendMargin, 2)}</span>],
                ]}
              />
            </Section>
            <Section title="الإيراد">
              <Table
                head={['اشتراكات', 'استضافة', 'شحن نقاط', 'دافعون', 'لكل دافع']}
                rows={[[usd(report.revenue.subscriptions, 2), usd(report.revenue.hosting, 2), usd(report.revenue.topups, 2), report.revenue.payingUsers, usd(report.revenue.perPayingUser, 2)]]}
              />
            </Section>
          </div>

          <Section title="التكلفة اليومية" className="mt-6">
            <div className="flex h-28 items-end gap-1" dir="ltr">
              {report.daily.map((d) => (
                <div key={d.date} title={`${d.date}: ${usd(d.cost)} • ${d.calls} طلب • ${d.users} مستخدم`} className="flex-1 rounded-t bg-brand-500/80" style={{ height: `${Math.max(3, (d.cost / maxDaily) * 100)}%` }} />
              ))}
              {report.daily.length === 0 && <p className="text-sm text-slate-400">لا توجد بيانات لهذا الشهر</p>}
            </div>
          </Section>

          <div className="mt-6 grid gap-4 lg:grid-cols-2">
            <Section title="أعلى المستخدمين تكلفة">
              <Table
                head={['المستخدم', 'الخطة', 'التكلفة', 'طلبات', 'مهام', 'مواقع']}
                rows={report.topUsers.map((u) => [
                  <span dir="ltr" className="text-xs">{u.email ?? u.userId.slice(0, 8)}</span>,
                  u.plan === 'pro' ? <Badge color="brand">Pro</Badge> : <Badge>مجاني</Badge>,
                  usd(u.cost),
                  u.calls,
                  u.tasks,
                  u.projects,
                ])}
              />
            </Section>
            <Section title="حسب الموديل">
              <Table
                head={['الموديل', 'طلبات', 'فشل', 'كاش', 'متوسط المدة', 'التكلفة']}
                rows={report.byModel.map((m) => [<span dir="ltr" className="font-mono text-xs">{m.model}</span>, m.calls, m.failures, m.cacheHits, `${(m.avgDurationMs / 1000).toFixed(1)}s`, usd(m.cost)])}
              />
            </Section>
          </div>

          <div className="mt-6 grid gap-4 lg:grid-cols-2">
            <Section title="تكلفة الطلب الكامل (كل أجزائه ومحاولاته)">
              <Table
                head={['طلبات', 'المتوسط', 'الوسيط', 'أعلى 10%', 'الأعلى']}
                rows={[[report.requests.cost.users, usd(report.requests.cost.average), usd(report.requests.cost.median), usd(report.requests.cost.p90), usd(report.requests.cost.max)]]}
              />
              <p className="mt-2 text-xs text-slate-500">
                {report.requests.multiPart} طلب كبير على أجزاء ({pct(report.requests.multiPartShare)}) • متوسط {report.requests.avgParts} جزء • متوسط تكلفته{' '}
                {usd(report.requests.multiPartAvgCost)} • أكبر طلب {report.requests.maxParts} أجزاء
              </p>
              <Table head={['تكلفة الطلب', 'عدد الطلبات']} rows={report.requests.bands.map((b) => [bandLabel(b), b.count])} />
            </Section>
            <Section title={`هامش كل مشترك Pro (${report.proMargins.users} مشترك)`}>
              <p className="text-sm text-slate-600">
                الهامش = ما دفعه − تكلفة AI − نصيب السيرفر من مواقعه المستضافة. الوسيط {usd(report.proMargins.margin.median, 2)}
                {report.proMargins.worst.length > 0 && <> • الأسوأ {usd(report.proMargins.worst[0].margin, 2)}</>} •{' '}
                <span className={report.proMargins.negative > 0 ? 'font-bold text-red-600' : ''}>{report.proMargins.negative} مشترك بهامش سالب</span>
              </p>
              <Table head={['تكلفة AI للمشترك هذا الشهر', 'مشتركين']} rows={report.proMargins.aiCostBands.map((b) => [bandLabel(b, 0), b.count])} />
            </Section>
          </div>

          <div className="mt-6 grid gap-4 lg:grid-cols-2">
            <Section title="أغلى الطلبات">
              <Table
                head={['الطلب', 'الموقع', 'أجزاء', 'نداءات', 'التكلفة']}
                rows={report.requests.top.map((t) => [
                  <span className="line-clamp-2 text-xs">{t.prompt ?? purposeNames[t.kind ?? ''] ?? '—'}</span>,
                  <span className="text-xs">{t.project ?? '—'}</span>,
                  t.parts,
                  t.calls,
                  usd(t.cost),
                ])}
              />
            </Section>
            <Section title="أغلى المواقع">
              <Table
                head={['الموقع', 'المالك', 'طلبات', 'التكلفة']}
                rows={report.projects.top.map((p) => [p.name ?? p.projectId.slice(0, 8), <span dir="ltr" className="text-xs">{p.email ?? '—'}</span>, p.tasks, usd(p.cost)])}
              />
              <p className="mt-2 text-xs text-slate-400">
                متوسط تكلفة الموقع {usd(report.projects.cost.average)} • الوسيط {usd(report.projects.cost.median)} • أعلى 10% {usd(report.projects.cost.p90)}
              </p>
            </Section>
          </div>

          {report.proMargins.worst.some((w) => w.margin < 0) && (
            <Section title="مشتركون يكلّفون أكثر مما يدفعون" className="mt-6">
              <Table
                head={['المستخدم', 'دفع', 'تكلفة AI', 'الهامش']}
                rows={report.proMargins.worst
                  .filter((w) => w.margin < 0)
                  .map((w) => [
                    <span dir="ltr" className="text-xs">{w.email ?? w.userId.slice(0, 8)}</span>,
                    usd(w.revenue, 2),
                    usd(w.aiCost, 2),
                    <span className="text-red-600">{usd(w.margin, 2)}</span>,
                  ])}
              />
            </Section>
          )}

        </>
      )}
    </Card>
  )
}

function Metric({ label, value, sub, highlight }: { label: string; value: ReactNode; sub?: string; highlight?: boolean }) {
  return (
    <div className={`rounded-xl border p-3 ${highlight ? 'border-brand-200 bg-brand-50/50' : 'border-slate-200'}`}>
      <p className="text-xs text-slate-500">{label}</p>
      <p className="mt-1 text-xl font-extrabold" dir="ltr">
        {value}
      </p>
      {sub && <p className="text-xs text-slate-400">{sub}</p>}
    </div>
  )
}

function Section({ title, children, className = '' }: { title: string; children: ReactNode; className?: string }) {
  return (
    <div className={`rounded-xl border border-slate-200 p-3 ${className}`}>
      <h3 className="mb-2 font-bold">{title}</h3>
      {children}
    </div>
  )
}

function Table({ head, rows }: { head: string[]; rows: ReactNode[][] }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead className="text-slate-500">
          <tr>
            {head.map((h, i) => (
              <th key={i} className="p-2 text-start font-medium">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={head.length} className="p-2 text-slate-400">
                لا توجد بيانات
              </td>
            </tr>
          ) : (
            rows.map((r, i) => (
              <tr key={i} className="border-t border-slate-100">
                {r.map((c, j) => (
                  <td key={j} className="p-2">
                    {c}
                  </td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  )
}
