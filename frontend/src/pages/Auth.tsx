import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Check, Sparkles } from 'lucide-react'
import { Logo } from '../components/AppShell'
import { LanguageSwitcher } from '../components/LanguageSwitcher'
import { ThemeToggle } from '../components/ThemeToggle'
import { SocialLogin } from '../components/SocialLogin'
import { Turnstile } from '../components/Turnstile'
import { Alert, Button, Input } from '../components/ui'
import { errorMessage } from '../lib/api'
import { useAuth } from '../lib/auth'
import { getAuthConfig } from '../lib/externalAuth'
import { t } from '../lib/i18n'
import { trackRegistration } from '../lib/tiktok'
import { PENDING_PROMPT_KEY } from './Landing'

function Shell({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  return (
    <div className="grid min-h-screen lg:grid-cols-2">
      <div className="flex flex-col px-4 py-5 sm:px-8">
        <div className="flex items-center justify-between">
          <Logo />
          <div className="flex items-center gap-1">
            <ThemeToggle />
            <LanguageSwitcher />
          </div>
        </div>
        <div className="mx-auto flex w-full max-w-sm flex-1 flex-col justify-center py-10">
          <h1 className="text-3xl font-extrabold tracking-tight text-ink">{title}</h1>
          <p className="mt-2 text-sm text-slate-500">{subtitle}</p>
          <div className="mt-8">{children}</div>
        </div>
      </div>
      <div className="relative hidden overflow-hidden bg-ink lg:block">
        <div className="bg-grid absolute inset-0 opacity-[.15] invert" />
        <div className="absolute -top-20 end-0 h-96 w-96 rounded-full bg-brand-500/40 blur-3xl" />
        <div className="absolute bottom-0 start-0 h-96 w-96 rounded-full bg-fuchsia-500/30 blur-3xl" />
        <div className="relative flex h-full flex-col justify-center px-16 text-white">
          <span className="inline-flex w-fit items-center gap-2 rounded-full bg-white/10 px-3 py-1 text-sm ring-1 ring-white/15">
            <Sparkles className="h-4 w-4" /> Casco Studio
          </span>
          <h2 className="mt-6 text-4xl font-extrabold leading-tight tracking-tight">{t('اكتب فكرتك، وموقعك يجهز في دقيقة')}</h2>
          <ul className="mt-8 space-y-4 text-white/80">
            {[t('موقعك الأول مجاناً'), t('عدّل بالكلام بأي لغة'), t('متجاوب على الموبايل وكل الشاشات'), t('استضافة ودومين خاص بضغطة')].map((x) => (
              <li key={x} className="flex items-center gap-3">
                <span className="grid h-6 w-6 place-items-center rounded-full bg-white/10">
                  <Check className="h-3.5 w-3.5" />
                </span>
                {x}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  )
}

function afterAuthPath() {
  const next = sessionStorage.getItem('casco_next')
  if (next && next.startsWith('/desktop?code=')) {
    sessionStorage.removeItem('casco_next')
    return next
  }
  return localStorage.getItem(PENDING_PROMPT_KEY) ? '/app/new' : '/app'
}

export function Login() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError('')
    setLoading(true)
    try {
      await login(email, password)
      navigate(afterAuthPath())
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setLoading(false)
    }
  }

  const onSocial = useCallback(() => navigate(afterAuthPath()), [navigate])

  return (
    <Shell title={t('مرحباً بعودتك')} subtitle={t('سجّل الدخول لمتابعة بناء مواقعك')}>
      <SocialLogin onDone={onSocial} />
      <form onSubmit={submit} className="mt-4 space-y-4">
        {error && <Alert tone="error">{error}</Alert>}
        <Input label={t('البريد الإلكتروني')} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} dir="ltr" />
        <Input label={t('كلمة المرور')} type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} dir="ltr" />
        <Button type="submit" size="lg" variant="dark" loading={loading} className="w-full">
          {t('دخول')}
        </Button>
        <p className="text-center text-sm text-slate-600">
          {t('ليس لديك حساب؟')}{' '}
          <Link to="/register" className="font-semibold text-brand-600 hover:underline">
            {t('أنشئ حساباً مجاناً')}
          </Link>
        </p>
      </form>
    </Shell>
  )
}

export function Register() {
  const { register } = useAuth()
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [siteKey, setSiteKey] = useState<string | null>(null)
  const [captcha, setCaptcha] = useState<string | null>(null)
  const [captchaReset, setCaptchaReset] = useState(0)

  useEffect(() => {
    getAuthConfig().then((c) => setSiteKey(c.turnstileSiteKey), () => setSiteKey(null))
  }, [])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (siteKey && !captcha) {
      setError(t('انتظر حتى يكتمل التحقق من أنك لست روبوتاً'))
      return
    }
    setError('')
    setLoading(true)
    try {
      const id = await register(name, email, password, captcha)
      await trackRegistration(email, id)
      navigate(afterAuthPath())
    } catch (err) {
      setError(errorMessage(err))
      if (siteKey) setCaptchaReset((n) => n + 1)
    } finally {
      setLoading(false)
    }
  }

  const onSocial = useCallback((created?: boolean, userId?: string) => {
    if (created && userId) void trackRegistration('', userId)
    navigate(afterAuthPath())
  }, [navigate])

  return (
    <Shell title={t('أنشئ حسابك مجاناً')} subtitle={t('موقعك الأول مجاناً، بدون بطاقة ائتمان')}>
      <SocialLogin onDone={onSocial} />
      <form onSubmit={submit} className="mt-4 space-y-4">
        {error && <Alert tone="error">{error}</Alert>}
        <Input label={t('الاسم')} autoComplete="name" required value={name} onChange={(e) => setName(e.target.value)} />
        <Input label={t('البريد الإلكتروني')} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} dir="ltr" />
        <Input
          label={t('كلمة المرور')}
          hint={t('8 أحرف على الأقل')}
          type="password"
          autoComplete="new-password"
          required
          minLength={8}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          dir="ltr"
        />
        {siteKey && <Turnstile siteKey={siteKey} onToken={setCaptcha} resetKey={captchaReset} />}
        <Button type="submit" size="lg" variant="dark" loading={loading} disabled={!!siteKey && !captcha} className="w-full">
          {t('إنشاء الحساب')}
        </Button>
        <p className="text-center text-sm text-slate-600">
          {t('لديك حساب؟')}{' '}
          <Link to="/login" className="font-semibold text-brand-600 hover:underline">
            {t('سجّل الدخول')}
          </Link>
        </p>
      </form>
    </Shell>
  )
}
