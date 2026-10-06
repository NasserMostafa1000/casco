import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ArrowLeft, ArrowUp, AtSign, Mail, MapPin, Paperclip, Phone, Sparkles, X, Zap } from 'lucide-react'
import { Logo } from '../components/AppShell'
import { ThemeToggle } from '../components/ThemeToggle'
import { MediaThumb } from '../components/MediaThumb'
import { ShareForPrompts } from '../components/ShareForPrompts'
import { Alert, Button, Modal, Spinner } from '../components/ui'
import { ApiError, api, errorMessage, post } from '../lib/api'
import { useAuth } from '../lib/auth'
import { MAX_VIDEO_BYTES, MEDIA_ACCEPT, isMediaFile, isVideoFile, prepareImage } from '../lib/images'
import { getLang, num, t } from '../lib/i18n'
import { usd, usePlans } from '../lib/plans'
import { clearPendingMedia, pendingMedia, setPendingMedia } from '../lib/pendingMedia'
import { PENDING_PROMPT_KEY } from './Landing'

const MAX_ATTACHMENTS = 10

type Pick = { file: File; url: string; video: boolean }
type Draft = { text: string; picks: Pick[] }
type Contact = { phone: string; address: string; email: string; social: string }

const EMAIL = /[\w.+-]+@[\w-]+(\.[\w-]+)+/
const PHONE = /\+?\d[\d\s-]{6,}\d/

function contactFrom(text: string): Contact {
  return { phone: text.match(PHONE)?.[0].trim() ?? '', address: '', email: text.match(EMAIL)?.[0] ?? '', social: '' }
}

function withContact(text: string, c: Contact) {
  const lines = [
    [t('الهاتف / واتساب'), c.phone],
    [t('العنوان / الموقع'), c.address],
    [t('البريد الإلكتروني'), c.email],
    [t('صفحات التواصل الاجتماعي'), c.social],
  ]
    .filter(([, v]) => v.trim())
    .map(([k, v]) => `- ${k}: ${v.trim().replace(/\s*\n\s*/g, ', ')}`)
  return lines.length ? `${text.trim()}\n\n${t('بيانات التواصل')}:\n${lines.join('\n')}` : text.trim()
}

