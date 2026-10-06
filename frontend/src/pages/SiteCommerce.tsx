import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { HostingModal } from '../components/HostingModal'
import { Alert, Badge, Button, Card, Empty, Input, Modal, Spinner, Textarea, formatDate, formatDateTime } from '../components/ui'
import { api, del, errorMessage, get, patch, post, put, supportWhatsAppUrl, SUPPORT_WHATSAPP, type HostingStatus } from '../lib/api'
import { t } from '../lib/i18n'

function useData<T>(url: string) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState('')
  const reload = useCallback(() => get<T>(url).then(setData).catch((e) => setError(errorMessage(e))), [url])
  useEffect(() => {
    void reload()
  }, [reload])
  return { data, error, reload }
}

function Loading({ error }: { error: string }) {
  return error ? <Alert tone="error">{error}</Alert> : <Spinner className="h-6 w-6 text-brand-600" />
}

function SubTabs<T extends string>({ value, onChange, items }: { value: T; onChange: (v: T) => void; items: [T, string][] }) {
  return (
    <div className="mb-4 flex gap-2">
      {items.map(([k, label]) => (
        <button key={k} onClick={() => onChange(k)} className={`rounded-full px-4 py-1.5 text-sm font-semibold ${value === k ? 'bg-slate-900 text-white' : 'bg-slate-100'}`}>
          {label}
        </button>
      ))}
    </div>
  )
}

async function uploadImage(projectId: string, file: File) {
  const fd = new FormData()
  fd.append('file', file)
  return (await api<{ url: string }>('POST', `/api/projects/${projectId}/data/uploads`, fd)).url
}

const money = (v: number, currency = '') => `${Number(v).toLocaleString('ar', { maximumFractionDigits: 2 })} ${currency}`

// ---------- Store ----------
interface Product {
  id?: string
  name: string
  description: string
  price: number
  comparePrice: number | null
  category: string | null
  images: string[]
  stock: number | null
  isActive: boolean
  sortOrder: number
}
const emptyProduct: Product = { name: '', description: '', price: 0, comparePrice: null, category: '', images: [], stock: null, isActive: true, sortOrder: 0 }

interface OrderLine {
  productId: string
  name: string
  price: number
  quantity: number
}
interface OrderRow {
  id: string
  number: number
  customerName: string
  phone: string
  email: string | null
  address: string | null
  city: string | null
  notes: string | null
  items: OrderLine[]
  subtotal: number
  shippingFee: number
  total: number
  currency: string
  paymentLabel: string
  status: string
  createdAt: string
  whatsappUrl: string
}

function orderStatusList(): [string, string, 'amber' | 'brand' | 'slate' | 'green' | 'red'][] {
  return [
    ['new', t('جديد'), 'amber'],
    ['confirmed', t('مؤكد'), 'brand'],
    ['shipped', t('تم الشحن'), 'slate'],
    ['delivered', t('تم التسليم'), 'green'],
    ['canceled', t('ملغي'), 'red'],
  ]
}

export function Store({ projectId }: { projectId: string }) {
  const [tab, setTab] = useState<'orders' | 'products'>('orders')
  return (
    <div>
      <SubTabs value={tab} onChange={setTab} items={[['orders', t('الطلبات')], ['products', t('المنتجات')]]} />
      <OnlinePaymentNote />
      <div className="mt-4">{tab === 'orders' ? <Orders projectId={projectId} /> : <Products projectId={projectId} />}</div>
    </div>
  )
}

function OnlinePaymentNote() {
  return (
    <Alert tone="info">
      {t('المتجر يستقبل الطلبات بالدفع عند الاستلام أو عبر واتساب أو التحويل البنكي أو الاستلام من المحل (تختارها من الإعدادات). الدفع الإلكتروني بالبطاقات يحتاج رخصة شركة وحساب بنكي بنفس اسم الرخصة، ومبرمج للتأكد من أن عمليات الدفع تتم بأمان — تواصل مع الدعم الفني لـ Casco على واتساب')}{' '}
      <a href={supportWhatsAppUrl()} target="_blank" rel="noreferrer" className="font-bold underline" dir="ltr">
        {SUPPORT_WHATSAPP}
      </a>
    </Alert>
  )
}

