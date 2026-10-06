import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ChevronRight } from 'lucide-react'
import hljs from 'highlight.js/lib/core'
import xml from 'highlight.js/lib/languages/xml'
import css from 'highlight.js/lib/languages/css'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import 'highlight.js/styles/github-dark.css'
import { get } from '../lib/api'
import { t } from '../lib/i18n'
import { parsePartialJson } from '../lib/partialJson'
import { Spinner } from './ui'

hljs.registerLanguage('xml', xml)
hljs.registerLanguage('css', css)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('json', json)

function languageFor(path: string) {
  const ext = path.split('.').pop()?.toLowerCase()
  if (ext === 'html' || ext === 'htm' || ext === 'svg' || ext === 'xml') return 'xml'
  if (ext === 'css') return 'css'
  if (ext === 'js' || ext === 'mjs') return 'javascript'
  if (ext === 'json') return 'json'
  return null
}

function highlight(code: string, path: string) {
  const language = languageFor(path)
  if (!language) return hljs.highlightAuto(code, []).value || escapeHtml(code)
  return hljs.highlight(code, { language, ignoreIllegals: true }).value
}

function escapeHtml(s: string) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

export function CodeBlock({ code, path, className = '' }: { code: string; path: string; className?: string }) {
  const html = useMemo(() => highlight(code, path), [code, path])
  const lineNumbers = useMemo(() => Array.from({ length: code.split('\n').length }, (_, i) => i + 1).join('\n'), [code])
  return (
    <div dir="ltr" className={`scrollbar-thin overflow-auto bg-[#0d1117] font-mono text-[12.5px] leading-5 ${className}`}>
      <div className="flex min-w-max">
        <pre className="sticky left-0 select-none bg-[#0d1117] px-3 py-3 text-right text-slate-600">{lineNumbers}</pre>
        <pre className="hljs flex-1 py-3 pr-6">
          <code dangerouslySetInnerHTML={{ __html: html }} />
        </pre>
      </div>
    </div>
  )
}

interface StreamedOp {
  path?: string
  op?: string
  content?: string
  edits?: { find?: string; replace?: string }[]
}

const opLabel = (): Record<string, [string, string]> => ({
  write: [t('ملف جديد'), 'bg-emerald-500/15 text-emerald-300'],
  edit: [t('تعديل'), 'bg-sky-500/15 text-sky-300'],
  delete: [t('حذف'), 'bg-red-500/15 text-red-300'],
})

function streamedPlan(text: string) {
  const parsed = parsePartialJson(text) as { thinking?: string; summary?: string; files?: StreamedOp[] } | undefined
  return {
    thinking: typeof parsed?.thinking === 'string' ? parsed.thinking : '',
    summary: typeof parsed?.summary === 'string' ? parsed.summary : '',
    files: parsed?.files?.filter((f) => f && typeof f === 'object') ?? [],
  }
}

export function streamedFiles(text: string): StreamedOp[] {
  return streamedPlan(text).files
}

