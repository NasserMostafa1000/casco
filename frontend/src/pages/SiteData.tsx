import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { Alert, Badge, Button, Card, Empty, Input, Modal, Spinner, Textarea, formatDateTime } from '../components/ui'
import { api, del, downloadFile, errorMessage, get, post, put, type ProjectDetail } from '../lib/api'
import { t, tServer } from '../lib/i18n'
import { useAuth } from '../lib/auth'
import { ArrowLeft, PenLine } from 'lucide-react'
import { SubdomainField, type SlugStatus } from '../components/SubdomainField'

import { Bookings, Collections, HostingCard, SiteSettingsForm, Store } from './SiteCommerce'

type Tab = 'messages' | 'store' | 'bookings' | 'data' | 'courses' | 'enrollments' | 'ads' | 'users' | 'settings'

export default function SiteData() {
  const { id = '' } = useParams()
  const [project, setProject] = useState<ProjectDetail | null>(null)
  const [tab, setTab] = useState<Tab>('messages')
  const [error, setError] = useState('')

  const load = useCallback(() => get<ProjectDetail>(`/api/projects/${id}`).then(setProject).catch((e) => setError(errorMessage(e))), [id])
  useEffect(() => {
    void load()
  }, [load])

  if (!project)
    return <div className="grid place-items-center py-20">{error ? <Alert tone="error">{error}</Alert> : <Spinner className="h-8 w-8 text-brand-600" />}</div>

  const modules = new Set([...(project.template?.modules ?? ['forms']), ...(project.features ?? [])])
  const tabs: [Tab, string][] = [['messages', t('الرسائل')]]
  if (modules.has('store')) tabs.push(['store', t('المتجر')])
  if (modules.has('bookings')) tabs.push(['bookings', t('الحجوزات')])
  if (modules.has('db')) tabs.push(['data', t('البيانات')])
  if (modules.has('courses')) tabs.push(['courses', t('الكورسات')], ['enrollments', t('الاشتراكات')], ['users', t('الطلاب')])
  if (modules.has('ads')) tabs.push(['ads', t('الإعلانات')])
  if (!modules.has('courses') && (modules.has('ads') || modules.has('auth'))) tabs.push(['users', t('المستخدمون')])
  tabs.push(['settings', t('الإعدادات')])

  return (
    <main className="mx-auto max-w-6xl px-4 py-8 sm:px-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <Link to="/app" className="inline-flex items-center gap-1 text-sm text-slate-500 hover:text-slate-800">
            <ArrowLeft className="flip-rtl h-4 w-4" />
            {t('مواقعي')}
          </Link>
          <h1 className="mt-1 truncate text-2xl font-extrabold tracking-tight text-ink sm:text-3xl">{project.name}</h1>
        </div>
        <Link to={`/app/p/${id}`} className="inline-flex h-10 items-center gap-2 rounded-xl bg-ink px-4 text-sm font-semibold text-white hover:bg-slate-800">
          <PenLine className="h-4 w-4" />
          {t('فتح المحرر')}
        </Link>
      </div>
      <div className="scrollbar-none -mx-4 mt-6 flex gap-1 overflow-x-auto px-4 sm:mx-0 sm:px-0">
        {tabs.map(([key, label]) => (
          <button
            key={key}
            onClick={() => setTab(key)}
            className={`shrink-0 rounded-xl px-4 py-2 text-sm font-medium transition ${tab === key ? 'bg-ink text-white' : 'text-slate-600 hover:bg-slate-100'}`}
          >
            {label}
          </button>
        ))}
      </div>
      <div className="mt-6">
        {tab === 'messages' && <Messages projectId={id} />}
        {tab === 'store' && <Store projectId={id} />}
        {tab === 'bookings' && <Bookings projectId={id} />}
        {tab === 'data' && <Collections projectId={id} />}
        {tab === 'courses' && <Courses projectId={id} />}
        {tab === 'enrollments' && <Enrollments projectId={id} />}
        {tab === 'users' && <SiteUsers projectId={id} />}
        {tab === 'ads' && <Ads projectId={id} />}
        {tab === 'settings' && <Settings project={project} onChange={load} showStore={modules.has('store')} showBookings={modules.has('bookings')} />}
      </div>
    </main>
  )
}

