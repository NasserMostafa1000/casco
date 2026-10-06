import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ArrowUpRight, Globe, Upload } from 'lucide-react'
import { Alert, Button, Card, Input, Spinner } from '../components/ui'
import { errorMessage, get, post } from '../lib/api'
import { formatDate, t } from '../lib/i18n'
import { rememberCheckout, tiktokClick } from '../lib/tiktok'
import { usd } from '../lib/plans'

interface ReactApp {
  id: string
  name: string
  slug: string
  publishedAt: string | null
  paidUntil: string | null
  daysLeft: number
  active: boolean
  stopped: boolean
  price: number
  siteUrl: string | null
}

export default function ReactHost() {
  const { id } = useParams()
  if (!id || id === 'new') return <CreateReact />
  return <ManageReact id={id} />
}

function CreateReact() {
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const r = await post<{ id: string }>('/api/projects/react', { name })
      navigate(`/app/react/${r.id}`, { replace: true })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="mx-auto max-w-lg px-4 py-10">
      <Link to="/app" className="text-sm font-semibold text-slate-500 hover:text-ink">
        {t('مواقعك')}
      </Link>
      <h1 className="mt-3 text-3xl font-extrabold tracking-tight text-ink">{t('رفع تطبيق React')}</h1>
      <p className="mt-2 text-slate-600">{t('ارفع ملف dist بعد الدفع السنوي، ونستضيفه لك على Casco.')}</p>
      <Card className="mt-6 p-5">
        <form onSubmit={(e) => void submit(e)} className="space-y-4">
          {error && <Alert tone="error">{error}</Alert>}
          <Input label={t('اسم التطبيق')} value={name} onChange={(e) => setName(e.target.value)} required minLength={2} maxLength={120} />
          <Button type="submit" loading={busy} className="w-full">
            {t('أنشئ التطبيق')}
          </Button>
        </form>
      </Card>
    </main>
  )
}

function ManageReact({ id }: { id: string }) {
  const [app, setApp] = useState<ReactApp | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [paying, setPaying] = useState(false)
  const [uploading, setUploading] = useState(false)

  const load = () => {
    get<ReactApp>(`/api/projects/${id}/react`).then(setApp).catch((e) => setError(errorMessage(e)))
  }

  useEffect(() => {
    load()
  }, [id])

  const pay = async () => {
    setPaying(true)
    setError('')
    try {
      const click = tiktokClick()
      const r = await post<{ paymentId: string; redirectUrl: string }>('/api/billing/checkout', {
        kind: 'hosting',
        projectId: id,
        interval: 'yearly',
        ttclid: click.ttclid,
        ttp: click.ttp,
      })
      if (app) {
        rememberCheckout({
          content_id: 'hosting-react-yearly',
          content_name: 'Casco React hosting',
          value: app.price,
          currency: 'USD',
        }, r.paymentId)
      }
      location.href = r.redirectUrl
    } catch (e) {
      setError(errorMessage(e))
      setPaying(false)
    }
  }

  const upload = async (file: File | undefined) => {
    if (!file) return
    setUploading(true)
    setError('')
    setNotice('')
    try {
      const body = new FormData()
      body.append('file', file)
      const r = await post<{ url: string }>(`/api/projects/${id}/react/dist`, body)
      setNotice(t('تم رفع التطبيق. الموقع شغال الآن.'))
      setApp((prev) => (prev ? { ...prev, publishedAt: prev.publishedAt ?? new Date().toISOString(), siteUrl: r.url, stopped: false } : prev))
      load()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setUploading(false)
    }
  }

  if (!app && !error) {
    return (
      <div className="grid min-h-[50vh] place-items-center">
        <Spinner className="h-8 w-8 text-brand-600" />
      </div>
    )
  }

  return (
    <main className="mx-auto max-w-lg px-4 py-10">
      <Link to="/app" className="text-sm font-semibold text-slate-500 hover:text-ink">
        {t('مواقعك')}
      </Link>
      <h1 className="mt-3 text-3xl font-extrabold tracking-tight text-ink">{app?.name ?? t('تطبيق React')}</h1>
      <p className="mt-2 text-slate-600">{t('استضافة سنوية')}{app ? ` • ${usd(app.price)}` : ''}</p>

      <div className="mt-6 space-y-4">
        {error && <Alert tone="error">{error}</Alert>}
        {notice && <Alert tone="success">{notice}</Alert>}
        {app && (
          <Card className="space-y-4 p-5">
            {app.active && app.daysLeft > 0 && (
              <Alert tone="success">{t('فاضل {n} يوم من الاستضافة', { n: app.daysLeft })}{app.paidUntil ? ` • ${t('حتى {date}', { date: formatDate(app.paidUntil) })}` : ''}</Alert>
            )}
            {app.stopped && <Alert tone="warning">{t('الاستضافة انتهت والموقع متوقف. جدّدها ليرجع يشتغل.')}</Alert>}
            {!app.active && !app.stopped && <Alert>{t('ادفع {price} في السنة ليشتغل الموقع', { price: usd(app.price) })}</Alert>}

            {app.siteUrl && app.active && (
              <a href={app.siteUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 text-sm font-semibold text-brand-600 hover:underline" dir="ltr">
                <Globe className="h-4 w-4" />
                {app.siteUrl.replace(/^https?:\/\//, '')}
                <ArrowUpRight className="h-3.5 w-3.5" />
              </a>
            )}

            {!app.active && (
              <Button onClick={() => void pay()} loading={paying} className="w-full">
                {app.stopped ? t('جدّد الاستضافة') : t('ادفع {price} في السنة', { price: usd(app.price) })}
              </Button>
            )}
            {app.active && (
              <Button variant="secondary" onClick={() => void pay()} loading={paying} className="w-full">
                {t('جدّد الاستضافة')}
              </Button>
            )}

            <div>
              <p className="text-sm text-slate-600">{t('ملف dist هو ناتج npm run build. اضغط المجلد أو محتوياته في ملف zip، ولازم يكون فيه index.html.')}</p>
              <label className={`mt-3 flex h-11 cursor-pointer items-center justify-center gap-2 rounded-xl bg-ink px-4 text-sm font-semibold text-white ${!app.active || uploading ? 'pointer-events-none opacity-50' : ''}`}>
                <Upload className="h-4 w-4" />
                {uploading ? t('جاري الرفع...') : t('ارفع ملف zip')}
                <input
                  type="file"
                  accept=".zip,application/zip"
                  className="hidden"
                  disabled={!app.active || uploading}
                  onChange={(e) => {
                    void upload(e.target.files?.[0])
                    e.target.value = ''
                  }}
                />
              </label>
            </div>
          </Card>
        )}
      </div>
    </main>
  )
}
