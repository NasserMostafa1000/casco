import { useEffect, useState } from 'react'
import { Navigate, useSearchParams } from 'react-router-dom'
import { Alert, Badge, Button, Card, Input, Spinner, Textarea } from '../components/ui'
import { del, errorMessage, get, post, put } from '../lib/api'
import { useAuth } from '../lib/auth'
import AdminAccounts from '../components/AdminAccounts'
import AdminPricing from '../components/AdminPricing'
import AdminUsers from '../components/AdminUsers'
import AdminWallet from '../components/AdminWallet'
import ServerMonitor from '../components/ServerMonitor'
import SystemHealth from '../components/SystemHealth'
import UnitEconomics from '../components/UnitEconomics'

interface Stats {
  users: number
  proUsers: number
  projects: number
  publishedSites: number
  revenue30d: number
  aiCost30d: number
  aiCalls30d: number
  aiFailures30d: number
  responseCacheHits30d: number
  inputTokens30d: number
  cachedInputTokens30d: number
  outputTokens30d: number
  creditsCharged30d: number
  tasks30d: number
  failedTasks30d: number
  byModel: { model: string; calls: number; cost: number; inputTokens: number; cachedInputTokens: number; outputTokens: number }[]
}

interface AiConfig {
  useFake: boolean
  providers: { name: string; configured: boolean }[]
  models: { id: string; provider: string; label: string | null; inputPer1M: number; cachedInputPer1M: number; outputPer1M: number; available: boolean }[]
  tiers: Record<string, string[]>
  defaults: Record<string, string[]>
}

const tierNames: Record<string, string> = { cheap: 'رخيص (الخطة المجانية)', standard: 'قياسي (Pro)', premium: 'قوي (الوضع القوي)' }

const tabs = [
  { key: 'overview', label: 'نظرة عامة' },
  { key: 'accounts', label: 'الحسابات' },
  { key: 'users', label: 'المستخدمون والمواقع' },
  { key: 'pricing', label: 'الأسعار' },
  { key: 'transfers', label: 'تحويلات المحفظة' },
  { key: 'models', label: 'الموديلات' },
] as const

