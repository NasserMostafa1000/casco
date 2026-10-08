import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { Logo } from '../components/AppShell'
import { Alert, Button } from '../components/ui'
import { apiUrl, errorMessage, post, revokeToken, tokenStore, type Me } from '../lib/api'
import { useAuth } from '../lib/auth'
import { getLang, t } from '../lib/i18n'

export function DesktopConnect() {
  const params = new URLSearchParams(location.search)
  const enter = params.get('enter')?.trim() ?? ''
  const next = safeBillingPath(params.get('next'))
  const { me, loading } = useAuth()
  const code = params.get('code')?.trim().toUpperCase() ?? ''
  if (code) sessionStorage.setItem('casco_next', `/desktop?code=${encodeURIComponent(code)}`)
  if (enter) return <EnterAccount code={enter} next={next} />

  return (
    <div className="flex min-h-screen flex-col px-4 py-5 sm:px-8">
      <Logo />
      <div className="mx-auto flex w-full max-w-md flex-1 flex-col justify-center py-10">
        <h1 className="text-3xl font-extrabold tracking-tight text-ink">{t('توصيل Casco Studio')}</h1>
        <div className="mt-8">
          {!code && <p className="text-sm text-slate-500">{t('افتح Casco Studio واضغط Sign in عشان يظهر هنا رمز الدخول.')}</p>}
          {code && loading && <p className="text-sm text-slate-500">…</p>}
          {code && !loading && !me && <SignedOut />}
          {code && !loading && me && <Approve code={code} me={me} />}
        </div>
      </div>
    </div>
  )
}

function safeBillingPath(raw: string | null) {
  if (raw && raw.startsWith('/app/billing') && !raw.startsWith('//')) return raw
  return '/app/billing'
}

function EnterAccount({ code, next }: { code: string; next: string }) {
  const [error, setError] = useState('')
  useEffect(() => {
    let cancelled = false
    // Do not send the browser's current login. This request must become the account open in Casco Studio.
    fetch(apiUrl('/api/auth/device/enter'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Casco-Lang': getLang() },
      body: JSON.stringify({ deviceCode: code }),
    })
      .then(async (res) => {
        const data = (await res.json().catch(() => ({}))) as { token?: string; message?: string }
        if (!res.ok || !data.token) throw new Error(data.message || 'رابط الدخول انتهى. ارجع لـ Casco Studio وحاول تاني.')
        return data.token
      })
      .then(token => {
        if (cancelled) return
        const previous = tokenStore.get()
        tokenStore.set(token)
        if (previous && previous !== token) revokeToken(previous)
        sessionStorage.setItem('casco_next', next)
        location.replace(next)
      })
      .catch(err => {
        if (!cancelled) setError(errorMessage(err))
      })
    return () => {
      cancelled = true
    }
  }, [code, next])

  return error
    ? <Alert tone="error">{error}</Alert>
    : <p className="text-sm text-slate-500">{t('جاري فتح تجديد الاشتراك…')}</p>
}

function SignedOut() {
  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-500">{t('سجّل الدخول بنفس حساب casco.studio للسماح لبرنامج الديسك توب.')}</p>
      <Link to="/login">
        <Button size="lg" variant="dark" className="w-full">{t('تسجيل الدخول')}</Button>
      </Link>
    </div>
  )
}

function Approve({ code, me }: { code: string; me: Me }) {
  const [error, setError] = useState('')
  const [done, setDone] = useState<'ok' | 'denied' | ''>('')
  const [busy, setBusy] = useState(false)

  const decide = async (path: '/api/auth/device/approve' | '/api/auth/device/deny', next: 'ok' | 'denied') => {
    setError('')
    setBusy(true)
    try {
      await post(path, { userCode: code })
      sessionStorage.removeItem('casco_next')
      setDone(next)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  if (done === 'ok') return <Alert>{t('تم توصيل Casco Studio. تقدر تقفل الصفحة وترجع للبرنامج.')}</Alert>
  if (done === 'denied') return <Alert tone="error">{t('تم رفض طلب الدخول.')}</Alert>

  return (
    <div className="space-y-4">
      {error && <Alert tone="error">{error}</Alert>}
      <p className="text-sm text-slate-500">{t('Casco Studio يطلب الدخول بهذا الحساب')}</p>
      <p className="text-lg font-semibold text-ink" dir="ltr">{me.user.email}</p>
      <p className="font-mono text-2xl tracking-[0.3em] text-ink" dir="ltr">{code}</p>
      <p className="text-sm text-slate-500">{t('النقاط المتاحة: {count}', { count: me.credits.available })}</p>
      <div className="flex gap-3">
        <Button size="lg" variant="dark" className="flex-1" loading={busy} onClick={() => void decide('/api/auth/device/approve', 'ok')}>{t('السماح')}</Button>
        <Button size="lg" variant="secondary" className="flex-1" disabled={busy} onClick={() => void decide('/api/auth/device/deny', 'denied')}>{t('رفض')}</Button>
      </div>
    </div>
  )
}
