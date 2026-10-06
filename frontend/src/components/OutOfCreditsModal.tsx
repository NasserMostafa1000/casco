import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Zap } from 'lucide-react'
import { errorMessage, post } from '../lib/api'
import { formatDate, num, t } from '../lib/i18n'
import { usd, usePlans } from '../lib/plans'
import { rememberCheckout, tiktokClick } from '../lib/tiktok'
import { Alert, Button, Modal } from './ui'

export interface CreditShortfall {
  available: number
  needed: number
  isPro: boolean
  tryStandard: boolean
  refillAt: string | null
}

/** Shown when a request is refused for lack of credits: top up in place instead of hunting for the billing page. */
export function OutOfCreditsModal({
  info,
  onClose,
  onUseStandard,
}: {
  info: { message: string; details: CreditShortfall } | null
  onClose: () => void
  onUseStandard: () => void
}) {
  const plans = usePlans()
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState('')
  if (!info) return null
  const d = info.details

  const buy = async (packId: string) => {
    setBusy(packId)
    setError('')
    try {
      const click = tiktokClick()
      const r = await post<{ paymentId: string; redirectUrl: string }>('/api/billing/checkout', { kind: 'topup', packId, ttclid: click.ttclid, ttp: click.ttp })
      const pack = plans?.topups.find((p) => p.id === packId)
      if (pack && plans) {
        rememberCheckout({
          content_id: pack.id,
          content_name: 'Casco credits',
          value: pack.price,
          currency: plans.currency || 'USD',
        }, r.paymentId)
      }
      location.href = r.redirectUrl
    } catch (e) {
      setError(errorMessage(e))
      setBusy(null)
    }
  }

  return (
    <Modal open onClose={onClose} title={d.tryStandard ? t('رصيدك لا يكفي للوضع القوي') : t('خلصت نقاطك')}>
      <div className="space-y-4">
        <p className="text-slate-600">{info.message}</p>
        {error && <Alert tone="error">{error}</Alert>}

        {d.tryStandard && (
          <Button className="w-full" onClick={onUseStandard}>
            {t('أرسل طلبي بالوضع العادي')}
          </Button>
        )}

        <div>
          <p className="mb-2 text-sm font-semibold">{t('اشحن رصيدك (النقاط لا تنتهي)')}</p>
          <div className="grid gap-2">
            {(plans?.topups ?? []).map((p) => (
              <button
                key={p.id}
                disabled={busy !== null}
                onClick={() => buy(p.id)}
                className="flex items-center justify-between rounded-xl border border-slate-200 p-3 text-start transition hover:border-brand-400 hover:bg-brand-50/40 disabled:opacity-60"
              >
                <span className="flex items-center gap-2 font-semibold">
                  <Zap className="h-4 w-4 fill-amber-400 text-amber-500" />
                  {t('{n} نقطة', { n: num(p.credits) })}
                </span>
                <span className="rounded-lg bg-ink px-3 py-1 text-sm font-semibold text-white">{busy === p.id ? '...' : usd(p.price)}</span>
              </button>
            ))}
            {plans && plans.topups.length === 0 && <p className="text-sm text-slate-500">{t('لا توجد باقات شحن متاحة حالياً، تواصل مع الدعم.')}</p>}
          </div>
        </div>

        {d.isPro && d.refillAt && (
          <p className="text-xs text-slate-500">
            {t('أو انتظر نقاطك الجديدة ({n} نقطة) يوم {date}.', { n: plans ? num(plans.pro.monthlyCredits) : '…', date: formatDate(d.refillAt) })}
          </p>
        )}
        {!d.isPro && (
          <Link to="/app/billing" className="block rounded-xl border border-brand-200 py-2.5 text-center font-semibold text-brand-700 hover:bg-brand-50">
            {t('أو اشترك في Pro بـ {price} شهرياً ({n} نقطة كل شهر)', { price: usd(plans?.pro.monthlyPrice), n: plans ? num(plans.pro.monthlyCredits) : '…' })}
          </Link>
        )}
      </div>
    </Modal>
  )
}