/** First build: files on the side, the file being written in front, then the next file. */
export function BuildStage({ text, attempt, part = 1 }: { text: string; attempt: number; part?: number }) {
  const plan = useMemo(() => streamedPlan(text), [text])
  const files = plan.files.filter((f) => f.path)
  const [picked, setPicked] = useState<number | null>(null)
  const active = Math.max(0, files.length - 1)
  const index = picked ?? active
  const file = files[index]

  useEffect(() => {
    setPicked(null)
  }, [files.length])

  return (
    <div className="flex min-h-0 flex-1 overflow-hidden rounded-2xl bg-[#1e1e1e] text-[#cccccc] shadow-lg">
      <aside className="flex w-44 shrink-0 flex-col border-e border-white/10 sm:w-56">
        <p className="px-3 py-2 text-[11px] font-semibold uppercase tracking-wide text-[#8b8b8b]">Files</p>
        <div className="scrollbar-thin min-h-0 flex-1 overflow-y-auto pb-2">
          {files.length === 0 && <p className="px-3 text-xs text-[#8b8b8b]">Planning next moves</p>}
          {files.map((f, i) => (
            <button
              key={`${f.path}-${i}`}
              type="button"
              onClick={() => setPicked(i)}
              className={`flex w-full items-center gap-2 px-3 py-1.5 text-left text-[13px] hover:bg-white/5 ${i === index ? 'bg-white/10 text-white' : ''}`}
              dir="ltr"
            >
              <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${i === files.length - 1 ? 'animate-pulse bg-emerald-400' : 'bg-transparent'}`} />
              <span className="truncate font-mono">{f.path}</span>
            </button>
          ))}
        </div>
      </aside>
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center gap-2 border-b border-white/10 px-3 py-2 text-xs">
          <span className="h-2 w-2 animate-pulse rounded-full bg-emerald-400" />
          <span className="truncate font-mono" dir="ltr">
            {file?.path ?? 'Planning next moves'}
          </span>
          {part > 1 && <span className="text-[#8b8b8b]">{t('الجزء {n}', { n: part })}</span>}
          {attempt > 1 && <span className="text-amber-300">{t('الإصلاح الحالي')}</span>}
        </div>
        <div className="min-h-0 flex-1">
          {!file && (
            <div className="px-4 py-6">
              <p className="text-sm text-[#8b8b8b]">Planning next moves</p>
              {plan.thinking && <p className="mt-3 whitespace-pre-wrap text-sm leading-relaxed text-[#d4d4d4]">{plan.thinking}</p>}
            </div>
          )}
          {file?.op === 'write' && typeof file.content === 'string' && <CodeBlock code={file.content} path={file.path ?? ''} className="h-full" />}
          {file?.op === 'edit' && (
            <div className="space-y-2 p-3">
              {(file.edits ?? []).filter(Boolean).map((e, j) => (
                <div key={j}>
                  {e.replace && <CodeBlock code={e.replace} path={file.path ?? ''} className="max-h-80 rounded-lg" />}
                </div>
              ))}
            </div>
          )}
          {file && file.op !== 'write' && file.op !== 'edit' && <CodeBlock code={file.path ?? ''} path={file.path ?? ''} className="h-full" />}
        </div>
      </div>
    </div>
  )
}

function clipLines(text: string, max = 8) {
  const lines = text.replace(/\s+$/, '').split('\n')
  if (lines.length <= max) return lines
  return [...lines.slice(0, max), '…']
}

/** Removed lines in red, added lines in green, the way a diff reads. */
function DiffPreview({ file }: { file: StreamedOp }) {
  const edits = (file.edits ?? []).filter((e) => e && (e.find || e.replace)).slice(0, 3)
  const added = file.op === 'write' && typeof file.content === 'string' ? clipLines(file.content, 10) : []
  if (edits.length === 0 && added.length === 0) return null
  return (
    <div className="border-t border-white/10 font-mono text-[11px] leading-4" dir="ltr">
      {edits.map((e, j) => (
        <div key={j} className="border-t border-white/5 first:border-t-0">
          {e.find && clipLines(e.find).map((line, k) => (
            <div key={`d${k}`} className="bg-red-500/15 px-2.5 text-red-200 whitespace-pre-wrap">{`- ${line}`}</div>
          ))}
          {e.replace && clipLines(e.replace).map((line, k) => (
            <div key={`a${k}`} className="bg-emerald-500/15 px-2.5 text-emerald-100 whitespace-pre-wrap">{`+ ${line}`}</div>
          ))}
        </div>
      ))}
      {added.map((line, k) => (
        <div key={k} className="bg-emerald-500/15 px-2.5 text-emerald-100 whitespace-pre-wrap">{`+ ${line}`}</div>
      ))}
    </div>
  )
}

export type ThinkStep = { attempt: number; part: number; text: string }

/** Finished files stay closed. The file Casco is editing stays open, and any file can be opened again. */
function ChatFiles({ files }: { files: StreamedOp[] }) {
  const [picked, setPicked] = useState<number | null>(null)
  const count = files.length
  const prev = useRef(count)
  useEffect(() => {
    if (count === prev.current) return
    prev.current = count
    setPicked(null)
  }, [count])
  const openIndex = picked === null ? count - 1 : picked
  return (
    <>
      {files.map((f, i) => {
        const open = i === openIndex
        const current = i === count - 1
        return (
          <div key={`${f.path}-${i}`} className="overflow-hidden rounded-lg border border-white/10 bg-white/5 text-xs">
            <button
              type="button"
              onClick={() => setPicked(open ? -1 : i)}
              aria-expanded={open}
              className="flex w-full items-center gap-2 px-2.5 py-1.5 text-left"
              dir="ltr"
            >
              <ChevronRight className={`h-3.5 w-3.5 shrink-0 text-slate-400 transition ${open ? 'rotate-90' : ''}`} />
              <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${current ? 'animate-pulse bg-emerald-400' : 'bg-slate-500'}`} />
              <span className="truncate font-mono text-slate-200">{f.path}</span>
              <span className="ms-auto text-slate-400">{f.op === 'write' ? t('ملف جديد') : f.op === 'delete' ? t('حذف') : t('تعديل')}</span>
            </button>
            {open && <DiffPreview file={f} />}
          </div>
        )
      })}
    </>
  )
}

