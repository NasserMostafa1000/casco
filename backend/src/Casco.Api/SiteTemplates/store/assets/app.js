(function () {
  var esc = Casco.util.escape;
  var state = { category: "", q: "", config: { currency: "AED", paymentMethods: [], shippingFee: 0, freeShippingOver: 0 } };
  var grid = document.getElementById("grid");
  var drawer = document.getElementById("drawer");
  var body = document.getElementById("drawerBody");
  function money(v) { return Casco.util.money(v, state.config.currency); }

  function card(p) {
    var old = p.comparePrice && p.comparePrice > p.price ? '<span class="text-slate-400 line-through text-sm ms-2">' + money(p.comparePrice) + '</span>' : '';
    return '<div class="bg-white rounded-2xl shadow-sm hover:shadow-lg transition overflow-hidden flex flex-col">' +
      '<img src="' + esc(p.image || "{{STOCK}}/400x400/product") + '" alt="' + esc(p.name) + '" class="aspect-square object-cover w-full">' +
      '<div class="p-4 flex-1 flex flex-col">' +
      '<h3 class="font-bold mb-1">' + esc(p.name) + '</h3>' +
      '<p class="text-slate-500 text-sm line-clamp-2 mb-3">' + esc(p.description || "") + '</p>' +
      '<div class="mt-auto flex items-center justify-between gap-2">' +
      '<div><span class="font-extrabold text-primary">' + money(p.price) + '</span>' + old + '</div>' +
      (p.inStock ? '<button class="btn text-sm px-3 py-2" data-add="' + esc(p.id) + '">أضف للسلة</button>' : '<span class="text-sm text-slate-400">نفدت الكمية</span>') +
      '</div></div></div>';
  }

  var products = {};
  function loadProducts() {
    Casco.store.products({ category: state.category, q: state.q, pageSize: 48 }).then(function (r) {
      if (r.currency) state.config.currency = r.currency;
      r.items.forEach(function (p) { products[p.id] = p; });
      grid.innerHTML = r.items.length ? r.items.map(card).join("")
        : '<p class="col-span-full text-center text-slate-500 py-10">لا توجد منتجات بعد. أضف منتجاتك من لوحة تحكم Casco ← البيانات ← المتجر.</p>';
    }).catch(function (e) {
      grid.innerHTML = '<p class="col-span-full text-center text-red-600 py-10">' + esc(e.message) + '</p>';
    });
  }

  function loadCategories() {
    Casco.store.categories().then(function (r) {
      var el = document.getElementById("categories");
      if (!r.items.length) return;
      var all = [{ name: "", label: "الكل" }].concat(r.items.map(function (c) { return { name: c.name, label: c.name }; }));
      el.innerHTML = all.map(function (c) {
        return '<button class="chip ' + (c.name === state.category ? "chip-on" : "") + '" data-cat="' + esc(c.name) + '">' + esc(c.label) + '</button>';
      }).join("");
    }).catch(function () {});
  }

  document.getElementById("categories").addEventListener("click", function (e) {
    var b = e.target.closest("[data-cat]");
    if (!b) return;
    state.category = b.getAttribute("data-cat");
    loadCategories();
    loadProducts();
  });
  var timer;
  document.getElementById("search").addEventListener("input", function (e) {
    clearTimeout(timer);
    timer = setTimeout(function () { state.q = e.target.value.trim(); loadProducts(); }, 350);
  });
  grid.addEventListener("click", function (e) {
    var b = e.target.closest("[data-add]");
    if (!b) return;
    Casco.cart.add(products[b.getAttribute("data-add")], 1);
    Casco.util.toast("تمت الإضافة إلى السلة");
  });

  function open() { drawer.classList.remove("hidden"); renderCart(); }
  function close() { drawer.classList.add("hidden"); }
  document.getElementById("cartBtn").addEventListener("click", open);
  drawer.addEventListener("click", function (e) { if (e.target.closest("[data-close]")) close(); });

  function shipping(subtotal, method) {
    if (method === "pickup") return 0;
    var c = state.config;
    return c.freeShippingOver > 0 && subtotal >= c.freeShippingOver ? 0 : (c.shippingFee || 0);
  }

  function renderCart() {
    var items = Casco.cart.items();
    if (!items.length) { body.innerHTML = '<p class="text-center text-slate-500 py-16">السلة فارغة</p>'; return; }
    var subtotal = Casco.cart.subtotal();
    var methods = state.config.paymentMethods.length ? state.config.paymentMethods : [{ id: "cod", label: "الدفع عند الاستلام" }];
    body.innerHTML =
      '<ul class="divide-y mb-4">' + items.map(function (i) {
        return '<li class="py-3 flex gap-3 items-center">' +
          '<img src="' + esc(i.image || "{{STOCK}}/100x100/product") + '" alt="" class="w-14 h-14 rounded-lg object-cover">' +
          '<div class="flex-1"><p class="font-semibold text-sm">' + esc(i.name) + '</p><p class="text-primary text-sm font-bold">' + money(i.price) + '</p></div>' +
          '<div class="flex items-center gap-1"><button class="w-7 h-7 rounded-lg border" data-qty="' + esc(i.productId) + '" data-d="1">+</button>' +
          '<span class="w-6 text-center">' + i.quantity + '</span>' +
          '<button class="w-7 h-7 rounded-lg border" data-qty="' + esc(i.productId) + '" data-d="-1">−</button></div></li>';
      }).join("") + '</ul>' +
      '<div class="bg-slate-50 rounded-xl p-3 text-sm space-y-1 mb-4">' +
      '<div class="flex justify-between"><span>المجموع</span><b>' + money(subtotal) + '</b></div>' +
      '<div class="flex justify-between"><span>التوصيل</span><b id="shipFee">' + money(shipping(subtotal, methods[0].id)) + '</b></div></div>' +
      '<form id="checkout" class="space-y-3">' +
      '<input type="text" name="_hp" style="display:none" tabindex="-1" autocomplete="off">' +
      '<div><label class="lbl">الاسم</label><input name="customerName" required class="inp"></div>' +
      '<div><label class="lbl">رقم الهاتف</label><input name="phone" type="tel" required class="inp"></div>' +
      '<div><label class="lbl">المدينة</label><input name="city" class="inp"></div>' +
      '<div><label class="lbl">العنوان بالتفصيل</label><input name="address" class="inp"></div>' +
      '<div><label class="lbl">ملاحظات</label><input name="notes" class="inp"></div>' +
      '<div><label class="lbl">طريقة الدفع</label><select name="paymentMethod" class="inp">' +
      methods.map(function (m) { return '<option value="' + esc(m.id) + '">' + esc(m.label) + '</option>'; }).join("") + '</select></div>' +
      '<button class="btn w-full py-3" type="submit">تأكيد الطلب</button></form>';
  }

  body.addEventListener("click", function (e) {
    var b = e.target.closest("[data-qty]");
    if (!b) return;
    var id = b.getAttribute("data-qty");
    var item = Casco.cart.items().filter(function (i) { return i.productId === id; })[0];
    if (item) Casco.cart.setQuantity(id, item.quantity + Number(b.getAttribute("data-d")));
    renderCart();
  });
  body.addEventListener("change", function (e) {
    if (e.target.name === "paymentMethod") document.getElementById("shipFee").textContent = money(shipping(Casco.cart.subtotal(), e.target.value));
  });
  body.addEventListener("submit", function (e) {
    if (e.target.id !== "checkout") return;
    e.preventDefault();
    var data = {};
    new FormData(e.target).forEach(function (v, k) { data[k] = v; });
    var btn = e.target.querySelector("[type=submit]");
    btn.disabled = true;
    Casco.store.checkout(data).then(function (o) {
      body.innerHTML = '<div class="text-center py-10 space-y-4">' +
        '<div class="text-5xl">✅</div><h3 class="text-xl font-extrabold">تم استلام طلبك!</h3>' +
        '<p>رقم الطلب: <b>#' + esc(o.number) + '</b></p><p>الإجمالي: <b>' + money(o.total) + '</b> — ' + esc(o.paymentLabel) + '</p>' +
        (o.bankDetails ? '<div class="bg-slate-50 rounded-xl p-3 text-sm whitespace-pre-line text-right">' + esc(o.bankDetails) + '</div>' : '') +
        (o.whatsappUrl ? '<a href="' + esc(o.whatsappUrl) + '" target="_blank" rel="noopener" class="inline-block bg-green-600 text-white font-bold px-6 py-3 rounded-xl">أرسل الطلب على واتساب</a>' : '') +
        '</div>';
    }).catch(function (err) {
      Casco.util.toast(err.message, "error");
      btn.disabled = false;
    });
  });

  Casco.store.config().then(function (c) { state.config = c; }).catch(function () {});
  loadCategories();
  loadProducts();
})();
