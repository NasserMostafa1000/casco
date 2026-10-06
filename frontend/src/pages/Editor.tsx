import { useCallback, useEffect, useRef, useState, type ClipboardEvent, type FormEvent, type KeyboardEvent, type ReactNode } from 'react'
import { Link, Navigate, useParams } from 'react-router-dom'
import {
  ArrowLeft,
  ArrowUp,
  Check,
  Code2,
  Database,
  Download,
  Eye,
  ExternalLink,
  Globe,
  History,
  Images,
  Lock,
  MessageSquare,
  Monitor,
  MoreHorizontal,
  Paperclip,
  Play,
  Rocket,
  RotateCw,
  Smartphone,
  Sparkles,
  Tablet,
  Wrench,
  X,
  Zap,
} from 'lucide-react'
import { BuildStage, ChatActivity, FileBrowser, LiveCode, streamedFiles, type ThinkStep } from '../components/Code'
import { PlanQuestions } from '../components/PlanQuestions'
import { HostingModal } from '../components/HostingModal'
import { Popover } from '../components/LanguageSwitcher'
import { ThemeToggle } from '../components/ThemeToggle'
import { OutOfCreditsModal, type CreditShortfall } from '../components/OutOfCreditsModal'
import { ShareForPrompts } from '../components/ShareForPrompts'
import { SubdomainField, type SlugStatus } from '../components/SubdomainField'
import { Alert, Button, Modal, Segmented, Spinner } from '../components/ui'
import { ApiError, api, del, downloadFile, errorMessage, get, post, put, supportWhatsAppUrl, type ProjectDetail, type TaskInfo } from '../lib/api'
import { useAuth } from '../lib/auth'
import { formatDateTime, num, t, tServer } from '../lib/i18n'
import { MAX_VIDEO_BYTES, MEDIA_ACCEPT, isMediaFile, isVideoFile, prepareImage } from '../lib/images'
import { MediaThumb } from '../components/MediaThumb'
import { usd, usePlans } from '../lib/plans'
import { followTask, type TaskStreamEvent } from '../lib/taskStream'

type Device = 'desktop' | 'tablet' | 'mobile'
type View = 'preview' | 'code'
type Upgrade = 'edit' | 'download' | null
const deviceWidth: Record<Device, string> = { desktop: '100%', tablet: '820px', mobile: '390px' }
const MAX_ATTACHMENTS = 10

const isDesktop = () => window.matchMedia('(min-width: 1024px)').matches

