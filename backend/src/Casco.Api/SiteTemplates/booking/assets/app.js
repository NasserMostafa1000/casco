(function () {
  var esc = Casco.util.escape;
  var state = { service: null, slot: null, currency: "AED", services: [] };
  var servicesEl = document.getElementById("services");
  var slotsEl = document.getElementById("slots");
  var dateEl = document.getElementById("date");
  var bookBtn = document.getElementById("bookBtn");

  function today() { var d = new Date(); d.setMinutes(d.getMinutes() - d.getTimezoneOffset()); return d.toISOString().slice(0, 10); }

  function renderServices() {
    servicesEl.innerHTML = state.services.map(function (s) {
      return '<button class="svc ' + (state.service && state.service.id === s.id ? "svc-on" : "") + '" data-svc="' + esc(s.id) + '">' +
        '<h3 class="font-bold text-lg mb-1">' + esc(s.name) + '</h3>' +
        '<p class="text-slate-500 text-sm mb-2">' + esc(s.description || "") + '</p>' +
        '<p class="text-sm"><b class="text-primary">' + Casco.util.money(s.price, state.currency) + '</b> · ' + s.durationMinutes + ' دقيقة</p></button>';
    }).join("");
  }

  Casco.bookings.services().then(function (r) {
    state.services = r.items;
    state.currency = r.currency;
    dateEl.min = today();
    dateEl.value = today();
    if (!r.items.length) {
      servicesEl.innerHTML = '<p class="col-span-full text-center text-slate-500">لا توجد خدمات بعد. أضف خدماتك من لوحة تحكم Casco ← البيانات ← الحجوزات.</p>';
      return;
    }
    renderServices();
  }).catch(function (e) {
    servicesEl.innerHTML = '<p class="col-span-full text-center text-red-600">' + esc(e.message) + '</p>';
  });

  function loadSlots() {
    state.slot = null;
    bookBtn.disabled = true;
    document.getElementById("picked").textContent = "اختر وقتاً من المواعيد المتاحة";
    slotsEl.innerHTML = '<p class="col-span-3 text-sm text-slate-500">جاري التحميل…</p>';
    Casco.bookings.slots(state.service.id, dateEl.value).then(function (r) {
      slotsEl.innerHTML = r.items.length ? r.items.map(function (s) {
        return '<button type="button" class="slot" data-start="' + esc(s.start) + '" data-time="' + esc(s.time) + '"' + (s.available ? "" : " disabled") + '>' + esc(s.time) + '</button>';
      }).join("") : '<p class="col-span-3 text-sm text-slate-500">لا توجد مواعيد متاحة في هذا اليوم، جرّب يوماً آخر.</p>';
    }).catch(function (e) { slotsEl.innerHTML = '<p class="col-span-3 text-sm text-red-600">' + esc(e.message) + '</p>'; });
  }

  servicesEl.addEventListener("click", function (e) {
    var b = e.target.closest("[data-svc]");
    if (!b) return;
    state.service = state.services.filter(function (s) { return s.id === b.getAttribute("data-svc"); })[0];
    renderServices();
    document.getElementById("step2").classList.remove("hidden");
    loadSlots();
  });
  dateEl.addEventListener("change", loadSlots);
  slotsEl.addEventListener("click", function (e) {
    var b = e.target.closest("[data-start]");
    if (!b || b.disabled) return;
    slotsEl.querySelectorAll(".slot-on").forEach(function (x) { x.classList.remove("slot-on"); });
    b.classList.add("slot-on");
    state.slot = b.getAttribute("data-start");
    document.getElementById("picked").textContent = "الموعد: " + dateEl.value + " الساعة " + b.getAttribute("data-time");
    bookBtn.disabled = false;
  });

  document.getElementById("bookForm").addEventListener("submit", function (e) {
    e.preventDefault();
    if (!state.slot) return;
    var data = { serviceId: state.service.id, start: state.slot };
    new FormData(e.target).forEach(function (v, k) { data[k] = v; });
    bookBtn.disabled = true;
    Casco.bookings.book(data).then(function (b) {
      document.getElementById("step2").classList.add("hidden");
      servicesEl.classList.add("hidden");
      var done = document.getElementById("done");
      done.classList.remove("hidden");
      done.innerHTML = '<div class="text-5xl">📅</div><h3 class="text-xl font-extrabold">' +
        (b.status === "confirmed" ? "تم تأكيد حجزك!" : "تم استلام طلب الحجز وسنؤكده لك قريباً") + '</h3>' +
        '<p>' + esc(b.service) + ' — ' + esc(b.localTime) + '</p>' +
        (b.whatsappUrl ? '<a href="' + esc(b.whatsappUrl) + '" target="_blank" rel="noopener" class="inline-block bg-green-600 text-white font-bold px-6 py-3 rounded-xl">أرسل تفاصيل الحجز على واتساب</a>' : '');
    }).catch(function (err) {
      Casco.util.toast(err.message, "error");
      bookBtn.disabled = false;
      if (err.status === 409) loadSlots();
    });
  });
})();
