import { getLang, t, tServer } from './i18n'

const TOKEN_KEY = 'casco_token'

/** Empty when the UI is served by the API itself; e.g. https://api.casco.studio when it is hosted on Cloudflare Pages. */
const API_BASE = (import.meta.env.VITE_API_URL ?? '').replace(/\/+$/, '')

export const apiUrl = (path: string) => (path.startsWith('/') ? API_BASE + path : path)

export class ApiError extends Error {
  status: number
  code?: string
  details?: unknown
  constructor(status: number, message: string, code?: string, details?: unknown) {
    super(message)
    this.status = status
    this.code = code
    this.details = details
  }
}

export const tokenStore = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (t: string) => localStorage.setItem(TOKEN_KEY, t),
  clear: () => localStorage.removeItem(TOKEN_KEY),
}

/** Tells the API to reject this token. Bypasses api(), which redirects to login on 401. */
export function revokeToken(token: string | null) {
  if (!token) return
  void fetch(apiUrl('/api/auth/logout'), {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
  }).catch(() => {})
}

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export async function api<T = unknown>(method: Method, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { 'X-Casco-Lang': getLang() }
  const token = tokenStore.get()
  if (token) headers.Authorization = `Bearer ${token}`
  let payload: BodyInit | undefined
  if (body instanceof FormData) payload = body
  else if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }

  const res = await fetch(apiUrl(path), { method, headers, body: payload })
  const text = await res.text()
  let data: unknown = null
  if (text) {
    try {
      data = JSON.parse(text)
    } catch {
      data = { message: text }
    }
  }
  if (!res.ok) {
    const d = (data ?? {}) as { message?: string; code?: string; title?: string; details?: unknown }
    if (res.status === 401 && token) {
      tokenStore.clear()
      if (!location.pathname.startsWith('/login')) location.href = '/login'
    }
    throw new ApiError(res.status, d.message || d.title || t('خطأ {status}', { status: res.status }), d.code, d.details)
  }
  return data as T
}

export const get = <T>(path: string) => api<T>('GET', path)
export const post = <T>(path: string, body?: unknown) => api<T>('POST', path, body ?? {})
export const put = <T>(path: string, body?: unknown) => api<T>('PUT', path, body ?? {})
export const patch = <T>(path: string, body?: unknown) => api<T>('PATCH', path, body ?? {})
export const del = <T>(path: string) => api<T>('DELETE', path)

/** Downloads an authenticated file (e.g. the site ZIP). */
export async function downloadFile(path: string, filename: string) {
  const token = tokenStore.get()
  const res = await fetch(apiUrl(path), { headers: token ? { Authorization: `Bearer ${token}` } : {} })
  if (!res.ok) {
    const d = (await res.json().catch(() => ({}))) as { message?: string; code?: string }
    throw new ApiError(res.status, d.message || t('خطأ {status}', { status: res.status }), d.code)
  }
  const url = URL.createObjectURL(await res.blob())
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}

export function errorMessage(e: unknown): string {
  return e instanceof Error ? tServer(e.message) : t('حدث خطأ غير متوقع')
}

// ---------- Types ----------
export interface TemplateInfo {
  key: string
  name: string
  description: string
  plan: 'free' | 'pro'
  allowed: boolean
}

export interface Me {
  user: { id: string; name: string; email: string; role: string; freeSiteUsed: boolean }
  plan: { key: 'free' | 'pro'; interval: string | null; periodEnd: string | null; isPro: boolean; daysLeft: number | null }
  credits: { plan: number; topup: number; reserved: number; available: number }
  limits: {
    maxProjects: number
    projectsCount: number
    canUsePremium: boolean
    canCustomDomain: boolean
    canMultiPage: boolean
    templates: TemplateInfo[]
  }
}

export interface ProjectSummary {
  id: string
  name: string
  templateKey: string
  slug: string
  publishedAt: string | null
  updatedAt: string
  createdAt: string
  siteUrl: string | null
  suspended: boolean
  hostingTier?: string | null
  hostingDaysLeft?: number | null
  hostingOnline?: boolean
}

export interface ChatQuestion {
  prompt: string
  options: { label: string; recommended: boolean }[]
}

export interface ChatMsg {
  id: number
  role: 'user' | 'assistant'
  content: string
  credits: number | null
  createdAt: string
  taskId: string | null
  images: string[] | null
  questions: ChatQuestion[] | null
}

export interface ProjectDetail {
  id: string
  name: string
  description: string
  templateKey: string
  slug: string
  siteKey: string
  customDomain: string | null
  customDomainVerified: boolean
  adsRequireApproval: boolean
  currentVersionId: string | null
  publishedVersionId: string | null
  publishedAt: string | null
  suspension: { reason: string | null; at: string | null; supportWhatsApp: string } | null
  template: { key: string; name: string; modules: string[] } | null
  files: string[]
  features: string[]
  requiredTier: HostingTier
  previewBase: string
  siteUrl: string
  subdomainUrl: string
  /** What follows the chosen name in the site address, e.g. ".casco.studio". */
  siteSuffix: string
  cnameTarget: string
  versions: {
    id: string
    number: number
    summary: string
    prompt: string | null
    model: string | null
    parts: number
    filesChanged: number
    createdAt: string
  }[]
  messages: ChatMsg[]
  activeTask: { id: string; status: string; kind: string } | null
  /** The last big build stopped before finishing; "كمل البناء" continues it. */
  continuable: { taskId: string; remaining: string; pagesLeft: number } | null
}

export interface TaskInfo {
  id: string
  status: 'queued' | 'running' | 'succeeded' | 'failed'
  error: string | null
  creditsCharged: number
  resultVersionId: string | null
}

export interface Plans {
  currency: string
  free: { maxProjects: number; signupCredits: number }
  pro: { monthlyPrice: number; yearlyPrice: number; monthlyCredits: number; maxProjects: number }
  topups: { id: string; credits: number; price: number }[]
  hosting: { staticMonthly: number; staticYearly: number; backendMonthly: number; backendYearly: number; reactYearly: number; graceDays: number }
  wallet?: { amount: number; currency: string; phone: string; methods: string[] }
}

export type HostingTier = 'static' | 'backend'

export interface HostingPrices {
  currency: string
  static: { monthly: number; yearly: number }
  backend: { monthly: number; yearly: number }
  graceDays: number
}

export interface HostingStatus {
  requiredTier: HostingTier
  tier: HostingTier | null
  paidUntil: string | null
  active: boolean
  enough: boolean
  inGrace: boolean
  daysLeft: number
  suspended: boolean
  suspensionReason: string | null
  suspendedAt: string | null
  prices: HostingPrices
}

export const SUPPORT_WHATSAPP = '+971569166263'
export const supportWhatsAppUrl = (text = 'مرحباً، أريد تفعيل الدفع الإلكتروني لموقعي على Casco') =>
  `https://wa.me/${SUPPORT_WHATSAPP.replace(/\D/g, '')}?text=${encodeURIComponent(text)}`