function useList<T>(url: string) {
  const [items, setItems] = useState<T | null>(null)
  const [error, setError] = useState('')
  const reload = useCallback(() => get<T>(url).then(setItems).catch((e) => setError(errorMessage(e))), [url])
  useEffect(() => {
    void reload()
  }, [reload])
  return { items, error, reload }
}

function Loading({ error }: { error: string }) {
  return error ? <Alert tone="error">{error}</Alert> : <Spinner className="h-6 w-6 text-brand-600" />
}

// ---------- Messages ----------
interface Submission {
  id: string
  formName: string
  data: Record<string, string>
  isRead: boolean
  createdAt: string
}

function Messages({ projectId }: { projectId: string }) {
  const { items, error, reload } = useList<{ total: number; unread: number; items: Submission[] }>(`/api/projects/${projectId}/data/submissions`)
  if (!items) return <Loading error={error} />
  if (items.items.length === 0) return <Empty title={t('لا توجد رسائل بعد')}>{t('عندما يرسل الزوار نموذج التواصل في موقعك ستظهر رسائلهم هنا.')}</Empty>
  return (
    <div className="space-y-3">
      <p className="text-sm text-slate-500">
        {t('{total} رسالة • {unread} غير مقروءة', { total: items.total, unread: items.unread })}
      </p>
      {items.items.map((s) => (
        <Card key={s.id} className={s.isRead ? '' : 'border-brand-200 bg-brand-50/40'}>
          <div className="flex items-start justify-between gap-3">
            <div className="space-y-1 text-sm">
              {Object.entries(s.data).map(([k, v]) => (
                <p key={k}>
                  <span className="font-bold text-slate-700">{k}:</span> <span className="whitespace-pre-wrap">{v}</span>
                </p>
              ))}
              <p className="text-xs text-slate-400">
                {formatDateTime(s.createdAt)} • {s.formName}
              </p>
            </div>
            <div className="flex shrink-0 gap-1">
              {!s.isRead && (
                <Button variant="secondary" onClick={() => post(`/api/projects/${projectId}/data/submissions/${s.id}/read`).then(reload)}>
                  {t('مقروءة')}
                </Button>
              )}
              <Button variant="ghost" onClick={() => confirm(t('حذف الرسالة؟')) && del(`/api/projects/${projectId}/data/submissions/${s.id}`).then(reload)}>
                {t('حذف')}
              </Button>
            </div>
          </div>
        </Card>
      ))}
    </div>
  )
}

// ---------- Courses ----------
interface Lesson {
  id?: string
  title: string
  videoUrl: string | null
  content: string | null
  isFreePreview: boolean
  durationMinutes: number
  sortOrder: number
}
interface Course {
  id?: string
  title: string
  description: string
  imageUrl: string | null
  price: number
  currency: string
  instructor: string | null
  category: string | null
  paymentLink: string | null
  isPublished: boolean
  sortOrder: number
  lessons?: Lesson[]
}

const emptyCourse: Course = { title: '', description: '', imageUrl: '', price: 0, currency: 'USD', instructor: '', category: '', paymentLink: '', isPublished: true, sortOrder: 0 }
const emptyLesson: Lesson = { title: '', videoUrl: '', content: '', isFreePreview: false, durationMinutes: 0, sortOrder: 0 }

