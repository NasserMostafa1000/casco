import { useEffect, useState } from 'react'
import { Button, Card, Spinner } from './ui'
import { apiUrl, errorMessage, get, post, tokenStore } from '../lib/api'

interface Transfer {
  id: string
  userId: string
  email: string
  name: string
  method: string
  amount: number
  currency: string
  status: string
  reviewNote: string | null
  createdAt: string
  hasGoogle: boolean
  hasApple: boolean
}

function Proof({ id }: { id: string }) {
  const [src, setSrc] = useState<string | null>(null)
  useEffect(() => {
    let url = ''
    let alive = true
    const token = tokenStore.get()
    fetch(apiUrl(`/api/admin/wallet-transfers/${id}/proof`), {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
    })
      .then((r) => (r.ok ? r.blob() : Promise.reject()))
      .then((blob) => {
        url = URL.createObjectURL(blob)
        if (alive) setSrc(url)
        else URL.revokeObjectURL(url)
      })
      .catch(() => {})
    return () => {
      alive = false
      if (url) URL.revokeObjectURL(url)
    }
  }, [id])
  if (!src) return <div className="grid h-40 place-items-center rounded-xl bg-slate-100 text-sm text-slate-400">...</div>
  return <img src={src} alt="" className="max-h-80 w-full rounded-xl object-contain bg-slate-100" />
}

export default function AdminWallet() {
  const [items, setItems] = useState<Transfer[] | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = () =>
    get<{ items: Transfer[] }>('/api/admin/wallet-transfers')
      .then((r) => setItems(r.items))
      .catch((e) => setError(errorMessage(e)))

  useEffect(() => {
    void load()
  }, [])

  const review = async (id: string, approve: boolean) => {
    setBusy(id)
    setError(null)
    try {
      if (approve) await post(`/api/admin/wallet-transfers/${id}/approve`, {})
      else {
        const note = window.prompt('سبب الرفض (اختياري)') ?? ''
        await post(`/api/admin/wallet-transfers/${id}/reject`, { note })
      }
      await load()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(null)
    }
  }

  if (!items) {
    return (
      <div className="space-y-3">
        {error && <p className="text-sm text-red-600">{error}</p>}
        {!error && <Spinner />}
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-500">صور تحويل فودافون كاش وإنستا باي. الموافقة تفعّل Pro على نفس حساب جوجل أو آبل.</p>
      {error && <p className="text-sm text-red-600">{error}</p>}
      {items.length === 0 && <p className="text-sm text-slate-500">لا توجد تحويلات.</p>}
      {items.map((t) => (
        <Card key={t.id} className="p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <p className="font-semibold">{t.name || t.email}</p>
              <p className="text-sm text-slate-500" dir="ltr">
                {t.email}
              </p>
              <p className="mt-1 text-sm">
                {t.method === 'instapay' ? 'إنستا باي' : 'فودافون كاش'} · {t.amount} {t.currency}
                {t.hasGoogle ? ' · جوجل' : ''}
                {t.hasApple ? ' · آبل' : ''}
              </p>
              <p className="text-xs text-slate-400">
                {t.status === 'approved' ? 'موافق' : t.status === 'rejected' ? 'مرفوض' : 'قيد المراجعة'}
                {t.reviewNote ? ` — ${t.reviewNote}` : ''}
              </p>
            </div>
            {t.status === 'pending' && (
              <div className="flex gap-2">
                <Button loading={busy === t.id} onClick={() => void review(t.id, true)}>
                  موافقة
                </Button>
                <Button variant="secondary" loading={busy === t.id} onClick={() => void review(t.id, false)}>
                  رفض
                </Button>
              </div>
            )}
          </div>
          <div className="mt-3">
            <Proof id={t.id} />
          </div>
        </Card>
      ))}
    </div>
  )
}