/** Edits stay in the chat: every thinking step, then the files being changed. */
export function ChatActivity({ text, thought = '', attempt, part = 1, steps = [] }: { text: string; thought?: string; attempt: number; part?: number; steps?: ThinkStep[] }) {
  const plan = useMemo(() => streamedPlan(text), [text])
  const files = plan.files.filter((f) => f.path)
  const idea = (thought || plan.thinking).trim()
  const shown = idea && steps.some((s) => s.text === idea) ? '' : idea
  if (files.length === 0) {
    const reply = plan.summary.trim() || shown
    return (
      <div className="max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-ss-md bg-slate-100 px-4 py-2.5 text-sm leading-relaxed text-slate-800">
        {reply || t('بفكر في الرد...')}
      </div>
    )
  }
  return (
    <div className="w-full max-w-[95%] space-y-2">
      <p className="text-sm text-slate-400">{files.length === 0 ? 'Planning next moves' : attempt > 1 ? t('الإصلاح الحالي') : t('جاري التعديل')}</p>
      {steps.map((s, i) => (
        <div key={`${s.attempt}-${s.part}-${i}`}>
          <p className="text-[11px] text-slate-500">{s.attempt > 1 ? t('تفكير الإصلاح {n}', { n: s.attempt }) : t('التفكير {n}', { n: i + 1 })}</p>
          <p className="whitespace-pre-wrap text-sm leading-relaxed text-slate-300">{s.text}</p>
        </div>
      ))}
      {shown && (
        <div>
          <p className="text-[11px] text-slate-500">{attempt > 1 ? t('تفكير الإصلاح {n}', { n: attempt }) : t('التفكير {n}', { n: steps.length + 1 })}</p>
          <p className="whitespace-pre-wrap text-sm leading-relaxed text-slate-300">{shown}</p>
        </div>
      )}
      {part > 1 && <p className="text-xs text-slate-500">{t('طلب كبير: الجزء {n}', { n: part })}</p>}
      <ChatFiles files={files} />
    </div>
  )
}

