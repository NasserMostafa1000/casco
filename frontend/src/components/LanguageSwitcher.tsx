import { useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Check, ChevronDown, Globe } from 'lucide-react'
import { get, tokenStore } from '../lib/api'
import { LANGUAGES, getLang, setLang, t } from '../lib/i18n'

function usePhone() {
  const [phone, setPhone] = useState(() => window.matchMedia('(max-width: 639px)').matches)
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 639px)')
    const onChange = () => setPhone(mq.matches)
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [])
  return phone
}

/** Small popover anchored to its trigger. On a phone it becomes a bottom sheet so it is never clipped. */
export function Popover({ trigger, children, align = 'end', className = '' }: { trigger: (open: boolean) => ReactNode; children: (close: () => void) => ReactNode; align?: 'start' | 'end'; className?: string }) {
  const [open, setOpen] = useState(false)
  const phone = usePhone()
  const ref = useRef<HTMLDivElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)
  const close = () => setOpen(false)
  useEffect(() => {
    if (!open) return
    const onDown = (e: Event) => {
      const target = e.target as Node
      if (ref.current?.contains(target) || panelRef.current?.contains(target)) return
      setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDown)
    document.addEventListener('touchstart', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('touchstart', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])
  useEffect(() => {
    if (!open || phone) return
    const panel = panelRef.current
    const triggerBox = ref.current?.getBoundingClientRect()
    if (!panel || !triggerBox) return
    if (window.innerHeight - triggerBox.bottom < panel.offsetHeight + 16) {
      panel.style.top = 'auto'
      panel.style.bottom = '100%'
      panel.style.marginTop = '0'
      panel.style.marginBottom = '0.5rem'
    }
  }, [open, phone])
  return (
    <div ref={ref} className={`relative shrink-0 ${className}`}>
      <div onClick={() => setOpen((o) => !o)}>{trigger(open)}</div>
      {open && phone &&
        createPortal(
          <div className="fixed inset-0 z-[80] flex items-end">
            <button type="button" className="absolute inset-0 bg-black/55" aria-label={t('إغلاق')} onClick={close} />
            <div
              ref={panelRef}
              className="relative max-h-[min(70vh,32rem)] w-full overflow-y-auto rounded-t-3xl border border-white/10 bg-[#1e1e1e] p-2 pb-[max(0.75rem,env(safe-area-inset-bottom))] text-[#ececec] shadow-2xl"
            >
              <div className="mx-auto mb-2 h-1 w-10 rounded-full bg-white/20" />
              {children(close)}
            </div>
          </div>,
          document.body,
        )}
      {open && !phone && (
        <div
          ref={panelRef}
          className={`absolute top-full z-50 mt-2 max-h-[min(70vh,24rem)] min-w-48 overflow-y-auto rounded-2xl border border-slate-200 bg-white p-1.5 text-slate-200 shadow-xl shadow-slate-900/10 ${align === 'end' ? 'end-0' : 'start-0'}`}
        >
          {children(close)}
        </div>
      )}
    </div>
  )
}

export function LanguageSwitcher({ compact = false, dark = false }: { compact?: boolean; dark?: boolean }) {
  const lang = getLang()
  const currentLang = LANGUAGES.find((l) => l.code === lang)!
  return (
    <Popover
      trigger={() => (
        <button
          type="button"
          aria-label={t('اللغة')}
          className={`inline-flex h-9 items-center gap-1.5 rounded-xl px-2.5 text-sm font-medium transition ${
            dark ? 'text-slate-300 hover:bg-white/10 hover:text-white' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900'
          }`}
        >
          <Globe className="h-4 w-4 shrink-0" />
          <span className={compact ? 'hidden sm:inline' : ''}>{currentLang.label}</span>
          {compact && <span className="text-xs font-bold sm:hidden">{currentLang.short}</span>}
          <ChevronDown className="h-3.5 w-3.5 shrink-0 opacity-60" />
        </button>
      )}
    >
      {(close) => (
        <div className="flex flex-col gap-1">
          <p className="px-3 py-2 text-xs font-semibold text-[#bdbdbd] sm:text-slate-400">{t('اللغة')}</p>
          {LANGUAGES.map((l) => (
            <button
              key={l.code}
              type="button"
              lang={l.code}
              onClick={() => {
                close()
                setLang(l.code)
                // The server e-mails each user in their interface language.
                if (tokenStore.get()) get('/api/me').catch(() => {})
              }}
              className={`flex min-h-11 w-full items-center justify-between gap-3 rounded-xl px-3 py-2.5 text-start text-base transition hover:bg-white/10 sm:text-sm sm:hover:bg-slate-50 ${l.code === lang ? 'bg-white/10 font-semibold text-white sm:bg-slate-100 sm:text-brand-700' : 'text-[#ececec] sm:text-slate-700'}`}
            >
              {l.label}
              {l.code === lang && <Check className="h-4 w-4 shrink-0" />}
            </button>
          ))}
        </div>
      )}
    </Popover>
  )
}
