import { useEffect, useState } from 'react'
import { ImagePlus } from 'lucide-react'
import { api, errorMessage, get } from '../lib/api'
import { t } from '../lib/i18n'
import { usePlans } from '../lib/plans'
import { Alert, Button } from './ui'

interface WalletRow {
  id: string
  purpose?: string
  projectId?: string | null
  status: string
  reviewNote: string | null
}

/** Pay for a Casco server by wallet transfer. A month is 105 EGP; a year is twelve months. */
export function ServerWalletPay({ projectId, period = 'monthly' }: { projectId: string; period?: 'monthly' | 'yearly' }) {
  const plans = usePlans()
  const [file, setFile] = useState<File | null>(null)
  const [rows, setRows] = useState<WalletRow[]>([])
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ tone: 'success' | 'error'; text: string } | null>(null)
  const phone = plans?.wallet?.phone ?? '01026159280'
  const monthly = plans?.wallet?.serverAmount ?? 105
  const amount = period === 'yearly' ? (plans?.wallet?.serverYearlyAmount ?? monthly * 12) : monthly
  const mine = rows.filter((r) => r.purpose === 'server' && r.projectId === projectId)
  const pending = mine.some((r) => r.status === 'pending')

  const load = () =>
    get<{ items: WalletRow[] }>('/api/billing/wallet-transfers')
      .then((r) => setRows(r.items))
      .catch(() => {})

  useEffect(() => {
    void load()
  }, [projectId])

  const send = async () => {
    if (!file) {
      setMsg({ tone: 'error', text: t('ارفع صورة التحويل') })
      return
    }
    setBusy(true)
    setMsg(null)
    try {
      const fd = new FormData()
      fd.set('purpose', 'server')
      fd.set('projectId', projectId)
      fd.set('interval', period)
      fd.set('method', 'vodafone_cash')
      fd.set('file', file)
      await api('POST', '/api/billing/wallet-transfers', fd)
      setFile(null)
      setMsg({ tone: 'success', text: t('تم إرسال صورة التحويل. هنراجعها ونفعّل السيرفر.') })
      await load()
    } catch (e) {
      setMsg({ tone: 'error', text: errorMessage(e) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="rounded-2xl border border-slate-200 p-4">
      <h4 className="font-semibold text-ink">{t('أو حوّل على المحفظة')}</h4>
      <p className="mt-2 text-2xl font-extrabold tracking-tight">
        {amount} {t('جنيه')}
        <span className="text-sm font-normal text-slate-500"> / {period === 'yearly' ? t('سنة') : t('شهر')}</span>
      </p>
      <p className="mt-1 font-mono text-xl font-extrabold tracking-wide text-ink" dir="ltr">
        {phone}
      </p>
      <p className="mt-2 text-sm leading-relaxed text-slate-600">
        {t('حوّل المبلغ على رقم المحفظة، ثم ارفع صورة التحويل. التفعيل خلال 24 ساعة.')}
      </p>
      <label className="mt-4 flex cursor-pointer items-center justify-center gap-3 rounded-2xl border-2 border-brand-500 bg-brand-50 px-4 py-3 text-sm font-bold text-brand-800">
        <ImagePlus className="h-5 w-5 shrink-0" />
        <span className="truncate">{file ? file.name : t('اختيار صورة التحويل')}</span>
        <input
          type="file"
          accept="image/jpeg,image/png,image/webp"
          className="sr-only"
          onChange={(e) => setFile(e.target.files?.[0] ?? null)}
        />
      </label>
      <Button className="mt-3 w-full" loading={busy} disabled={pending} onClick={() => void send()}>
        {pending ? t('في تحويل قيد المراجعة') : t('إرسال صورة التحويل')}
      </Button>
      {msg && (
        <div className="mt-3">
          <Alert tone={msg.tone}>{msg.text}</Alert>
        </div>
      )}
      {mine.length > 0 && (
        <ul className="mt-3 space-y-1 text-sm text-slate-500">
          {mine.map((r) => (
            <li key={r.id}>
              {r.status === 'approved' ? t('تمت الموافقة') : r.status === 'rejected' ? t('مرفوض') : t('قيد المراجعة')}
              {r.reviewNote ? ` — ${r.reviewNote}` : ''}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