function Courses({ projectId }: { projectId: string }) {
  const { items, error, reload } = useList<Course[]>(`/api/projects/${projectId}/data/courses`)
  const [editing, setEditing] = useState<Course | null>(null)
  const [lessonFor, setLessonFor] = useState<{ courseId: string; lesson: Lesson } | null>(null)
  const base = `/api/projects/${projectId}/data`

  if (!items) return <Loading error={error} />
  return (
    <div className="space-y-4">
      <div className="flex justify-between">
        <p className="text-sm text-slate-500">{t('الكورسات التي تضيفها هنا تظهر تلقائياً في موقعك.')}</p>
        <Button onClick={() => setEditing({ ...emptyCourse })}>{t('+ كورس جديد')}</Button>
      </div>
      {items.length === 0 && <Empty title={t('لا توجد كورسات بعد')}>{t('أضف أول كورس ليظهر في منصتك.')}</Empty>}
      {items.map((c) => (
        <Card key={c.id}>
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="flex gap-3">
              {c.imageUrl && <img src={c.imageUrl} alt="" className="h-16 w-24 rounded-lg object-cover" />}
              <div>
                <h3 className="font-bold">
                  {c.title} {!c.isPublished && <Badge>{t('مخفي')}</Badge>}
                </h3>
                <p className="text-sm text-slate-500">
                  {c.price > 0 ? `${c.price} ${c.currency}` : t('مجاني')} {c.instructor && `• ${c.instructor}`}
                </p>
              </div>
            </div>
            <div className="flex gap-1">
              <Button variant="secondary" onClick={() => setEditing(c)}>
                {t('تعديل')}
              </Button>
              <Button variant="ghost" onClick={() => confirm(t('حذف الكورس وكل دروسه؟')) && del(`${base}/courses/${c.id}`).then(reload)}>
                {t('حذف')}
              </Button>
            </div>
          </div>
          <div className="mt-4 space-y-2 border-t border-slate-100 pt-3">
            {c.lessons?.map((l) => (
              <div key={l.id} className="flex items-center justify-between rounded-lg bg-slate-50 px-3 py-2 text-sm">
                <span>
                  {l.title} {l.isFreePreview && <Badge color="green">{t('معاينة مجانية')}</Badge>}
                </span>
                <span className="flex gap-2">
                  <button className="text-brand-600" onClick={() => setLessonFor({ courseId: c.id!, lesson: l })}>
                    {t('تعديل')}
                  </button>
                  <button className="text-red-600" onClick={() => confirm(t('حذف الدرس؟')) && del(`${base}/lessons/${l.id}`).then(reload)}>
                    {t('حذف')}
                  </button>
                </span>
              </div>
            ))}
            <button className="text-sm font-bold text-brand-600" onClick={() => setLessonFor({ courseId: c.id!, lesson: { ...emptyLesson, sortOrder: c.lessons?.length ?? 0 } })}>
              {t('+ إضافة درس')}
            </button>
          </div>
        </Card>
      ))}
      {editing && <CourseForm projectId={projectId} course={editing} onClose={() => setEditing(null)} onSaved={reload} />}
      {lessonFor && <LessonForm projectId={projectId} {...lessonFor} onClose={() => setLessonFor(null)} onSaved={reload} />}
    </div>
  )
}

