import { useEffect, useState } from 'react'
import { Check } from 'lucide-react'
import { errorMessage, get, post, type HostingStatus, type HostingTier } from '../lib/api'
import { formatDate, t } from '../lib/i18n'
import { usd, usePlans, yearlySavingLabel } from '../lib/plans'
import { rememberCheckout, tiktokClick } from '../lib/tiktok'
import { Alert, Badge, Button, Modal, Segmented, Spinner } from './ui'

const tierInfo = (): Record<HostingTier, { title: string; points: string[] }> => ({
  static: {
    title: t('استضافة الموقع'),
    points: [t('صفحات موقعك على سيرفرات Casco'), t('نموذج التواصل ورسائل الزوار'), t('شهادة أمان HTTPS ودومين خاص')],
  },
  backend: {
    title: t('استضافة الموقع + الباك إند'),
    points: [t('كل مميزات استضافة الموقع'), t('حسابات الأعضاء وقاعدة البيانات'), t('المتجر والطلبات والحجوزات والكورسات والإعلانات'), t('دوال الخادم والتنبيهات')],
  },
})

/** Lets the owner pay the monthly/yearly hosting of one site (required to publish and to keep it online). */
export function HostingModal({ projectId, open, onClose, reason }: { projectId: string; open: boolean; onClose: () => void; reason?: string }) {
  const plans = usePlans()
  const [status, setStatus] = useState<HostingStatus | null>(null)
  const [period, setPeriod] = useState<'monthly' | 'yearly'>('monthly')
  const yearlySaving = status ? yearlySavingLabel(status.prices.static.monthly, status.prices.static.yearly) : null
  const [busy, setBusy] = useState<HostingTier | null>(null)
  const [error, setError] = useState('')
  const info = tierInfo()

  useEffect(() => {
    if (!open) return
    setError('')
    get<HostingStatus>(`/api/projects/${projectId}/hosting`).then(setStatus).catch((e) => setError(errorMessage(e)))
  }, [open, projectId])

  const checkout = async (tier: HostingTier) => {
    setBusy(tier)
    setError('')
    try {
      const click = tiktokClick()
      const r = await post<{ paymentId: string; redirectUrl: string }>('/api/billing/checkout', { kind: 'hosting', projectId, tier, interval: period, ttclid: click.ttclid, ttp: click.ttp })
      if (status) {
        rememberCheckout({
          content_id: `hosting-${tier}-${period}`,
          content_name: tier === 'backend' ? 'Casco hosting with backend' : 'Casco hosting',
          value: status.prices[tier][period],
          currency: status.prices.currency || 'USD',
        }, r.paymentId)
      }
      location.href = r.redirectUrl
    } catch (e) {
      setError(errorMessage(e))
      setBusy(null)
    }
  }

  const tierName = (tier: HostingTier) => (tier === 'backend' ? t('موقع + باك إند') : t('موقع'))

  return (
    <Modal open={open} onClose={onClose} title={t('استضافة موقعك على Casco')}>
      {!status ? (
        error ? <Alert tone="error">{error}</Alert> : <Spinner className="h-6 w-6 text-brand-600" />
      ) : (
        <div className="space-y-4">
          {reason && <Alert tone="info">{reason}</Alert>}
          {error && <Alert tone="error">{error}</Alert>}
          {status.paidUntil && (
            <p className="flex flex-wrap items-center gap-2 text-sm text-slate-600">
              {t('الاستضافة الحالية:')} <b>{tierName(status.tier ?? status.requiredTier)}</b> — {status.active ? t('فعّالة حتى') : t('انتهت في')} {formatDate(status.paidUntil)}
              {status.inGrace && <Badge color="amber">{t('فترة سماح')}</Badge>}
            </p>
          )}
          <Segmented
            className="flex w-full [&>button]:flex-1"
            value={period}
            onChange={setPeriod}
            options={[
              { value: 'monthly', label: t('شهري') },
              { value: 'yearly', label: yearlySaving ? `${t('سنوي')} (${yearlySaving})` : t('سنوي') },
            ]}
          />
          <div className="grid gap-3">
            {(['static', 'backend'] as const).map((tier) => {
              const price = status.prices[tier][period]
              const blocked = tier === 'static' && status.requiredTier === 'backend'
              return (
                <div
                  key={tier}
                  className={`rounded-2xl border p-4 ${status.requiredTier === tier ? 'border-brand-400 bg-brand-50/40 ring-2 ring-brand-500/10' : 'border-slate-200'} ${blocked ? 'opacity-50' : ''}`}
                >
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <h4 className="flex flex-wrap items-center gap-2 font-semibold text-ink">
                        {info[tier].title} {status.requiredTier === tier && <Badge color="brand">{t('مناسب لموقعك')}</Badge>}
                      </h4>
                      <p className="mt-1 text-2xl font-extrabold tracking-tight">
                        ${price}
                        <span className="text-sm font-normal text-slate-500"> / {period === 'monthly' ? t('شهر') : t('سنة')}</span>
                      </p>
                    </div>
                    <Button loading={busy === tier} disabled={blocked || busy !== null} onClick={() => checkout(tier)}>
                      {status.tier === tier && status.active ? t('تجديد') : t('اشترك')}
                    </Button>
                  </div>
                  <ul className="mt-3 space-y-1 text-sm text-slate-600">
                    {info[tier].points.map((p) => (
                      <li key={p} className="flex gap-2">
                        <Check className="mt-0.5 h-4 w-4 shrink-0 text-brand-600" />
                        {p}
                      </li>
                    ))}
                  </ul>
                  {blocked && <p className="mt-2 text-xs text-slate-500">{t('موقعك يستخدم الباك إند (حسابات أو بيانات أو طلبات)، لذلك يحتاج استضافة الباك إند.')}</p>}
                </div>
              )
            })}
          </div>
          <p className="text-xs text-slate-500">
            {t('الاستضافة لكل موقع على حدة. إذا لم تُجدَّد يتوقف الموقع بعد {days} أيام سماح. التعديل على الموقع بالذكاء الاصطناعي يحتاج اشتراك Casco Pro (من {price} شهرياً).', {
              days: status.prices.graceDays,
              price: usd(plans?.pro.monthlyPrice),
            })}
          </p>
          <p className="text-xs font-medium text-slate-600">
            {t('لا يوجد تجديد تلقائي. لازم تجدّد الاستضافة بنفسك قبل ما المدة تخلّص حتى ما يتوقف الموقع.')}
          </p>
        </div>
      )}
    </Modal>
  )
}
