import { useEffect, useState, type ReactNode } from 'react'
import { del, errorMessage, get, put } from '../lib/api'
import { loadPlans } from '../lib/plans'
import { Alert, Button, Card, Spinner, formatDateTime } from './ui'

export interface PricingSettings {
  proMonthlyPrice: number
  proYearlyPrice: number
  proMonthlyCredits: number
  proMaxProjects: number
  freeMaxProjects: number
  signupBonusCredits: number
  hostingStaticMonthly: number
  hostingBackendMonthly: number
  hostingYearlyPricedMonths: number
  hostingGraceDays: number
  topupPacks: { id: string; credits: number; price: number }[]
  usdPerCredit: number
  infraMonthlyUsd: number
  domainMonthlyUsd: number
  emailMonthlyUsd: number
  paymentFeePercent: number
  operatingExpenses: { name: string; monthlyUsd: number }[]
}

interface PricingResponse {
  current: PricingSettings
  defaults: PricingSettings
  updatedAt: string | null
}

type NumberKey = { [K in keyof PricingSettings]: PricingSettings[K] extends number ? K : never }[keyof PricingSettings]

const money = (n: number) => `$${Number(n.toFixed(2))}`

export default function AdminPricing() {
  const [data, setData] = useState<PricingResponse | null>(null)
  const [form, setForm] = useState<PricingSettings | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ tone: 'success' | 'error'; text: string } | null>(null)

  const apply = (r: PricingResponse) => {
    const current = { ...r.current, domainMonthlyUsd: r.current.domainMonthlyUsd ?? 0, emailMonthlyUsd: r.current.emailMonthlyUsd ?? 0, operatingExpenses: [] }
    setData({ ...r, current })
    setForm(structuredClone(current))
  }

  useEffect(() => {
    get<PricingResponse>('/api/admin/pricing').then(apply).catch((e) => setMsg({ tone: 'error', text: errorMessage(e) }))
  }, [])

  if (!form || !data) return msg ? <Alert tone="error">{msg.text}</Alert> : <Spinner className="h-6 w-6 text-brand-600" />

  const set = (key: NumberKey, value: string) => setForm((f) => (f ? { ...f, [key]: value === '' ? 0 : Number(value) } : f))
  const dirty = JSON.stringify(form) !== JSON.stringify(data.current)

  const run = async (fn: () => Promise<PricingResponse>, success: string) => {
    setBusy(true)
    setMsg(null)
    try {
      apply(await fn())
      await loadPlans(true)
      setMsg({ tone: 'success', text: success })
    } catch (e) {
      setMsg({ tone: 'error', text: errorMessage(e) })
    } finally {
      setBusy(false)
    }
  }

  const field = (key: NumberKey, label: string, opts: { step?: string; suffix?: string; hint?: ReactNode } = {}) => (
    <label className="block">
      <span className="mb-1 block text-sm font-medium text-slate-700">{label}</span>
      <div className="flex items-center gap-2">
        <input
          type="number"
          step={opts.step ?? '1'}
          min="0"
          dir="ltr"
          value={form[key]}
          onChange={(e) => set(key, e.target.value)}
          className="w-full rounded-xl border border-slate-200 px-3 py-2 text-sm outline-none focus:border-brand-500 focus:ring-2 focus:ring-brand-100"
        />
        {opts.suffix && <span className="shrink-0 text-sm text-slate-500">{opts.suffix}</span>}
      </div>
      {opts.hint && <span className="mt-1 block text-xs text-slate-500">{opts.hint}</span>}
      {form[key] !== data.defaults[key] && <span className="mt-0.5 block text-xs text-amber-600">الافتراضي: {data.defaults[key]}</span>}
    </label>
  )

  const proAiCap = form.proMonthlyCredits * form.usdPerCredit
  const feeOf = (price: number) => (price * form.paymentFeePercent) / 100
  const yearlyMonths = form.proMonthlyPrice > 0 ? form.proYearlyPrice / form.proMonthlyPrice : 0

  return (
    <div className="space-y-6">
      <Alert tone="info">
        أي تغيير هنا يطبق فوراً على صفحة الأسعار ورسائل الموقع وعمليات الدفع الجديدة. الاشتراكات والاستضافات المدفوعة قبل التغيير تكمل مدتها بنفس سعرها.
      </Alert>
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}

      <Section title="اشتراك Pro" note="التعديل بالذكاء الاصطناعي، مواقع متعددة الصفحات، الكورسات والإعلانات">
        {field('proMonthlyPrice', 'السعر الشهري', { step: '0.01', suffix: '$' })}
        {field('proYearlyPrice', 'السعر السنوي', { step: '0.01', suffix: '$', hint: yearlyMonths ? `يساوي ${yearlyMonths.toFixed(1)} شهر` : undefined })}
        {field('proMonthlyCredits', 'النقاط الشهرية', {
          hint: `لو استخدم المشترك كل نقاطه: تكلفة AI عليك ${money(proAiCap)} من ${money(form.proMonthlyPrice)} (بعد رسوم الدفع يتبقى ${money(form.proMonthlyPrice - feeOf(form.proMonthlyPrice) - proAiCap)})`
        })}
        {field('proMaxProjects', 'أقصى عدد مواقع')}
      </Section>

      <Section title="الخطة المجانية">
        {field('freeMaxProjects', 'أقصى عدد مواقع')}
        {field('signupBonusCredits', 'نقاط هدية التسجيل', { hint: `تكلفة AI قصوى ${money(form.signupBonusCredits * form.usdPerCredit)} لكل مستخدم جديد` })}
      </Section>

      <Section title="الاستضافة (لكل موقع)" note="بدون تجديد يتوقف الموقع بعد أيام السماح">
        {field('hostingStaticMonthly', 'موقع عادي (شهرياً)', { step: '0.01', suffix: '$' })}
        {field('hostingBackendMonthly', 'موقع + باك إند (شهرياً)', { step: '0.01', suffix: '$' })}
        {field('hostingYearlyPricedMonths', 'السنوي يُحسب كم شهر؟', {
          suffix: 'شهر',
          hint: `السنوي: ${money(form.hostingStaticMonthly * form.hostingYearlyPricedMonths)} للعادي و ${money(form.hostingBackendMonthly * form.hostingYearlyPricedMonths)} للباك إند`
        })}
        {field('hostingGraceDays', 'أيام السماح بعد الانتهاء', { suffix: 'يوم' })}
      </Section>

      <Card>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <h3 className="font-bold">باقات شحن النقاط</h3>
            <p className="text-xs text-slate-500">المعرّف بالإنجليزي الصغير (مثال: small). النقاط لا تنتهي.</p>
          </div>
          <Button
            variant="secondary"
            disabled={form.topupPacks.length >= 10}
            onClick={() => setForm({ ...form, topupPacks: [...form.topupPacks, { id: `pack${form.topupPacks.length + 1}`, credits: 1000, price: 3 }] })}
          >
            + باقة
          </Button>
        </div>
        <div className="mt-3 space-y-2">
          {form.topupPacks.length === 0 && <p className="text-sm text-slate-500">لا توجد باقات، المستخدمون لن يستطيعوا شحن نقاط.</p>}
          {form.topupPacks.map((p, i) => {
            const cost = p.credits * form.usdPerCredit
            const update = (patch: Partial<typeof p>) =>
              setForm({ ...form, topupPacks: form.topupPacks.map((x, j) => (j === i ? { ...x, ...patch } : x)) })
            return (
              <div key={i} className="grid grid-cols-2 items-end gap-2 rounded-xl bg-slate-50 p-3 md:grid-cols-[1fr_1fr_1fr_2fr_auto]">
                <MiniInput label="المعرّف" value={p.id} onChange={(v) => update({ id: v })} />
                <MiniInput label="النقاط" type="number" value={p.credits} onChange={(v) => update({ credits: Number(v) })} />
                <MiniInput label="السعر $" type="number" step="0.01" value={p.price} onChange={(v) => update({ price: Number(v) })} />
                <p className="text-xs text-slate-500">
                  تكلفة AI القصوى {money(cost)} • صافي بعد الرسوم {money(p.price - feeOf(p.price) - cost)}
                  {cost > 0 && ` • ${(p.price / cost).toFixed(1)}× التكلفة`}
                </p>
                <Button variant="ghost" className="text-red-600" onClick={() => setForm({ ...form, topupPacks: form.topupPacks.filter((_, j) => j !== i) })}>
                  حذف
                </Button>
              </div>
            )
          })}
        </div>
      </Card>

      <Section title="حساب التكلفة" note="قيمة النقطة لخصم الـ AI. مصاريف السيرفرات والبريد وغيرها تتسجل في صفحة الحسابات.">
        {field('usdPerCredit', 'قيمة النقطة (تكلفة AI بالدولار)', {
          step: '0.0001',
          suffix: '$',
          hint: 'كل عملية AI تكلفتها الحقيقية ÷ هذه القيمة = النقاط المخصومة. تقليلها يعني خصم نقاط أكثر لنفس العملية.'
        })}
        {field('paymentFeePercent', 'نسبة رسوم الدفع', { step: '0.1', suffix: '%' })}
      </Section>

      <div className="sticky bottom-4 flex flex-wrap items-center gap-2 rounded-2xl border border-slate-200 bg-white/95 p-3 shadow-lg backdrop-blur">
        <Button loading={busy} disabled={!dirty} onClick={() => run(() => put<PricingResponse>('/api/admin/pricing', form), 'تم حفظ الأسعار وتطبيقها فوراً')}>
          حفظ الأسعار
        </Button>
        <Button variant="secondary" disabled={!dirty || busy} onClick={() => setForm(structuredClone(data.current))}>
          تراجع
        </Button>
        <Button
          variant="ghost"
          disabled={busy}
          onClick={() => confirm('الرجوع لأسعار ملف الإعدادات؟') && run(() => del<PricingResponse>('/api/admin/pricing'), 'تمت العودة للأسعار الافتراضية')}
        >
          استرجاع الافتراضي
        </Button>
        <span className="ms-auto text-xs text-slate-500">
          {dirty ? 'تغييرات غير محفوظة' : data.updatedAt ? `آخر تعديل: ${formatDateTime(data.updatedAt)}` : 'الأسعار الافتراضية من ملف الإعدادات'}
        </span>
      </div>
    </div>
  )
}

function Section({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <Card>
      <h3 className="font-bold">{title}</h3>
      {note && <p className="text-xs text-slate-500">{note}</p>}
      <div className="mt-4 grid gap-4 md:grid-cols-2">{children}</div>
    </Card>
  )
}

function MiniInput({ label, value, onChange, type = 'text', step, dir }: { label: string; value: string | number; onChange: (v: string) => void; type?: string; step?: string; dir?: 'ltr' | 'rtl' | 'auto' }) {
  return (
    <label className="block">
      <span className="mb-0.5 block text-xs text-slate-500">{label}</span>
      <input
        type={type}
        step={step}
        dir={dir ?? (type === 'number' ? 'ltr' : 'auto')}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="w-full rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-sm outline-none focus:border-brand-500"
      />
    </label>
  )
}
