import { useCallback, useEffect, useState } from 'react'
import { Share2 } from 'lucide-react'
import { errorMessage, get, post } from '../lib/api'
import { num, t } from '../lib/i18n'

type ShareStatus = {
  link: string
  joined: number
  required: number
  claimed: boolean
}

export function ShareForPrompts({ onGranted }: { onGranted: () => void }) {
  const [share, setShare] = useState<ShareStatus | null>(null)
  const [copied, setCopied] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    try {
      setShare(await get<ShareStatus>('/api/me/share'))
    } catch {
      /* keep the last status if a refresh fails */
    }
  }, [])

  useEffect(() => {
    void load()
    const onVisible = () => {
      if (document.visibilityState === 'visible') void load()
    }
    document.addEventListener('visibilitychange', onVisible)
    return () => document.removeEventListener('visibilitychange', onVisible)
  }, [load])

  const shareLink = async () => {
    if (!share) return
    setCopied(false)
    if (navigator.share) {
      try {
        await navigator.share({ title: 'Casco', url: share.link })
        return
      } catch {
        /* cancelled or unsupported; the link stays selectable below */
      }
    }
    try {
      await navigator.clipboard.writeText(share.link)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  const claim = async () => {
    setBusy(true)
    setError('')
    try {
      await post('/api/me/share/claim', {})
      onGranted()
    } catch (err) {
      setError(errorMessage(err))
      void load()
    } finally {
      setBusy(false)
    }
  }

  if (!share || share.claimed) return null

  const ready = share.joined >= share.required

  return (
    <div className="mt-5 rounded-2xl border border-slate-200 bg-slate-50 p-4">
      <p className="font-semibold text-ink">{t('شارك 10 أصدقاء واحصل على برومبتين')}</p>
      <p className="mt-1 text-sm leading-relaxed text-slate-600">
        {t('الصديق لازم ينشئ حسابًا جديدًا من رابطك. البرومبتين يتضافوا بعد 10 حسابات فعلًا، مش بمجرد المشاركة. المكافأة مرة واحدة فقط.')}
      </p>
      <p className="mt-3 text-sm font-medium text-slate-800">
        {t('انضم {n} من {total}', { n: share ? num(share.joined) : '…', total: share ? num(share.required) : '10' })}
      </p>
      <input
        readOnly
        value={share.link}
        onFocus={(e) => e.target.select()}
        className="mt-3 w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700"
        aria-label={t('رابط المشاركة')}
      />
      <button
        type="button"
        onClick={() => void shareLink()}
        className="mt-3 flex h-11 w-full items-center justify-center gap-2 rounded-xl border border-slate-200 bg-white font-semibold text-slate-800"
      >
        <Share2 className="h-4 w-4" />
        {copied ? t('تم نسخ الرابط') : t('شارك الرابط')}
      </button>
      <button
        type="button"
        disabled={!ready || busy}
        onClick={() => void claim()}
        className="mt-2 flex h-11 w-full items-center justify-center rounded-xl bg-ink font-semibold text-white disabled:bg-slate-200 disabled:text-slate-400"
      >
        {t('احصل على البرومبتين')}
      </button>
      {error && <p className="mt-2 text-sm text-red-600">{error}</p>}
    </div>
  )
}
