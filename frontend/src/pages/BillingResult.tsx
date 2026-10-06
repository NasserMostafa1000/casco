import { useEffect, useState, type ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { AlertTriangle, CheckCircle2, Clock, Undo2, XCircle } from 'lucide-react'
import { Card, Spinner } from '../components/ui'
import { errorMessage, post } from '../lib/api'
import { useAuth } from '../lib/auth'
import { t } from '../lib/i18n'
import { trackPurchase } from '../lib/tiktok'

type State = 'checking' | 'completed' | 'pending' | 'failed' | 'canceled' | 'error'

export default function BillingResult() {
  const [params] = useSearchParams()
  const { refresh } = useAuth()
  const [state, setState] = useState<State>('checking')
  const [error, setError] = useState('')
  const [hostingProject, setHostingProject] = useState<string | null>(null)
  const [reactHost, setReactHost] = useState(false)
  const pid = params.get('pid')

  useEffect(() => {
    if (!pid) {
      setState('error')
      return
    }
    let cancelled = false
    let attempts = 0
    const check = async () => {
      try {
        const r = await post<{ status: string; kind: string; projectId: string | null; hostingTier: string | null }>(`/api/billing/payments/${pid}/confirm`)
        if (cancelled) return
        if (r.kind === 'hosting' && r.projectId) setHostingProject(r.projectId)
        if (r.hostingTier === 'react') setReactHost(true)
        if (r.status === 'completed') {
          setState('completed')
          trackPurchase(pid, { content_id: r.kind, content_name: r.kind === 'hosting' ? 'Casco hosting' : r.kind === 'topup' ? 'Casco credits' : 'Casco Pro' })
          await refresh()
          return
        }
        if (r.status === 'failed' || r.status === 'canceled') {
          setState(r.status)
          return
        }
        if (params.get('canceled')) {
          setState('canceled')
          return
        }
        attempts++
        if (attempts < 8) window.setTimeout(check, 2500)
        else setState('pending')
      } catch (e) {
        if (!cancelled) {
          setError(errorMessage(e))
          setState('error')
        }
      }
    }
    void check()
    return () => {
      cancelled = true
    }
  }, [pid, params, refresh])

  const content: Record<State, [ReactNode, string, string]> = {
    checking: [<Spinner key="s" className="h-10 w-10 text-brand-600" />, t('جاري التحقق من الدفع...'), t('لحظات من فضلك.')],
    completed: [
      <CheckCircle2 key="c" className="h-12 w-12 text-emerald-500" />,
      t('تم الدفع بنجاح!'),
      reactHost ? t('تم تفعيل استضافة React. ارفع ملف dist ليظهر التطبيق على الإنترنت.') : hostingProject ? t('تم تفعيل استضافة موقعك. ارجع للمحرر واضغط "نشر الموقع" ليظهر على الإنترنت.') : t('تم تفعيل اشتراكك/نقاطك. استمتع ببناء مواقعك.'),
    ],
    pending: [<Clock key="p" className="h-12 w-12 text-amber-500" />, t('الدفع قيد المعالجة'), t('سيتم التفعيل تلقائياً فور تأكيد Ziina. يمكنك تحديث الصفحة بعد قليل.')],
    failed: [<XCircle key="f" className="h-12 w-12 text-red-500" />, t('فشلت عملية الدفع'), t('لم يتم خصم أي مبلغ. حاول مرة أخرى أو استخدم بطاقة أخرى.')],
    canceled: [<Undo2 key="u" className="h-12 w-12 text-slate-400" />, t('تم إلغاء الدفع'), t('لم يتم خصم أي مبلغ.')],
    error: [<AlertTriangle key="e" className="h-12 w-12 text-amber-500" />, t('تعذر التحقق من الدفع'), error || t('رابط غير صالح.')],
  }
  const [icon, title, text] = content[state]

  return (
    <main className="mx-auto max-w-lg px-4 py-16">
      <Card className="p-8 text-center">
        <div className="grid place-items-center">{icon}</div>
        <h1 className="mt-5 text-2xl font-extrabold text-ink">{title}</h1>
        <p className="mt-2 text-slate-600">{text}</p>
        <div className="mt-8 flex flex-col justify-center gap-2 sm:flex-row">
          {reactHost && hostingProject ? (
            <Link to={`/app/react/${hostingProject}`} className="inline-flex h-11 items-center justify-center rounded-xl bg-ink px-5 font-semibold text-white">
              {t('افتح صفحة الرفع')}
            </Link>
          ) : hostingProject ? (
            <Link to={`/app/p/${hostingProject}`} className="inline-flex h-11 items-center justify-center rounded-xl bg-ink px-5 font-semibold text-white">
              {t('فتح المحرر والنشر')}
            </Link>
          ) : (
            <Link to="/app" className="inline-flex h-11 items-center justify-center rounded-xl bg-ink px-5 font-semibold text-white">
              {t('مواقعي')}
            </Link>
          )}
          <Link to="/app/billing" className="inline-flex h-11 items-center justify-center rounded-xl border border-slate-200 px-5 font-semibold">
            {t('الاشتراك')}
          </Link>
        </div>
      </Card>
    </main>
  )
}
