import { get } from './api'
import { t } from './i18n'

export interface AuthConfig {
  googleClientId: string | null
  appleClientId: string | null
  turnstileSiteKey: string | null
}

let configPromise: Promise<AuthConfig> | null = null

export function getAuthConfig(): Promise<AuthConfig> {
  configPromise ??= get<AuthConfig>('/api/auth/config').catch((e) => {
    configPromise = null
    throw e
  })
  return configPromise
}

const scripts = new Map<string, Promise<void>>()

export function loadScript(src: string): Promise<void> {
  let p = scripts.get(src)
  if (!p) {
    p = new Promise<void>((resolve, reject) => {
      const el = document.createElement('script')
      el.src = src
      el.async = true
      el.onload = () => resolve()
      el.onerror = () => {
        scripts.delete(src)
        el.remove()
        reject(new Error(t('تعذر تحميل {host}', { host: new URL(src).hostname })))
      }
      document.head.appendChild(el)
    })
    scripts.set(src, p)
  }
  return p
}

interface GoogleIdApi {
  initialize(options: { client_id: string; callback: (r: { credential: string }) => void; ux_mode?: 'popup'; use_fedcm_for_button?: boolean }): void
  renderButton(el: HTMLElement, options: Record<string, string | number>): void
}

interface AppleSignInResponse {
  authorization: { id_token: string }
  user?: { name?: { firstName?: string; lastName?: string } }
}

interface AppleIdApi {
  auth: {
    init(options: { clientId: string; scope: string; redirectURI: string; usePopup: boolean }): void
    signIn(): Promise<AppleSignInResponse>
  }
}

interface TurnstileApi {
  render(el: HTMLElement, options: Record<string, unknown>): string
  reset(id: string): void
  remove(id: string): void
}

declare global {
  interface Window {
    google?: { accounts: { id: GoogleIdApi } }
    AppleID?: AppleIdApi
    turnstile?: TurnstileApi
  }
}

export const GOOGLE_SCRIPT = 'https://accounts.google.com/gsi/client'
export const APPLE_SCRIPT = 'https://appleid.cdn-apple.com/appleauth/static/jsapi/appleid/1/ar_SA/appleid.auth.js'
export const TURNSTILE_SCRIPT = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'
