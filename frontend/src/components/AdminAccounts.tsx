import { useCallback, useEffect, useState } from 'react'
import { del, errorMessage, get, post, put } from '../lib/api'
import { Alert, Button, Card, Input, Select, Spinner, Textarea, formatDateTime } from './ui'

type Cadence = 'day' | 'month' | 'year' | 'once'
type View = 'month' | 'year' | 'all'

interface Entry {
  id: number
  name: string
  amountUsd: number
  cadence: Cadence
  onDate: string
  inPeriod: number
}

interface Report {
  label: string
  spent: number
  received: number
  net: number
  monthlyRunRate: number
  income: { subscriptions: number; hosting: number; topups: number; store?: number }
  entries: Entry[]
}

interface EarningsSummary {
  currency: string
  feePercent: number
  available: number
  grossSales: number
  fees: number
  netSales: number
  paidOut: number
  adjustments: number
  salesCount: number
  payoutCount: number
}

interface OwnerRow {
  id: string
  email: string
  name: string
  adminNote: string | null
  plan: string
  credits: number
  sites: number
  backendSites: number
  lastPaymentAt: string | null
  lastWalletAt: string | null
  lastWalletStatus: string | null
  earnings: EarningsSummary
}

interface OwnersPage {
  total: number
  page: number
  pageSize: number
  items: OwnerRow[]
}

const cadenceLabel: Record<Cadence, string> = { day: 'يومي', month: 'شهري', year: 'سنوي', once: 'مرة واحدة' }
const usd = (n: number) => `$${Number(n).toFixed(2)}`
const money = (n: number, c: string) => `${Number(n).toFixed(2)} ${c}`
const today = () => new Date().toISOString().slice(0, 10)

const emptyForm = () => ({ name: '', amount: '', cadence: 'month' as Cadence, onDate: today() })

