import { useEffect, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Apple, ArrowRight, Monitor } from 'lucide-react'
import { Logo } from '../components/AppShell'
import { LanguageSwitcher } from '../components/LanguageSwitcher'
import { ThemeToggle } from '../components/ThemeToggle'
import { get } from '../lib/api'
import { t } from '../lib/i18n'

const REPO = 'NasserMostafa1000/casco-studio'

const FILES = {
  windows: 'CascoStudio-Windows-x64.exe',
  macArm: 'CascoStudio-macOS-arm64.zip',
  macIntel: 'CascoStudio-macOS-x64.zip',
} as const

type AssetName = (typeof FILES)[keyof typeof FILES]

type Asset = { name: string; size: number; url: string }

function releaseUrl(name: string) {
  return `https://github.com/${REPO}/releases/latest/download/${name}`
}

function formatSize(bytes: number) {
  if (!bytes) return ''
  const mb = bytes / (1024 * 1024)
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`
}

export default function Download() {
  const [version, setVersion] = useState('')
  const [assets, setAssets] = useState<Asset[]>([])
  const [ready, setReady] = useState(false)
  const [notice, setNotice] = useState('')
  const forced = new URLSearchParams(location.search).get('update') === '1'

  useEffect(() => {
    document.title = `${t('تنزيل')} Casco Studio | استوديو ومحرر أكواد`
    document.querySelector('meta[name="description"]')?.setAttribute('content', 'Download Casco Studio, an AI code studio and code editor for Windows and Mac, like Cursor and Claude.')
    document.querySelector('link[rel="canonical"]')?.setAttribute('href', 'https://casco.studio/download')
  }, [])

  useEffect(() => {
    if (!forced) return
    let cancelled = false
    get<{ message?: string }>('/api/desktop/update')
      .then((data) => {
        if (!cancelled) setNotice(data.message || '')
      })
      .catch(() => {
        if (!cancelled) setNotice('')
      })
    return () => {
      cancelled = true
    }
  }, [forced])

  useEffect(() => {
    const ctrl = new AbortController()
    fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { signal: ctrl.signal })
      .then((res) => (res.ok ? res.json() : null))
      .then((data: { tag_name?: string; assets?: { name: string; size: number; browser_download_url: string }[] } | null) => {
        if (!data) return
        setVersion(data.tag_name || '')
        setAssets((data.assets || []).map((a) => ({ name: a.name, size: a.size, url: a.browser_download_url })))
      })
      .catch(() => {})
      .finally(() => setReady(true))
    return () => ctrl.abort()
  }, [])

  const find = (name: AssetName) => assets.find((a) => a.name === name)

  return (
    <div className="min-h-screen bg-white">
      <header className="border-b border-slate-200/70 bg-white/85 backdrop-blur-xl">
        <div className="mx-auto flex h-16 max-w-6xl items-center gap-4 px-4 sm:px-6">
          <Logo />
          <div className="ms-auto flex items-center gap-2">
            <ThemeToggle />
            <LanguageSwitcher compact />
            <Link to="/" className="inline-flex h-9 items-center rounded-xl px-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-100">
              {t('الموقع')}
            </Link>
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-5xl px-4 py-16 sm:px-6 sm:py-24">
        {forced && (
          <section className="mb-8 rounded-3xl border border-amber-300 bg-amber-50 p-5 text-amber-950">
            <h2 className="text-lg font-extrabold">{t('تحديث مطلوب')}</h2>
            <p className="mt-2 text-base">{notice || t('فيه نسخة جديدة من Casco Studio. نزّلها عشان تكمل استخدام البرنامج.')}</p>
          </section>
        )}
        <h1 className="text-4xl font-extrabold tracking-tight text-ink sm:text-6xl">{t('تنزيل')}</h1>
        <p className="mt-3 text-sm font-semibold uppercase tracking-wider text-brand-600">Casco Studio</p>
        <p className="mt-4 max-w-2xl text-lg text-slate-600">
          {t('برنامج الكمبيوتر فيه Casco Agent. سجّل الدخول بنفس حسابك على casco.studio.')}
        </p>
        {version && <p className="mt-3 text-sm text-slate-500">{t('آخر نسخة')}: {version}</p>}

        <div className="mt-10 grid gap-4 md:grid-cols-2">
          <Card
            icon={<Monitor className="h-6 w-6" />}
            title="Windows"
            text={t('ويندوز 10 أو أحدث، 64-bit.')}
            primary={{ label: t('تنزيل ويندوز'), href: find(FILES.windows)?.url || releaseUrl(FILES.windows), size: find(FILES.windows)?.size }}
          />
          <Card
            icon={<Apple className="h-6 w-6" />}
            title="macOS"
            text={t('ماك Apple Silicon أو Intel. النسخة غير موقعة من أبل، ولو النظام منع الفتح اضغط يمين ثم Open.')}
            primary={{ label: t('تنزيل ماك Apple Silicon'), href: find(FILES.macArm)?.url || releaseUrl(FILES.macArm), size: find(FILES.macArm)?.size }}
            secondary={{ label: t('تنزيل ماك Intel'), href: find(FILES.macIntel)?.url || releaseUrl(FILES.macIntel), size: find(FILES.macIntel)?.size }}
          />
        </div>

        {ready && assets.length === 0 && (
          <p className="mt-8 text-sm text-slate-500">{t('أول نسخة بتتبني على GitHub. زرار التنزيل هيشتغل لما البناء يخلص.')}</p>
        )}
      </main>
    </div>
  )
}

function Card({
  icon,
  title,
  text,
  primary,
  secondary,
}: {
  icon: ReactNode
  title: string
  text: string
  primary: { label: string; href?: string; size?: number }
  secondary?: { label: string; href?: string; size?: number }
}) {
  return (
    <section className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
      <div className="grid h-12 w-12 place-items-center rounded-2xl bg-brand-50 text-brand-700">{icon}</div>
      <h2 className="mt-4 text-2xl font-extrabold text-ink">{title}</h2>
      <p className="mt-2 text-slate-600">{text}</p>
      <DownloadLink {...primary} strong />
      {secondary && <DownloadLink {...secondary} />}
    </section>
  )
}

function DownloadLink({ label, href, size, strong }: { label: string; href?: string; size?: number; strong?: boolean }) {
  if (!href) {
    return <p className={strong ? 'mt-6 text-sm text-slate-500' : 'mt-3 text-sm text-slate-500'}>{t('الملف لسه بيتبني ورابط التنزيل هيظهر هنا لما يخلص.')}</p>
  }
  return (
    <a
      href={href}
      className={
        strong
          ? 'mt-6 inline-flex h-12 items-center gap-2 rounded-2xl bg-ink px-5 font-semibold text-white transition hover:bg-slate-800'
          : 'mt-3 inline-flex h-11 items-center gap-2 rounded-2xl border border-slate-200 px-4 text-sm font-semibold text-slate-800 transition hover:bg-slate-50'
      }
    >
      {label}
      {size ? <span className="text-xs font-medium opacity-70">{formatSize(size)}</span> : null}
      <ArrowRight className="flip-rtl h-4 w-4" />
    </a>
  )
}