/** New site as a chat: the user describes the site and the AI picks the name, language and site type. */
export default function NewProject() {
  const { me, loading, refresh } = useAuth()
  const navigate = useNavigate()
  const atLimit = !!me && me.limits.projectsCount >= me.limits.maxProjects
  // What the visitor typed (on the landing page or here) before signing in: it waits in the box for them.
  const [input, setInput] = useState(() => localStorage.getItem(PENDING_PROMPT_KEY) ?? '')
  const [picks, setPicks] = useState<Pick[]>(() => pendingMedia())
  const [draft, setDraft] = useState<Draft | null>(null)
  const [contact, setContact] = useState<Contact>({ phone: '', address: '', email: '', social: '' })
  const [sent, setSent] = useState<string | null>(null)
  const [sentPreviews, setSentPreviews] = useState<Pick[]>([])
  const [error, setError] = useState('')
  const [freeLimit, setFreeLimit] = useState(false)
  const plans = usePlans()
  const box = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    if (!me) return
    localStorage.removeItem(PENDING_PROMPT_KEY)
    // Clear after this mount settles. Doing it immediately drops the files on React's dev double-mount.
    const id = setTimeout(() => {
      clearPendingMedia()
    }, 0)
    return () => clearTimeout(id)
  }, [me])

  const send = useCallback(
    async (text: string, attached: Pick[]) => {
      const description = text.trim() || (attached.length > 0 ? t('ابنِ الموقع باستخدام الصور المرفقة') : '')
      if (!description) return
      setError('')
      setSent(description)
      setSentPreviews(attached)
      try {
        const withMedia = attached.length > 0
        const r = await post<{ id: string; taskId: string | null; notice: string | null }>('/api/projects', {
          name: '',
          description,
          templateKey: 'auto',
          language: getLang(),
          generate: !withMedia,
        })
        if (withMedia) {
          const urls: string[] = []
          for (const p of attached) {
            const fd = new FormData()
            fd.append('file', await prepareImage(p.file))
            urls.push((await api<{ url: string }>('POST', `/api/projects/${r.id}/images`, fd)).url)
          }
          try {
            await post(`/api/projects/${r.id}/generate`, { images: urls, language: getLang() })
          } catch (genErr) {
            sessionStorage.setItem(`casco_notice_${r.id}`, r.notice ?? errorMessage(genErr))
          }
        }
        if (r.notice) sessionStorage.setItem(`casco_notice_${r.id}`, r.notice)
        setPicks([])
        await refresh()
        navigate(`/app/p/${r.id}`, { replace: true })
      } catch (err) {
        if (err instanceof ApiError && err.code === 'free_prompt_limit') setFreeLimit(true)
        else setError(errorMessage(err))
        setSent(null)
        setSentPreviews([])
        setInput(text)
        setPicks(attached)
      }
    },
    [navigate, refresh],
  )

  useEffect(() => {
    const el = box.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 160)}px`
  }, [input])

  const picksRef = useRef(picks)
  picksRef.current = picks
  const draftRef = useRef(draft)
  draftRef.current = draft
  useEffect(
    () => () => {
      const keep = new Set(pendingMedia().map((p) => p.url))
      for (const p of [...picksRef.current, ...(draftRef.current?.picks ?? [])]) {
        if (!keep.has(p.url)) URL.revokeObjectURL(p.url)
      }
    },
    [],
  )

  if (loading)
    return (
      <div className="grid min-h-screen place-items-center text-brand-600">
        <Spinner className="h-8 w-8" />
      </div>
    )

  const busy = sent !== null
  const locked = busy || atLimit || draft !== null

  const addFiles = (list: FileList | File[]) => {
    const room = MAX_ATTACHMENTS - picks.length
    const media = Array.from(list).filter(isMediaFile)
    const tooBig = media.some((f) => isVideoFile(f) && f.size > MAX_VIDEO_BYTES)
    const extra = media.filter((f) => !isVideoFile(f) || f.size <= MAX_VIDEO_BYTES).slice(0, room)
    if (extra.length === 0) {
      if (room <= 0) setError(t('يمكنك إرفاق {n} صور كحد أقصى في الطلب الواحد', { n: MAX_ATTACHMENTS }))
      else if (tooBig) setError(t('حجم الفيديو يجب ألا يتجاوز 50 ميجابايت'))
      return
    }
    setError(tooBig ? t('حجم الفيديو يجب ألا يتجاوز 50 ميجابايت') : '')
    setPicks((p) => [...p, ...extra.map((file) => ({ file, url: URL.createObjectURL(file), video: isVideoFile(file) }))])
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (locked || (!input.trim() && picks.length === 0)) return
    if (!me) {
      // Sign in first, then come back here with the description (and attachments) still in place.
      localStorage.setItem(PENDING_PROMPT_KEY, input.trim() || t('ابنِ الموقع باستخدام الصور المرفقة'))
      setPendingMedia(picks)
      navigate('/login')
      return
    }
    setDraft({ text: input, picks })
    setContact(contactFrom(input))
    setInput('')
    setPicks([])
  }
  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    // Enter sends on a keyboard; on phones Enter adds a new line and the button sends.
    if (e.key === 'Enter' && !e.shiftKey && window.matchMedia('(pointer: fine)').matches) submit(e)
  }

  const build = (skip: boolean) => {
    if (!draft) return
    const { text, picks: attached } = draft
    setDraft(null)
    void send(skip ? text : withContact(text, contact), attached)
  }
  const editDraft = () => {
    if (!draft) return
    setInput(draft.text)
    setPicks(draft.picks)
    setDraft(null)
  }

  const shown = draft ?? (sent ? { text: sent, picks: sentPreviews } : null)

  return (
    <div className="flex h-dvh flex-col bg-slate-50">
      <header className="flex h-14 shrink-0 items-center gap-2 border-b border-slate-200 bg-white px-2 sm:px-4">
        {me ? (
          <>
            <Link to="/app" className="grid h-9 w-9 shrink-0 place-items-center rounded-xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-900" title={t('رجوع')}>
              <ArrowLeft className="flip-rtl h-5 w-5" />
            </Link>
            <h1 className="min-w-0 flex-1 truncate text-sm font-semibold text-ink sm:text-base">{t('موقع جديد')}</h1>
            <Link
              to="/app/billing"
              title={t('النقاط المتاحة')}
              className="inline-flex h-9 items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-3 text-sm font-semibold text-slate-800 shadow-sm transition hover:border-slate-300"
            >
              <Zap className="h-4 w-4 fill-amber-400 text-amber-500" />
              {num(me.credits.available)}
            </Link>
            <ThemeToggle />
          </>
        ) : (
          <>
            <Logo className="flex-1" />
            <ThemeToggle />
            <Link to="/login" className="inline-flex h-9 items-center rounded-xl px-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-100">
              {t('دخول')}
            </Link>
          </>
        )}
      </header>

      <main className="min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto flex max-w-2xl flex-col gap-4 px-4 py-6">
          <Bubble from="ai">
            <p className="font-semibold text-ink">{me ? t('أهلاً {name}!', { name: me.user.name.split(' ')[0] }) : t('أهلاً بك!')}</p>
            <p className="mt-1">{t('صف لي موقعك: ما نشاطك، وما الأقسام التي تريدها، وكيف يتواصل معك عملاؤك؟ سأبنيه لك خلال دقيقة، وتقدر تعدّل عليه بعدها بالكلام.')}</p>
          </Bubble>

          {atLimit && (
            <Bubble from="ai">
              {t('وصلت للحد الأقصى من المواقع في خطتك.')}{' '}
              <Link to="/app/billing" className="font-semibold text-brand-600 underline">
                {t('اشترك في Pro')}
              </Link>
            </Bubble>
          )}

          {shown && (
            <Bubble from="user">
              {shown.picks.length > 0 && (
                <div className="mb-2 flex flex-wrap gap-1.5">
                  {shown.picks.map((p) => (
                    <MediaThumb key={p.url} src={p.url} video={p.video} className="h-16 w-16 rounded-lg" />
                  ))}
                </div>
              )}
              {shown.text.trim() || t('ابنِ الموقع باستخدام الصور المرفقة')}
            </Bubble>
          )}

          {draft && (
            <Bubble from="ai">
              <p className="font-semibold text-ink">{t('فكرة حلوة! قبل ما أبدأ:')}</p>
              <p className="mt-1">{t('ضيف بيانات التواصل عشان تظهر في الموقع (زر واتساب، الخريطة، الفوتر). كلها اختيارية وتقدر تضيفها بعدين.')}</p>
              <div className="mt-3 space-y-2">
                <ContactField icon={<Phone className="h-4 w-4" />} label={t('رقم الهاتف / واتساب')} value={contact.phone} dir="ltr" type="tel"
                  onChange={(phone) => setContact((c) => ({ ...c, phone }))} placeholder="+971 50 000 0000" />
                <ContactField icon={<MapPin className="h-4 w-4" />} label={t('العنوان أو رابط الموقع على الخريطة')} value={contact.address}
                  onChange={(address) => setContact((c) => ({ ...c, address }))} placeholder={t('مثال: دبي، شارع الشيخ زايد')} />
                <ContactField icon={<Mail className="h-4 w-4" />} label={t('البريد الإلكتروني')} value={contact.email} dir="ltr" type="email"
                  onChange={(email) => setContact((c) => ({ ...c, email }))} placeholder="info@example.com" />
                <ContactField icon={<AtSign className="h-4 w-4" />} label={t('صفحات التواصل الاجتماعي')} value={contact.social} dir="ltr"
                  onChange={(social) => setContact((c) => ({ ...c, social }))} placeholder="instagram.com/... , facebook.com/..." />
              </div>
              <div className="mt-4 flex flex-wrap items-center gap-2">
                <Button variant="dark" onClick={() => build(false)}>
                  <Sparkles className="h-4 w-4" />
                  {t('ابنِ الموقع')}
                </Button>
                <Button variant="secondary" onClick={() => build(true)}>
                  {t('تخطي')}
                </Button>
                <button type="button" onClick={editDraft} className="ms-auto text-sm font-semibold text-slate-500 hover:text-slate-800">
                  {t('تعديل الوصف')}
                </button>
              </div>
            </Bubble>
          )}

          {sent && (
            <Bubble from="ai">
              <span className="inline-flex items-center gap-2 text-slate-500">
                <Spinner className="h-4 w-4 text-brand-600" />
                {t('جاري التجهيز...')}
              </span>
            </Bubble>
          )}

          {error && <Alert tone="error">{error}</Alert>}
        </div>
      </main>

      <footer className="shrink-0 border-t border-slate-200 bg-white px-3 pt-3 pb-[max(0.75rem,env(safe-area-inset-bottom))]">
        {me && !me.plan.isPro && (
          <p className="mx-auto mb-2 max-w-2xl text-center text-xs text-slate-500">
            {t('بعد تسجيل الدخول لك طلب مجاني واحد، ويخلّص بالكامل. بعده اشحن الحساب نفسه من صفحة الفوترة.')}
          </p>
        )}
        <form onSubmit={submit} className="mx-auto max-w-2xl">
          <div className="rounded-2xl border border-slate-200 bg-white p-2 shadow-sm transition focus-within:border-brand-400 focus-within:ring-4 focus-within:ring-brand-500/10">
            {picks.length > 0 && (
              <div className="flex flex-wrap gap-2 px-1 pb-2">
                {picks.map((p) => (
                  <div key={p.url} className="relative">
                    <MediaThumb src={p.url} video={p.video} className="h-14 w-14 rounded-lg ring-1 ring-slate-200" />
                    <button
                      type="button"
                      disabled={locked}
                      onClick={() => {
                        URL.revokeObjectURL(p.url)
                        setPicks((list) => list.filter((x) => x.url !== p.url))
                      }}
                      className="absolute -end-1.5 -top-1.5 grid h-5 w-5 place-items-center rounded-full bg-ink text-white"
                      aria-label={t('إزالة')}
                    >
                      <X className="h-3 w-3" />
                    </button>
                  </div>
                ))}
              </div>
            )}
            <div className="flex items-end gap-1">
              <label
                className={`grid h-10 w-10 shrink-0 cursor-pointer place-items-center rounded-xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-800 ${locked ? 'pointer-events-none opacity-50' : ''}`}
                title={t('إرفاق صور أو فيديو من جهازك')}
              >
                <Paperclip className="h-5 w-5" />
                <input
                  type="file"
                  accept={MEDIA_ACCEPT}
                  multiple
                  className="hidden"
                  disabled={locked}
                  onChange={(e) => {
                    addFiles(e.target.files ?? [])
                    e.target.value = ''
                  }}
                />
              </label>
              <textarea
                ref={box}
                value={input}
                onChange={(e) => setInput(e.target.value)}
                onKeyDown={onKeyDown}
                rows={1}
                autoFocus
                disabled={locked}
                placeholder={
                  picks.length > 0
                    ? t('أين تريد وضع الصور؟ مثال: اجعل الأولى خلفية الهيدر والباقي في معرض أعمالنا')
                    : t('صف موقعك... مثال: موقع لمطعم مشويات في دبي فيه المنيو وحجز طاولات ورقم واتساب')
                }
                className="max-h-40 min-h-10 flex-1 resize-none bg-transparent px-2 py-2 text-base text-slate-900 outline-none placeholder:text-slate-400 disabled:cursor-not-allowed"
              />
              <button
                type="submit"
                disabled={locked || (!input.trim() && picks.length === 0)}
                className="grid h-10 w-10 shrink-0 place-items-center rounded-xl bg-ink text-white transition hover:bg-slate-800 disabled:bg-slate-200 disabled:text-slate-400"
                aria-label={t('إرسال')}
              >
                {busy ? <Spinner className="h-4 w-4" /> : <ArrowUp className="h-5 w-5" />}
              </button>
            </div>
          </div>
        </form>
      </footer>
      <Modal open={freeLimit} onClose={() => setFreeLimit(false)} title={t('عدّل موقعك بالذكاء الاصطناعي')}>
        <p className="text-slate-600">{t('الطلب المجاني اتستخدم. سجّل الدخول بنفس حساب جوجل أو آبل واشحن حسابك من صفحة الفوترة، من غير ما تلصق أي توكن.')}</p>
        <Link to="/app/billing" className="mt-6 flex h-12 items-center justify-center rounded-xl bg-gradient-to-r from-brand-600 to-fuchsia-500 font-semibold text-white shadow-lg shadow-brand-600/25">
          {t('اشترك الآن بـ {price} شهرياً', { price: usd(plans?.pro.monthlyPrice) })}
        </Link>
        <ShareForPrompts onGranted={() => setFreeLimit(false)} />
      </Modal>
    </div>
  )
}

function ContactField({ icon, label, value, onChange, placeholder, dir, type = 'text' }: {
  icon: ReactNode
  label: string
  value: string
  onChange: (value: string) => void
  placeholder?: string
  dir?: 'ltr' | 'rtl'
  type?: string
}) {
  return (
    <label className="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 focus-within:border-brand-400 focus-within:bg-white">
      <span className="shrink-0 text-slate-400" title={label}>{icon}</span>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={label}
        dir={dir}
        className="h-10 min-w-0 flex-1 bg-transparent text-sm text-slate-900 outline-none placeholder:text-slate-400"
      />
    </label>
  )
}

function Bubble({ from, children }: { from: 'ai' | 'user'; children: ReactNode }) {
  if (from === 'user')
    return <div className="ms-auto max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-se-md bg-ink px-4 py-3 text-sm leading-relaxed text-white sm:text-base">{children}</div>
  return (
    <div className="flex max-w-[92%] items-start gap-2.5">
      <span className="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-gradient-to-br from-brand-500 to-fuchsia-500 text-white shadow-md shadow-brand-600/25">
        <Sparkles className="h-4 w-4" />
      </span>
      <div className="min-w-0 flex-1 rounded-2xl rounded-ss-md border border-slate-200 bg-white px-4 py-3 text-sm leading-relaxed text-slate-700 shadow-sm sm:text-base">{children}</div>
    </div>
  )
}