export default function AdminAccounts() {
  const [view, setView] = useState<View>('month')
  const [month, setMonth] = useState(() => new Date().toISOString().slice(0, 7))
  const [year, setYear] = useState(() => String(new Date().getFullYear()))
  const [report, setReport] = useState<Report | null>(null)
  const [error, setError] = useState('')
  const [form, setForm] = useState(emptyForm)
  const [editing, setEditing] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [owners, setOwners] = useState<OwnersPage | null>(null)
  const [ownerQ, setOwnerQ] = useState('')
  const [selected, setSelected] = useState<OwnerRow | null>(null)
  const [ledgerType, setLedgerType] = useState<'payout' | 'adjustment'>('payout')
  const [ledgerAmount, setLedgerAmount] = useState('')
  const [ledgerNote, setLedgerNote] = useState('')
  const [adminNote, setAdminNote] = useState('')

  const load = useCallback(() => {
    const q = view === 'all' ? 'all=true' : view === 'year' ? `year=${year}` : `month=${month}`
    get<Report>(`/api/admin/accounts?${q}`).then(setReport).catch((e) => setError(errorMessage(e)))
  }, [view, month, year])

  const loadOwners = useCallback(() => {
    const q = ownerQ.trim() ? `?search=${encodeURIComponent(ownerQ.trim())}` : ''
    get<OwnersPage>(`/api/admin/earnings${q}`).then(setOwners).catch((e) => setError(errorMessage(e)))
  }, [ownerQ])

  useEffect(load, [load])
  useEffect(loadOwners, [loadOwners])

  const save = async () => {
    setBusy(true)
    setError('')
    const body = { name: form.name, amountUsd: Number(form.amount), cadence: form.cadence, onDate: form.onDate }
    try {
      if (editing == null) await post('/api/admin/accounts', body)
      else await put(`/api/admin/accounts/${editing}`, body)
      setForm(emptyForm())
      setEditing(null)
      load()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (id: number) => {
    if (!confirm('حذف البند؟')) return
    setError('')
    try {
      await del(`/api/admin/accounts/${id}`)
      if (editing === id) {
        setEditing(null)
        setForm(emptyForm())
      }
      load()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const openOwner = (row: OwnerRow) => {
    setSelected(row)
    setAdminNote(row.adminNote ?? '')
    setLedgerAmount('')
    setLedgerNote('')
    setLedgerType('payout')
  }

  const saveNote = async () => {
    if (!selected) return
    setBusy(true)
    setError('')
    try {
      const r = await put<{ adminNote: string | null }>(`/api/admin/users/${selected.id}/admin-note`, { note: adminNote })
      setSelected({ ...selected, adminNote: r.adminNote })
      loadOwners()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const postLedger = async () => {
    if (!selected) return
    setBusy(true)
    setError('')
    try {
      await post(`/api/admin/users/${selected.id}/earnings`, {
        type: ledgerType,
        amount: Number(ledgerAmount),
        currency: selected.earnings.currency,
        note: ledgerNote,
      })
      setLedgerAmount('')
      setLedgerNote('')
      loadOwners()
      const refreshed = await get<OwnersPage>(`/api/admin/earnings?search=${encodeURIComponent(selected.email)}`)
      const next = refreshed.items.find((i) => i.id === selected.id)
      if (next) openOwner(next)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      {error && <Alert tone="error">{error}</Alert>}

      <Card>
        <div className="flex flex-wrap items-end justify-between gap-3">
          <div>
            <h2 className="text-lg font-bold">أرصدة أصحاب المواقع</h2>
            <p className="text-sm text-slate-500">مبيعات المتاجر عبر زينة كاسكو (عمولة المنصة تُخصم تلقائياً). الصرف يدوي من هنا.</p>
          </div>
          <div className="flex gap-2">
            <Input placeholder="بحث بالبريد أو الاسم" value={ownerQ} onChange={(e) => setOwnerQ(e.target.value)} className="w-56" />
            <Button variant="secondary" onClick={loadOwners}>تحديث</Button>
          </div>
        </div>
        {!owners ? (
          <Spinner className="mt-6 h-6 w-6 text-brand-600" />
        ) : owners.items.length === 0 ? (
          <p className="mt-4 text-sm text-slate-400">لا أرصدة مبيعات بعد.</p>
        ) : (
          <div className="mt-4 overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-slate-50 text-slate-500">
                <tr>
                  <th className="p-3 text-start">العميل</th>
                  <th className="p-3 text-start">الخطة / نقاط</th>
                  <th className="p-3 text-start">مواقع</th>
                  <th className="p-3 text-start">رصيد متاح</th>
                  <th className="p-3 text-start">مبيعات / عمولة</th>
                  <th className="p-3 text-start">ملاحظة</th>
                  <th className="p-3" />
                </tr>
              </thead>
              <tbody>
                {owners.items.map((u) => (
                  <tr key={u.id} className="border-t border-slate-100">
                    <td className="p-3">
                      <div className="font-medium">{u.name}</div>
                      <div className="text-xs text-slate-400" dir="ltr">{u.email}</div>
                    </td>
                    <td className="p-3">
                      {u.plan === 'pro' ? 'Pro' : 'مجاني'}
                      <div className="text-xs text-slate-400">{u.credits.toLocaleString('ar')} نقطة</div>
                    </td>
                    <td className="p-3">
                      {u.sites}
                      <div className="text-xs text-slate-400">{u.backendSites} باك إند</div>
                    </td>
                    <td className="p-3 font-semibold" dir="ltr">{money(u.earnings.available, u.earnings.currency)}</td>
                    <td className="p-3 text-xs" dir="ltr">
                      {money(u.earnings.grossSales, u.earnings.currency)}
                      <div className="text-slate-400">عمولة {money(u.earnings.fees, u.earnings.currency)} • صُرف {money(u.earnings.paidOut, u.earnings.currency)}</div>
                    </td>
                    <td className="p-3 text-xs text-slate-500 max-w-[12rem] truncate">{u.adminNote || '—'}</td>
                    <td className="p-3 text-end">
                      <Button variant="secondary" onClick={() => openOwner(u)}>إدارة</Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {selected && (
        <Card className="space-y-3">
          <div className="flex flex-wrap items-start justify-between gap-2">
            <div>
              <h3 className="font-bold">{selected.name}</h3>
              <p className="text-sm text-slate-500" dir="ltr">{selected.email}</p>
              <p className="mt-1 text-sm">
                الرصيد المتاح:{' '}
                <b dir="ltr">{money(selected.earnings.available, selected.earnings.currency)}</b>
                {' '}• عمولة كاسكو {selected.earnings.feePercent}%
              </p>
              <p className="text-xs text-slate-400">
                آخر دفعة Ziina: {selected.lastPaymentAt ? formatDateTime(selected.lastPaymentAt) : '—'}
                {' '}• آخر تحويل محفظة: {selected.lastWalletAt ? formatDateTime(selected.lastWalletAt) : '—'}
                {selected.lastWalletStatus ? ` (${selected.lastWalletStatus})` : ''}
              </p>
            </div>
            <Button variant="secondary" onClick={() => setSelected(null)}>إغلاق</Button>
          </div>
          <Textarea label="ملاحظة أدمن" rows={2} value={adminNote} onChange={(e) => setAdminNote(e.target.value)} />
          <Button variant="secondary" loading={busy} onClick={() => void saveNote()}>حفظ الملاحظة</Button>
          <div className="grid gap-3 sm:grid-cols-4 border-t border-slate-100 pt-3">
            <Select label="الإجراء" value={ledgerType} onChange={(e) => setLedgerType(e.target.value as 'payout' | 'adjustment')}>
              <option value="payout">تسجيل صرف</option>
              <option value="adjustment">تعديل رصيد (+/−)</option>
            </Select>
            <Input
              label={ledgerType === 'payout' ? 'مبلغ الصرف' : 'المبلغ (سالب للخصم)'}
              type="number"
              step="0.01"
              dir="ltr"
              value={ledgerAmount}
              onChange={(e) => setLedgerAmount(e.target.value)}
            />
            <Input label="ملاحظة" value={ledgerNote} onChange={(e) => setLedgerNote(e.target.value)} />
            <div className="flex items-end">
              <Button loading={busy} disabled={!ledgerAmount} onClick={() => void postLedger()}>
                تنفيذ
              </Button>
            </div>
          </div>
        </Card>
      )}

      <Card>
        <div className="flex flex-wrap items-end justify-between gap-3">
          <div>
            <h2 className="text-lg font-bold">الحسابات</h2>
            <p className="text-sm text-slate-500">سجّل اللي بتصرفه. اللي جايلك بيتحسب من مدفوعات العملاء.</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button variant={view === 'month' ? 'dark' : 'secondary'} onClick={() => setView('month')}>شهر</Button>
            <Button variant={view === 'year' ? 'dark' : 'secondary'} onClick={() => setView('year')}>سنة</Button>
            <Button variant={view === 'all' ? 'dark' : 'secondary'} onClick={() => setView('all')}>الكل</Button>
            {view === 'month' && <Input type="month" value={month} onChange={(e) => setMonth(e.target.value)} dir="ltr" />}
            {view === 'year' && <Input type="number" min={2000} max={2100} value={year} onChange={(e) => setYear(e.target.value)} dir="ltr" className="w-28" />}
          </div>
        </div>
        {!report ? (
          error ? null : <Spinner className="mt-6 h-6 w-6 text-brand-600" />
        ) : (
          <>
            <div className="mt-4 grid gap-3 sm:grid-cols-3">
              <Total label="صرفت" value={usd(report.spent)} tone="text-red-600" />
              <Total label="جالي" value={usd(report.received)} tone="text-emerald-600" />
              <Total label="الصافي" value={usd(report.net)} tone={report.net >= 0 ? 'text-emerald-600' : 'text-red-600'} />
            </div>
            <p className="mt-3 text-xs text-slate-500">
              الجاي من العملاء: اشتراكات {usd(report.income.subscriptions)} • استضافة {usd(report.income.hosting)} • شحن نقاط {usd(report.income.topups)}
              {report.income.store != null ? ` • متاجر ${usd(report.income.store)}` : ''}.
              المصروف الشهري المستمر {usd(report.monthlyRunRate)}.
            </p>
          </>
        )}
      </Card>

      <Card>
        <h3 className="font-bold">{editing == null ? 'بند جديد' : 'تعديل البند'}</h3>
        <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Input label="الاسم" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="مثال: OpenAI، سيرفر، بريد" />
          <Input label="المبلغ $" type="number" min="0" step="0.01" dir="ltr" value={form.amount} onChange={(e) => setForm({ ...form, amount: e.target.value })} />
          <Select label="التكرار" value={form.cadence} onChange={(e) => setForm({ ...form, cadence: e.target.value as Cadence })}>
            <option value="day">يومي</option>
            <option value="month">شهري</option>
            <option value="year">سنوي</option>
            <option value="once">مرة واحدة</option>
          </Select>
          <Input label={form.cadence === 'once' ? 'تاريخ الدفع' : 'يبدأ من'} type="date" dir="ltr" value={form.onDate} onChange={(e) => setForm({ ...form, onDate: e.target.value })} />
        </div>
        <div className="mt-3 flex gap-2">
          <Button disabled={busy || !form.name.trim() || form.amount === ''} loading={busy} onClick={() => void save()}>
            {editing == null ? 'إضافة' : 'حفظ'}
          </Button>
          {editing != null && (
            <Button variant="secondary" onClick={() => { setEditing(null); setForm(emptyForm()) }}>
              إلغاء
            </Button>
          )}
        </div>
      </Card>

      <Card className="overflow-x-auto p-0">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-500">
            <tr>
              <th className="p-3 text-start">البند</th>
              <th className="p-3 text-start">التكرار</th>
              <th className="p-3 text-start">المبلغ</th>
              <th className="p-3 text-start">التاريخ</th>
              <th className="p-3 text-start">في الفترة</th>
              <th className="p-3" />
            </tr>
          </thead>
          <tbody>
            {report?.entries.length === 0 && (
              <tr>
                <td colSpan={6} className="p-4 text-slate-400">لا توجد بنود بعد.</td>
              </tr>
            )}
            {report?.entries.map((e) => (
              <tr key={e.id} className="border-t border-slate-100">
                <td className="p-3 font-medium">{e.name}</td>
                <td className="p-3">{cadenceLabel[e.cadence] ?? e.cadence}</td>
                <td className="p-3" dir="ltr">{usd(e.amountUsd)}</td>
                <td className="p-3" dir="ltr">{e.onDate}</td>
                <td className="p-3" dir="ltr">{usd(e.inPeriod)}</td>
                <td className="p-3 text-end">
                  <button
                    className="text-brand-700"
                    onClick={() => {
                      setEditing(e.id)
                      setForm({ name: e.name, amount: String(e.amountUsd), cadence: e.cadence, onDate: e.onDate })
                    }}
                  >
                    تعديل
                  </button>
                  <button className="ms-3 text-red-600" onClick={() => void remove(e.id)}>حذف</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
    </div>
  )
}

function Total({ label, value, tone }: { label: string; value: string; tone: string }) {
  return (
    <div className="rounded-xl border border-slate-200 p-3">
      <p className="text-xs text-slate-500">{label}</p>
      <p className={`mt-1 text-2xl font-extrabold ${tone}`} dir="ltr">{value}</p>
    </div>
  )
}