function Orders({ projectId }: { projectId: string }) {
  const orderStatuses = orderStatusList()
  const [status, setStatus] = useState('')
  const { data, error, reload } = useData<{ total: number; newCount: number; items: OrderRow[] }>(`/api/projects/${projectId}/data/orders${status ? `?status=${status}` : ''}`)
  const setOrderStatus = (id: string, s: string) => post(`/api/projects/${projectId}/data/orders/${id}/status`, { status: s }).then(reload)
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-2">
        {[['', t('الكل')] as const, ...orderStatuses.map(([k, l]) => [k, l] as const)].map(([k, label]) => (
          <button key={k} onClick={() => setStatus(k)} className={`rounded-full px-3 py-1 text-sm ${status === k ? 'bg-brand-600 text-white' : 'bg-slate-100'}`}>
            {label}
          </button>
        ))}
      </div>
      {!data ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <Empty title={t('لا توجد طلبات بعد')}>{t('عندما يطلب العملاء من متجرك ستظهر طلباتهم هنا، وتصلك رسالة على بريدك.')}</Empty>
      ) : (
        data.items.map((o) => {
          const st = orderStatuses.find((s) => s[0] === o.status)
          return (
            <Card key={o.id}>
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="space-y-1 text-sm">
                  <h3 className="text-base font-bold">
                    {t('طلب #{n}', { n: o.number })} <Badge color={st?.[2] ?? 'slate'}>{st?.[1] ?? o.status}</Badge>
                  </h3>
                  <p>
                    <b>{o.customerName}</b> • <span dir="ltr">{o.phone}</span> {o.email && <span dir="ltr">• {o.email}</span>}
                  </p>
                  {(o.address || o.city) && (
                    <p className="text-slate-600">
                      📍 {o.address} {o.city && `${t('،')} ${o.city}`}
                    </p>
                  )}
                  <ul className="text-slate-600">
                    {o.items.map((i) => (
                      <li key={i.productId}>
                        {i.quantity} × {i.name} — {money(i.price * i.quantity, o.currency)}
                      </li>
                    ))}
                  </ul>
                  {o.notes && <p className="text-slate-500">{t('ملاحظات:')} {o.notes}</p>}
                  <p>
                    {t('الإجمالي:')} <b>{money(o.total, o.currency)}</b> {o.shippingFee > 0 && <span className="text-slate-500">{t('(منها توصيل {fee})', { fee: money(o.shippingFee, o.currency) })}</span>} •{' '}
                    {t(o.paymentLabel)}
                  </p>
                  <p className="text-xs text-slate-400">{formatDateTime(o.createdAt)}</p>
                </div>
                <div className="flex shrink-0 flex-col gap-2">
                  <select className="rounded-xl border border-slate-200 px-3 py-2 text-sm" value={o.status} onChange={(e) => setOrderStatus(o.id, e.target.value)}>
                    {orderStatuses.map(([k, l]) => (
                      <option key={k} value={k}>
                        {l}
                      </option>
                    ))}
                  </select>
                  {o.whatsappUrl && (
                    <a href={o.whatsappUrl} target="_blank" rel="noreferrer" className="rounded-xl bg-emerald-600 px-3 py-2 text-center text-sm font-bold text-white">
                      {t('واتساب العميل')}
                    </a>
                  )}
                </div>
              </div>
            </Card>
          )
        })
      )}
    </div>
  )
}

function Products({ projectId }: { projectId: string }) {
  const { data, error, reload } = useData<Product[]>(`/api/projects/${projectId}/data/products`)
  const [editing, setEditing] = useState<Product | null>(null)
  if (!data) return <Loading error={error} />
  return (
    <div className="space-y-3">
      <div className="flex justify-between">
        <p className="text-sm text-slate-500">{t('المنتجات التي تضيفها هنا تظهر تلقائياً في متجرك.')}</p>
        <Button onClick={() => setEditing({ ...emptyProduct })}>{t('+ منتج جديد')}</Button>
      </div>
      {data.length === 0 && <Empty title={t('لا توجد منتجات بعد')}>{t('أضف أول منتج ليظهر في متجرك.')}</Empty>}
      <div className="grid gap-3 sm:grid-cols-2">
        {data.map((p) => (
          <Card key={p.id} className="flex gap-3">
            {p.images[0] ? <img src={p.images[0]} alt="" className="h-20 w-20 rounded-lg object-cover" /> : <div className="h-20 w-20 rounded-lg bg-slate-100" />}
            <div className="min-w-0 flex-1">
              <h3 className="font-bold">
                {p.name} {!p.isActive && <Badge>{t('مخفي')}</Badge>} {p.stock === 0 && <Badge color="red">{t('نفد')}</Badge>}
              </h3>
              <p className="text-sm text-slate-500">
                {money(p.price)} {p.category && `• ${p.category}`} {p.stock !== null && `• ${t('المخزون {n}', { n: p.stock })}`}
              </p>
              <div className="mt-2 flex gap-1">
                <Button variant="secondary" onClick={() => setEditing(p)}>
                  {t('تعديل')}
                </Button>
                <Button variant="ghost" onClick={() => confirm(t('حذف المنتج؟')) && del(`/api/projects/${projectId}/data/products/${p.id}`).then(reload)}>
                  {t('حذف')}
                </Button>
              </div>
            </div>
          </Card>
        ))}
      </div>
      {editing && <ProductForm projectId={projectId} product={editing} onClose={() => setEditing(null)} onSaved={reload} />}
    </div>
  )
}