export default function Editor() {
  const { id = '' } = useParams()
  const { me, refresh } = useAuth()
  const [project, setProject] = useState<ProjectDetail | null>(null)
  const [error, setError] = useState('')
  const [input, setInput] = useState('')
  const [premium, setPremium] = useState(false)
  const [sending, setSending] = useState(false)
  const [taskId, setTaskId] = useState<string | null>(null)
  const [device, setDevice] = useState<Device>('desktop')
  const [view, setView] = useState<View>('preview')
  const [mobileTab, setMobileTab] = useState<'chat' | 'preview'>('chat')
  const [page, setPage] = useState('index.html')
  const [previewVersion, setPreviewVersion] = useState<string | null>(null)
  const [frameKey, setFrameKey] = useState(0)
  const [runtimeErrors, setRuntimeErrors] = useState<string[]>([])
  const [publishing, setPublishing] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [publishedUrl, setPublishedUrl] = useState<string | null>(null)
  const [chooseName, setChooseName] = useState<string | null>(null)
  const [nameStatus, setNameStatus] = useState<SlugStatus>({ state: 'idle' })
  const [nameError, setNameError] = useState('')
  const [showVersions, setShowVersions] = useState(false)
  const [upgrade, setUpgrade] = useState<Upgrade>(null)
  const plans = usePlans()
  const [hosting, setHosting] = useState<{ reason?: string } | null>(null)
  const [outOfCredits, setOutOfCredits] = useState<{ message: string; details: CreditShortfall } | null>(null)
  const [live, setLive] = useState({ text: '', attempt: 1, part: 1, thought: '' })
  const [steps, setSteps] = useState<ThinkStep[]>([])
  const [autoFix, setAutoFix] = useState(true)
  const liveRef = useRef(live)
  liveRef.current = live
  const errorsRef = useRef(runtimeErrors)
  errorsRef.current = runtimeErrors
  const [attachments, setAttachments] = useState<string[]>([])
  const [uploading, setUploading] = useState(0)
  const [gallery, setGallery] = useState<string[] | null>(null)
  const [notice] = useState<string | null>(() => sessionStorage.getItem(`casco_notice_${id}`))
  const frameRef = useRef<HTMLIFrameElement>(null)
  const probeRef = useRef<HTMLIFrameElement>(null)
  const probedVersion = useRef<string | null>(null)
  const [probePage, setProbePage] = useState<string | null>(null)
  const chatScrollRef = useRef<HTMLDivElement>(null)
  const finishedTask = useRef<string | null>(null)
  const versionAtStart = useRef<string | null>(null)
  const [queue, setQueue] = useState<{ text: string; premium: boolean; images: string[] }[]>([])
  const queueRef = useRef(queue)
  queueRef.current = queue
  const prevTask = useRef<string | null>(null)
  const flushArmed = useRef(false)
  const isPro = !!me?.plan.isPro

  const suggestions = [t('غيّر الألوان لدرجات الأزرق'), t('أضف قسم آراء العملاء'), t('أضف زر واتساب عائم'), t('حسّن النصوص لتكون أكثر إقناعاً'), t('أضف قسم الأسئلة الشائعة')]
  const [hints, setHints] = useState<string[]>([])
  const [hintText, setHintText] = useState('')
  useEffect(() => {
    if (!isPro) return
    let stop = false
    get<{ hints: string[] }>(`/api/projects/${id}/hints`).then((r) => { if (!stop) setHints(r.hints ?? []) }).catch(() => {})
    return () => { stop = true }
  }, [id, isPro])
  useEffect(() => {
    if (!isPro || hints.length === 0 || input.trim() || taskId || attachments.length > 0) return
    let item = 0
    let count = 0
    let timer = 0
    const step = () => {
      const full = hints[item] ?? ''
      count += 1
      if (count <= full.length) {
        setHintText(full.slice(0, count))
        timer = window.setTimeout(step, 55)
        return
      }
      timer = window.setTimeout(() => {
        item = (item + 1) % hints.length
        count = 0
        setHintText('')
        timer = window.setTimeout(step, 350)
      }, 2400)
    }
    timer = window.setTimeout(step, 400)
    return () => window.clearTimeout(timer)
  }, [hints, isPro, input, taskId, attachments.length])

  const load = useCallback(async () => {
    try {
      const p = await get<ProjectDetail>(`/api/projects/${id}`)
      setProject(p)
      setTaskId(p.activeTask?.id ?? null)
      return p
    } catch (e) {
      setError(errorMessage(e))
      return null
    }
  }, [id])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (notice) sessionStorage.removeItem(`casco_notice_${id}`)
  }, [notice, id])

  useEffect(() => {
    const el = chatScrollRef.current
    if (!el) return
    const frame = requestAnimationFrame(() => {
      el.scrollTop = el.scrollHeight
    })
    return () => cancelAnimationFrame(frame)
  }, [project?.messages.length, taskId, mobileTab, steps.length, live.thought, live.text, queue.length])

  const finish = useCallback(
    async (tid: string) => {
      if (finishedTask.current === tid) return
      finishedTask.current = tid
      setTaskId(null)
      setRuntimeErrors([])
      setPreviewVersion(null)
      const p = await load()
      await refresh()
      setFrameKey((k) => k + 1)
      if (p && p.currentVersionId !== versionAtStart.current) {
        setView('preview')
        setMobileTab('preview')
      } else setMobileTab('chat')
    },
    [load, refresh],
  )

  useEffect(() => {
    if (!taskId) return
    versionAtStart.current = project?.currentVersionId ?? null
    // Captured when the task starts, so a question that changes no files stays in the chat.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [taskId])

  // Live code stream of the running task (desktop shows it in the code tab).
  useEffect(() => {
    if (!taskId) return
    setLive({ text: '', attempt: 1, part: 1, thought: '' })
    setSteps([])
    const keepStep = () => {
      const l = liveRef.current
      const match = l.text.match(/"thinking"\s*:\s*"((?:\\.|[^"\\])*)"/)
      let fromJson = ''
      if (match) {
        try { fromJson = JSON.parse(`"${match[1]}"`) as string } catch { fromJson = '' }
      }
      const idea = (l.thought || fromJson).trim()
      if (!idea) return
      setSteps((s) => (s.some((x) => x.text === idea) ? s : [...s, { attempt: l.attempt, part: l.part, text: idea }]))
    }
    const controller = new AbortController()
    const onEvent = (e: TaskStreamEvent) => {
      if (e.type === 'snapshot') setLive({ text: e.text ?? '', attempt: e.attempt || 1, part: e.part || 1, thought: '' })
      else if (e.type === 'delta') setLive((l) => ({ ...l, text: l.text + e.text }))
      else if (e.type === 'thinking') setLive((l) => ({ ...l, thought: l.thought + (e.text ?? '') }))
      else if (e.type === 'attempt' || e.type === 'reset') {
        keepStep()
        if (e.type === 'attempt') setLive({ text: '', attempt: e.attempt, part: e.part || 1, thought: '' })
        else setLive((l) => ({ ...l, text: '', thought: '' }))
      }
      else if (e.type === 'done') {
        keepStep()
        void finish(taskId)
      }
    }
    followTask(taskId, onEvent, controller.signal).catch(() => {
      /* polling below still completes the task */
    })
    return () => controller.abort()
  }, [taskId, finish])

  // Fallback: poll the task status in case the stream connection drops.
  useEffect(() => {
    if (!taskId) return
    let cancelled = false
    let timer = 0
    const tick = async () => {
      try {
        const task = await get<TaskInfo>(`/api/tasks/${taskId}`)
        if (cancelled) return
        if (task.status === 'succeeded' || task.status === 'failed') {
          await finish(taskId)
          return
        }
      } catch {
        /* keep polling */
      }
      if (!cancelled) timer = window.setTimeout(tick, 3000)
    }
    timer = window.setTimeout(tick, 3000)
    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [taskId, finish])

  // Runtime errors reported by the sandboxed preview.
  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      if (e.source !== frameRef.current?.contentWindow && e.source !== probeRef.current?.contentWindow) return
      const data = e.data as { type?: string; message?: string }
      if (data?.type === 'casco-error' && data.message) {
        setRuntimeErrors((prev) => (prev.includes(data.message!) || prev.length >= 10 ? prev : [...prev, data.message!]))
      }
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [])

  const uploadImages = async (files: File[]) => {
    const room = MAX_ATTACHMENTS - attachments.length
    const media = files.filter(isMediaFile)
    const tooBig = media.some((f) => isVideoFile(f) && f.size > MAX_VIDEO_BYTES)
    const picked = media.filter((f) => !isVideoFile(f) || f.size <= MAX_VIDEO_BYTES).slice(0, room)
    if (picked.length === 0) {
      if (room <= 0) setError(t('يمكنك إرفاق {n} صور كحد أقصى في الطلب الواحد', { n: MAX_ATTACHMENTS }))
      else if (tooBig) setError(t('حجم الفيديو يجب ألا يتجاوز 50 ميجابايت'))
      return
    }
    setError(tooBig ? t('حجم الفيديو يجب ألا يتجاوز 50 ميجابايت') : '')
    setUploading((n) => n + picked.length)
    await Promise.all(
      picked.map(async (file) => {
        try {
          const fd = new FormData()
          fd.append('file', await prepareImage(file))
          const r = await api<{ url: string }>('POST', `/api/projects/${id}/images`, fd)
          setAttachments((a) => (a.includes(r.url) ? a : [...a, r.url]))
        } catch (e) {
          setError(errorMessage(e))
        } finally {
          setUploading((n) => n - 1)
        }
      }),
    )
  }

  const openGallery = async () => {
    setGallery([])
    try {
      setGallery(await get<string[]>(`/api/projects/${id}/images`))
    } catch (e) {
      setGallery(null)
      setError(errorMessage(e))
    }
  }

  const deleteImage = async (url: string) => {
    if (!confirm(t('حذف الصورة؟ إذا كانت مستخدمة في الموقع ستختفي منه.'))) return
    try {
      await del(`/api/projects/${id}/images/${url.split('/').pop()}`)
      setGallery((g) => g?.filter((u) => u !== url) ?? null)
      setAttachments((a) => a.filter((u) => u !== url))
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const creditsProblem = (e: unknown) => {
    if (!(e instanceof ApiError) || e.code !== 'insufficient_credits' || !e.details) return false
    setOutOfCredits({ message: tServer(e.message), details: e.details as CreditShortfall })
    void refresh()
    return true
  }

  const send = async (text: string, usePremium = premium, resume = false, imageOverride?: string[]) => {
    const images = imageOverride ?? (resume ? [] : attachments)
    if ((!text.trim() && images.length === 0 && !resume) || !project || (imageOverride === undefined && uploading > 0)) return false
    setAutoFix(true)
    setSending(true)
    setError('')
    try {
      const r = await post<{ taskId: string }>(`/api/projects/${id}/messages`, {
        content: text,
        premium: usePremium,
        images,
        continue: resume,
      })
      if (!resume && imageOverride === undefined) {
        setInput('')
        setAttachments([])
      }
      setTaskId(r.taskId)
      await load()
      return true
    } catch (e) {
      if (e instanceof ApiError && (e.code === 'pro_required_edit' || e.code === 'free_prompt_limit')) setUpgrade('edit')
      else if (!creditsProblem(e)) setError(errorMessage(e))
      return false
    } finally {
      setSending(false)
    }
  }

  const fixErrors = async () => {
    const errors = errorsRef.current
    if (errors.length === 0) return
    setSending(true)
    setError('')
    try {
      const r = await post<{ taskId: string }>(`/api/projects/${id}/fix`, { errors, premium })
      setRuntimeErrors([])
      setTaskId(r.taskId)
      setMobileTab('chat')
      await load()
    } catch (e) {
      if (creditsProblem(e)) setAutoFix(false)
      else setError(errorMessage(e))
    } finally {
      setSending(false)
    }
  }

  // Open every page once, not only the one on screen, so a broken page is still found.
  const htmlPages = project?.files.filter((f) => f.endsWith('.html')).join('|') ?? ''
  useEffect(() => {
    if (!isPro || !autoFix || taskId || !htmlPages) return
    const version = project?.currentVersionId ?? ''
    if (probedVersion.current === version) return
    const pages = htmlPages.split('|').filter((f) => f && f !== page)
    if (pages.length === 0) {
      probedVersion.current = version
      return
    }
    let i = 0
    setProbePage(pages[0])
    const timer = window.setInterval(() => {
      i += 1
      if (i >= pages.length) {
        window.clearInterval(timer)
        setProbePage(null)
        probedVersion.current = version
        return
      }
      setProbePage(pages[i])
    }, 10000)
    return () => window.clearInterval(timer)
  }, [project?.currentVersionId, htmlPages, isPro, autoFix, taskId, page])

  // Casco keeps repairing until the preview is clean. Each round is a billed task.
  useEffect(() => {
    if (!isPro || !autoFix || taskId || sending || runtimeErrors.length === 0 || queueRef.current.length > 0) return
    const timer = window.setTimeout(() => { void fixErrors() }, 1200)
    return () => window.clearTimeout(timer)
  }, [runtimeErrors, taskId, sending, isPro, autoFix, queue.length])

  useEffect(() => {
    const was = prevTask.current
    prevTask.current = taskId
    if (was && !taskId) flushArmed.current = true
    if (taskId || sending || !flushArmed.current || queueRef.current.length === 0) return
    const [next, ...rest] = queueRef.current
    queueRef.current = rest
    setQueue(rest)
    flushArmed.current = rest.length > 0
    setMobileTab('chat')
    void send(next.text, next.premium, false, next.images).then((ok) => {
      if (!ok) flushArmed.current = queueRef.current.length > 0
    })
  }, [taskId, sending, queue.length])

  const publish = async () => {
    setError('')
    if (!project?.publishedAt) {
      setChooseName(project?.slug ?? '')
      return
    }
    await publishNow()
  }

  const publishNow = async (slug?: string) => {
    setPublishing(true)
    setError('')
    try {
      // The chosen name is saved before publishing so it stays reserved if hosting still has to be paid first.
      if (slug && slug !== project?.slug) await put(`/api/projects/${id}/slug`, { slug })
      const r = await post<{ url: string }>(`/api/projects/${id}/publish`)
      setChooseName(null)
      setPublishedUrl(r.url)
      await load()
    } catch (e) {
      if (e instanceof ApiError && e.code === 'hosting_required') {
        setChooseName(null)
        await load()
        setHosting({ reason: tServer(e.message) })
      } else if (slug) setNameError(errorMessage(e))
      else setError(errorMessage(e))
    } finally {
      setPublishing(false)
    }
  }

  const download = async () => {
    if (!isPro || !project) return setUpgrade('download')
    setDownloading(true)
    try {
      await downloadFile(`/api/projects/${id}/export`, `${project.slug}.zip`)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setDownloading(false)
    }
  }

  const restore = async (versionId: string) => {
    try {
      await post(`/api/projects/${id}/versions/${versionId}/restore`)
      setShowVersions(false)
      setPreviewVersion(null)
      await load()
      setFrameKey((k) => k + 1)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const submitMessage = () => {
    if ((!input.trim() && attachments.length === 0) || uploading > 0) return
    if (taskId || sending) {
      const next = [...queueRef.current, { text: input.trim(), premium, images: attachments }]
      queueRef.current = next
      setQueue(next)
      setInput('')
      setAttachments([])
      return
    }
    void send(input)
  }

  const removeQueued = (index: number) => {
    const next = queueRef.current.filter((_, i) => i !== index)
    queueRef.current = next
    setQueue(next)
  }

  const onSubmit = (e: FormEvent) => {
    e.preventDefault()
    submitMessage()
  }

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && isDesktop()) {
      e.preventDefault()
      submitMessage()
    }
  }

  const onPaste = (e: ClipboardEvent<HTMLTextAreaElement>) => {
    const files = Array.from(e.clipboardData.files)
    if (files.some((f) => f.type.startsWith('image/'))) {
      e.preventDefault()
      void uploadImages(files)
    }
  }

  if (!project)
    return (
      <div className="grid min-h-screen place-items-center p-4">
        {error ? <Alert tone="error">{error}</Alert> : <Spinner className="h-8 w-8 text-brand-600" />}
      </div>
    )

  if (project.templateKey === 'react') return <Navigate to={`/app/react/${id}`} replace />

  const busy = !!taskId
  const writingCode = streamedFiles(live.text).some((f) => !!f.path)
  const firstBuild = busy && writingCode && project.versions.length <= 1
  const latestMessageId = project.messages.reduce((max, m) => Math.max(max, m.id), 0)
  const pages = project.files.filter((f) => f.endsWith('.html'))
  const previewSrc = `${project.previewBase}${page}${previewVersion ? `?v=${previewVersion}` : ''}`
  const unpublishedChanges = project.publishedAt && project.publishedVersionId !== project.currentVersionId
  const canSend = (input.trim() || attachments.length > 0) && uploading === 0
  const publishLabel = project.publishedAt ? (unpublishedChanges ? t('نشر التعديلات') : t('منشور')) : t('نشر')

  return (
    <div className="flex h-dvh flex-col bg-slate-50">
      {/* Top bar */}
      <header className="flex h-14 shrink-0 items-center gap-2 border-b border-slate-200 bg-white px-2 sm:px-3">
        <Link to="/app" className="grid h-9 w-9 shrink-0 place-items-center rounded-xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-900" title={t('رجوع')}>
          <ArrowLeft className="flip-rtl h-5 w-5" />
        </Link>
        <div className="min-w-0 flex-1 lg:flex-none">
          <h1 className="truncate text-sm font-semibold text-ink sm:text-base">{project.name}</h1>
          <p className="hidden truncate text-xs text-slate-500 sm:block">{project.template ? t(project.template.name) : ''}</p>
        </div>

        <div className="mx-auto hidden items-center gap-2 lg:flex">
          <Segmented
            value={view}
            onChange={setView}
            options={[
              { value: 'preview', label: <><Eye className="h-4 w-4" />{t('المعاينة')}</> },
              {
                value: 'code',
                label: (
                  <>
                    <Code2 className="h-4 w-4" />
                    {t('الكود')}
                    {busy && <span className="h-2 w-2 animate-pulse rounded-full bg-emerald-500" />}
                  </>
                ),
              },
            ]}
          />
        </div>

        <div className="flex shrink-0 items-center gap-1.5">
          <ThemeToggle />
          <div className="hidden items-center gap-1.5 md:flex">
            <Button variant="ghost" size="sm" onClick={() => setShowVersions(true)} title={t('النسخ')}>
              <History className="h-4 w-4" />
              <span className="hidden xl:inline">{t('النسخ')}</span>
              <span className="text-slate-400">{project.versions.length}</span>
            </Button>
            <Link
              to={`/app/p/${id}/data`}
              className="inline-flex h-8 items-center gap-2 rounded-lg px-3 text-xs font-semibold text-slate-600 transition hover:bg-slate-100 hover:text-slate-900"
              title={t('البيانات والإعدادات')}
            >
              <Database className="h-4 w-4" />
              <span className="hidden xl:inline">{t('البيانات والإعدادات')}</span>
            </Link>
            <Button variant="ghost" size="sm" onClick={download} loading={downloading} disabled={busy} title={t('تنزيل الكود')}>
              {isPro ? <Download className="h-4 w-4" /> : <Lock className="h-4 w-4" />}
              <span className="hidden xl:inline">{t('تنزيل الكود')}</span>
            </Button>
            {project.publishedAt && (
              <a
                href={project.siteUrl}
                target="_blank"
                rel="noreferrer"
                className="inline-flex h-8 items-center gap-1.5 rounded-lg px-3 text-xs font-semibold text-brand-600 transition hover:bg-brand-50"
              >
                <ExternalLink className="h-4 w-4" />
                <span className="hidden xl:inline">{t('فتح الموقع')}</span>
              </a>
            )}
          </div>
          <Popover
            className="md:hidden"
            trigger={() => (
              <button className="grid h-9 w-9 place-items-center rounded-xl text-slate-600 hover:bg-slate-100" aria-label={t('المزيد')}>
                <MoreHorizontal className="h-5 w-5" />
              </button>
            )}
          >
            {(close) => (
              <div className="w-56">
                <MenuItem
                  icon={<History className="h-4 w-4" />}
                  onClick={() => {
                    close()
                    setShowVersions(true)
                  }}
                >
                  {t('النسخ')} ({project.versions.length})
                </MenuItem>
                <Link to={`/app/p/${id}/data`} className="flex items-center gap-2.5 rounded-xl px-3 py-2.5 text-sm text-slate-700 hover:bg-slate-50">
                  <Database className="h-4 w-4 text-slate-400" />
                  {t('البيانات والإعدادات')}
                </Link>
                <MenuItem
                  icon={isPro ? <Download className="h-4 w-4" /> : <Lock className="h-4 w-4" />}
                  onClick={() => {
                    close()
                    void download()
                  }}
                >
                  {t('تنزيل الكود')}
                </MenuItem>
                {project.publishedAt && (
                  <a href={project.siteUrl} target="_blank" rel="noreferrer" className="flex items-center gap-2.5 rounded-xl px-3 py-2.5 text-sm text-slate-700 hover:bg-slate-50">
                    <ExternalLink className="h-4 w-4 text-slate-400" />
                    {t('فتح الموقع')}
                  </a>
                )}
              </div>
            )}
          </Popover>
          <Button size="sm" onClick={publish} loading={publishing} disabled={busy} variant={unpublishedChanges || !project.publishedAt ? 'dark' : 'secondary'} className="h-9">
            {!isPro ? <Lock className="h-3.5 w-3.5" /> : project.publishedAt && !unpublishedChanges ? <Check className="h-4 w-4 text-emerald-600" /> : <Rocket className="h-4 w-4" />}
            {publishLabel}
          </Button>
        </div>
      </header>

      {project.suspension && (
        <div className="border-b border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-800">
          <b>{t('هذا الموقع موقوف من إدارة Casco')}</b>
          {project.suspension.reason && <> — {t('السبب:')} {project.suspension.reason}</>}. {t('الزوار يرون صفحة "الموقع متوقف مؤقتاً" ولا يمكن النشر حالياً.')}{' '}
          <a
            href={supportWhatsAppUrl(`مرحباً، موقعي "${project.name}" موقوف على Casco وأريد الاستفسار`)}
            target="_blank"
            rel="noreferrer"
            className="font-semibold underline"
          >
            {t('تواصل مع الدعم على واتساب')}
          </a>
        </div>
      )}

      {/* Phone: switch between chat and preview */}
      <div className="flex shrink-0 justify-center border-b border-slate-200 bg-white px-3 py-2 lg:hidden">
        <Segmented
          className="w-full max-w-sm [&>button]:flex-1"
          value={mobileTab}
          onChange={setMobileTab}
          options={[
            { value: 'chat', label: <><MessageSquare className="h-4 w-4" />{t('المحادثة')}{busy && <span className="h-2 w-2 animate-pulse rounded-full bg-emerald-500" />}</> },
            { value: 'preview', label: <><Eye className="h-4 w-4" />{t('المعاينة')}</> },
          ]}
        />
      </div>

      <div className="flex min-h-0 flex-1">
        {/* Chat */}
        <aside className={`${firstBuild ? 'hidden' : mobileTab === 'chat' ? 'flex' : 'hidden'} min-h-0 w-full flex-col border-slate-200 bg-white lg:flex lg:w-[380px] lg:shrink-0 lg:border-e`}>
          <div ref={chatScrollRef} className="scrollbar-thin flex-1 space-y-4 overflow-y-auto px-4 py-5">
            {notice && <Alert tone="warning">{tServer(notice)}</Alert>}
            {project.messages.map((m) =>
              m.role === 'user' ? (
                <div key={m.id} className="flex justify-end">
                  <div className="max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-ee-md bg-brand-600 px-4 py-2.5 text-sm leading-relaxed text-white shadow-sm">
                    {m.images && m.images.length > 0 && (
                      <div className="mb-2 flex flex-wrap gap-1.5">
                        {m.images.map((src) => (
                          <MediaThumb key={src} src={src} className="h-14 w-14 rounded-lg ring-1 ring-white/30" />
                        ))}
                      </div>
                    )}
                    {m.content}
                  </div>
                </div>
              ) : (
                <div key={m.id} className="flex gap-2.5">
                  <AssistantAvatar />
                  <div className="min-w-0 max-w-[85%]">
                    <div className="whitespace-pre-wrap rounded-2xl rounded-ss-md bg-slate-100 px-4 py-2.5 text-sm leading-relaxed text-slate-800">{tServer(m.content)}</div>
                    {m.questions && m.questions.length > 0 && m.id === latestMessageId && !busy && (
                      <PlanQuestions questions={m.questions} busy={sending} onAnswer={(text) => void send(text)} />
                    )}
                    {m.credits !== null && m.credits > 0 && (
                      <p className="mt-1 flex items-center gap-1 px-1 text-[11px] text-slate-400">
                        <Zap className="h-3 w-3" />
                        {t('{n} نقطة', { n: num(m.credits) })}
                      </p>
                    )}
                  </div>
                </div>
              ),
            )}
            {!busy && project.continuable && (
              <div className="flex gap-2.5">
                <AssistantAvatar />
                <div className="w-full max-w-[85%] rounded-2xl border border-sky-200 bg-gradient-to-br from-sky-50 to-brand-50 p-4 text-sm">
                  <p className="font-semibold text-ink">
                    {project.continuable.pagesLeft > 0 ? t('باقي {n} صفحة لإكمال موقعك', { n: project.continuable.pagesLeft }) : t('باقي جزء من طلبك لم يُبنَ بعد')}
                  </p>
                  <p className="mt-1 line-clamp-2 text-xs text-slate-600">{project.continuable.remaining}</p>
                  <Button className="mt-3 w-full" loading={sending} onClick={() => void send(t('كمل البناء'), premium, true)}>
                    <Play className="flip-rtl h-4 w-4" />
                    {t('كمل البناء')}
                  </Button>
                </div>
              </div>
            )}
            {busy && (
              <div className="flex gap-2.5">
                <AssistantAvatar pulse />
                <ChatActivity text={live.text} thought={live.thought} attempt={live.attempt} part={live.part} steps={steps} />
              </div>
            )}
            {queue.map((item, i) => (
              <div key={`${i}-${item.text}`} className="flex justify-end">
                <div className="max-w-[85%] rounded-2xl rounded-ee-md border border-dashed border-brand-300 bg-brand-50 px-4 py-2.5 text-sm text-ink">
                  <p className="mb-1 text-[11px] font-semibold text-brand-700">{t('في الطابور')} · {i + 1}</p>
                  {item.images.length > 0 && (
                    <div className="mb-2 flex flex-wrap gap-1.5">
                      {item.images.map((src) => (
                        <MediaThumb key={src} src={src} className="h-14 w-14 rounded-lg" />
                      ))}
                    </div>
                  )}
                  {item.text && <p className="whitespace-pre-wrap">{item.text}</p>}
                  <button type="button" onClick={() => removeQueued(i)} className="mt-1 text-xs text-slate-500 underline">
                    {t('إزالة')}
                  </button>
                </div>
              </div>
            ))}
          </div>

          {/* Composer */}
          <div className="border-t border-slate-100 bg-white p-3 pb-[max(0.75rem,env(safe-area-inset-bottom))]">
            {error && (
              <div className="mb-2">
                <Alert tone="error">
                  {error}{' '}
                  {/Pro|رصيد|credit|क्रेडिट/i.test(error) && (
                    <Link to="/app/billing" className="font-semibold underline">
                      {t('الاشتراك')}
                    </Link>
                  )}
                </Alert>
              </div>
            )}
            {!busy && attachments.length === 0 && !(isPro && hints.length > 0) && (
              <div className="scrollbar-none -mx-3 mb-2 flex gap-2 overflow-x-auto px-3">
                {suggestions.map((s) => (
                  <button
                    key={s}
                    onClick={() => setInput(s)}
                    className="shrink-0 rounded-full border border-slate-200 px-3 py-1 text-xs text-slate-600 transition hover:border-brand-200 hover:bg-brand-50 hover:text-brand-700"
                  >
                    {s}
                  </button>
                ))}
              </div>
            )}
            <form
              onSubmit={onSubmit}
              className="rounded-2xl border border-slate-200 bg-white shadow-sm transition focus-within:border-brand-400 focus-within:ring-4 focus-within:ring-brand-500/10"
            >
              {(attachments.length > 0 || uploading > 0) && (
                <div className="flex flex-wrap gap-2 px-3 pt-3">
                  {attachments.map((src) => (
                    <div key={src} className="relative">
                      <MediaThumb src={src} className="h-14 w-14 rounded-lg ring-1 ring-slate-200" />
                      <button
                        type="button"
                        onClick={() => setAttachments((a) => a.filter((u) => u !== src))}
                        className="absolute -end-1.5 -top-1.5 grid h-5 w-5 place-items-center rounded-full bg-ink text-white"
                        aria-label={t('إزالة')}
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </div>
                  ))}
                  {Array.from({ length: uploading }, (_, i) => (
                    <div key={i} className="grid h-14 w-14 place-items-center rounded-lg bg-slate-100">
                      <Spinner className="h-5 w-5 text-brand-600" />
                    </div>
                  ))}
                </div>
              )}
              <textarea
                value={input}
                onChange={(e) => setInput(e.target.value)}
                onKeyDown={onKeyDown}
                onPaste={onPaste}
                rows={2}
                placeholder={
                  busy
                    ? t('اكتب رسالتك، هتتبعت تلقائي بعد ما يخلّص')
                    : attachments.length > 0
                      ? t('أين تريد وضع الصور؟ مثال: اجعل الأولى خلفية الهيدر والباقي في معرض أعمالنا')
                      : isPro && hintText
                        ? hintText
                        : t('اطلب أي تعديل على موقعك...')
                }
                className="block max-h-40 min-h-[3.5rem] w-full resize-none bg-transparent px-3.5 pt-3 text-sm outline-none placeholder:text-slate-400 disabled:opacity-60"
              />
              <div className="flex items-center gap-1 px-2 pb-2">
                <label
                  className="grid h-10 w-10 cursor-pointer place-items-center rounded-xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-800"
                  title={t('إرفاق صور أو فيديو من جهازك')}
                >
                  <Paperclip className="h-5 w-5" />
                  <input
                    type="file"
                    accept={MEDIA_ACCEPT}
                    multiple
                    className="hidden"
                    onChange={(e) => {
                      void uploadImages(Array.from(e.target.files ?? []))
                      e.target.value = ''
                    }}
                  />
                </label>
                <button
                  type="button"
                  onClick={openGallery}
                  className="grid h-10 w-10 place-items-center rounded-xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-800"
                  title={t('صوري السابقة')}
                >
                  <Images className="h-4 w-4" />
                </button>
                {me?.limits.canUsePremium ? (
                  <button
                    type="button"
                    onClick={() => setPremium((p) => !p)}
                    title={t('الوضع القوي (نقاط أكثر)')}
                    className={`inline-flex h-8 items-center gap-1 rounded-lg px-2 text-xs font-semibold transition ${premium ? 'bg-amber-100 text-amber-800' : 'text-slate-500 hover:bg-slate-100'}`}
                  >
                    <Zap className={`h-3.5 w-3.5 ${premium ? 'fill-amber-500' : ''}`} />
                    {t('الوضع القوي')}
                  </button>
                ) : (
                  <Link to="/app/billing" className="inline-flex h-8 items-center gap-1 rounded-lg px-2 text-xs font-semibold text-brand-600 hover:bg-brand-50">
                    <Sparkles className="h-3.5 w-3.5" />
                    {t('ترقية إلى Pro')}
                  </Link>
                )}
                <span className="ms-auto hidden items-center gap-1 text-xs text-slate-400 sm:inline-flex" title={t('النقاط المتاحة')}>
                  <Zap className="h-3 w-3" />
                  {num(me?.credits.available ?? 0)}
                </span>
                <button
                  type="submit"
                  disabled={!canSend || sending}
                  className="ms-auto grid h-10 w-10 place-items-center rounded-xl bg-ink text-white transition hover:bg-slate-800 disabled:bg-slate-200 disabled:text-slate-400 sm:ms-2"
                  aria-label={t('إرسال')}
                >
                  {sending ? <Spinner className="h-4 w-4" /> : <ArrowUp className="h-4 w-4" />}
                </button>
              </div>
            </form>
          </div>
        </aside>

        {/* Preview / code */}
        <section className={`${firstBuild || mobileTab === 'preview' ? 'flex' : 'hidden'} min-h-0 min-w-0 flex-1 flex-col lg:flex`}>
          <div className={`shrink-0 items-center gap-2 border-b border-slate-200 bg-white/70 px-3 py-2 ${firstBuild ? 'hidden' : 'flex'}`}>
            <Segmented
              value={device}
              onChange={(d) => {
                setDevice(d)
                setView('preview')
              }}
              options={[
                { value: 'desktop', label: <Monitor className="h-4 w-4" />, title: t('كمبيوتر') },
                { value: 'tablet', label: <Tablet className="h-4 w-4" />, title: t('تابلت') },
                { value: 'mobile', label: <Smartphone className="h-4 w-4" />, title: t('موبايل') },
              ]}
            />
            {pages.length > 1 && (
              <select
                value={page}
                onChange={(e) => setPage(e.target.value)}
                className="h-9 min-w-0 max-w-[12rem] rounded-xl border border-slate-200 bg-white px-2.5 text-sm outline-none focus:border-brand-400"
                dir="ltr"
              >
                {pages.map((p) => (
                  <option key={p}>{p}</option>
                ))}
              </select>
            )}
            <button
              onClick={() => setFrameKey((k) => k + 1)}
              className="ms-auto grid h-9 w-9 place-items-center rounded-xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-800"
              title={t('تحديث المعاينة')}
            >
              <RotateCw className="h-4 w-4" />
            </button>
          </div>

          <div className="flex min-h-0 flex-1 flex-col p-2 sm:p-3">
            {runtimeErrors.length > 0 && !busy && (
              <div className="mb-2 flex flex-wrap items-center justify-between gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
                <span>
                  {isPro && autoFix
                    ? t('لقيت {n} خطأ، كاسكو هيصلحه لوحده.', { n: runtimeErrors.length })
                    : t('اكتشفنا {n} خطأ في الصفحة.', { n: runtimeErrors.length })}
                </span>
                <div className="flex gap-2">
                  <Button variant="secondary" size="sm" onClick={() => { setRuntimeErrors([]); setAutoFix(false) }}>
                    {t('تجاهل')}
                  </Button>
                  {(!isPro || !autoFix) && (
                    <Button size="sm" onClick={fixErrors} loading={sending}>
                      <Wrench className="h-3.5 w-3.5" />
                      {t('أصلحها تلقائياً')}
                    </Button>
                  )}
                </div>
              </div>
            )}
            {previewVersion && (
              <div className="mb-2 flex flex-wrap items-center justify-between gap-2 rounded-xl bg-ink px-4 py-2 text-sm text-white">
                <span>{t('تعاين نسخة قديمة')}</span>
                <div className="flex gap-3">
                  <button className="font-semibold underline" onClick={() => restore(previewVersion)}>
                    {t('استرجاع هذه النسخة')}
                  </button>
                  <button className="text-white/70 hover:text-white" onClick={() => setPreviewVersion(null)}>
                    {t('عودة للحالية')}
                  </button>
                </div>
              </div>
            )}
            {firstBuild && (
              <div className="flex min-h-0 flex-1">
                <BuildStage text={live.text} attempt={live.attempt} part={live.part} />
              </div>
            )}
            <div className={`min-h-0 flex-1 justify-center overflow-auto rounded-2xl bg-slate-200/50 ${device === 'desktop' ? 'p-0 sm:p-2' : 'p-3 sm:p-6'} ${firstBuild || view === 'code' ? 'hidden' : 'flex'}`}>
              <iframe
                key={`${frameKey}-${previewSrc}`}
                ref={frameRef}
                src={previewSrc}
                title={t('معاينة الموقع')}
                sandbox="allow-scripts allow-forms allow-popups allow-modals allow-same-origin"
                className={`h-full bg-white shadow-xl shadow-slate-900/10 transition-all ${device === 'mobile' ? 'rounded-[2rem] ring-8 ring-ink' : device === 'tablet' ? 'rounded-2xl ring-8 ring-ink' : 'rounded-xl ring-1 ring-slate-200'}`}
                style={{ width: deviceWidth[device], maxWidth: '100%' }}
              />
            </div>
            {probePage && project.previewBase && (
              <iframe
                ref={probeRef}
                src={`${project.previewBase}${probePage}`}
                title=""
                sandbox="allow-scripts allow-forms allow-popups allow-modals allow-same-origin"
                className="pointer-events-none absolute h-px w-px opacity-0"
              />
            )}
            {view === 'code' && !firstBuild && (
              <div className="hidden min-h-0 flex-1 flex-col lg:flex">
                {busy ? (
                  <LiveCode text={live.text} attempt={live.attempt} part={live.part} />
                ) : (
                  <FileBrowser
                    projectId={id}
                    versionId={previewVersion ?? project.currentVersionId}
                    actions={
                      <button onClick={download} className="inline-flex items-center gap-1.5 rounded-md bg-white/10 px-2.5 py-1 text-slate-200 hover:bg-white/20">
                        {isPro ? <Download className="h-3.5 w-3.5" /> : <Lock className="h-3.5 w-3.5" />}
                        {t('تنزيل ZIP')}
                      </button>
                    }
                  />
                )}
              </div>
            )}
          </div>
        </section>
      </div>

      <Modal open={showVersions} onClose={() => setShowVersions(false)} title={t('نسخ الموقع')} wide>
        <div className="space-y-2">
          {project.versions.map((v) => (
            <div key={v.id} className="flex flex-col gap-3 rounded-2xl border border-slate-200 p-3.5 sm:flex-row sm:items-center sm:justify-between">
              <div className="min-w-0">
                <p className="flex flex-wrap items-center gap-2 font-semibold text-ink">
                  {t('النسخة {n}', { n: v.number })}
                  {v.id === project.currentVersionId && <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">{t('الحالية')}</span>}
                  {v.id === project.publishedVersionId && <span className="rounded-full bg-brand-50 px-2 py-0.5 text-xs font-medium text-brand-700">{t('المنشورة')}</span>}
                </p>
                {v.prompt && <p className="mt-1 line-clamp-2 text-sm text-slate-800">«{tServer(v.prompt)}»</p>}
                <p className="truncate text-sm text-slate-500">{tServer(v.summary)}</p>
                <p className="mt-1 text-xs text-slate-400">
                  {formatDateTime(v.createdAt)}
                  {v.filesChanged > 0 && ` • ${t('{n} ملف تغيّر', { n: v.filesChanged })}`}
                  {v.parts > 1 && ` • ${t('{n} أجزاء', { n: v.parts })}`}
                  {v.model && <span dir="ltr"> • {v.model}</span>}
                </p>
              </div>
              {v.id !== project.currentVersionId && (
                <div className="flex shrink-0 gap-1.5">
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => {
                      setPreviewVersion(v.id)
                      setShowVersions(false)
                      setMobileTab('preview')
                    }}
                  >
                    {t('معاينة')}
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => restore(v.id)}>
                    {t('استرجاع')}
                  </Button>
                </div>
              )}
            </div>
          ))}
        </div>
      </Modal>

      <Modal open={gallery !== null} onClose={() => setGallery(null)} title={t('صوري')}>
        {gallery && gallery.length === 0 ? (
          <p className="text-sm text-slate-500">{t('لا توجد صور بعد. ارفع صوراً بزر الإرفاق وستظهر هنا لتستخدمها مرة أخرى.')}</p>
        ) : (
          <div className="grid grid-cols-3 gap-2 sm:grid-cols-4">
            {gallery?.map((src) => {
              const selected = attachments.includes(src)
              return (
                <div key={src} className="group relative">
                  <button
                    onClick={() => setAttachments((a) => (selected ? a.filter((u) => u !== src) : a.length < MAX_ATTACHMENTS ? [...a, src] : a))}
                    className={`block w-full overflow-hidden rounded-xl ring-2 ${selected ? 'ring-brand-500' : 'ring-transparent'}`}
                  >
                    <MediaThumb src={src} className="aspect-square w-full" />
                  </button>
                  {selected && (
                    <span className="absolute end-1 top-1 grid h-5 w-5 place-items-center rounded-full bg-brand-600 text-white">
                      <Check className="h-3 w-3" />
                    </span>
                  )}
                  <button
                    onClick={() => deleteImage(src)}
                    className="absolute bottom-1 start-1 rounded-md bg-white/90 px-1.5 text-xs text-red-600 opacity-100 transition sm:opacity-0 sm:group-hover:opacity-100"
                  >
                    {t('حذف')}
                  </button>
                </div>
              )
            })}
          </div>
        )}
        <Button className="mt-4 w-full" onClick={() => setGallery(null)}>
          {t('تم ({n} مختارة)', { n: attachments.length })}
        </Button>
      </Modal>

      <Modal open={upgrade !== null} onClose={() => setUpgrade(null)} title={upgrade === 'download' ? t('تنزيل كود الموقع') : t('عدّل موقعك بالذكاء الاصطناعي')}>
        <p className="text-slate-600">
          {upgrade === 'download'
            ? t('تنزيل ملفات موقعك كاملة (HTML و CSS و JavaScript) متاح لمشتركي Pro.')
            : t('الطلب المجاني اتستخدم. سجّل الدخول بنفس حساب جوجل أو آبل واشحن حسابك من صفحة الفوترة، من غير ما تلصق أي توكن.')}
        </p>
        <ul className="mt-5 space-y-2.5 text-sm text-slate-700">
          {[
            t('تعديلات غير محدودة بالكلام العادي ({n} نقطة شهرياً)', { n: plans ? num(plans.pro.monthlyCredits) : '…' }),
            t('صفحات متعددة ومنصات كورسات وإعلانات ومتاجر'),
            t('تنزيل كود الموقع كاملاً'),
            t('الوضع القوي للتعديلات الصعبة'),
          ].map((x) => (
            <li key={x} className="flex gap-2.5">
              <Check className="mt-0.5 h-4 w-4 shrink-0 text-brand-600" />
              {x}
            </li>
          ))}
        </ul>
        <Link to="/app/billing" className="mt-6 flex h-12 items-center justify-center rounded-xl bg-gradient-to-r from-brand-600 to-fuchsia-500 font-semibold text-white shadow-lg shadow-brand-600/25">
          {t('اشترك الآن بـ {price} شهرياً', { price: usd(plans?.pro.monthlyPrice) })}
        </Link>
        {upgrade === 'edit' && <ShareForPrompts onGranted={() => setUpgrade(null)} />}
      </Modal>

      <HostingModal projectId={id} open={hosting !== null} reason={hosting?.reason} onClose={() => setHosting(null)} />

      <OutOfCreditsModal
        info={outOfCredits}
        onClose={() => setOutOfCredits(null)}
        onUseStandard={() => {
          setOutOfCredits(null)
          setPremium(false)
          void send(input, false)
        }}
      />

      <Modal open={chooseName !== null} onClose={() => setChooseName(null)} title={t('اختر اسم موقعك')}>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (nameStatus.state === 'ok' && chooseName) void publishNow(chooseName)
          }}
          className="space-y-4"
        >
          <p className="text-sm text-slate-600">{t('هذا هو الرابط الذي سيفتح عليه موقعك وتشاركه مع عملائك.')}</p>
          <SubdomainField
            projectId={project.id}
            currentSlug={project.slug}
            suffix={project.siteSuffix}
            value={chooseName ?? ''}
            onChange={(v) => {
              setChooseName(v)
              setNameError('')
            }}
            onStatus={setNameStatus}
            autoFocus
          />
          <div className="flex items-center gap-2 rounded-xl bg-slate-50 px-3 py-2.5 text-sm ring-1 ring-slate-100">
            <Globe className="h-4 w-4 shrink-0 text-slate-400" />
            <span className="truncate font-medium text-slate-700" dir="ltr">
              {project.subdomainUrl.split('://')[0]}://<b className="text-ink">{chooseName || 'my-site'}</b>
              {project.siteSuffix}
            </span>
          </div>
          {nameError && <Alert tone="error">{nameError}</Alert>}
          <p className="text-xs text-slate-500">{t('يمكنك تغيير الاسم لاحقاً أو ربط دومينك الخاص (مثل yourname.com) من الإعدادات.')}</p>
          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <Button type="button" variant="secondary" onClick={() => setChooseName(null)}>
              {t('إلغاء')}
            </Button>
            <Button type="submit" variant="dark" loading={publishing} disabled={nameStatus.state !== 'ok'}>
              <Rocket className="h-4 w-4" />
              {t('نشر الموقع')}
            </Button>
          </div>
        </form>
      </Modal>

      <Modal open={!!publishedUrl} onClose={() => setPublishedUrl(null)} title={t('🎉 موقعك منشور!')}>
        <p className="text-slate-600">{t('موقعك متاح الآن على الرابط:')}</p>
        <a
          href={publishedUrl ?? '#'}
          target="_blank"
          rel="noreferrer"
          className="mt-3 flex items-center justify-between gap-2 break-all rounded-xl bg-slate-100 p-3 font-semibold text-brand-600"
          dir="ltr"
        >
          {publishedUrl}
          <ExternalLink className="h-4 w-4 shrink-0" />
        </a>
        <p className="mt-4 text-sm text-slate-500">
          {t('تريد ربط دومينك الخاص (مثل yourname.com)؟')}{' '}
          <Link to={`/app/p/${id}/data`} className="font-semibold text-brand-600">
            {t('من الإعدادات')}
          </Link>
        </p>
      </Modal>
    </div>
  )
}

function AssistantAvatar({ pulse = false }: { pulse?: boolean }) {
  return (
    <span className={`grid h-7 w-7 shrink-0 place-items-center rounded-full bg-gradient-to-br from-brand-500 to-fuchsia-500 text-white shadow-sm ${pulse ? 'animate-pulse' : ''}`}>
      <Sparkles className="h-3.5 w-3.5" />
    </span>
  )
}

function MenuItem({ icon, onClick, children }: { icon: ReactNode; onClick: () => void; children: ReactNode }) {
  return (
    <button onClick={onClick} className="flex w-full items-center gap-2.5 rounded-xl px-3 py-2.5 text-start text-sm text-slate-700 hover:bg-slate-50">
      <span className="text-slate-400">{icon}</span>
      {children}
    </button>
  )
}
