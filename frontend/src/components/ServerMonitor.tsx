import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, Mail, RefreshCw, Server } from 'lucide-react'
import { Alert, Button, Card } from './ui'
import { apiUrl, errorMessage, get, post, tokenStore } from '../lib/api'

type Level = 'ok' | 'warning' | 'critical'

interface Reading {
  key: string
  label: string
  value: number | null
  display: string
  level: Level
  threshold: string
}

interface MonitorView {
  lastCheck: string | null
  intervalSeconds: number
  servers: { id: string; name: string; monitored: boolean; lastReport: string | null; readings: Reading[] }[]
  alerts: { id: string; server: string; label: string; level: Level; since: string; display: string | null }[]
  email: { enabled: boolean; mode: 'off' | 'pickup' | 'smtp'; sent: number; failed: number; lastError: string | null; recipients: string[] }
  errors: { at: string; level: string; category: string; message: string; exception: string | null }[]
  errorsLast10Minutes: number
}

const levelStyle: Record<Level, string> = {
  ok: 'bg-emerald-50 text-emerald-700',
  warning: 'bg-orange-50 text-orange-700',
  critical: 'bg-red-50 text-red-700',
}
const levelText: Record<Level, string> = { ok: 'طبيعي', warning: 'تحذير', critical: 'خطر' }

const emailKinds: [string, string][] = [
  ['pro_expiring', 'تذكير: Pro ينتهي قريباً'],
  ['pro_expired', 'انتهى اشتراك Pro'],
  ['hosting_expiring', 'تذكير: الاستضافة تنتهي'],
  ['hosting_grace', 'فترة السماح'],
  ['hosting_stopped', 'الموقع توقف'],
  ['receipt', 'إيصال الدفع'],
  ['credits_low', 'النقاط قاربت على النفاد'],
  ['site_order', 'طلب جديد على موقع'],
  ['server_alert', 'تنبيه السيرفر (لك)'],
  ['server_recovered', 'رجوع السيرفر لطبيعته (لك)'],
]

const time = (iso: string) => new Date(iso).toLocaleString('ar-EG-u-nu-latn', { dateStyle: 'short', timeStyle: 'medium' })

function EmailPreview() {
  const [kind, setKind] = useState('pro_expiring')
  const [lang, setLang] = useState('ar')
  const [html, setHtml] = useState('')
  useEffect(() => {
    const token = tokenStore.get()
    fetch(apiUrl(`/api/admin/email/preview?kind=${kind}&lang=${lang}`), { headers: token ? { Authorization: `Bearer ${token}` } : {} })
      .then((r) => (r.ok ? r.text() : ''))
      .then(setHtml)
      .catch(() => setHtml(''))
  }, [kind, lang])
  return (
    <div className="mt-6">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="me-auto font-bold">معاينة الإيميلات</h3>
        <select value={kind} onChange={(e) => setKind(e.target.value)} className="h-9 rounded-xl border border-slate-200 bg-white px-2 text-sm">
          {emailKinds.map(([k, label]) => (
            <option key={k} value={k}>
              {label}
            </option>
          ))}
        </select>
        <select value={lang} onChange={(e) => setLang(e.target.value)} className="h-9 rounded-xl border border-slate-200 bg-white px-2 text-sm">
          <option value="ar">العربية</option>
          <option value="en">English</option>
          <option value="hi">हिन्दी</option>
        </select>
      </div>
      <iframe title="email preview" srcDoc={html} sandbox="" className="mt-3 h-[640px] w-full rounded-xl border border-slate-200 bg-[#f1f3f9]" />
    </div>
  )
}