function ProductForm({ projectId, product, onClose, onSaved }: { projectId: string; product: Product; onClose: () => void; onSaved: () => void }) {
  const [p, setP] = useState<Product>(product)
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const set = <K extends keyof Product>(k: K, v: Product[K]) => setP((prev) => ({ ...prev, [k]: v }))

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError('')
    const body = { ...p, price: Number(p.price) || 0, comparePrice: p.comparePrice ? Number(p.comparePrice) : null, stock: p.stock === null || (p.stock as unknown) === '' ? null : Number(p.stock) }
    try {
      if (p.id) await put(`/api/projects/${projectId}/data/products/${p.id}`, body)
      else await post(`/api/projects/${projectId}/data/products`, body)
      onSaved()
      onClose()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal open onClose={onClose} title={p.id ? t('تعديل المنتج') : t('منتج جديد')}>
      <form onSubmit={save} className="space-y-3">
        {error && <Alert tone="error">{error}</Alert>}
        <Input label={t('اسم المنتج')} required value={p.name} onChange={(e) => set('name', e.target.value)} />
        <Textarea label={t('الوصف')} rows={3} value={p.description} onChange={(e) => set('description', e.target.value)} />
        <div className="grid grid-cols-2 gap-3">
          <Input label={t('السعر')} type="number" min={0} step="0.01" required value={p.price} onChange={(e) => set('price', Number(e.target.value))} />
          <Input label={t('السعر قبل الخصم (اختياري)')} type="number" min={0} step="0.01" value={p.comparePrice ?? ''} onChange={(e) => set('comparePrice', e.target.value ? Number(e.target.value) : null)} />
          <Input label={t('التصنيف')} value={p.category ?? ''} onChange={(e) => set('category', e.target.value)} />
          <Input label={t('المخزون (فارغ = غير محدود)')} type="number" min={0} value={p.stock ?? ''} onChange={(e) => set('stock', e.target.value === '' ? null : Number(e.target.value))} />
        </div>
        <div>
          <span className="mb-1 block text-sm font-medium text-slate-700">{t('الصور')}</span>
          <div className="flex flex-wrap gap-2">
            {p.images.map((url) => (
              <div key={url} className="relative">
                <img src={url} alt="" className="h-16 w-16 rounded-lg object-cover" />
                <button type="button" className="absolute -top-2 -left-2 h-5 w-5 rounded-full bg-red-600 text-xs text-white" onClick={() => set('images', p.images.filter((u) => u !== url))}>
                  ✕
                </button>
              </div>
            ))}
            <label className="grid h-16 w-16 cursor-pointer place-items-center rounded-lg border-2 border-dashed border-slate-300 text-2xl text-slate-400">
              +
              <input
                type="file"
                accept="image/*"
                className="hidden"
                onChange={async (e) => {
                  const file = e.target.files?.[0]
                  if (!file) return
                  try {
                    const url = await uploadImage(projectId, file)
                    setP((prev) => ({ ...prev, images: [...prev.images, url] }))
                  } catch (err) {
                    setError(errorMessage(err))
                  }
                }}
              />
            </label>
          </div>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={p.isActive} onChange={(e) => set('isActive', e.target.checked)} /> {t('ظاهر في المتجر')}
        </label>
        <Button type="submit" loading={saving} className="w-full">
          {t('حفظ')}
        </Button>
      </form>
    </Modal>
  )
}

// ---------- Bookings ----------
interface ServiceRow {
  id?: string
  name: string
  description: string
  durationMinutes: number
  price: number
  isActive: boolean
  sortOrder: number
}
interface BookingRow {
  id: string
  service: string
  localTime: string
  customerName: string
  phone: string
  email: string | null
  notes: string | null
  status: string
  whatsappUrl: string
}
function bookingStatusList(): [string, string, 'amber' | 'brand' | 'green' | 'red'][] {
  return [
    ['pending', t('بانتظار التأكيد'), 'amber'],
    ['confirmed', t('مؤكد'), 'brand'],
    ['completed', t('تم'), 'green'],
    ['canceled', t('ملغي'), 'red'],
  ]
}

