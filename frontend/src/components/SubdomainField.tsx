import { useEffect, useState } from 'react'
import { Check, Loader2, X } from 'lucide-react'
import { errorMessage, get } from '../lib/api'
import { t, tServer } from '../lib/i18n'

interface SlugCheck {
  slug: string
  available: boolean
  code: string | null
  message: string | null
  url: string
  suggestion: string | null
}

export type SlugStatus = { state: 'idle' | 'checking' } | { state: 'ok'; url: string } | { state: 'bad'; message: string; suggestion: string | null }

/** Keeps only what a subdomain allows while the owner types: a-z, 0-9 and single dashes. */
export function cleanSlug(input: string) {
  return input
    .toLowerCase()
    .replace(/[\s_.]+/g, '-')
    .replace(/[^a-z0-9-]/g, '')
    .replace(/-{2,}/g, '-')
    .replace(/^-/, '')
    .slice(0, 40)
}

/** "name" + ".casco.studio" input with a live availability check against the API. */
export function SubdomainField({
  projectId,
  currentSlug,
  suffix,
  value,
  onChange,
  onStatus,
  autoFocus,
}: {
  projectId: string
  currentSlug: string
  suffix: string
  value: string
  onChange: (slug: string) => void
  onStatus?: (status: SlugStatus) => void
  autoFocus?: boolean
}) {
  const [status, setStatus] = useState<SlugStatus>({ state: 'idle' })

  useEffect(() => {
    const slug = value.replace(/-$/, '')
    if (slug === currentSlug) {
      const s: SlugStatus = { state: 'ok', url: '' }
      setStatus(s)
      onStatus?.(s)
      return
    }
    if (slug.length < 3) {
      const s: SlugStatus = { state: 'bad', message: t('3 أحرف على الأقل'), suggestion: null }
      setStatus(s)
      onStatus?.(s)
      return
    }
    setStatus({ state: 'checking' })
    onStatus?.({ state: 'checking' })
    let cancelled = false
    const timer = setTimeout(async () => {
      let s: SlugStatus
      try {
        const r = await get<SlugCheck>(`/api/projects/${projectId}/slug/check?slug=${encodeURIComponent(slug)}`)
        s = r.available ? { state: 'ok', url: r.url } : { state: 'bad', message: tServer(r.message), suggestion: r.suggestion }
      } catch (e) {
        s = { state: 'bad', message: errorMessage(e), suggestion: null }
      }
      if (cancelled) return
      setStatus(s)
      onStatus?.(s)
    }, 350)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
    // onStatus is a callback prop; re-checking only when the name changes is intended.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, currentSlug, projectId])

  const ring =
    status.state === 'ok' ? 'border-emerald-300 ring-emerald-500/15' : status.state === 'bad' ? 'border-red-300 ring-red-500/15' : 'border-slate-200 ring-brand-500/15'

  return (
    <div>
      <div dir="ltr" className={`flex items-center overflow-hidden rounded-xl border bg-white ring-4 transition focus-within:ring-4 ${ring}`}>
        <input
          value={value}
          onChange={(e) => onChange(cleanSlug(e.target.value))}
          autoFocus={autoFocus}
          spellCheck={false}
          autoCapitalize="off"
          autoComplete="off"
          inputMode="url"
          placeholder="my-site"
          aria-label={t('اسم الموقع في الرابط')}
          className="min-w-0 flex-1 bg-transparent px-3.5 py-3 text-base font-semibold text-ink outline-none placeholder:font-normal placeholder:text-slate-400"
        />
        <span className="shrink-0 border-s border-slate-100 bg-slate-50 px-3 py-3 text-sm font-medium text-slate-500">{suffix}</span>
        <span className="grid w-10 shrink-0 place-items-center">
          {status.state === 'checking' && <Loader2 className="h-4 w-4 animate-spin text-slate-400" />}
          {status.state === 'ok' && <Check className="h-4 w-4 text-emerald-600" />}
          {status.state === 'bad' && <X className="h-4 w-4 text-red-500" />}
        </span>
      </div>
      <div className="mt-1.5 min-h-5 text-xs">
        {status.state === 'ok' && <span className="text-emerald-700">{t('الاسم متاح ✓')}</span>}
        {status.state === 'bad' && (
          <span className="text-red-600">
            {status.message}
            {status.suggestion && (
              <>
                {' '}
                <button type="button" onClick={() => onChange(status.suggestion!)} className="font-semibold text-brand-600 underline" dir="ltr">
                  {t('استخدم {name}', { name: status.suggestion })}
                </button>
              </>
            )}
          </span>
        )}
        {(status.state === 'idle' || status.state === 'checking') && <span className="text-slate-500">{t('حروف إنجليزية صغيرة وأرقام وشرطة (-)')}</span>}
      </div>
    </div>
  )
}
