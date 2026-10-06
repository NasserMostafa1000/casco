import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import {
  ArrowRight,
  Building2,
  CalendarCheck,
  Check,
  ChevronDown,
  Code2,
  Database,
  Globe,
  GraduationCap,
  Languages,
  Megaphone,
  Menu,
  MessageSquare,
  Paperclip,
  Puzzle,
  ShoppingBag,
  Smartphone,
  Sparkles,
  X,
} from 'lucide-react'
import { Logo } from '../components/AppShell'
import { LanguageSwitcher } from '../components/LanguageSwitcher'
import { ThemeToggle } from '../components/ThemeToggle'
import { MediaThumb } from '../components/MediaThumb'
import { useAuth } from '../lib/auth'
import { num, t } from '../lib/i18n'
import { MAX_VIDEO_BYTES, MEDIA_ACCEPT, isMediaFile, isVideoFile } from '../lib/images'
import { pendingMedia, setPendingMedia, type PendingMedia } from '../lib/pendingMedia'
import { usd, usePlans, yearlySavingLabel } from '../lib/plans'

export const PENDING_PROMPT_KEY = 'casco_pending_prompt'
const MAX_ATTACHMENTS = 10

export default function Landing() {
  const [prompt, setPrompt] = useState('')
  const [picks, setPicks] = useState<PendingMedia[]>([])
  const [fileError, setFileError] = useState('')
  const [menu, setMenu] = useState(false)
  const [scrolled, setScrolled] = useState(false)
  const plans = usePlans()
  const { me } = useAuth()
  const navigate = useNavigate()
  const yearlySaving = plans ? yearlySavingLabel(plans.pro.monthlyPrice, plans.pro.yearlyPrice) : null

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 8)
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  const picksRef = useRef(picks)
  picksRef.current = picks
  useEffect(
    () => () => {
      const keep = new Set(pendingMedia().map((p) => p.url))
      for (const p of picksRef.current) if (!keep.has(p.url)) URL.revokeObjectURL(p.url)
    },
    [],
  )

  const addFiles = (list: FileList | File[]) => {
    const room = MAX_ATTACHMENTS - picks.length
    const media = Array.from(list).filter(isMediaFile)
    const tooBig = media.some((f) => isVideoFile(f) && f.size > MAX_VIDEO_BYTES)
    const extra = media.filter((f) => !isVideoFile(f) || f.size <= MAX_VIDEO_BYTES).slice(0, room)
    if (extra.length === 0) {
      if (room <= 0) setFileError(t('يمكنك إرفاق {n} صور كحد أقصى في الطلب الواحد', { n: MAX_ATTACHMENTS }))
      else if (tooBig) setFileError(t('حجم الفيديو يجب ألا يتجاوز 50 ميجابايت'))
      return
    }
    setFileError(tooBig ? t('حجم الفيديو يجب ألا يتجاوز 50 ميجابايت') : '')
    setPicks((p) => [...p, ...extra.map((file) => ({ file, url: URL.createObjectURL(file), video: isVideoFile(file) }))])
  }

  const start = () => {
    const text = prompt.trim()
    if (text || picks.length > 0) localStorage.setItem(PENDING_PROMPT_KEY, text || t('ابنِ الموقع باستخدام الصور المرفقة'))
    setPendingMedia(picks)
    navigate(me ? '/app/new' : '/register')
  }

  const links = [
    ['#features', t('المميزات')],
    ['#templates', t('ماذا تبني؟')],
    ['#pricing', t('الأسعار')],
    ['#faq', t('الأسئلة الشائعة')],
    ['/download', t('تنزيل البرنامج')],
  ]

  const features = [
    { icon: MessageSquare, title: t('عدّل بالكلام'), text: t('اكتب "غيّر اللون للأزرق" أو "أضف صفحة من نحن" والذكاء الاصطناعي ينفذ فوراً.') },
    { icon: Languages, title: t('بأي لغة'), text: t('مواقع بالعربية أو الإنجليزية أو الهندية أو أي لغة، مع اتجاه صحيح من اليمين أو اليسار.') },
    { icon: Smartphone, title: t('متجاوب على كل الشاشات'), text: t('كل موقع مصمم ليعمل بشكل مثالي على الموبايل والتابلت والكمبيوتر.') },
    { icon: Database, title: t('باك إند حقيقي'), text: t('حسابات مستخدمين، قاعدة بيانات، طلبات، حجوزات ودوال خادم بدون أي إعداد.') },
    { icon: Globe, title: t('استضافة ودومين'), text: t('انشر بضغطة على سيرفراتنا مع HTTPS، واربط دومينك الخاص.') },
    { icon: Code2, title: t('الكود ملكك'), text: t('شاهد الكود وهو يُكتب مباشرة، ونزّل ملفات موقعك كاملة في أي وقت.') },
  ]

  const useCases = [
    { icon: Building2, title: t('موقع تعريفي'), text: t('خدمات، آراء عملاء، أسئلة شائعة، ونموذج تواصل تصلك رسائله فوراً.'), pro: false },
    { icon: ShoppingBag, title: t('متجر إلكتروني'), text: t('منتجات وسلة وطلبات بالدفع عند الاستلام أو واتساب، مع إدارة المخزون.'), pro: false },
    { icon: CalendarCheck, title: t('حجز مواعيد'), text: t('عيادات وصالونات واستشارات: خدمات ومواعيد متاحة وتأكيد الحجوزات.'), pro: false },
    { icon: GraduationCap, title: t('منصة كورسات'), text: t('تسجيل طلاب، دروس فيديو، اشتراكات مجانية ومدفوعة، ولوحة إدارة.'), pro: true },
    { icon: Megaphone, title: t('منصة إعلانات مبوبة'), text: t('المستخدمون ينشرون إعلاناتهم بالصور، مع بحث وفلاتر وتواصل.'), pro: true },
    { icon: Puzzle, title: t('أي فكرة تانية'), text: t('تقييمات، تسجيلات، توظيف، أدلة، فعاليات… نبني قاعدة البيانات والباك إند لموقعك.'), pro: false },
  ]

  const faqs = [
    [t('هل أحتاج خبرة في البرمجة؟'), t('لا. تكتب وصف موقعك بكلامك العادي، وتعدّل عليه بنفس الطريقة. ولو أنت مبرمج تقدر تشوف الكود وتنزّله.')],
    [t('بأي لغة يمكن أن يكون موقعي؟'), t('بأي لغة: العربية والإنجليزية والهندية والفرنسية وغيرها. اختر اللغة عند إنشاء الموقع أو اكتب وصفك بها، ويُضبط اتجاه الصفحة تلقائياً.')],
    [t('هل الموقع يعمل على الموبايل؟'), t('نعم، كل المواقع متجاوبة وتعمل على كل الشاشات، وتقدر تعاين شكلها على الموبايل والتابلت من المحرر.')],
    [t('العميل بيدفع 18$ أو 180$ كام مرة؟'), t('مرة واحدة بس، مقابل كود موقعه ومهمة بنائه. المبلغ مش بيتدفع تاني عشان الموقع يفضل شغال. الموقع هيفضل شغال عادي، لكن مش هيقدر يحدّث الكود بالذكاء الاصطناعي.')],
    [t('كيف تعمل النقاط؟'), t('كل بناء أو تعديل بالذكاء الاصطناعي يستهلك نقاطاً حسب حجمه. الموقع الكبير ممكن يستهلك نقاط الشهر كلها قبل ما يخلص، والتعديلات الصغيرة تستهلك نقاطاً قليلة، ولا يُخصم شيء لو فشل الطلب.')],
    [t('هل أستطيع ربط دومين خاص؟'), t('نعم، بعد تفعيل الاستضافة تقدر تربط دومينك الخاص مع شهادة HTTPS مجانية.')],
  ]

  return (
    <div className="overflow-x-clip bg-white">
      <header className={`fixed inset-x-0 top-0 z-40 transition ${scrolled || menu ? 'border-b border-slate-200/70 bg-white/85 backdrop-blur-xl' : ''}`}>
        <div className="mx-auto flex h-16 max-w-6xl items-center gap-4 px-4 sm:px-6">
          <Logo />
          <nav className="ms-8 hidden items-center gap-1 lg:flex">
            {links.map(([href, label]) =>
              href.startsWith('/') ? (
                <Link key={href} to={href} className="rounded-lg px-3 py-2 text-sm font-medium text-slate-600 transition hover:text-slate-900">
                  {label}
                </Link>
              ) : (
                <a key={href} href={href} className="rounded-lg px-3 py-2 text-sm font-medium text-slate-600 transition hover:text-slate-900">
                  {label}
                </a>
              ),
            )}
          </nav>
          <div className="ms-auto flex items-center gap-2">
            <ThemeToggle />
            <LanguageSwitcher compact />
            {me ? (
              <Link to="/app" className="inline-flex h-9 items-center rounded-xl bg-ink px-4 text-sm font-semibold text-white transition hover:bg-slate-800">
                {t('لوحة التحكم')}
              </Link>
            ) : (
              <>
                <Link to="/login" className="hidden h-9 items-center rounded-xl px-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-100 sm:inline-flex">
                  {t('تسجيل الدخول')}
                </Link>
                <Link to="/register" className="inline-flex h-9 shrink-0 items-center rounded-xl bg-ink px-3 text-sm font-semibold whitespace-nowrap text-white transition hover:bg-slate-800 sm:px-4">
                  {t('ابدأ مجاناً')}
                </Link>
              </>
            )}
            <button onClick={() => setMenu((m) => !m)} className="grid h-9 w-9 place-items-center rounded-xl text-slate-700 hover:bg-slate-100 lg:hidden" aria-label={t('القائمة')}>
              {menu ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
            </button>
          </div>
        </div>
        {menu && (
          <div className="animate-fade-up border-t border-slate-100 px-4 pb-5 pt-2 lg:hidden">
            {links.map(([href, label]) =>
              href.startsWith('/') ? (
                <Link key={href} to={href} onClick={() => setMenu(false)} className="block rounded-xl px-3 py-3 text-base font-medium text-slate-700 hover:bg-slate-50">
                  {label}
                </Link>
              ) : (
                <a key={href} href={href} onClick={() => setMenu(false)} className="block rounded-xl px-3 py-3 text-base font-medium text-slate-700 hover:bg-slate-50">
                  {label}
                </a>
              ),
            )}
            <div className="mt-2 flex items-center justify-between border-t border-slate-100 pt-3">
              <div className="flex items-center gap-1">
                <ThemeToggle />
                <LanguageSwitcher />
              </div>
              {!me && (
                <Link to="/login" className="rounded-xl px-3 py-2 text-sm font-semibold text-slate-700">
                  {t('تسجيل الدخول')}
                </Link>
              )}
            </div>
          </div>
        )}
      </header>

      {/* Hero */}
      <section className="relative isolate pt-28 sm:pt-36">
        <div className="bg-grid absolute inset-0 -z-10 [mask-image:radial-gradient(ellipse_at_top,black_30%,transparent_70%)]" />
        <div className="absolute -top-24 start-1/2 -z-10 h-[28rem] w-[28rem] -translate-x-1/2 animate-float rounded-full bg-brand-400/30 blur-3xl rtl:translate-x-1/2" />
        <div className="absolute top-40 -start-20 -z-10 h-72 w-72 animate-float rounded-full bg-fuchsia-400/25 blur-3xl [animation-delay:-3s]" />
        <div className="absolute top-20 -end-16 -z-10 h-80 w-80 animate-float rounded-full bg-sky-300/30 blur-3xl [animation-delay:-5s]" />

        <div className="mx-auto max-w-4xl px-4 text-center sm:px-6">
          <span className="inline-flex animate-fade-up items-center gap-2 rounded-full border border-brand-200/70 bg-white/70 px-3.5 py-1.5 text-xs font-semibold text-brand-700 shadow-sm backdrop-blur sm:text-sm">
            <Sparkles className="h-4 w-4" />
            {t('بدون برمجة • موقعك الأول مجاناً')}
          </span>
          <h1 className="mt-6 animate-fade-up text-4xl font-extrabold tracking-tight text-ink [animation-delay:80ms] sm:text-6xl lg:text-7xl">
            {t('حوّل فكرتك إلى')}
            <br />
            <span className="text-gradient">{t('موقع جاهز في دقيقة')}</span>
          </h1>
          <p className="mx-auto mt-6 max-w-2xl animate-fade-up text-base text-slate-600 [animation-delay:160ms] sm:text-lg">
            {t('Casco Studio يبني لك أي موقع بالذكاء الاصطناعي: مواقع تعريفية ومتاجر وحجز مواعيد ومنصات كورسات — بالواجهة والباك إند والاستضافة، وبأي لغة.')}
          </p>

          <div className="mx-auto mt-10 max-w-2xl animate-fade-up rounded-3xl bg-gradient-to-br from-brand-500/40 via-violet-500/20 to-fuchsia-500/40 p-px shadow-2xl shadow-brand-900/10 [animation-delay:240ms]">
            <div className="rounded-[calc(1.5rem-1px)] bg-white p-2.5 text-start sm:p-3">
              {picks.length > 0 && (
                <div className="flex flex-wrap gap-2 px-1 pb-2">
                  {picks.map((p) => (
                    <div key={p.url} className="relative">
                      <MediaThumb src={p.url} video={p.video} className="h-14 w-14 rounded-lg ring-1 ring-slate-200" />
                      <button
                        type="button"
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
              <textarea
                value={prompt}
                onChange={(e) => setPrompt(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) start()
                }}
                rows={3}
                placeholder={t('صف موقعك... مثال: موقع لمطعم مشويات في دبي فيه المنيو وحجز طاولات ورقم واتساب')}
                className="w-full resize-none rounded-2xl bg-transparent p-3 text-base text-slate-900 outline-none placeholder:text-slate-400"
              />
              {fileError && <p className="px-3 pb-1 text-sm text-red-600">{fileError}</p>}
              <div className="flex items-center gap-2">
                <label
                  className="grid h-12 w-12 shrink-0 cursor-pointer place-items-center rounded-2xl text-slate-500 transition hover:bg-slate-100 hover:text-slate-800"
                  title={t('إرفاق صور أو فيديو من جهازك')}
                >
                  <Paperclip className="h-5 w-5" />
                  <input
                    type="file"
                    accept={MEDIA_ACCEPT}
                    multiple
                    className="hidden"
                    onChange={(e) => {
                      addFiles(e.target.files ?? [])
                      e.target.value = ''
                    }}
                  />
                </label>
                <button
                  onClick={start}
                  className="ms-auto inline-flex h-12 min-w-0 flex-1 items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-brand-600 via-violet-600 to-fuchsia-500 px-6 font-semibold text-white shadow-lg shadow-brand-600/30 transition hover:brightness-110 active:scale-[.98] sm:flex-none"
                >
                  {t('ابنِ موقعي')}
                  <ArrowRight className="flip-rtl h-4 w-4" />
                </button>
              </div>
            </div>
          </div>
          <div className="mt-6 flex flex-wrap items-center justify-center gap-x-6 gap-y-2 text-sm text-slate-500">
            {[t('بدون بطاقة ائتمان'), t('عربي • English • हिन्दी'), t('استضافة واستضافة الباك إند')].map((x) => (
              <span key={x} className="inline-flex items-center gap-1.5">
                <Check className="h-4 w-4 text-emerald-500" />
                {x}
              </span>
            ))}
          </div>
        </div>

        <ProductPreview />
      </section>

      {/* Features */}
      <section id="features" className="scroll-mt-20 py-24">
        <div className="mx-auto max-w-6xl px-4 sm:px-6">
          <SectionTitle eyebrow={t('المميزات')} title={t('كل ما تحتاجه لإطلاق موقعك')} text={t('من الفكرة إلى موقع منشور على دومينك، بدون مبرمجين وبدون أدوات معقدة.')} />
          <div className="mt-14 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {features.map((f) => (
              <div key={f.title} className="group rounded-3xl border border-slate-200/80 bg-white p-6 transition hover:-translate-y-0.5 hover:border-brand-200 hover:shadow-xl hover:shadow-brand-900/5">
                <div className="grid h-11 w-11 place-items-center rounded-2xl bg-gradient-to-br from-brand-50 to-fuchsia-50 text-brand-600 ring-1 ring-brand-100 transition group-hover:scale-105">
                  <f.icon className="h-5 w-5" />
                </div>
                <h3 className="mt-5 text-lg font-semibold text-ink">{f.title}</h3>
                <p className="mt-2 text-sm leading-relaxed text-slate-600">{f.text}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* How it works */}
      <section className="bg-slate-50 py-24">
        <div className="mx-auto max-w-6xl px-4 sm:px-6">
          <SectionTitle eyebrow={t('كيف يعمل؟')} title={t('ثلاث خطوات فقط')} />
          <div className="mt-14 grid gap-4 md:grid-cols-3">
            {[
              [t('اكتب وصف موقعك'), t('بأي لغة، زي ما تشرح لمصمم: النشاط، الأقسام، الألوان وطريقة التواصل.')],
              [t('شاهده يُبنى أمامك'), t('الذكاء الاصطناعي يكتب التصميم والمحتوى والكود مباشرة، وتعدّل بالكلام.')],
              [
                t('انشر بضغطة'),
                t('فعّل الاستضافة ({static} شهرياً، أو {backend} لو فيه باك إند) وانشر على رابط جاهز أو دومينك.', {
                  static: usd(plans?.hosting.staticMonthly),
                  backend: usd(plans?.hosting.backendMonthly),
                }),
              ],
            ].map(([title, text], i) => (
              <div key={title} className="relative rounded-3xl bg-white p-7 shadow-sm ring-1 ring-slate-200/70">
                <span className="text-gradient text-5xl font-extrabold">{i + 1}</span>
                <h3 className="mt-4 text-lg font-semibold text-ink">{title}</h3>
                <p className="mt-2 text-sm leading-relaxed text-slate-600">{text}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Use cases */}
      <section id="templates" className="scroll-mt-20 py-24">
        <div className="mx-auto max-w-6xl px-4 sm:px-6">
          <SectionTitle eyebrow={t('ماذا تبني؟')} title={t('من صفحة بسيطة إلى منصة كاملة')} />
          <div className="mt-14 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {useCases.map((u) => (
              <div key={u.title} className="rounded-3xl border border-slate-200/80 p-6 transition hover:border-slate-300 hover:shadow-lg hover:shadow-slate-900/5">
                <div className="flex items-center justify-between">
                  <div className="grid h-11 w-11 place-items-center rounded-2xl bg-slate-900 text-white">
                    <u.icon className="h-5 w-5" />
                  </div>
                  <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${u.pro ? 'bg-brand-50 text-brand-700 ring-1 ring-brand-200' : 'bg-emerald-50 text-emerald-700 ring-1 ring-emerald-200'}`}>
                    {u.pro ? 'Pro' : t('مجاني')}
                  </span>
                </div>
                <h3 className="mt-5 text-lg font-semibold text-ink">{u.title}</h3>
                <p className="mt-2 text-sm leading-relaxed text-slate-600">{u.text}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Pricing */}
      <section id="pricing" className="scroll-mt-20 bg-ink py-24 text-white">
        <div className="mx-auto max-w-6xl px-4 sm:px-6">
          <SectionTitle dark eyebrow={t('الأسعار')} title={t('أسعار بسيطة وواضحة')} text={t('العميل يدفع 18$ أو 180$ مرة واحدة مقابل كود موقعه ومهمة بنائه.')} />
          <div className="mt-14 grid gap-4 lg:grid-cols-3">
            <PriceCard
              title={t('مجاني')}
              price="$0"
              note={t('للأبد')}
              points={[t('طلبان للذكاء الاصطناعي'), t('متجر أو حجوزات أو نموذج تواصل'), t('معاينة كاملة وشاهد الكود وهو يُكتب'), t('انشره بدفع الاستضافة فقط')]}
              action={
                <button onClick={start} className="mt-8 h-11 w-full rounded-xl bg-white/10 font-semibold text-white ring-1 ring-white/15 transition hover:bg-white/15">
                  {t('ابدأ مجاناً')}
                </button>
              }
            />
            <PriceCard
              featured
              title="Pro"
              price={usd(plans?.pro.monthlyPrice)}
              period={t('مرة واحدة')}
              note={t('أو {price} مرة واحدة', { price: usd(plans?.pro.yearlyPrice) }) + (yearlySaving ? ` (${yearlySaving})` : '')}
              explain={t('المبلغ ده مرة واحدة بس، مقابل كود موقعك ومهمة بنائه. مش هيتدفع تاني عشان موقعك يفضل شغال. موقعك هيفضل شغال عادي، لكن مش هتقدر تحدّث الكود بالذكاء الاصطناعي.')}
              points={[
                t('التعديل على مواقعك بالكلام في أي وقت'),
                t('مواقع متعددة الصفحات'),
                t('منصات كورسات وإعلانات كاملة'),
                t('{n} نقطة شهرياً + وضع الذكاء القوي', { n: num(plans?.pro.monthlyCredits ?? 5000) }),
                t('تنزيل كود موقعك كاملاً'),
                t('الدفع مرة واحدة لبناء الموقع'),
              ]}
              action={
                <button onClick={start} className="mt-8 h-11 w-full rounded-xl bg-white font-semibold text-ink transition hover:bg-slate-100">
                  {t('ابدأ الآن')}
                </button>
              }
            />
            <PriceCard
              title={t('الاستضافة (لكل موقع)')}
              price={usd(plans?.hosting.staticMonthly)}
              period={t('/شهرياً')}
              note={t('موقع + باك إند: {price} شهرياً', { price: usd(plans?.hosting.backendMonthly) })}
              points={[t('نشر على سيرفرات Casco + HTTPS'), t('ربط دومينك الخاص'), t('الباك إند: حسابات، قاعدة بيانات، طلبات، حجوزات'), t('بدون تجديد يتوقف الموقع'), t('لا يوجد تجديد تلقائي — التجديد يدوي')]}
            />
          </div>
        </div>
      </section>

      {/* FAQ */}
      <section id="faq" className="scroll-mt-20 py-24">
        <div className="mx-auto max-w-3xl px-4 sm:px-6">
          <SectionTitle eyebrow={t('الأسئلة الشائعة')} title={t('عندك سؤال؟')} />
          <div className="mt-12 divide-y divide-slate-200 rounded-3xl border border-slate-200">
            {faqs.map(([q, a]) => (
              <details key={q} className="group px-6 py-5 [&_summary::-webkit-details-marker]:hidden">
                <summary className="flex cursor-pointer list-none items-center justify-between gap-4 font-semibold text-ink">
                  {q}
                  <ChevronDown className="h-5 w-5 shrink-0 text-slate-400 transition group-open:rotate-180" />
                </summary>
                <p className="mt-3 text-sm leading-relaxed text-slate-600">{a}</p>
              </details>
            ))}
          </div>
        </div>
      </section>

      {/* CTA */}
      <section className="px-4 pb-24 sm:px-6">
        <div className="relative mx-auto max-w-6xl overflow-hidden rounded-[2rem] bg-gradient-to-br from-brand-600 via-violet-600 to-fuchsia-600 px-6 py-16 text-center text-white sm:px-12">
          <div className="bg-grid absolute inset-0 opacity-20" />
          <h2 className="relative text-3xl font-extrabold tracking-tight sm:text-5xl">{t('جاهز تبني موقعك؟')}</h2>
          <p className="relative mx-auto mt-4 max-w-xl text-white/80">{t('اكتب فكرتك الآن، وشاهد موقعك يجهز خلال دقيقة.')}</p>
          <button onClick={start} className="relative mt-8 inline-flex h-12 items-center gap-2 rounded-2xl bg-white px-7 font-semibold text-brand-700 shadow-xl transition hover:bg-slate-50">
            {t('ابدأ مجاناً')}
            <ArrowRight className="flip-rtl h-4 w-4" />
          </button>
        </div>
      </section>

      <footer className="border-t border-slate-200">
        <div className="mx-auto flex max-w-6xl flex-col items-center justify-between gap-4 px-4 py-8 sm:flex-row sm:px-6">
          <div className="flex items-center gap-3">
            <Logo />
            <span className="text-sm text-slate-500">{t('ابنِ أي موقع بالذكاء الاصطناعي')}</span>
          </div>
          <div className="flex items-center gap-4">
            <div className="flex items-center gap-1">
              <ThemeToggle />
              <LanguageSwitcher />
            </div>
            <Link to="/download" className="text-sm text-slate-500 underline-offset-2 hover:underline">{t('تنزيل البرنامج')}</Link>
            <a href="/pricing" className="text-sm text-slate-500 underline-offset-2 hover:underline">{t('دفع بناء الموقع مرة واحدة')}</a>
            <a href="/owner" className="text-sm text-slate-500 underline-offset-2 hover:underline">{t('مالك Casco: ناصر مصطفي البربري')}</a>
            <span className="text-sm text-slate-500">© {new Date().getFullYear()} Casco Studio</span>
          </div>
        </div>
      </footer>
    </div>
  )
}

function SectionTitle({ eyebrow, title, text, dark = false }: { eyebrow: string; title: string; text?: string; dark?: boolean }) {
  return (
    <div className="mx-auto max-w-2xl text-center">
      <p className={`text-sm font-semibold uppercase tracking-wider ${dark ? 'text-brand-300' : 'text-brand-600'}`}>{eyebrow}</p>
      <h2 className={`mt-3 text-3xl font-extrabold tracking-tight sm:text-5xl ${dark ? 'text-white' : 'text-ink'}`}>{title}</h2>
      {text && <p className={`mt-4 text-base sm:text-lg ${dark ? 'text-slate-400' : 'text-slate-600'}`}>{text}</p>}
    </div>
  )
}

function PriceCard({
  title,
  price,
  period,
  note,
  explain,
  points,
  action,
  featured = false,
}: {
  title: string
  price: string
  period?: string
  note?: string
  explain?: string
  points: string[]
  action?: ReactNode
  featured?: boolean
}) {
  return (
    <div className={`relative flex flex-col rounded-3xl p-8 ${featured ? 'bg-gradient-to-b from-brand-600 to-violet-700 shadow-2xl shadow-brand-600/30 ring-1 ring-white/20 lg:-my-4 lg:py-12' : 'bg-white/[.04] ring-1 ring-white/10'}`}>
      {featured && <span className="absolute -top-3 start-8 rounded-full bg-white px-3 py-1 text-xs font-bold text-brand-700 shadow">{t('الأكثر اختياراً')}</span>}
      <h3 className="text-lg font-semibold">{title}</h3>
      <p className="mt-4 flex items-baseline gap-1">
        <span className="text-5xl font-extrabold tracking-tight">{price}</span>
        {period && <span className="text-sm text-white/70">{period}</span>}
      </p>
      {note && <p className={`mt-1 text-sm ${featured ? 'text-white/80' : 'text-slate-400'}`}>{note}</p>}
      {explain && <p className={`mt-4 text-sm leading-relaxed ${featured ? 'text-white' : 'text-slate-300'}`}>{explain}</p>}
      <ul className="mt-8 flex-1 space-y-3 text-sm">
        {points.map((p) => (
          <li key={p} className="flex gap-3">
            <Check className={`mt-0.5 h-4 w-4 shrink-0 ${featured ? 'text-white' : 'text-brand-300'}`} />
            <span className={featured ? 'text-white/90' : 'text-slate-300'}>{p}</span>
          </li>
        ))}
      </ul>
      {action}
    </div>
  )
}

/** Static mock of the editor: chat on one side, a site preview on the other. */
function ProductPreview() {
  return (
    <div className="mx-auto mt-16 max-w-5xl px-4 sm:mt-20 sm:px-6">
      <div className="rounded-[1.75rem] bg-gradient-to-b from-slate-200/80 to-slate-100/40 p-2 shadow-2xl shadow-brand-900/10 ring-1 ring-slate-200/60 sm:p-3">
        <div className="overflow-hidden rounded-2xl bg-white ring-1 ring-slate-200">
          <div className="flex items-center gap-2 border-b border-slate-100 px-4 py-3">
            <span className="h-3 w-3 rounded-full bg-red-400" />
            <span className="h-3 w-3 rounded-full bg-amber-400" />
            <span className="h-3 w-3 rounded-full bg-emerald-400" />
            <div className="mx-auto hidden rounded-lg bg-slate-100 px-10 py-1 text-xs text-slate-400 sm:block" dir="ltr">
              your-restaurant.casco.studio
            </div>
          </div>
          <div className="grid md:grid-cols-[18rem_1fr]">
            <div className="hidden space-y-3 border-e border-slate-100 bg-slate-50/60 p-4 md:block">
              <div className="ms-auto w-fit max-w-[90%] rounded-2xl rounded-se-sm bg-brand-600 px-3.5 py-2 text-xs text-white">{t('موقع لمطعم مشويات مع المنيو وحجز طاولات')}</div>
              <div className="w-fit max-w-[90%] rounded-2xl rounded-ss-sm bg-white px-3.5 py-2 text-xs text-slate-700 ring-1 ring-slate-200">
                {t('جهزت لك الموقع: هيدر بصورة، المنيو بالأسعار، نموذج حجز، وزر واتساب.')}
              </div>
              <div className="ms-auto w-fit max-w-[90%] rounded-2xl rounded-se-sm bg-brand-600 px-3.5 py-2 text-xs text-white">{t('خلي الألوان أحمر وذهبي')}</div>
              <div className="flex w-fit items-center gap-2 rounded-2xl bg-white px-3.5 py-2 text-xs text-slate-500 ring-1 ring-slate-200">
                <span className="h-2 w-2 animate-pulse rounded-full bg-emerald-500" />
                {t('جاري التعديل...')}
              </div>
            </div>
            <div className="p-3 sm:p-5">
              <div className="overflow-hidden rounded-xl bg-gradient-to-br from-red-700 via-red-600 to-amber-500 p-6 text-white sm:p-10">
                <div className="flex items-center justify-between text-[10px] opacity-90 sm:text-xs">
                  <span className="font-bold">🔥 Grill House</span>
                  <span className="hidden gap-4 sm:flex">
                    <span>Menu</span>
                    <span>Book</span>
                    <span>Contact</span>
                  </span>
                </div>
                <p className="mt-8 text-xl font-extrabold sm:text-3xl">{t('أشهى المشويات في المدينة')}</p>
                <p className="mt-2 text-xs opacity-80 sm:text-sm">{t('احجز طاولتك الآن أو اطلب عبر واتساب')}</p>
                <div className="mt-5 flex gap-2">
                  <span className="rounded-lg bg-white px-3 py-1.5 text-[10px] font-bold text-red-700 sm:text-xs">{t('احجز طاولة')}</span>
                  <span className="rounded-lg bg-white/20 px-3 py-1.5 text-[10px] font-bold sm:text-xs">{t('المنيو')}</span>
                </div>
              </div>
              <div className="mt-3 grid grid-cols-3 gap-3">
                {['from-amber-200 to-orange-300', 'from-rose-200 to-red-300', 'from-yellow-200 to-amber-300'].map((g) => (
                  <div key={g} className="rounded-xl ring-1 ring-slate-100">
                    <div className={`h-14 rounded-t-xl bg-gradient-to-br sm:h-20 ${g}`} />
                    <div className="space-y-1.5 p-2">
                      <div className="h-2 w-3/4 rounded bg-slate-200" />
                      <div className="h-2 w-1/2 rounded bg-slate-100" />
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