export function Bookings({ projectId }: { projectId: string }) {
  const [tab, setTab] = useState<'bookings' | 'services'>('bookings')
  return (
    <div>
      <SubTabs value={tab} onChange={setTab} items={[['bookings', t('الحجوزات')], ['services', t('الخدمات')]]} />
      {tab === 'bookings' ? <BookingList projectId={projectId} /> : <Services projectId={projectId} />}
      <p className="mt-4 text-sm text-slate-500">{t('ساعات العمل وعدد الحجوزات في نفس الوقت تضبطها من تبويب الإعدادات.')}</p>
    </div>
  )
}

function BookingList({ projectId }: { projectId: string }) {
  const bookingStatuses = bookingStatusList()
  const [upcoming, setUpcoming] = useState(true)
  const { data, error, reload } = useData<{ pendingCount: number; items: BookingRow[] }>(`/api/projects/${projectId}/data/bookings?upcoming=${upcoming}`)
  return (
    <div className="space-y-3">
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={upcoming} onChange={(e) => setUpcoming(e.target.checked)} /> {t('القادمة فقط')}
      </label>
      {!data ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <Empty title={t('لا توجد حجوزات')}>{t('عندما يحجز العملاء من موقعك ستظهر الحجوزات هنا.')}</Empty>
      ) : (
        data.items.map((b) => {
          const st = bookingStatuses.find((s) => s[0] === b.status)
          return (
            <Card key={b.id} className="flex flex-wrap items-center justify-between gap-3">
              <div className="text-sm">
                <h3 className="text-base font-bold">
                  {b.service} <Badge color={st?.[2] ?? 'amber'}>{st?.[1] ?? b.status}</Badge>
                </h3>
                <p dir="ltr" className="text-end font-semibold">
                  {b.localTime}
                </p>
                <p>
                  {b.customerName} • <span dir="ltr">{b.phone}</span>
                </p>
                {b.notes && <p className="text-slate-500">{b.notes}</p>}
              </div>
              <div className="flex gap-2">
                <select
                  className="rounded-xl border border-slate-200 px-3 py-2 text-sm"
                  value={b.status}
                  onChange={(e) => post(`/api/projects/${projectId}/data/bookings/${b.id}/status`, { status: e.target.value }).then(reload)}
                >
                  {bookingStatuses.map(([k, l]) => (
                    <option key={k} value={k}>
                      {l}
                    </option>
                  ))}
                </select>
                {b.whatsappUrl && (
                  <a href={b.whatsappUrl} target="_blank" rel="noreferrer" className="rounded-xl bg-emerald-600 px-3 py-2 text-sm font-bold text-white">
                    {t('واتساب')}
                  </a>
                )}
              </div>
            </Card>
          )
        })
      )}
    </div>
  )
}

