import { useEffect, useRef } from 'react'
import { loadScript, TURNSTILE_SCRIPT } from '../lib/externalAuth'

/** Cloudflare Turnstile widget. Tokens are single-use: bump `resetKey` after every failed submit. */
export function Turnstile({ siteKey, onToken, resetKey }: { siteKey: string; onToken: (token: string | null) => void; resetKey: number }) {
  const box = useRef<HTMLDivElement>(null)
  const widget = useRef<string | null>(null)
  const callback = useRef(onToken)

  useEffect(() => {
    callback.current = onToken
  }, [onToken])

  useEffect(() => {
    let cancelled = false
    loadScript(TURNSTILE_SCRIPT)
      .then(() => {
        if (cancelled || !box.current || !window.turnstile) return
        widget.current = window.turnstile.render(box.current, {
          sitekey: siteKey,
          language: 'ar',
          callback: (token: string) => callback.current(token),
          'expired-callback': () => callback.current(null),
          'error-callback': () => callback.current(null),
        })
      })
      .catch(() => callback.current(null))
    return () => {
      cancelled = true
      if (widget.current) window.turnstile?.remove(widget.current)
      widget.current = null
    }
  }, [siteKey])

  useEffect(() => {
    if (resetKey > 0 && widget.current) {
      window.turnstile?.reset(widget.current)
      callback.current(null)
    }
  }, [resetKey])

  return <div ref={box} className="flex min-h-[65px] justify-center" />
}
