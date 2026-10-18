import { useEffect, useState } from 'react'
import { Navigate } from 'react-router-dom'
import { Alert, Card, Spinner } from '../components/ui'
import { errorMessage, get } from '../lib/api'
import { useAuth } from '../lib/auth'
import { t } from '../lib/i18n'

interface Money {
  currency: string
  amount: number
}

interface Earnings {
  count: number
  users: { name: string; paid: Money[] }[]
  totals: Money[]
  profit: Money[]
}

function money(rows: Money[]) {
  if (rows.length === 0) return t('لم يدفع')
  return rows.map((row) => `${row.amount.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${row.currency}`).join(' + ')
}

export default function PartnerEarnings() {
  const { me } = useAuth()
  const [data, setData] = useState<Earnings | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!me?.partner) return
    get<Earnings>('/api/partner/earnings').then(setData).catch((e) => setError(errorMessage(e)))
  }, [me])

  if (me && !me.partner) return <Navigate to="/app" replace />

  return (
    <main className="mx-auto max-w-3xl px-4 py-8">
      <h1 className="text-2xl font-extrabold">{t('أرباحي')}</h1>
      <p className="mt-1 text-sm text-slate-500">{t('المستخدمون الجدد هذا الشهر')}</p>
      {error && (
        <div className="mt-4">
          <Alert tone="error">{error}</Alert>
        </div>
      )}
      {!data && !error && <Spinner className="mt-6 h-6 w-6 text-brand-600" />}
      {data && (
        <>
          <div className="mt-6 grid gap-3 sm:grid-cols-3">
            <Card>
              <p className="text-sm text-slate-500">{t('عدد المستخدمين')}</p>
              <p className="mt-1 text-2xl font-extrabold">{data.count}</p>
            </Card>
            <Card>
              <p className="text-sm text-slate-500">{t('إجمالي ما دفعوه')}</p>
              <p className="mt-1 text-2xl font-extrabold" dir="ltr">{money(data.totals)}</p>
            </Card>
            <Card>
              <p className="text-sm text-slate-500">{t('أرباحك 10%')}</p>
              <p className="mt-1 text-2xl font-extrabold text-brand-700" dir="ltr">{data.profit.length === 0 ? '0' : money(data.profit)}</p>
            </Card>
          </div>
          <Card className="mt-6 overflow-x-auto p-0">
            {data.users.length === 0 ? (
              <p className="p-4 text-sm text-slate-500">{t('لا يوجد مستخدمون جدد هذا الشهر.')}</p>
            ) : (
              <table className="w-full text-sm">
                <thead className="bg-slate-50 text-slate-500">
                  <tr>
                    <th className="p-3 text-start">{t('المستخدم')}</th>
                    <th className="p-3 text-start">{t('دفع')}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.users.map((user, index) => (
                    <tr key={`${user.name}-${index}`} className="border-t border-slate-100">
                      <td className="p-3">{user.name}</td>
                      <td className="p-3" dir="ltr">{money(user.paid)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </Card>
        </>
      )}
    </main>
  )
}