function Services({ projectId }: { projectId: string }) {
  const { data, error, reload } = useData<ServiceRow[]>(`/api/projects/${projectId}/data/booking-services`)
  const [editing, setEditing] = useState<ServiceRow | null>(null)
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState('')
  if (!data) return <Loading error={error} />

  const save = async (e: FormEvent) => {
    e.preventDefault()
    if (!editing) return
    setSaving(true)
    setFormError('')
    try {
      const body = { ...editing, price: Number(editing.price) || 0, durationMinutes: Number(editing.durationMinutes) || 30 }
      if (editing.id) await put(`/api/projects/${projectId}/data/booking-services/${editing.id}`, body)
      else await post(`/api/projects/${projectId}/data/booking-services`, body)
      setEditing(null)
      await reload()
    } catch (err) {
      setFormError(errorMessage(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <Button onClick={() => setEditing({ name: '', description: '', durationMinutes: 30, price: 0, isActive: true, sortOrder: data.length })}>{t('+ خدمة جديدة')}</Button>
      </div>
      {data.length === 0 && <Empty title={t('لا توجد خدمات')}>{t('أضف الخدمات التي يمكن حجزها (مثل: كشف، قص شعر، استشارة).')}</Empty>}
      {data.map((s) => (
        <Card key={s.id} className="flex items-center justify-between gap-3">
          <div>
            <h3 className="font-bold">
              {s.name} {!s.isActive && <Badge>{t('مخفية')}</Badge>}
            </h3>
            <p className="text-sm text-slate-500">
              {t('{n} دقيقة', { n: s.durationMinutes })} • {s.price > 0 ? money(s.price) : t('مجاني')}
            </p>
          </div>
          <div className="flex gap-1">
            <Button variant="secondary" onClick={() => setEditing(s)}>
              {t('تعديل')}
            </Button>
            <Button variant="ghost" onClick={() => confirm(t('حذف الخدمة؟')) && del(`/api/projects/${projectId}/data/booking-services/${s.id}`).then(reload)}>
              {t('حذف')}
            </Button>
          </div>
        </Card>
      ))}
      {editing && (
        <Modal open onClose={() => setEditing(null)} title={editing.id ? t('تعديل الخدمة') : t('خدمة جديدة')}>
          <form onSubmit={save} className="space-y-3">
            {formError && <Alert tone="error">{formError}</Alert>}
            <Input label={t('اسم الخدمة')} required value={editing.name} onChange={(e) => setEditing({ ...editing, name: e.target.value })} />
            <Textarea label={t('الوصف')} rows={2} value={editing.description} onChange={(e) => setEditing({ ...editing, description: e.target.value })} />
            <div className="grid grid-cols-2 gap-3">
              <Input label={t('المدة (دقيقة)')} type="number" min={5} value={editing.durationMinutes} onChange={(e) => setEditing({ ...editing, durationMinutes: Number(e.target.value) })} />
              <Input label={t('السعر')} type="number" min={0} step="0.01" value={editing.price} onChange={(e) => setEditing({ ...editing, price: Number(e.target.value) })} />
            </div>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={editing.isActive} onChange={(e) => setEditing({ ...editing, isActive: e.target.checked })} /> {t('متاحة للحجز')}
            </label>
            <Button type="submit" loading={saving} className="w-full">
              {t('حفظ')}
            </Button>
          </form>
        </Modal>
      )}
    </div>
  )
}

// ---------- Custom collections ----------
interface FieldInfo {
  name: string
  type: string
  label: string
  required: boolean
  options: string[] | null
  private: boolean
  readonly: boolean
}
interface CollectionInfo {
  name: string
  label: string
  count: number
  fields: FieldInfo[]
}
type RecordRow = Record<string, unknown> & { id: string; createdAt: string }

export function Collections({ projectId }: { projectId: string }) {
  const { data, error } = useData<CollectionInfo[]>(`/api/projects/${projectId}/data/collections`)
  const [active, setActive] = useState<string | null>(null)
  if (!data) return <Loading error={error} />
  if (data.length === 0) return <Empty title={t('لا توجد بيانات مخصصة')}>{t('عندما يضيف الذكاء الاصطناعي قوائم بيانات لموقعك (مثل التقييمات أو التسجيلات) ستظهر هنا.')}</Empty>
  const current = data.find((c) => c.name === active) ?? data[0]
  return (
    <div>
      <div className="mb-4 flex flex-wrap gap-2">
        {data.map((c) => (
          <button key={c.name} onClick={() => setActive(c.name)} className={`rounded-full px-4 py-1.5 text-sm font-semibold ${current.name === c.name ? 'bg-slate-900 text-white' : 'bg-slate-100'}`}>
            {c.label} ({c.count})
          </button>
        ))}
      </div>
      <CollectionTable key={current.name} projectId={projectId} collection={current} />
    </div>
  )
}

function display(v: unknown) {
  if (v === null || v === undefined || v === '') return '—'
  if (typeof v === 'boolean') return v ? '✓' : '✗'
  if (Array.isArray(v)) return v.join(t('، '))
  return String(v)
}

function CollectionTable({ projectId, collection }: { projectId: string; collection: CollectionInfo }) {
  const base = `/api/projects/${projectId}/data/collections/${collection.name}`
  const [q, setQ] = useState('')
  const { data, error, reload } = useData<{ items: RecordRow[]; total: number }>(`${base}${q ? `?q=${encodeURIComponent(q)}` : ''}`)
  const [editing, setEditing] = useState<Record<string, unknown> | null>(null)
  const [formError, setFormError] = useState('')
  const fields = collection.fields

  const save = async (e: FormEvent) => {
    e.preventDefault()
    if (!editing) return
    setFormError('')
    const body: Record<string, unknown> = {}
    for (const f of fields) {
      const v = editing[f.name]
      if (v === undefined) continue
      if (f.type === 'number' || f.type === 'integer') body[f.name] = v === '' ? null : Number(v)
      else if (f.type === 'images' || f.type === 'tags') body[f.name] = typeof v === 'string' ? v.split(/[,،\n]/).map((s) => s.trim()).filter(Boolean) : v
      else body[f.name] = v
    }
    try {
      if (editing.id) await patch(`${base}/${editing.id as string}`, body)
      else await post(base, body)
      setEditing(null)
      await reload()
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap justify-between gap-2">
        <input className="rounded-xl border border-slate-200 px-3 py-2 text-sm" placeholder={t('بحث…')} value={q} onChange={(e) => setQ(e.target.value)} />
        <Button onClick={() => setEditing({})}>{t('+ إضافة')}</Button>
      </div>
      {!data ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <Empty title={t('لا توجد عناصر بعد')} />
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-slate-500">
              <tr>
                {fields.map((f) => (
                  <th key={f.name} className="p-3 text-start whitespace-nowrap">
                    {f.label} {f.private && '🔒'}
                  </th>
                ))}
                <th className="p-3 text-start">{t('التاريخ')}</th>
                <th className="p-3" />
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={r.id} className="border-t border-slate-100">
                  {fields.map((f) => (
                    <td key={f.name} className="max-w-60 truncate p-3">
                      {f.type === 'image' && typeof r[f.name] === 'string' ? <img src={r[f.name] as string} alt="" className="h-10 w-10 rounded object-cover" /> : display(r[f.name])}
                    </td>
                  ))}
                  <td className="p-3 whitespace-nowrap text-slate-400">{formatDateTime(r.createdAt)}</td>
                  <td className="p-3 whitespace-nowrap text-end">
                    <button className="text-brand-600" onClick={() => setEditing({ ...r })}>
                      {t('تعديل')}
                    </button>{' '}
                    <button className="text-red-600" onClick={() => confirm(t('حذف العنصر؟')) && del(`${base}/${r.id}`).then(reload)}>
                      {t('حذف')}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
      {editing && (
        <Modal open onClose={() => setEditing(null)} title={editing.id ? t('تعديل') : t('إضافة')}>
          <form onSubmit={save} className="space-y-3">
            {formError && <Alert tone="error">{formError}</Alert>}
            {fields.map((f) => {
              const value = editing[f.name]
              const setValue = (v: unknown) => setEditing({ ...editing, [f.name]: v })
              if (f.type === 'boolean')
                return (
                  <label key={f.name} className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={value === true} onChange={(e) => setValue(e.target.checked)} /> {f.label}
                  </label>
                )
              if (f.type === 'select')
                return (
                  <label key={f.name} className="block text-sm">
                    <span className="mb-1 block font-medium text-slate-700">{f.label}</span>
                    <select className="w-full rounded-xl border border-slate-200 px-3 py-2.5" value={(value as string) ?? ''} onChange={(e) => setValue(e.target.value)}>
                      <option value="">—</option>
                      {f.options?.map((o) => (
                        <option key={o}>{o}</option>
                      ))}
                    </select>
                  </label>
                )
              if (f.type === 'longtext') return <Textarea key={f.name} label={f.label} rows={3} value={(value as string) ?? ''} onChange={(e) => setValue(e.target.value)} />
              const inputType = f.type === 'number' || f.type === 'integer' ? 'number' : f.type === 'date' ? 'date' : f.type === 'email' ? 'email' : 'text'
              const text = Array.isArray(value) ? value.join(t('، ')) : ((value as string | number | undefined) ?? '')
              return <Input key={f.name} label={f.label + (f.required ? ' *' : '')} type={inputType} value={text} onChange={(e) => setValue(e.target.value)} />
            })}
            <Button type="submit" className="w-full">
              {t('حفظ')}
            </Button>
          </form>
        </Modal>
      )}
    </div>
  )
}

// ---------- Site settings ----------
interface WorkingDay {
  day: number
  open: string
  close: string
  closed: boolean
}
interface SiteSettings {
  whatsApp: string | null
  notifyEmail: string | null
  currency: string
  shippingFee: number
  freeShippingOver: number
  paymentMethods: string[]
  bankDetails: string | null
  booking: {
    timeZone: string
    slotMinutes: number
    daysAhead: number
    capacity: number
    minNoticeMinutes: number
    autoConfirm: boolean
    week: WorkingDay[]
    closedDates: string[]
  }
}
interface SettingsResponse {
  settings: SiteSettings
  paymentMethods: { id: string; label: string }[]
}
function dayNameList() {
  return [t('الأحد'), t('الإثنين'), t('الثلاثاء'), t('الأربعاء'), t('الخميس'), t('الجمعة'), t('السبت')]
}

const paymentMethodNames: Record<string, string> = {
  cod: 'الدفع عند الاستلام',
  whatsapp: 'الاتفاق على الدفع عبر واتساب',
  transfer: 'تحويل بنكي',
  pickup: 'الدفع والاستلام من المحل',
}

export function SiteSettingsForm({ projectId, showStore, showBookings }: { projectId: string; showStore: boolean; showBookings: boolean }) {
  const { data, error } = useData<SettingsResponse>(`/api/projects/${projectId}/data/settings`)
  const [s, setS] = useState<SiteSettings | null>(null)
  const [msg, setMsg] = useState<{ tone: 'success' | 'error'; text: string } | null>(null)
  const [saving, setSaving] = useState(false)
  useEffect(() => {
    if (data) setS(data.settings)
  }, [data])
  if (!data || !s) return <Loading error={error} />

  const setB = (patchB: Partial<SiteSettings['booking']>) => setS({ ...s, booking: { ...s.booking, ...patchB } })
  const dayNames = dayNameList()
  const setDay = (day: number, patchD: Partial<WorkingDay>) => setB({ week: s.booking.week.map((w) => (w.day === day ? { ...w, ...patchD } : w)) })

  const save = async () => {
    setSaving(true)
    setMsg(null)
    try {
      await put(`/api/projects/${projectId}/data/settings`, { settings: s })
      setMsg({ tone: 'success', text: t('تم حفظ الإعدادات') })
    } catch (e) {
      setMsg({ tone: 'error', text: errorMessage(e) })
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card className="space-y-4">
      <h3 className="font-bold">{t('التواصل والتنبيهات')}</h3>
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      <div className="grid gap-3 sm:grid-cols-2">
        <Input label={t('رقم واتساب (بالصيغة الدولية)')} dir="ltr" placeholder="971501234567" value={s.whatsApp ?? ''} onChange={(e) => setS({ ...s, whatsApp: e.target.value })} />
        <Input label={t('بريد التنبيهات (فارغ = بريد حسابك)')} dir="ltr" type="email" value={s.notifyEmail ?? ''} onChange={(e) => setS({ ...s, notifyEmail: e.target.value })} />
      </div>
      <p className="text-xs text-slate-500">{t('يصلك بريد عند كل طلب أو حجز أو رسالة جديدة، ويستطيع العميل إرسال تفاصيل طلبه لك على واتساب بضغطة زر.')}</p>

      {showStore && (
        <>
          <h3 className="border-t border-slate-100 pt-4 font-bold">{t('المتجر')}</h3>
          <div className="grid gap-3 sm:grid-cols-3">
            <Input label={t('العملة')} dir="ltr" maxLength={3} value={s.currency} onChange={(e) => setS({ ...s, currency: e.target.value.toUpperCase() })} />
            <Input label={t('رسوم التوصيل')} type="number" min={0} step="0.01" value={s.shippingFee} onChange={(e) => setS({ ...s, shippingFee: Number(e.target.value) })} />
            <Input label={t('توصيل مجاني فوق (0 = لا)')} type="number" min={0} step="0.01" value={s.freeShippingOver} onChange={(e) => setS({ ...s, freeShippingOver: Number(e.target.value) })} />
          </div>
          <div>
            <span className="mb-1 block text-sm font-medium text-slate-700">{t('طرق الدفع المتاحة')}</span>
            <div className="flex flex-wrap gap-4">
              {data.paymentMethods.map((m) => (
                <label key={m.id} className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={s.paymentMethods.includes(m.id)}
                    onChange={(e) => setS({ ...s, paymentMethods: e.target.checked ? [...s.paymentMethods, m.id] : s.paymentMethods.filter((x) => x !== m.id) })}
                  />
                  {t(paymentMethodNames[m.id] ?? m.label)}
                </label>
              ))}
            </div>
          </div>
          {s.paymentMethods.includes('transfer') && (
            <Textarea label={t('بيانات التحويل البنكي (تظهر للعميل بعد الطلب)')} rows={3} value={s.bankDetails ?? ''} onChange={(e) => setS({ ...s, bankDetails: e.target.value })} />
          )}
          <OnlinePaymentNote />
        </>
      )}

      {showBookings && (
        <>
          <h3 className="border-t border-slate-100 pt-4 font-bold">{t('الحجوزات')}</h3>
          <div className="grid gap-3 sm:grid-cols-3">
            <Input label={t('المنطقة الزمنية')} dir="ltr" value={s.booking.timeZone} onChange={(e) => setB({ timeZone: e.target.value })} />
            <Input label={t('الفاصل بين المواعيد (دقيقة)')} type="number" min={5} value={s.booking.slotMinutes} onChange={(e) => setB({ slotMinutes: Number(e.target.value) })} />
            <Input label={t('حجوزات في نفس الوقت')} type="number" min={1} value={s.booking.capacity} onChange={(e) => setB({ capacity: Number(e.target.value) })} />
            <Input label={t('الحجز متاح لكام يوم قدام')} type="number" min={1} value={s.booking.daysAhead} onChange={(e) => setB({ daysAhead: Number(e.target.value) })} />
            <Input label={t('أقل مدة قبل الموعد (دقيقة)')} type="number" min={0} value={s.booking.minNoticeMinutes} onChange={(e) => setB({ minNoticeMinutes: Number(e.target.value) })} />
          </div>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={s.booking.autoConfirm} onChange={(e) => setB({ autoConfirm: e.target.checked })} /> {t('تأكيد الحجوزات تلقائياً')}
          </label>
          <div className="space-y-2">
            {s.booking.week.map((w) => (
              <div key={w.day} className="flex flex-wrap items-center gap-3 text-sm">
                <span className="w-20 font-semibold">{dayNames[w.day]}</span>
                <label className="flex items-center gap-1">
                  <input type="checkbox" checked={!w.closed} onChange={(e) => setDay(w.day, { closed: !e.target.checked })} /> {t('مفتوح')}
                </label>
                {!w.closed && (
                  <>
                    <input type="time" className="rounded-lg border border-slate-200 px-2 py-1" value={w.open} onChange={(e) => setDay(w.day, { open: e.target.value })} />
                    <span>{t('إلى')}</span>
                    <input type="time" className="rounded-lg border border-slate-200 px-2 py-1" value={w.close} onChange={(e) => setDay(w.day, { close: e.target.value })} />
                  </>
                )}
              </div>
            ))}
          </div>
          <Input
            label={t('أيام الإجازات (yyyy-mm-dd مفصولة بفاصلة)')}
            dir="ltr"
            value={s.booking.closedDates.join(', ')}
            onChange={(e) => setB({ closedDates: e.target.value.split(/[,،\s]+/).filter(Boolean) })}
          />
        </>
      )}
      <Button loading={saving} onClick={save}>
        {t('حفظ الإعدادات')}
      </Button>
    </Card>
  )
}

// ---------- Hosting ----------
export function HostingCard({ projectId }: { projectId: string }) {
  const { data, error } = useData<HostingStatus>(`/api/projects/${projectId}/hosting`)
  const [open, setOpen] = useState(false)
  if (!data) return <Loading error={error} />
  const tierName = (tier: string | null) => (tier === 'backend' ? t('موقع + باك إند') : tier === 'static' ? t('موقع') : '—')
  return (
    <Card className="space-y-3">
      <h3 className="font-bold">{t('استضافة الموقع على سيرفرات Casco')}</h3>
      <p className="text-sm text-slate-600">
        {t('موقعك يحتاج:')} <b>{tierName(data.requiredTier)}</b> {t('(${price} شهرياً)', { price: data.prices[data.requiredTier].monthly })}
      </p>
      {data.paidUntil ? (
        <p className="text-sm">
          {t('الاستضافة الحالية:')} <b>{tierName(data.tier)}</b> — {data.active ? t('فعّالة حتى') : t('انتهت في')} <b>{formatDate(data.paidUntil)}</b>{' '}
          {data.inGrace ? <Badge color="amber">{t('فترة سماح — جدّد الآن حتى لا يتوقف الموقع')}</Badge> : data.active ? <Badge color="green">{t('فعّالة')}</Badge> : <Badge color="red">{t('متوقفة')}</Badge>}
        </p>
      ) : (
        <p className="text-sm text-slate-500">{t('لم تفعّل الاستضافة بعد. فعّلها لتنشر موقعك على الإنترنت.')}</p>
      )}
      {data.active && !data.enough && <Alert tone="warning">{t('موقعك أصبح يستخدم الباك إند، لذلك يحتاج الترقية لاستضافة الباك إند قبل نشر التعديلات.')}</Alert>}
      <Button onClick={() => setOpen(true)}>{data.active ? t('تجديد أو ترقية الاستضافة') : t('تفعيل الاستضافة')}</Button>
      <HostingModal projectId={projectId} open={open} onClose={() => setOpen(false)} />
    </Card>
  )
}
