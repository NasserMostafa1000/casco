/* Shared script. Each page sets <body data-page="..."> and runs its init function (see `pages` at the bottom). */
(() => {
  'use strict';
  let C;
  const IMG = '{{STOCK}}/800x500/online-course-laptop';
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const e = (s) => C.util.escape(s == null ? '' : String(s));
  const url = (u) => (/^https?:\/\//i.test(u || '') ? String(u).trim() : '');
  const items = (r) => (r && r.items) || [];
  const fail = (err) => C.util.toast((err && err.message) || 'حدث خطأ غير متوقع', 'error');

  /* ---------- UI helpers ---------- */
  const skeletons = (n) => '<div class="bg-white rounded-2xl shadow-sm animate-pulse"><div class="aspect-[8/5] bg-slate-200 rounded-t-2xl"></div><div class="p-5 space-y-3"><div class="h-4 bg-slate-200 rounded w-3/4"></div><div class="h-3 bg-slate-200 rounded"></div></div></div>'.repeat(n);
  const stateBox = (icon, title, text, extra = '') =>
    `<div class="col-span-full text-center py-16 px-4 bg-white rounded-2xl shadow-sm"><div class="text-5xl mb-3">${icon}</div><h3 class="font-bold text-lg mb-1">${title}</h3><p class="text-slate-500">${text}</p>${extra}</div>`;
  const errorBox = () => stateBox('⚠️', 'تعذر تحميل البيانات', 'يرجى المحاولة مرة أخرى لاحقاً.');
  const btnLink = (href, label) => `<a href="${href}" class="inline-block mt-5 bg-primary hover:bg-primary-dark text-white font-bold px-6 py-3 rounded-xl">${label}</a>`;
  const BADGES = { active: ['مفعّل', 'bg-green-100 text-green-700'], pending: ['بانتظار تأكيد الدفع', 'bg-amber-100 text-amber-700'] };
  const badge = (s) => { const [t, c] = BADGES[s] || [s, 'bg-slate-100 text-slate-600']; return `<span class="${c} text-xs font-bold px-3 py-1 rounded-full whitespace-nowrap">${e(t)}</span>`; };

  /* `tag` replaces the price (used for status badges on my-courses). */
  const courseCard = (c, tag) => `
    <a href="course.html?id=${encodeURIComponent(c.id)}" class="group bg-white rounded-2xl shadow-sm hover:shadow-xl transition overflow-hidden flex flex-col">
      <div class="relative overflow-hidden">
        <img src="${e(url(c.imageUrl) || IMG)}" alt="${e(c.title)}" loading="lazy" class="w-full aspect-[8/5] object-cover group-hover:scale-105 transition duration-500">
        ${c.category ? `<span class="absolute top-3 right-3 bg-white/90 text-primary-dark text-xs font-bold px-3 py-1 rounded-full">${e(c.category)}</span>` : ''}
      </div>
      <div class="p-5 flex flex-col flex-1">
        <h3 class="font-bold text-lg mb-2 line-clamp-2 group-hover:text-primary">${e(c.title)}</h3>
        <p class="text-slate-500 text-sm line-clamp-2 mb-4">${e(c.description)}</p>
        <div class="mt-auto flex justify-between items-center gap-2 text-sm">
          <span class="text-slate-500 truncate">${c.instructor ? '👤 ' + e(c.instructor) : ''}${c.lessonsCount ? ' · ' + e(c.lessonsCount) + ' درس' : ''}</span>
          ${tag || `<b class="text-primary whitespace-nowrap">${e(C.util.money(c.price, c.currency))}</b>`}
        </div>
      </div>
    </a>`;

  /* Shows skeletons, loads a list, renders it with `card` or shows the empty/error state. */
  async function fillGrid(grid, n, load, card, empty) {
    grid.innerHTML = skeletons(n);
    try {
      const list = items(await load());
      grid.innerHTML = list.map(card).join('') || empty;
      return list;
    } catch (err) { grid.innerHTML = errorBox(); fail(err); return null; }
  }

  function videoEmbed(link) {
    let u = url(link), m;
    if (!u) return '';
    if (/\.mp4(\?|#|$)/i.test(u)) return `<video controls class="w-full h-full bg-black" src="${e(u)}"></video>`;
    if ((m = u.match(/youtube\.com\/watch\?(?:.*&)?v=([\w-]+)/) || u.match(/youtu\.be\/([\w-]+)/))) u = 'https://www.youtube.com/embed/' + m[1];
    else if ((m = u.match(/vimeo\.com\/(?:video\/)?(\d+)/))) u = 'https://player.vimeo.com/video/' + m[1];
    return `<iframe src="${e(u)}" class="w-full h-full" title="مشغل الفيديو" allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen></iframe>`;
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
      : '<a href="login.html" class="hover:text-primary">تسجيل الدخول</a><a href="register.html" class="bg-primary hover:bg-primary-dark text-white px-4 py-2 rounded-xl">حساب جديد</a>';
    $$('[data-auth-nav]').forEach((el) => { el.innerHTML = html; });
    $$('[data-logout]').forEach((b) => b.addEventListener('click', () => {
      const go = () => { location.href = 'index.html'; };
      Promise.resolve(C.auth.logout()).then(go, go);
    }));
  }

  /* ---------- Pages ---------- */
  function home() {
    fillGrid($('#featured'), 6, async () => ({ items: items(await C.courses.list()).slice(0, 6) }),
      (c) => courseCard(c), stateBox('📚', 'لا توجد كورسات بعد', 'ترقبوا إطلاق كورسات جديدة قريباً.'));
  }

  async function coursesPage() {
    const grid = $('#grid'), input = $('#search'), chips = $('#chips');
    let cat = C.util.qs('category') || '';
    input.value = C.util.qs('q') || '';
    const render = () => {
      const q = input.value.trim().toLowerCase();
      const list = all.filter((c) => (!cat || c.category === cat) &&
        (!q || [c.title, c.description, c.instructor, c.category].join(' ').toLowerCase().includes(q)));
      grid.innerHTML = list.map((c) => courseCard(c)).join('')
        || stateBox('🔍', 'لا توجد نتائج', all.length ? 'جرّب كلمة بحث أو تصنيفاً مختلفاً.' : 'لم تتم إضافة كورسات بعد.');
      $('#count').textContent = list.length + ' كورس';
      $$('button', chips).forEach((b) => {
        const on = b.dataset.cat === cat;
        b.className = 'px-4 py-2 rounded-full text-sm font-semibold border ' + (on ? 'bg-primary text-white border-primary' : 'bg-white border-slate-200 hover:border-primary');
        b.setAttribute('aria-pressed', on);
      });
    };
    const all = await fillGrid(grid, 6, () => C.courses.list(), () => '', '');
    if (!all) return;
    const cats = [...new Set(all.map((c) => c.category).filter(Boolean))];
    chips.innerHTML = ['', ...cats].map((c) => `<button type="button" data-cat="${e(c)}">${c ? e(c) : 'الكل'}</button>`).join('');
    chips.addEventListener('click', (ev) => { const b = ev.target.closest('[data-cat]'); if (b) { cat = b.dataset.cat; render(); } });
    input.addEventListener('input', render);
    render();
  }

  async function coursePage() {
    const id = C.util.qs('id');
    let course;
    const show = (name) => ['loading', 'error', 'content'].forEach((k) => { $('#' + k).hidden = k !== name; });
    const showError = (msg) => { $('#errorMsg').textContent = msg; show('error'); };
    const text = (sel, v) => { $(sel).textContent = v == null ? '' : v; };

    const renderAction = () => {
      const st = course.enrollment && course.enrollment.status, link = url(course.paymentLink), pay = $('#payBtn');
      $('#notice').hidden = st !== 'pending';
      pay.hidden = !link;
      if (link) pay.href = link;
      $('#action').innerHTML = st === 'active'
        ? '<span class="bg-white/20 font-bold px-4 py-2 rounded-xl">✓ أنت مشترك</span><button type="button" data-start class="bg-white text-primary-dark font-bold px-6 py-3 rounded-xl hover:bg-slate-100">ابدأ التعلم</button>'
        : st === 'pending' ? '<span class="bg-white/20 font-bold px-4 py-2 rounded-xl">⏳ بانتظار التفعيل</span>'
        : `<button type="button" data-enroll class="bg-white text-primary-dark font-bold px-6 py-3 rounded-xl shadow-lg hover:bg-slate-100">${course.price ? 'اشترك الآن' : 'اشترك مجاناً'}</button>`;
    };

    const render = () => {
      const c = course, lessons = c.lessons || [];
      document.title = c.title + ' | ' + document.title.split(' | ').pop();
      text('#cTitle', c.title);
      text('#cDesc', c.description);
      text('#cCategory', c.category);
      $('#cCategory').hidden = !c.category;
      text('#cInstructor', c.instructor ? '👤 ' + c.instructor : '');
      text('#cLessons', '📚 ' + lessons.length + ' درس');
      text('#cPrice', C.util.money(c.price, c.currency));
      $('#cImage').src = url(c.imageUrl) || IMG;
      $('#cImage').alt = c.title || '';
      renderAction();
      $('#lessons').innerHTML = lessons.map((l, i) => `
        <li><button type="button" data-lesson="${i}" class="w-full flex items-center gap-3 p-4 text-right hover:bg-slate-50 ${l.locked ? 'opacity-60' : ''}">
          <span class="w-8 h-8 shrink-0 rounded-full flex items-center justify-center text-sm font-bold ${l.locked ? 'bg-slate-100' : 'bg-primary/10 text-primary'}">${l.locked ? '🔒' : i + 1}</span>
          <span class="flex-1 min-w-0"><span class="block font-semibold truncate">${e(l.title)}</span>
            <span class="text-xs text-slate-500">${l.durationMinutes ? e(l.durationMinutes) + ' دقيقة ' : ''}${l.isFreePreview ? '<b class="text-green-600">معاينة مجانية</b>' : ''}</span></span>
          ${l.locked ? '' : '<span class="text-primary">▶</span>'}
        </button></li>`).join('') || '<li class="p-6 text-center text-slate-500">لم تتم إضافة دروس بعد.</li>';
    };

    const load = async () => {
      try { course = await C.courses.get(id); } catch (err) { return showError(err.message || 'تعذر تحميل الكورس.'); }
      if (!course) return showError('الكورس غير موجود.');
      render();
      show('content');
    };

    const play = (i) => {
      const l = course.lessons[i], media = $('#playerMedia');
      if (!l) return;
      if (l.locked) return C.util.toast('هذا الدرس متاح للمشتركين فقط 🔒');
      const video = videoEmbed(l.videoUrl);
      media.className = 'aspect-video bg-black';
      media.innerHTML = video;
      media.hidden = !video;
      text('#playerTitle', l.title);
      text('#playerContent', l.content || (video ? '' : 'لا يوجد محتوى لهذا الدرس بعد.'));
      $$('[data-lesson]').forEach((b) => b.classList.toggle('bg-primary/5', b.dataset.lesson === String(i)));
      $('#player').scrollIntoView({ behavior: 'smooth' });
    };

    if (!id) return showError('لم يتم تحديد الكورس.');
    $('#lessons').addEventListener('click', (ev) => { const b = ev.target.closest('[data-lesson]'); if (b) play(+b.dataset.lesson); });
    $('#action').addEventListener('click', async (ev) => {
      if (ev.target.closest('[data-start]')) {
        const i = (course.lessons || []).findIndex((l) => !l.locked);
        return i < 0 ? C.util.toast('لا توجد دروس متاحة بعد') : play(i);
      }
      const btn = ev.target.closest('[data-enroll]');
      if (!btn || !C.auth.requireLogin('login.html')) return;
      btn.disabled = true;
      btn.textContent = 'جارٍ الاشتراك...';
      try {
        const r = (await C.courses.enroll(course.id)) || {};
        if (r.status === 'active') { C.util.toast('تم الاشتراك بنجاح 🎉'); return load(); }
        course.enrollment = { status: 'pending' };
        if (r.paymentLink) course.paymentLink = r.paymentLink;
      } catch (err) { fail(err); }
      renderAction();
    });
    load();
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

  function myCourses() {
    if (!C.auth.requireLogin('login.html')) return;
    fillGrid($('#grid'), 3, () => C.courses.mine(), (c) => courseCard(c, badge(c.status)),
      stateBox('🎒', 'لم تشترك في أي كورس بعد', 'تصفح الكورسات وابدأ رحلة التعلم الآن.', btnLink('courses.html', 'تصفح الكورسات')));
  }

  const pages = {
    home, courses: coursesPage, course: coursePage, 'my-courses': myCourses,
    login: () => authForm('login'), register: () => authForm('register'),
  };

  document.addEventListener('DOMContentLoaded', () => {
    initMenu();
    if (!window.Casco) return;
    C = window.Casco;
    initAuthNav();
    const init = pages[document.body.dataset.page];
    if (init) init();
  });
})();
