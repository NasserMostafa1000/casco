import { useEffect, useState } from 'react'
import { get, type Plans } from './api'
import { t } from './i18n'

let cached: Promise<Plans> | null = null

/** Prices are editable from the admin dashboard, so every price shown in the UI comes from here. */
export function loadPlans(refresh = false): Promise<Plans> {
  if (!cached || refresh) {
    cached = get<Plans>('/api/billing/plans').catch((e) => {
      cached = null
      throw e
    })
  }
  return cached
}

export function usePlans(): Plans | null {
  const [plans, setPlans] = useState<Plans | null>(null)
  useEffect(() => {
    let alive = true
    loadPlans()
      .then((p) => alive && setPlans(p))
      .catch(() => {})
    return () => {
      alive = false
    }
  }, [])
  return plans
}

export const usd = (n: number | undefined | null) => (n == null ? '…' : `$${Number(Number(n).toFixed(2))}`)

/** "شهرين مجاناً" style label for a yearly price, or null when yearly isn't cheaper. */
export function yearlySavingLabel(monthly: number, yearly: number): string | null {
  if (!monthly || !yearly) return null
  const free = Math.round(12 - yearly / monthly)
  if (free < 1) return null
  if (free === 1) return t('شهر مجاناً')
  if (free === 2) return t('شهرين مجاناً')
  return t('{n} شهور مجاناً', { n: free })
}
