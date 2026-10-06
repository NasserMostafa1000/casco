import { post } from './api'

type Ttq = {
  page: () => void
  identify: (data: Record<string, string>) => void
  track: (event: string, props?: Record<string, unknown>, options?: { event_id: string }) => void
}

declare global {
  interface Window {
    ttq?: Ttq
  }
}

export type CheckoutItem = {
  content_id: string
  content_name: string
  value: number
  currency: string
}

const CHECKOUT_KEY = 'casco_tt_checkout'
const CLICK_KEY = 'casco_ttclid'

export function tiktokClick() {
  const fromUrl = new URLSearchParams(location.search).get('ttclid')
  if (fromUrl) sessionStorage.setItem(CLICK_KEY, fromUrl)
  const ttclid = sessionStorage.getItem(CLICK_KEY) || ''
  const ttp = document.cookie.split('; ').find((c) => c.startsWith('_ttp='))?.slice(5) || ''
  return { ttclid, ttp, url: location.href }
}

function relay(event: string, eventId: string, contentId: string, contentName: string, extra?: { value?: number; currency?: string }) {
  const click = tiktokClick()
  void post('/api/tiktok/events', {
    event,
    eventId,
    url: click.url,
    contentId,
    contentName,
    value: extra?.value,
    currency: extra?.currency,
    ttclid: click.ttclid || undefined,
    ttp: click.ttp || undefined,
  }).catch(() => {})
}

async function sha256(value: string) {
  const bytes = new TextEncoder().encode(value)
  const digest = await crypto.subtle.digest('SHA-256', bytes)
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('')
}

function eventId() {
  return `${Date.now()}_${Math.random().toString(36).slice(2, 8)}`
}

function track(event: string, contentId: string, contentName: string, extra?: { value?: number; currency?: string }, id = eventId()) {
  window.ttq?.track(
    event,
    {
      contents: [{ content_id: contentId, content_type: 'product', content_name: contentName }],
      ...(extra?.value != null ? { value: extra.value, currency: extra.currency || 'USD' } : {}),
    },
    { event_id: id },
  )
  relay(event, id, contentId, contentName, extra)
}

export async function identifyUser(user: { id: string; email: string }) {
  const email = user.email.trim().toLowerCase()
  if (!email || !window.ttq) return
  window.ttq.identify({
    email: await sha256(email),
    external_id: await sha256(user.id),
  })
}

export function trackView(pathname: string) {
  const names: Record<string, string> = {
    '/': 'Casco',
    '/login': 'Casco login',
    '/register': 'Casco signup',
    '/app/billing': 'Casco billing',
  }
  track('ViewContent', pathname || '/', names[pathname] ?? 'Casco')
}

export async function trackRegistration(email: string, userId: string) {
  if (!userId) return
  const normalized = email.trim().toLowerCase()
  if (normalized && window.ttq) window.ttq.identify({ email: await sha256(normalized) })
  track('CompleteRegistration', 'casco-account', 'Casco account', undefined, `reg_${userId}`)
}

export function rememberCheckout(item: CheckoutItem, paymentId: string) {
  sessionStorage.setItem(CHECKOUT_KEY, JSON.stringify(item))
  track('InitiateCheckout', item.content_id, item.content_name, { value: item.value, currency: item.currency }, `checkout_${paymentId}`)
}

export function trackPurchase(paymentId: string, fallback: { content_id: string; content_name: string }) {
  const sentKey = `casco_tt_purchased_${paymentId}`
  if (sessionStorage.getItem(sentKey)) return
  sessionStorage.setItem(sentKey, '1')
  let item: CheckoutItem | null = null
  try {
    item = JSON.parse(sessionStorage.getItem(CHECKOUT_KEY) || 'null') as CheckoutItem | null
  } catch {
    item = null
  }
  sessionStorage.removeItem(CHECKOUT_KEY)
  track(
    'Purchase',
    item?.content_id || fallback.content_id,
    item?.content_name || fallback.content_name,
    item ? { value: item.value, currency: item.currency } : undefined,
    `purchase_${paymentId}`,
  )
}
