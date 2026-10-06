import { useEffect, useRef, useState } from 'react'
import { errorMessage } from '../lib/api'
import { useAuth } from '../lib/auth'
import { APPLE_SCRIPT, getAuthConfig, GOOGLE_SCRIPT, loadScript, type AuthConfig } from '../lib/externalAuth'
import { getLang, t } from '../lib/i18n'
import { Alert, Spinner } from './ui'

/** "Continue with Google / Apple" buttons. Renders nothing when neither provider is configured. */
export function SocialLogin({ onDone }: { onDone: (created?: boolean, userId?: string) => void }) {
  const { external } = useAuth()
  const [config, setConfig] = useState<AuthConfig | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const googleBox = useRef<HTMLDivElement>(null)
  const done = useRef(onDone)

  useEffect(() => {
    done.current = onDone
  }, [onDone])

  useEffect(() => {
    getAuthConfig().then(setConfig, () => setConfig(null))
  }, [])

  const finish = async (provider: 'google' | 'apple', idToken: string, name?: string) => {
    setError('')
    setBusy(true)
    try {
      const result = await external(provider, idToken, name)
      done.current(result.created, result.id)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  const finishRef = useRef(finish)
  useEffect(() => {
    finishRef.current = finish
  })

  const googleClientId = config?.googleClientId
  useEffect(() => {
    if (!googleClientId) return
    let cancelled = false
    loadScript(GOOGLE_SCRIPT)
      .then(() => {
        const box = googleBox.current
        if (cancelled || !box || !window.google) return
        window.google.accounts.id.initialize({
          client_id: googleClientId,
          callback: (r) => void finishRef.current('google', r.credential),
          ux_mode: 'popup',
          use_fedcm_for_button: true,
        })
        box.replaceChildren()
        window.google.accounts.id.renderButton(box, {
          theme: 'outline',
          size: 'large',
          shape: 'pill',
          text: 'continue_with',
          locale: getLang(),
          width: Math.min(400, box.clientWidth || 320),
        })
      })
      .catch((e) => setError(errorMessage(e)))
    return () => {
      cancelled = true
    }
  }, [googleClientId])

  const appleSignIn = async () => {
    if (!config?.appleClientId) return
    setError('')
    try {
      await loadScript(APPLE_SCRIPT)
      if (!window.AppleID) throw new Error(t('تعذر تحميل Apple'))
      window.AppleID.auth.init({
        clientId: config.appleClientId,
        scope: 'name email',
        redirectURI: `${window.location.origin}/login`,
        usePopup: true,
      })
      const r = await window.AppleID.auth.signIn()
      const name = [r.user?.name?.firstName, r.user?.name?.lastName].filter(Boolean).join(' ') || undefined
      await finish('apple', r.authorization.id_token, name)
    } catch (e) {
      // The user closing the popup is not an error.
      const code = (e as { error?: string } | null)?.error
      if (code === 'popup_closed_by_user' || code === 'user_cancelled_authorize') return
      setError(code ? t('تعذر تسجيل الدخول عبر Apple، حاول مرة أخرى') : errorMessage(e))
    }
  }

  if (!config || (!config.googleClientId && !config.appleClientId)) return null

  return (
    <div className="space-y-3">
      {error && <Alert tone="error">{error}</Alert>}
      {config.googleClientId && <div ref={googleBox} className="flex min-h-[44px] justify-center" />}
      {config.appleClientId && (
        <button
          type="button"
          onClick={appleSignIn}
          disabled={busy}
          className="flex h-11 w-full items-center justify-center gap-2 rounded-full bg-black px-4 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:opacity-60"
        >
          <svg viewBox="0 0 24 24" className="h-5 w-5" fill="currentColor" aria-hidden="true">
            <path d="M16.37 12.64c-.02-2.3 1.88-3.4 1.96-3.46-1.07-1.56-2.73-1.78-3.32-1.8-1.41-.14-2.76.83-3.47.83-.72 0-1.82-.81-2.99-.79-1.54.02-2.96.9-3.75 2.27-1.6 2.78-.41 6.89 1.15 9.14.76 1.1 1.67 2.34 2.86 2.3 1.15-.05 1.58-.74 2.97-.74 1.38 0 1.77.74 2.98.72 1.23-.02 2.01-1.12 2.76-2.23.87-1.28 1.23-2.52 1.25-2.58-.03-.01-2.39-.92-2.4-3.66zM14.1 5.9c.63-.77 1.06-1.83.94-2.9-.91.04-2.02.61-2.67 1.37-.58.67-1.1 1.76-.96 2.8 1.02.08 2.06-.52 2.69-1.27z" />
          </svg>
          {t('المتابعة باستخدام Apple')}
        </button>
      )}
      {busy && (
        <div className="flex justify-center">
          <Spinner />
        </div>
      )}
      <div className="flex items-center gap-3 text-xs text-slate-400">
        <span className="h-px flex-1 bg-slate-200" />
        {t('أو بالبريد الإلكتروني')}
        <span className="h-px flex-1 bg-slate-200" />
      </div>
    </div>
  )
}
