import { useCallback, useEffect, useState } from 'react'
import { del, errorMessage, get, post, put } from '../lib/api'
import { Alert, Button, Card, Input, Select, Spinner } from './ui'

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
  income: { subscriptions: number; hosting: number; topups: number }
  entries: Entry[]
}

const cadenceLabel: Record<Cadence, string> = { day: 'يومي', month: 'شهري', year: 'سنوي', once: 'مرة واحدة' }
const usd = (n: number) => `$${Number(n).toFixed(2)}`
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

  const load = useCallback(() => {
    const q = view === 'all' ? 'all=true' : view === 'year' ? `year=${year}` : `month=${month}`
    get<Report>(`/api/admin/accounts?${q}`).then(setReport).catch((e) => setError(errorMessage(e)))
  }, [view, month, year])

  useEffect(load, [load])

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

  return (
    <div className="space-y-4">
      {error && <Alert tone="error">{error}</Alert>}
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
              الجاي من العملاء: اشتراكات {usd(report.income.subscriptions)} • استضافة {usd(report.income.hosting)} • شحن نقاط {usd(report.income.topups)}.
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
