/* Shared script. Each page sets <body data-page="..."> and runs its init function (see `pages` at the bottom). */
(() => {
  'use strict';
  let C;

  /* ---------- Site data (edit freely) ---------- */
  const CATEGORIES = [['سيارات', '🚗'], ['عقارات', '🏠'], ['إلكترونيات', '📱'], ['أثاث', '🛋️'], ['وظائف', '💼'], ['خدمات', '🛠️'], ['حيوانات', '🐾'], ['أخرى', '📦']];
  const CITIES = ['دبي', 'أبوظبي', 'الشارقة', 'عجمان', 'رأس الخيمة', 'الفجيرة', 'أم القيوين', 'أخرى'];
  const CURRENCIES = ['AED', 'SAR', 'EGP', 'USD'];
  const PAGE_SIZE = 12, MAX_IMAGES = 5;
  const IMG = '{{STOCK}}/800x500/marketplace-shopping';

  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const e = (s) => C.util.escape(s == null ? '' : String(s));
  const url = (u) => (/^https?:\/\//i.test(u || '') ? String(u).trim() : '');
  const items = (r) => (r && r.items) || [];
  const fail = (err) => C.util.toast((err && err.message) || 'حدث خطأ غير متوقع', 'error');
  const icon = (cat) => ((CATEGORIES.find((c) => c[0] === cat) || [])[1] || '📦');
  const date = (iso) => (iso ? C.util.date(iso) : '');

  /* ---------- UI helpers ---------- */
  const skeletons = (n) => '<div class="bg-white rounded-2xl shadow-sm animate-pulse"><div class="aspect-[4/3] bg-slate-200 rounded-t-2xl"></div><div class="p-4 space-y-3"><div class="h-5 bg-slate-200 rounded w-1/2"></div><div class="h-4 bg-slate-200 rounded"></div></div></div>'.repeat(n);
  const stateBox = (ic, title, text, extra = '') =>
    `<div class="col-span-full text-center py-16 px-4 bg-white rounded-2xl shadow-sm"><div class="text-5xl mb-3">${ic}</div><h3 class="font-bold text-lg mb-1">${title}</h3><p class="text-slate-500">${text}</p>${extra}</div>`;
  const errorBox = () => stateBox('⚠️', 'تعذر تحميل الإعلانات', 'يرجى المحاولة مرة أخرى لاحقاً.');
  const btnLink = (href, label) => `<a href="${href}" class="inline-block mt-5 bg-primary hover:bg-primary-dark text-white font-bold px-6 py-3 rounded-xl">${label}</a>`;
  const BADGES = { approved: ['منشور', 'bg-green-100 text-green-700'], pending: ['قيد المراجعة', 'bg-amber-100 text-amber-700'], rejected: ['مرفوض', 'bg-red-100 text-red-700'] };
  const badge = (s) => { const [t, c] = BADGES[s] || [s, 'bg-slate-100 text-slate-600']; return `<span class="${c} text-xs font-bold px-3 py-1 rounded-full">${e(t)}</span>`; };

  const adCard = (ad) => `
    <a href="ad.html?id=${encodeURIComponent(ad.id)}" class="group bg-white rounded-2xl shadow-sm hover:shadow-xl transition overflow-hidden flex flex-col">
      <div class="relative overflow-hidden">
        <img src="${e(url(ad.image) || IMG)}" alt="${e(ad.title)}" loading="lazy" class="w-full aspect-[4/3] object-cover group-hover:scale-105 transition duration-500">
        ${ad.category ? `<span class="absolute top-3 right-3 bg-white/90 text-xs font-bold px-2.5 py-1 rounded-full">${icon(ad.category)} ${e(ad.category)}</span>` : ''}
      </div>
      <div class="p-4 flex flex-col flex-1">
        <div class="text-lg font-extrabold text-primary mb-1">${e(C.util.money(ad.price, ad.currency))}</div>
        <h3 class="font-semibold line-clamp-2 mb-3 group-hover:text-primary">${e(ad.title)}</h3>
        <div class="mt-auto flex justify-between text-xs text-slate-500"><span>📍 ${e(ad.city || '—')}</span><span>${e(date(ad.createdAt))}</span></div>
      </div>
    </a>`;

  /* Shows skeletons, loads a list, renders it with `card` or shows the empty/error state. */
  async function fillGrid(grid, n, load, card, empty) {
    grid.innerHTML = skeletons(n);
    try {
      const r = (await load()) || {};
      grid.innerHTML = items(r).map((x) => card(x)).join('') || empty;
      return r;
    } catch (err) { grid.innerHTML = errorBox(); fail(err); return null; }
  }

  /* Appends options to <select data-fill="categories|cities|currencies">, keeping any existing first option. */
  function fillSelects() {
    const src = { categories: CATEGORIES.map((c) => c[0]), cities: CITIES, currencies: CURRENCIES };
    $$('select[data-fill]').forEach((s) => (src[s.dataset.fill] || []).forEach((v) => s.add(new Option(v, v))));
  }

  /* ---------- Header: mobile menu + auth state ---------- */
  function initMenu() {
    const btn = $('[data-menu-btn]'), menu = $('[data-mobile-menu]');
    if (!btn || !menu) return;
    btn.addEventListener('click', () => {
      const open = menu.classList.contains('hidden');
      menu.classList.toggle('hidden', !open);
      menu.classList.toggle('flex', open);
      btn.setAttribute('aria-expanded', open);
    });
  }

  function initAuthNav() {
    const u = C.auth.user();
    const html = u
      ? `<span class="text-slate-600">👋 ${e(u.name)}</span><button type="button" data-logout class="text-red-600 hover:underline">خروج</button>`
      : '<a href="login.html" class="hover:text-primary">دخول</a><a href="register.html" class="hover:text-primary">حساب جديد</a>';
    $$('[data-auth-nav]').forEach((el) => { el.innerHTML = html; });
    $$('[data-logout]').forEach((b) => b.addEventListener('click', () => {
      const go = () => { location.href = 'index.html'; };
      Promise.resolve(C.auth.logout()).then(go, go);
    }));
  }

  /* ---------- Pages ---------- */
  function home() {
    $('#cats').innerHTML = CATEGORIES.map(([name, ic]) => `
      <a href="search.html?category=${encodeURIComponent(name)}" class="bg-white rounded-2xl p-5 text-center shadow-sm hover:shadow-lg hover:-translate-y-1 transition">
        <div class="text-4xl mb-2">${ic}</div><div class="font-bold">${name}</div></a>`).join('');
    fillGrid($('#latest'), 8, () => C.ads.list({ page: 1, pageSize: 8 }), adCard,
      stateBox('📭', 'لا توجد إعلانات بعد', 'كن أول من ينشر إعلاناً!', btnLink('post.html', '+ أضف إعلانك')));
  }

  async function searchPage() {
    const form = $('#filters'), query = {};
    const page = Math.max(1, parseInt(C.util.qs('page'), 10) || 1);
    ['q', 'category', 'city'].forEach((k) => {
      const v = C.util.qs(k) || '';
      form.elements[k].value = v;
      if (v) query[k] = v;
    });
    const r = await fillGrid($('#results'), 6, () => C.ads.list({ ...query, page, pageSize: PAGE_SIZE }), adCard,
      stateBox('🔍', 'لا توجد نتائج', 'جرّب تغيير كلمات البحث أو الفلاتر.', btnLink('search.html', 'عرض كل الإعلانات')));
    if (!r) return;
    const total = r.total || items(r).length, pages = Math.ceil(total / (r.pageSize || PAGE_SIZE));
    $('#count').textContent = total + ' إعلان';
    if (pages < 2) return;
    const btn = (p, label, on) => `<a href="search.html?${new URLSearchParams({ ...query, page: p })}" class="min-w-10 text-center px-3 py-2 rounded-xl font-semibold ${on ? 'bg-primary text-white' : 'bg-white hover:bg-slate-100 shadow-sm'}">${label}</a>`;
    let html = page > 1 ? btn(page - 1, 'السابق') : '';
    for (let p = Math.max(1, page - 2); p <= Math.min(pages, page + 2); p++) html += btn(p, p, p === page);
    $('#pager').innerHTML = html + (page < pages ? btn(page + 1, 'التالي') : '');
  }

  async function adPage() {
    const id = C.util.qs('id');
    const show = (name) => ['loading', 'error', 'content'].forEach((k) => { $('#' + k).hidden = k !== name; });
    const showError = (msg) => { $('#errorMsg').textContent = msg; show('error'); };
    const text = (sel, v) => { $(sel).textContent = v == null ? '' : v; };
    if (!id) return showError('لم يتم تحديد الإعلان.');
    let ad;
    try { ad = await C.ads.get(id); } catch (err) { return showError(err.message || 'تعذر تحميل الإعلان.'); }
    if (!ad) return showError('الإعلان غير موجود.');

    document.title = ad.title + ' | ' + document.title.split(' | ').pop();
    text('#aTitle', ad.title);
    text('#aPrice', C.util.money(ad.price, ad.currency));
    text('#aCity', '📍 ' + (ad.city || '—'));
    text('#aDate', ad.createdAt ? '🕒 ' + date(ad.createdAt) : '');
    text('#aCategory', icon(ad.category) + ' ' + ad.category);
    $('#aCategory').hidden = !ad.category;
    text('#aViews', '👁 ' + (ad.views || 0) + ' مشاهدة');
    text('#aDesc', ad.description || 'لا يوجد وصف.');
    text('#aSeller', ad.sellerName || 'معلن');

    const images = (ad.images || []).map(url).filter(Boolean);
    if (!images.length) images.push(IMG);
    const main = $('#mainImg'), thumbs = $('#thumbs');
    const pick = (i) => {
      main.src = images[i];
      $$('button', thumbs).forEach((b, j) => { b.classList.toggle('ring-primary', i === j); b.classList.toggle('ring-transparent', i !== j); });
    };
    main.alt = ad.title || '';
    if (images.length > 1) thumbs.innerHTML = images.map((src, i) => `
      <button type="button" data-i="${i}" aria-label="صورة ${i + 1}" class="shrink-0 w-20 h-16 rounded-xl overflow-hidden ring-2"><img src="${e(src)}" alt="" class="w-full h-full object-cover"></button>`).join('');
    thumbs.addEventListener('click', (ev) => { const b = ev.target.closest('[data-i]'); if (b) pick(+b.dataset.i); });
    pick(0);

    const phone = String(ad.phone || '').replace(/[^\d+]/g, '');
    const wa = String(ad.whatsapp || ad.phone || '').replace(/\D/g, '');
    $('#callBtn').hidden = !phone;
    $('#waBtn').hidden = !wa;
    $('#noContact').hidden = !!(phone || wa);
    if (phone) { $('#callBtn').href = 'tel:' + phone; text('#phoneText', phone); }
    if (wa) $('#waBtn').href = `https://wa.me/${wa}?text=${encodeURIComponent('مرحباً، بخصوص إعلانك: ' + (ad.title || ''))}`;
    show('content');
  }

  function postPage() {
    if (!C.auth.requireLogin('login.html')) return;
    const form = $('#postForm'), input = $('#images'), previews = $('#previews');
    let files = [];
    input.addEventListener('change', () => {
      files = [...(input.files || [])].filter((f) => f.type.startsWith('image/'));
      if (files.length > MAX_IMAGES) { C.util.toast(`يمكنك رفع ${MAX_IMAGES} صور كحد أقصى`, 'error'); files = files.slice(0, MAX_IMAGES); }
      previews.replaceChildren(...files.map((f) => Object.assign(document.createElement('img'), {
        src: URL.createObjectURL(f), alt: f.name, className: 'w-full aspect-square object-cover rounded-xl',
      })));
    });
    form.addEventListener('submit', async (ev) => {
      ev.preventDefault();
      const d = Object.fromEntries(new FormData(form)), btn = form.querySelector('[type=submit]'), label = btn.textContent;
      btn.disabled = true;
      btn.textContent = 'جارٍ النشر...';
      try {
        const r = (await C.ads.create({ ...d, price: Number(d.price) || 0, images: files })) || {};
        const pending = r.status === 'pending';
        $('#pendingMsg').hidden = !pending;
        C.util.toast(pending ? 'تم استلام إعلانك، إعلانك قيد المراجعة' : 'تم نشر إعلانك بنجاح 🎉');
        setTimeout(() => { location.href = 'my-ads.html'; }, pending ? 2500 : 1200);
      } catch (err) { fail(err); btn.disabled = false; btn.textContent = label; }
    });
  }

  async function myAds() {
    if (!C.auth.requireLogin('login.html')) return;
    const list = $('#list');
    const empty = () => stateBox('📭', 'ليس لديك إعلانات بعد', 'انشر إعلانك الأول الآن.', btnLink('post.html', '+ أضف إعلان'));
    const r = await fillGrid(list, 2, () => C.ads.mine(), (ad) => `
        <div data-ad class="bg-white rounded-2xl shadow-sm p-4 flex flex-col sm:flex-row sm:items-center gap-4">
          <img src="${e(url(ad.image) || IMG)}" alt="" class="w-full sm:w-32 aspect-[4/3] object-cover rounded-xl">
          <div class="flex-1 min-w-0">
            <div class="flex flex-wrap items-center gap-2 mb-1"><h3 class="font-bold truncate">${e(ad.title)}</h3>${badge(ad.status)}</div>
            <div class="text-primary font-extrabold">${e(C.util.money(ad.price, ad.currency))}</div>
            <div class="text-xs text-slate-500">${e(date(ad.createdAt))}</div>
          </div>
          <div class="flex gap-2">
            <a href="ad.html?id=${encodeURIComponent(ad.id)}" class="px-4 py-2 rounded-xl bg-slate-100 hover:bg-slate-200 font-semibold text-sm">عرض</a>
            <button type="button" data-del="${e(ad.id)}" class="px-4 py-2 rounded-xl bg-red-50 text-red-600 hover:bg-red-100 font-semibold text-sm">حذف</button>
          </div>
        </div>`, empty());
    if (!r) return;
    list.addEventListener('click', async (ev) => {
      const b = ev.target.closest('[data-del]');
      if (!b || !confirm('هل أنت متأكد من حذف هذا الإعلان؟')) return;
      b.disabled = true;
      try {
        await C.ads.remove(b.dataset.del);
        b.closest('[data-ad]').remove();
        C.util.toast('تم حذف الإعلان');
        if (!$('[data-ad]', list)) list.innerHTML = empty();
      } catch (err) { fail(err); b.disabled = false; }
    });
  }

  function authForm(kind) {
    const form = $('#authForm');
    $$('a[data-keep-next]').forEach((a) => { a.href = a.getAttribute('href') + location.search; });
    form.addEventListener('submit', async (ev) => {
      ev.preventDefault();
      const d = Object.fromEntries(new FormData(form)), btn = form.querySelector('[type=submit]'), label = btn.textContent;
      if (kind === 'register' && d.password !== d.password2) return C.util.toast('كلمتا المرور غير متطابقتين', 'error');
      btn.disabled = true;
      btn.textContent = 'جارٍ التحميل...';
      try {
        if (kind === 'login') await C.auth.login({ email: d.email, password: d.password });
        else await C.auth.register({ name: d.name, email: d.email, password: d.password });
        C.auth.redirectAfterLogin('index.html');
      } catch (err) { fail(err); btn.disabled = false; btn.textContent = label; }
    });
  }

  const pages = {
    home, search: searchPage, ad: adPage, post: postPage, 'my-ads': myAds,
    login: () => authForm('login'), register: () => authForm('register'),
  };

  document.addEventListener('DOMContentLoaded', () => {
    initMenu();
    fillSelects();
    if (!window.Casco) return;
    C = window.Casco;
    initAuthNav();
    const init = pages[document.body.dataset.page];
    if (init) init();
  });
})();