/** Model output rendered while it streams: one highlighted block per file change. */
export function LiveCode({ text, attempt, part = 1 }: { text: string; attempt: number; part?: number }) {
  const parsed = useMemo(() => parsePartialJson(text) as { summary?: string; files?: StreamedOp[] } | undefined, [text])
  const scrollRef = useRef<HTMLDivElement>(null)
  const stick = useRef(true)

  useEffect(() => {
    const el = scrollRef.current
    if (el && stick.current) el.scrollTop = el.scrollHeight
  }, [text])

  const files = parsed?.files?.filter((f) => f && typeof f === 'object') ?? []

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl bg-[#0d1117] text-slate-200 shadow-lg">
      <div className="flex items-center gap-2 border-b border-white/10 px-4 py-2.5 text-sm">
        <span className="h-2.5 w-2.5 animate-pulse rounded-full bg-emerald-400" />
        <span className="font-semibold">{t('الذكاء الاصطناعي يكتب الكود الآن')}</span>
        {part > 1 && <span className="rounded-full bg-sky-500/15 px-2 py-0.5 text-xs text-sky-300">{t('طلب كبير: الجزء {n}', { n: part })}</span>}
        {attempt > 1 && <span className="rounded-full bg-amber-500/15 px-2 py-0.5 text-xs text-amber-300">{t('مراجعة وإصلاح تلقائي ({n})', { n: attempt })}</span>}
        <span className="ms-auto text-xs text-slate-500" dir="ltr">
          {text.length.toLocaleString()} chars
        </span>
      </div>
      <div
        ref={scrollRef}
        onScroll={(e) => {
          const el = e.currentTarget
          stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80
        }}
        className="scrollbar-thin min-h-0 flex-1 space-y-4 overflow-y-auto p-4"
      >
        {!text && (
          <div className="flex items-center gap-2 text-sm text-slate-400">
            <Spinner className="h-4 w-4" /> {t('يقرأ ملفات موقعك ويخطط للتعديل...')}
          </div>
        )}
        {text && files.length === 0 && <CodeBlock code={text} path="output.json" className="rounded-xl" />}
        {files.map((f, i) => {
          const [label, color] = opLabel()[f.op ?? ''] ?? ['...', 'bg-white/10 text-slate-300']
          return (
            <div key={i} className="overflow-hidden rounded-xl border border-white/10">
              <div className="flex items-center gap-2 bg-white/5 px-3 py-2 text-xs">
                <span className={`rounded px-1.5 py-0.5 font-semibold ${color}`}>{label}</span>
                <span className="font-mono text-slate-300" dir="ltr">
                  {f.path ?? '...'}
                </span>
              </div>
              {f.op === 'write' && typeof f.content === 'string' && <CodeBlock code={f.content} path={f.path ?? ''} />}
              {f.op === 'edit' &&
                (f.edits ?? []).filter(Boolean).map((e, j) => (
                  <div key={j} className="border-t border-white/10">
                    {typeof e.find === 'string' && e.find && (
                      <div className="border-l-2 border-red-400/60 bg-red-500/5">
                        <CodeBlock code={e.find} path={f.path ?? ''} className="max-h-40 !bg-transparent opacity-70" />
                      </div>
                    )}
                    {typeof e.replace === 'string' && (
                      <div className="border-l-2 border-emerald-400/70 bg-emerald-500/5">
                        <CodeBlock code={e.replace} path={f.path ?? ''} className="!bg-transparent" />
                      </div>
                    )}
                  </div>
                ))}
            </div>
          )
        })}
        {typeof parsed?.summary === 'string' && parsed.summary && (
          <div className="rounded-xl bg-white/5 px-4 py-3 text-sm leading-relaxed text-slate-300">{parsed.summary}</div>
        )}
      </div>
    </div>
  )
}

/** Read-only browser for the files of one site version. */
export function FileBrowser({ projectId, versionId, actions }: { projectId: string; versionId: string | null; actions?: ReactNode }) {
  const [files, setFiles] = useState<Record<string, string> | null>(null)
  const [selected, setSelected] = useState('index.html')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!versionId) return
    setFiles(null)
    get<{ files: Record<string, string> }>(`/api/projects/${projectId}/versions/${versionId}`)
      .then((v) => {
        setFiles(v.files)
        setSelected((s) => (s in v.files ? s : Object.keys(v.files)[0] ?? ''))
      })
      .catch(() => setError(t('تعذر تحميل الملفات')))
  }, [projectId, versionId])

  const paths = files ? Object.keys(files) : []
  return (
    <div className="flex min-h-0 flex-1 overflow-hidden rounded-2xl bg-[#0d1117] text-slate-200 shadow-lg">
      <aside className="scrollbar-thin w-56 shrink-0 overflow-y-auto border-e border-white/10 py-2" dir="ltr">
        <p className="px-3 pb-2 text-[11px] font-semibold uppercase tracking-wider text-slate-500">Files</p>
        {paths.map((p) => (
          <button
            key={p}
            onClick={() => setSelected(p)}
            className={`block w-full truncate px-3 py-1.5 text-left font-mono text-xs ${p === selected ? 'bg-white/10 text-white' : 'text-slate-400 hover:bg-white/5'}`}
          >
            {p}
          </button>
        ))}
      </aside>
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center gap-2 border-b border-white/10 px-4 py-2 text-xs">
          <span className="font-mono text-slate-300" dir="ltr">
            {selected}
          </span>
          <div className="ms-auto flex items-center gap-2">{actions}</div>
        </div>
        {error ? (
          <p className="p-4 text-sm text-red-300">{error}</p>
        ) : !files ? (
          <div className="p-4">
            <Spinner className="h-5 w-5 text-slate-400" />
          </div>
        ) : (
          <CodeBlock code={files[selected] ?? ''} path={selected} className="min-h-0 flex-1" />
        )}
      </div>
    </div>
  )
}