export default function Admin() {
  const { me } = useAuth()
  const [params, setParams] = useSearchParams()
  const tab = params.get('tab') ?? 'overview'
  const [stats, setStats] = useState<Stats | null>(null)
  const [ai, setAi] = useState<AiConfig | null>(null)
  const [tiers, setTiers] = useState<Record<string, string[]>>({})
  const [msg, setMsg] = useState<{ tone: 'success' | 'error'; text: string } | null>(null)

  useEffect(() => {
    if (me?.user.role !== 'Admin') return
    get<Stats>('/api/admin/stats').then(setStats).catch((e) => setMsg({ tone: 'error', text: errorMessage(e) }))
    get<AiConfig>('/api/admin/ai').then((c) => {
      setAi(c)
      setTiers(c.tiers)
    })
  }, [me])

  if (me?.user.role !== 'Admin') return <Navigate to="/app" replace />

  const act = async (fn: () => Promise<unknown>, success: string) => {
    setMsg(null)
    try {
      await fn()
      setMsg({ tone: 'success', text: success })
    } catch (e) {
      setMsg({ tone: 'error', text: errorMessage(e) })
    }
  }

  const move = (tier: string, index: number, dir: -1 | 1) =>
    setTiers((t) => {
      const list = [...(t[tier] ?? [])]
      const j = index + dir
      if (j < 0 || j >= list.length) return t
      ;[list[index], list[j]] = [list[j], list[index]]
      return { ...t, [tier]: list }
    })

  const cacheRate = stats && stats.inputTokens30d > 0 ? Math.round((stats.cachedInputTokens30d / stats.inputTokens30d) * 100) : 0

  return (
    <main className="mx-auto max-w-7xl px-4 py-8">
      <h1 className="text-2xl font-extrabold">لوحة الإدارة</h1>
      <nav className="mt-4 flex flex-wrap gap-1 border-b border-slate-200">
        {tabs.map((t) => (
          <button
            key={t.key}
            onClick={() => {
              setMsg(null)
              setParams(t.key === 'overview' ? {} : { tab: t.key })
            }}
            className={`-mb-px border-b-2 px-4 py-2 text-sm font-semibold ${tab === t.key ? 'border-brand-600 text-brand-700' : 'border-transparent text-slate-500 hover:text-slate-800'}`}
          >
            {t.label}
          </button>
        ))}
      </nav>
      {msg && (
        <div className="mt-4">
          <Alert tone={msg.tone}>{msg.text}</Alert>
        </div>
      )}

      {tab === 'accounts' && (
        <div className="mt-6">
          <AdminAccounts />
        </div>
      )}

      {tab === 'users' && (
        <div className="mt-6">
          <AdminUsers />
        </div>
      )}

      {tab === 'pricing' && (
        <div className="mt-6">
          <AdminPricing />
        </div>
      )}

      {tab === 'overview' && (!stats ? (
        <Spinner className="mt-6 h-6 w-6 text-brand-600" />
      ) : (
        <>
          <div className="mt-6 grid grid-cols-2 gap-3 md:grid-cols-4">
            <Stat label="المستخدمون" value={stats.users} sub={`${stats.proUsers} مشترك Pro`} />
            <Stat label="المواقع" value={stats.projects} sub={`${stats.publishedSites} منشور`} />
            <Stat label="الإيراد (30 يوم)" value={`$${stats.revenue30d.toFixed(2)}`} />
            <Stat label="تكلفة الذكاء الاصطناعي (30 يوم)" value={`$${stats.aiCost30d.toFixed(4)}`} sub={`${stats.aiCalls30d} طلب • ${stats.aiFailures30d} فشل`} />
            <Stat label="نسبة Prompt Cache" value={`${cacheRate}%`} sub="من توكنز الإدخال" />
            <Stat label="ردود من الكاش المحلي" value={stats.responseCacheHits30d} sub="بدون أي تكلفة" />
            <Stat label="النقاط المخصومة" value={stats.creditsCharged30d.toLocaleString('ar')} />
            <Stat label="المهام" value={stats.tasks30d} sub={`${stats.failedTasks30d} فشلت`} />
          </div>

          <Card className="mt-6 overflow-x-auto p-0">
            <table className="w-full text-sm">
              <thead className="bg-slate-50 text-slate-500">
                <tr>
                  <th className="p-3 text-start">الموديل</th>
                  <th className="p-3 text-start">الطلبات</th>
                  <th className="p-3 text-start">Input</th>
                  <th className="p-3 text-start">Cached</th>
                  <th className="p-3 text-start">Output</th>
                  <th className="p-3 text-start">التكلفة</th>
                </tr>
              </thead>
              <tbody>
                {stats.byModel.map((m) => (
                  <tr key={m.model} className="border-t border-slate-100" dir="ltr">
                    <td className="p-3 text-start font-mono">{m.model}</td>
                    <td className="p-3 text-start">{m.calls}</td>
                    <td className="p-3 text-start">{m.inputTokens.toLocaleString()}</td>
                    <td className="p-3 text-start">{m.cachedInputTokens.toLocaleString()}</td>
                    <td className="p-3 text-start">{m.outputTokens.toLocaleString()}</td>
                    <td className="p-3 text-start">${Number(m.cost).toFixed(5)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
          <BroadcastMail users={stats.users} onDone={(text) => setMsg({ tone: 'success', text })} onError={(text) => setMsg({ tone: 'error', text })} />
          <DirectMail onDone={(text) => setMsg({ tone: 'success', text })} onError={(text) => setMsg({ tone: 'error', text })} />
          <SystemHealth />
          <ServerMonitor />
          <UnitEconomics />
        </>
      ))}

      {tab === 'transfers' && (
        <div className="mt-6">
          <AdminWallet />
        </div>
      )}

      {tab === 'models' && ai && (
        <Card className="mt-8">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <h2 className="text-lg font-bold">تبديل الموديلات</h2>
              <p className="text-sm text-slate-500">لكل مستوى: الموديل الأول هو الأساسي، والباقي احتياطي تلقائي لو فشل الأول. التغيير فوري بدون إعادة تشغيل.</p>
            </div>
            <div className="flex gap-2">
              {ai.providers.map((p) => (
                <Badge key={p.name} color={p.configured ? 'green' : 'red'}>
                  {p.name} {p.configured ? '✓' : 'بدون مفتاح'}
                </Badge>
              ))}
              {ai.useFake && <Badge color="amber">وضع تجريبي (Fake)</Badge>}
            </div>
          </div>
          <div className="mt-4 grid gap-4 md:grid-cols-3">
            {Object.keys(tierNames).map((tier) => (
              <div key={tier} className="rounded-xl border border-slate-200 p-3">
                <p className="font-bold">{tierNames[tier]}</p>
                <div className="mt-2 space-y-1">
                  {(tiers[tier] ?? []).map((id, i) => {
                    const m = ai.models.find((x) => x.id === id)
                    return (
                      <div key={id} className="flex items-center justify-between rounded-lg bg-slate-50 px-2 py-1.5 text-sm">
                        <span dir="ltr" className={m?.available ? '' : 'text-red-500 line-through'}>
                          {i + 1}. {id}
                        </span>
                        <span className="flex gap-1">
                          <button onClick={() => move(tier, i, -1)}>▲</button>
                          <button onClick={() => move(tier, i, 1)}>▼</button>
                          <button onClick={() => setTiers((t) => ({ ...t, [tier]: t[tier].filter((x) => x !== id) }))} className="text-red-500">
                            ✕
                          </button>
                        </span>
                      </div>
                    )
                  })}
                </div>
                <select
                  className="mt-2 w-full rounded-lg border border-slate-200 p-1.5 text-sm"
                  value=""
                  onChange={(e) => e.target.value && setTiers((t) => ({ ...t, [tier]: [...(t[tier] ?? []), e.target.value] }))}
                >
                  <option value="">+ إضافة موديل</option>
                  {ai.models
                    .filter((m) => !(tiers[tier] ?? []).includes(m.id))
                    .map((m) => (
                      <option key={m.id} value={m.id}>
                        {m.label ?? m.id} (${m.inputPer1M}/${m.outputPer1M})
                      </option>
                    ))}
                </select>
              </div>
            ))}
          </div>
          <div className="mt-4 flex gap-2">
            <Button onClick={() => act(() => put('/api/admin/ai/tiers', { tiers }), 'تم حفظ الموديلات')}>حفظ</Button>
            <Button
              variant="secondary"
              onClick={() =>
                act(async () => {
                  const t = await del<Record<string, string[]>>('/api/admin/ai/tiers')
                  setTiers(t)
                }, 'تمت العودة للإعدادات الافتراضية')
              }
            >
              استرجاع الافتراضي
            </Button>
            <Button variant="ghost" onClick={() => act(() => post('/api/admin/ziina/webhook'), 'تم تسجيل Webhook في Ziina')}>
              تسجيل Webhook لـ Ziina
            </Button>
          </div>
          <div className="mt-4 overflow-x-auto">
            <table className="w-full text-xs text-slate-500" dir="ltr">
              <thead>
                <tr>
                  <th className="p-1 text-left">Model</th>
                  <th className="p-1 text-left">Provider</th>
                  <th className="p-1 text-left">Input $/1M</th>
                  <th className="p-1 text-left">Cached $/1M</th>
                  <th className="p-1 text-left">Output $/1M</th>
                </tr>
              </thead>
              <tbody>
                {ai.models.map((m) => (
                  <tr key={m.id}>
                    <td className="p-1 font-mono">{m.id}</td>
                    <td className="p-1">{m.provider}</td>
                    <td className="p-1">{m.inputPer1M}</td>
                    <td className="p-1">{m.cachedInputPer1M}</td>
                    <td className="p-1">{m.outputPer1M}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

    </main>
  )
}

function DirectMail({ onDone, onError }: { onDone: (text: string) => void; onError: (text: string) => void }) {
  const [to, setTo] = useState('')
  const [subject, setSubject] = useState('')
  const [message, setMessage] = useState('')
  const [sending, setSending] = useState(false)
  const send = async () => {
    if (to.trim().length < 3 || subject.trim().length < 2 || message.trim().length < 2) return onError('اكتب البريد والعنوان ونص الرسالة')
    setSending(true)
    try {
      const r = await post<{ sent: number }>('/api/admin/email/send', { to, subject, message })
      setTo('')
      setSubject('')
      setMessage('')
      onDone(`اتبعثت الرسالة لـ ${r.sent} بريد`)
    } catch (e) {
      onError(errorMessage(e))
    } finally {
      setSending(false)
    }
  }
  return (
    <Card className="mt-6">
      <h2 className="text-lg font-bold">إرسال بريد لأي عنوان</h2>
      <p className="mt-1 text-sm text-slate-500">مش لازم يكون عنده حساب على Casco. اكتب بريد أو أكتر، كل واحد في سطر.</p>
      <div className="mt-4 space-y-3">
        <Textarea label="البريد" rows={3} value={to} dir="ltr" placeholder="name@example.com" onChange={(e) => setTo(e.target.value)} />
        <Input label="العنوان" value={subject} maxLength={160} onChange={(e) => setSubject(e.target.value)} />
        <Textarea label="الرسالة" rows={5} value={message} maxLength={4000} onChange={(e) => setMessage(e.target.value)} />
        <Button onClick={() => void send()} loading={sending} disabled={to.trim().length < 3 || subject.trim().length < 2 || message.trim().length < 2}>
          إرسال
        </Button>
      </div>
    </Card>
  )
}

function BroadcastMail({ users, onDone, onError }: { users: number; onDone: (text: string) => void; onError: (text: string) => void }) {
  const [subject, setSubject] = useState('')
  const [message, setMessage] = useState('')
  const [sending, setSending] = useState(false)
  const send = async () => {
    if (subject.trim().length < 2 || message.trim().length < 2) return onError('اكتب عنوان الرسالة ونصها')
    if (!confirm(`إرسال هذه الرسالة إلى ${users} مستخدم؟`)) return
    setSending(true)
    try {
      const r = await post<{ sent: number }>('/api/admin/email/broadcast', { subject, message })
      setSubject('')
      setMessage('')
      onDone(`اتبعثت الرسالة لـ ${r.sent} مستخدم`)
    } catch (e) {
      onError(errorMessage(e))
    } finally {
      setSending(false)
    }
  }
  return (
    <Card className="mt-6">
      <h2 className="text-lg font-bold">رسالة لكل المستخدمين</h2>
      <p className="mt-1 text-sm text-slate-500">الإيميل يوصل لكل حساب مسجّل على Casco.</p>
      <div className="mt-4 space-y-3">
        <Input label="العنوان" value={subject} maxLength={160} onChange={(e) => setSubject(e.target.value)} />
        <Textarea label="الرسالة" rows={5} value={message} maxLength={4000} onChange={(e) => setMessage(e.target.value)} />
        <Button onClick={() => void send()} loading={sending} disabled={subject.trim().length < 2 || message.trim().length < 2}>
          إرسال إلى كل المستخدمين
        </Button>
      </div>
    </Card>
  )
}

function Stat({ label, value, sub }: { label: string; value: string | number; sub?: string }) {
  return (
    <Card>
      <p className="text-xs text-slate-500">{label}</p>
      <p className="mt-1 text-2xl font-extrabold">{value}</p>
      {sub && <p className="text-xs text-slate-400">{sub}</p>}
    </Card>
  )
}