/** Both servers' resources, active alerts, e-mail status and recent errors (admin only, Arabic). */
export default function ServerMonitor() {
  const [view, setView] = useState<MonitorView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState<'check' | 'test' | null>(null)
  const [openError, setOpenError] = useState<number | null>(null)

  const load = useCallback(() => {
    get<MonitorView>('/api/admin/monitor')
      .then((v) => {
        setView(v)
        setError(null)
      })
      .catch((e) => setError(errorMessage(e)))
  }, [])

  useEffect(() => {
    load()
    const timer = window.setInterval(load, 30_000)
    return () => window.clearInterval(timer)
  }, [load])

  const check = async () => {
    setBusy('check')
    try {
      const r = await post<{ changes: number; view: MonitorView }>('/api/admin/monitor/check')
      setView(r.view)
      setNotice(r.changes > 0 ? `تم الفحص: ${r.changes} تغيير وتم إرسال تنبيه` : 'تم الفحص: لا يوجد تغيير')
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(null)
    }
  }

  const sendTest = async () => {
    setBusy('test')
    try {
      const r = await post<{ sent: number; to: string[]; mode: string }>('/api/admin/email/test', { kind: 'all', lang: 'ar' })
      setNotice(`تم إرسال ${r.sent} إيميل تجريبي إلى ${r.to.join('، ')}${r.mode === 'pickup' ? ' (وضع التطوير: محفوظة في data/mail)' : ''}`)
      load()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(null)
    }
  }

  return (
    <Card className="mt-8">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 className="flex items-center gap-2 text-lg font-bold">
            <Server className="h-5 w-5 text-brand-600" />
            مراقبة السيرفرين والتنبيهات
          </h2>
          <p className="text-sm text-slate-500">
            فحص كل {view?.intervalSeconds ?? 60} ثانية. عند الخطر يصلك إيميل من no-reply@casco.studio.
            {view?.lastCheck && ` آخر فحص: ${time(view.lastCheck)}`}
          </p>
        </div>
        <div className="flex gap-2">
          <Button variant="secondary" onClick={sendTest} loading={busy === 'test'}>
            <Mail className="h-4 w-4" />
            إيميل تجريبي
          </Button>
          <Button variant="secondary" onClick={check} loading={busy === 'check'}>
            <RefreshCw className="h-4 w-4" />
            فحص الآن
          </Button>
        </div>
      </div>

      {error && (
        <div className="mt-3">
          <Alert tone="error">{error}</Alert>
        </div>
      )}
      {notice && (
        <div className="mt-3">
          <Alert tone="success">{notice}</Alert>
        </div>
      )}

      {view && (
        <>
          <div className={`mt-4 rounded-xl border p-3 text-sm ${view.email.enabled ? 'border-slate-200' : 'border-amber-300 bg-amber-50'}`}>
            {view.email.enabled ? (
              <>
                <b>الإيميل:</b> {view.email.mode === 'smtp' ? 'مفعّل (SMTP)' : 'وضع التطوير (يُحفظ في data/mail)'} • أُرسل {view.email.sent} • فشل{' '}
                {view.email.failed} • التنبيهات تذهب إلى: <span dir="ltr">{view.email.recipients.join(', ') || '—'}</span>
                {view.email.lastError && <p className="mt-1 text-red-700">آخر خطأ: {view.email.lastError}</p>}
              </>
            ) : (
              <>
                <b>الإيميل غير مفعّل.</b> اضبط Smtp__Host و Smtp__User و Smtp__Password في ملف .env على السيرفر 1 حتى تصلك التنبيهات ويصل للمستخدمين التذكير بالدفع.
              </>
            )}
          </div>

          {view.alerts.length > 0 ? (
            <div className="mt-4 space-y-2">
              {view.alerts.map((a) => (
                <div key={a.id} className={`flex flex-wrap items-center gap-2 rounded-xl p-3 text-sm ${levelStyle[a.level]}`}>
                  <AlertTriangle className="h-4 w-4" />
                  <b>{a.server} — {a.label}</b>
                  <span dir="ltr">{a.display}</span>
                  <span className="ms-auto text-xs opacity-80">منذ {time(a.since)}</span>
                </div>
              ))}
            </div>
          ) : (
            <p className="mt-4 flex items-center gap-2 text-sm font-medium text-emerald-700">
              <CheckCircle2 className="h-4 w-4" />
              لا توجد تنبيهات الآن
            </p>
          )}

          <div className="mt-4 grid gap-4 lg:grid-cols-2">
            {view.servers.map((s) => (
              <div key={s.id} className="rounded-2xl border border-slate-200 p-4">
                <div className="flex items-center justify-between gap-2">
                  <h3 className="font-bold">{s.name}</h3>
                  {s.lastReport && <span className="text-xs text-slate-500">{time(s.lastReport)}</span>}
                </div>
                {!s.monitored && (
                  <p className="mt-2 rounded-lg bg-amber-50 p-2 text-xs text-amber-800">
                    موارد هذا السيرفر غير مراقبة: اضبط Monitor__AgentToken هنا و MONITOR_TOKEN على السيرفر 2 (راجع README).
                  </p>
                )}
                <div className="mt-3 divide-y divide-slate-100">
                  {s.readings.map((r) => (
                    <div key={r.key} className="flex items-center gap-3 py-2 text-sm">
                      <span className="w-40 shrink-0 text-slate-500">{r.label}</span>
                      <span className="min-w-0 flex-1 truncate font-semibold" dir="ltr">
                        {r.display}
                      </span>
                      <span className={`rounded-full px-2 py-0.5 text-xs font-bold ${levelStyle[r.level]}`} title={`الحد: ${r.threshold}`}>
                        {levelText[r.level]}
                      </span>
                    </div>
                  ))}
                  {s.readings.length === 0 && <p className="py-2 text-sm text-slate-400">لا توجد قراءات بعد</p>}
                </div>
              </div>
            ))}
          </div>

          <div className="mt-6">
            <h3 className="font-bold">
              آخر الأخطاء <span className="text-sm font-normal text-slate-500">({view.errorsLast10Minutes} في آخر 10 دقائق • الملفات الكاملة في data/logs على السيرفر 1)</span>
            </h3>
            {view.errors.length === 0 ? (
              <p className="mt-2 text-sm text-slate-400">لا توجد أخطاء مسجلة منذ آخر تشغيل</p>
            ) : (
              <div className="mt-2 max-h-96 divide-y divide-slate-100 overflow-auto rounded-xl border border-slate-200" dir="ltr">
                {view.errors.map((e, i) => (
                  <button key={i} type="button" onClick={() => setOpenError(openError === i ? null : i)} className="block w-full p-2.5 text-start text-xs hover:bg-slate-50">
                    <span className="font-mono text-slate-400">{new Date(e.at).toISOString().replace('T', ' ').slice(0, 19)}</span>{' '}
                    <span className="font-semibold text-red-700">{e.category}</span> <span className="text-slate-700">{e.message}</span>
                    {openError === i && e.exception && <pre className="mt-2 whitespace-pre-wrap break-all rounded-lg bg-slate-900 p-2 text-[11px] text-slate-100">{e.exception}</pre>}
                  </button>
                ))}
              </div>
            )}
          </div>
        </>
      )}

      <EmailPreview />
    </Card>
  )
}