function CourseForm({ projectId, course, onClose, onSaved }: { projectId: string; course: Course; onClose: () => void; onSaved: () => void }) {
  const [c, setC] = useState<Course>(course)
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const set = <K extends keyof Course>(k: K, v: Course[K]) => setC((prev) => ({ ...prev, [k]: v }))

  const upload = async (file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    try {
      const r = await api<{ url: string }>('POST', `/api/projects/${projectId}/data/uploads`, fd)
      set('imageUrl', r.url)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError('')
    const body = { ...c, imageUrl: c.imageUrl || null, paymentLink: c.paymentLink || null, price: Number(c.price) || 0 }
    try {
      if (c.id) await put(`/api/projects/${projectId}/data/courses/${c.id}`, body)
      else await post(`/api/projects/${projectId}/data/courses`, body)
      onSaved()
      onClose()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal open onClose={onClose} title={c.id ? t('تعديل الكورس') : t('كورس جديد')}>
      <form onSubmit={save} className="space-y-3">
        {error && <Alert tone="error">{error}</Alert>}
        <Input label={t('العنوان')} required value={c.title} onChange={(e) => set('title', e.target.value)} />
        <Textarea label={t('الوصف')} rows={3} value={c.description} onChange={(e) => set('description', e.target.value)} />
        <div className="grid grid-cols-2 gap-3">
          <Input label={t('السعر (0 = مجاني)')} type="number" min={0} step="0.01" value={c.price} onChange={(e) => set('price', Number(e.target.value))} />
          <Input label={t('العملة')} value={c.currency} onChange={(e) => set('currency', e.target.value.toUpperCase())} maxLength={3} dir="ltr" />
          <Input label={t('المدرب')} value={c.instructor ?? ''} onChange={(e) => set('instructor', e.target.value)} />
          <Input label={t('التصنيف')} value={c.category ?? ''} onChange={(e) => set('category', e.target.value)} />
        </div>
        <Input label={t('رابط الدفع (Ziina / Stripe / أي رابط)')} value={c.paymentLink ?? ''} onChange={(e) => set('paymentLink', e.target.value)} dir="ltr" placeholder="https://..." />
        <div>
          <Input label={t('رابط الصورة')} value={c.imageUrl ?? ''} onChange={(e) => set('imageUrl', e.target.value)} dir="ltr" placeholder="https://..." />
          <label className="mt-1 inline-block cursor-pointer text-sm font-semibold text-brand-600">
            {t('أو ارفع صورة')}
            <input type="file" accept="image/*" className="hidden" onChange={(e) => e.target.files?.[0] && upload(e.target.files[0])} />
          </label>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={c.isPublished} onChange={(e) => set('isPublished', e.target.checked)} /> {t('ظاهر في الموقع')}
        </label>
        <p className="text-xs text-slate-500">{t('الكورسات المدفوعة: يشترك الطالب ويدفع عبر رابط الدفع، ثم تفعّل اشتراكه من تبويب "الاشتراكات".')}</p>
        <Button type="submit" loading={saving} className="w-full">
          {t('حفظ')}
        </Button>
      </form>
    </Modal>
  )
}

function LessonForm(props: { projectId: string; courseId: string; lesson: Lesson; onClose: () => void; onSaved: () => void }) {
  const [l, setL] = useState<Lesson>(props.lesson)
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const set = <K extends keyof Lesson>(k: K, v: Lesson[K]) => setL((prev) => ({ ...prev, [k]: v }))

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError('')
    const body = { ...l, videoUrl: l.videoUrl || null, durationMinutes: Number(l.durationMinutes) || 0 }
    try {
      if (l.id) await put(`/api/projects/${props.projectId}/data/lessons/${l.id}`, body)
      else await post(`/api/projects/${props.projectId}/data/courses/${props.courseId}/lessons`, body)
      props.onSaved()
      props.onClose()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal open onClose={props.onClose} title={l.id ? t('تعديل الدرس') : t('درس جديد')}>
      <form onSubmit={save} className="space-y-3">
        {error && <Alert tone="error">{error}</Alert>}
        <Input label={t('عنوان الدرس')} required value={l.title} onChange={(e) => set('title', e.target.value)} />
        <Input label={t('رابط الفيديو (YouTube / Vimeo / Bunny)')} value={l.videoUrl ?? ''} onChange={(e) => set('videoUrl', e.target.value)} dir="ltr" placeholder="https://..." />
        <Textarea label={t('محتوى نصي (اختياري)')} rows={3} value={l.content ?? ''} onChange={(e) => set('content', e.target.value)} />
        <div className="grid grid-cols-2 gap-3">
          <Input label={t('المدة بالدقائق')} type="number" min={0} value={l.durationMinutes} onChange={(e) => set('durationMinutes', Number(e.target.value))} />
          <Input label={t('الترتيب')} type="number" value={l.sortOrder} onChange={(e) => set('sortOrder', Number(e.target.value))} />
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={l.isFreePreview} onChange={(e) => set('isFreePreview', e.target.checked)} /> {t('معاينة مجانية (متاح بدون اشتراك)')}
        </label>
        <Button type="submit" loading={saving} className="w-full">
          {t('حفظ')}
        </Button>
      </form>
    </Modal>
  )
}

// ---------- Enrollments ----------
interface EnrollmentRow {
  id: string
  status: string
  createdAt: string
  courseTitle: string
  price: number
  currency: string
  studentName: string
  studentEmail: string
}

function Enrollments({ projectId }: { projectId: string }) {
  const { items, error, reload } = useList<EnrollmentRow[]>(`/api/projects/${projectId}/data/enrollments`)
  if (!items) return <Loading error={error} />
  if (items.length === 0) return <Empty title={t('لا توجد اشتراكات بعد')} />
  return (
    <Card className="overflow-x-auto p-0">
      <table className="w-full text-sm">
        <thead className="bg-slate-50 text-slate-500">
          <tr>
            <th className="p-3 text-start">{t('الطالب')}</th>
            <th className="p-3 text-start">{t('الكورس')}</th>
            <th className="p-3 text-start">{t('الحالة')}</th>
            <th className="p-3 text-start">{t('التاريخ')}</th>
            <th className="p-3" />
          </tr>
        </thead>
        <tbody>
          {items.map((e) => (
            <tr key={e.id} className="border-t border-slate-100">
              <td className="p-3">
                {e.studentName}
                <div className="text-xs text-slate-400" dir="ltr">
                  {e.studentEmail}
                </div>
              </td>
              <td className="p-3">{e.courseTitle}</td>
              <td className="p-3">{e.status === 'active' ? <Badge color="green">{t('مفعّل')}</Badge> : <Badge color="amber">{t('بانتظار الدفع')}</Badge>}</td>
              <td className="p-3 text-slate-500">{formatDateTime(e.createdAt)}</td>
              <td className="p-3 text-end">
                {e.status !== 'active' && (
                  <Button variant="success" onClick={() => post(`/api/projects/${projectId}/data/enrollments/${e.id}/approve`).then(reload)}>
                    {t('تفعيل')}
                  </Button>
                )}
                <Button variant="ghost" onClick={() => confirm(t('إلغاء الاشتراك؟')) && del(`/api/projects/${projectId}/data/enrollments/${e.id}`).then(reload)}>
                  {t('إلغاء')}
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  )
}

function SiteUsers({ projectId }: { projectId: string }) {
  const { items, error } = useList<{ id: string; name: string; email: string; createdAt: string }[]>(`/api/projects/${projectId}/data/users`)
  if (!items) return <Loading error={error} />
  if (items.length === 0) return <Empty title={t('لا يوجد مستخدمون مسجلون بعد')} />
  return (
    <Card className="p-0">
      {items.map((u) => (
        <div key={u.id} className="flex justify-between border-b border-slate-100 p-3 text-sm last:border-0">
          <span>
            {u.name}{' '}
            <span className="text-slate-400" dir="ltr">
              {u.email}
            </span>
          </span>
          <span className="text-slate-400">{formatDateTime(u.createdAt)}</span>
        </div>
      ))}
    </Card>
  )
}

// ---------- Ads ----------
interface AdRow {
  id: string
  title: string
  description: string
  price: number
  currency: string
  category: string | null
  city: string | null
  phone: string | null
  status: string
  views: number
  createdAt: string
  images: string[]
}

function Ads({ projectId }: { projectId: string }) {
  const [status, setStatus] = useState('')
  const { items, error, reload } = useList<AdRow[]>(`/api/projects/${projectId}/data/ads${status ? `?status=${status}` : ''}`)
  const setAdStatus = (id: string, s: string) => post(`/api/projects/${projectId}/data/ads/${id}/status`, { status: s }).then(reload)

  return (
    <div className="space-y-4">
      <div className="flex gap-2">
        {[
          ['', t('الكل')],
          ['pending', t('بانتظار المراجعة')],
          ['approved', t('منشورة')],
          ['rejected', t('مرفوضة')],
        ].map(([k, label]) => (
          <button key={k} onClick={() => setStatus(k)} className={`rounded-full px-3 py-1 text-sm ${status === k ? 'bg-slate-900 text-white' : 'bg-slate-100'}`}>
            {label}
          </button>
        ))}
      </div>
      {!items ? (
        <Loading error={error} />
      ) : items.length === 0 ? (
        <Empty title={t('لا توجد إعلانات')} />
      ) : (
        items.map((a) => (
          <Card key={a.id}>
            <div className="flex flex-wrap gap-4">
              {a.images?.[0] && <img src={a.images[0]} alt="" className="h-20 w-28 rounded-lg object-cover" />}
              <div className="min-w-0 flex-1">
                <h3 className="font-bold">
                  {a.title}{' '}
                  <Badge color={a.status === 'approved' ? 'green' : a.status === 'pending' ? 'amber' : 'red'}>
                    {a.status === 'approved' ? t('منشور') : a.status === 'pending' ? t('بانتظار المراجعة') : t('مرفوض')}
                  </Badge>
                </h3>
                <p className="text-sm text-slate-500">
                  {a.price} {a.currency} • {a.category} • {a.city} • 👁 {a.views}
                </p>
                <p className="mt-1 line-clamp-2 text-sm">{a.description}</p>
              </div>
              <div className="flex shrink-0 flex-col gap-1">
                {a.status !== 'approved' && (
                  <Button variant="success" onClick={() => setAdStatus(a.id, 'approved')}>
                    {t('قبول')}
                  </Button>
                )}
                {a.status !== 'rejected' && (
                  <Button variant="secondary" onClick={() => setAdStatus(a.id, 'rejected')}>
                    {t('رفض')}
                  </Button>
                )}
                <Button variant="ghost" onClick={() => confirm(t('حذف الإعلان؟')) && del(`/api/projects/${projectId}/data/ads/${a.id}`).then(reload)}>
                  {t('حذف')}
                </Button>
              </div>
            </div>
          </Card>
        ))
      )}
    </div>
  )
}

// ---------- Settings ----------
function Settings({ project, onChange, showStore, showBookings }: { project: ProjectDetail; onChange: () => void; showStore: boolean; showBookings: boolean }) {
  const { me, refresh } = useAuth()
  const navigate = useNavigate()
  const [slug, setSlug] = useState(project.slug)
  const [slugStatus, setSlugStatus] = useState<SlugStatus>({ state: 'idle' })
  const [domain, setDomain] = useState(project.customDomain ?? '')
  const [msg, setMsg] = useState<{ tone: 'success' | 'error' | 'info'; text: string } | null>(null)
  const [busy, setBusy] = useState('')

  const run = async (key: string, fn: () => Promise<unknown>, success?: string) => {
    setBusy(key)
    setMsg(null)
    try {
      const r = await fn()
      const message = (r as { message?: string } | null)?.message
      setMsg({ tone: (r as { verified?: boolean } | null)?.verified === false ? 'info' : 'success', text: message ? tServer(message) : success ?? t('تم الحفظ') })
      onChange()
    } catch (e) {
      setMsg({ tone: 'error', text: errorMessage(e) })
    } finally {
      setBusy('')
    }
  }

  const exportZip = async () => {
    if (!me?.plan.isPro) {
      setMsg({ tone: 'info', text: t('تنزيل ملفات الموقع متاح لمشتركي Pro فقط.') })
      return
    }
    setBusy('export')
    try {
      await downloadFile(`/api/projects/${project.id}/export`, `${project.slug}.zip`)
    } catch (e) {
      setMsg({ tone: 'error', text: errorMessage(e) })
    } finally {
      setBusy('')
    }
  }

  return (
    <div className="space-y-4">
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      <HostingCard projectId={project.id} />
      <SiteSettingsForm projectId={project.id} showStore={showStore} showBookings={showBookings} />
      <Card className="space-y-3">
        <h3 className="font-bold">{t('رابط الموقع')}</h3>
        <div className="flex flex-col gap-2 sm:flex-row sm:items-start">
          <div className="min-w-0 flex-1">
            <SubdomainField projectId={project.id} currentSlug={project.slug} suffix={project.siteSuffix} value={slug} onChange={setSlug} onStatus={setSlugStatus} />
          </div>
          <Button
            loading={busy === 'slug'}
            disabled={slug === project.slug || slugStatus.state !== 'ok'}
            onClick={() => run('slug', () => put(`/api/projects/${project.id}/slug`, { slug }))}
            className="sm:mt-0.5"
          >
            {t('حفظ')}
          </Button>
        </div>
        <a href={project.subdomainUrl} target="_blank" rel="noreferrer" className="block truncate text-sm text-brand-600 hover:underline" dir="ltr">
          {project.subdomainUrl}
        </a>
        {project.publishedAt && slug !== project.slug && <Alert tone="warning">{t('بعد تغيير الاسم سيتوقف الرابط القديم عن العمل.')}</Alert>}
      </Card>

      <Card className="space-y-3">
        <h3 className="font-bold">{t('الدومين الخاص')}</h3>
        <div className="flex flex-wrap items-end gap-2">
          <div className="flex-1">
            <Input value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="www.example.com" dir="ltr" />
          </div>
          <Button loading={busy === 'domain'} onClick={() => run('domain', () => put(`/api/projects/${project.id}/domain`, { domain }))}>
            {t('حفظ')}
          </Button>
          {project.customDomain && (
            <Button variant="secondary" loading={busy === 'verify'} onClick={() => run('verify', () => post(`/api/projects/${project.id}/domain/verify`))}>
              {t('تحقق من الربط')}
            </Button>
          )}
        </div>
        {project.customDomain && (
          <div className="text-sm">
            {project.customDomainVerified ? (
              <Badge color="green">{t('مربوط ✓')}</Badge>
            ) : (
              <p className="text-slate-600">
                {t('من لوحة تحكم الدومين أضف سجل CNAME باسم www يشير إلى {target} ثم اضغط "تحقق من الربط".', { target: project.cnameTarget })}
              </p>
            )}
          </div>
        )}
        <p className="text-xs text-slate-500">{t('ربط الدومين متاح مع أي استضافة فعّالة للموقع.')}</p>
      </Card>

      {project.template?.modules.includes('ads') && (
        <Card>
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              defaultChecked={project.adsRequireApproval}
              onChange={(e) => run('ads', () => put(`/api/projects/${project.id}/data/settings`, { adsRequireApproval: e.target.checked }))}
            />
            <span className="font-semibold">{t('مراجعة الإعلانات قبل نشرها')}</span>
          </label>
        </Card>
      )}

      <Card className="flex flex-wrap gap-2">
        <Button variant="secondary" loading={busy === 'export'} onClick={exportZip}>
          {t('تحميل ملفات الموقع (ZIP)')} {!me?.plan.isPro && '🔒'}
        </Button>
        {project.publishedAt && (
          <Button variant="secondary" loading={busy === 'unpublish'} onClick={() => confirm(t('إيقاف نشر الموقع؟')) && run('unpublish', () => post(`/api/projects/${project.id}/unpublish`), t('تم إيقاف النشر'))}>
            {t('إيقاف النشر')}
          </Button>
        )}
        <Button
          variant="danger"
          loading={busy === 'delete'}
          onClick={async () => {
            if (!confirm(t('حذف الموقع نهائياً مع كل بياناته؟ لا يمكن التراجع.'))) return
            setBusy('delete')
            try {
              await del(`/api/projects/${project.id}`)
              await refresh()
              navigate('/app')
            } catch (e) {
              setMsg({ tone: 'error', text: errorMessage(e) })
              setBusy('')
            }
          }}
        >
          {t('حذف الموقع')}
        </Button>
      </Card>
    </div>
  )
}
