/*! Casco Site SDK v1 */
(function () {
  "use strict";

  var cfg = window.__CASCO__ || {};
  var base = (cfg.api || "") + "/api/s/" + (cfg.siteKey || "");
  var tokenKey = "casco_" + (cfg.siteKey || "site");
  var memory = {};

  function storageGet(k) {
    try { return window.localStorage.getItem(k); } catch (e) { return memory[k] || null; }
  }
  function storageSet(k, v) {
    try { window.localStorage.setItem(k, v); } catch (e) { memory[k] = v; }
  }
  function storageDel(k) {
    try { window.localStorage.removeItem(k); } catch (e) { delete memory[k]; }
  }

  function getSession() {
    var raw = storageGet(tokenKey);
    if (!raw) return null;
    try { return JSON.parse(raw); } catch (e) { return null; }
  }

  function request(method, path, body, isForm) {
    var headers = {};
    var session = getSession();
    if (session && session.token) headers["Authorization"] = "Bearer " + session.token;
    if (cfg.preview) headers["X-Casco-Preview"] = "1";
    var init = { method: method, headers: headers };
    if (body !== undefined) {
      if (isForm) {
        init.body = body;
      } else {
        headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(body);
      }
    }
    return fetch(base + path, init).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        if (text) { try { data = JSON.parse(text); } catch (e) { data = { message: text }; } }
        if (!res.ok) {
          if (res.status === 401) storageDel(tokenKey);
          var err = new Error((data && (data.message || data.title || data.detail)) || ("HTTP " + res.status));
          err.status = res.status;
          err.data = data;
          throw err;
        }
        return data;
      });
    });
  }

  function toQuery(params) {
    if (!params) return "";
    var parts = [];
    Object.keys(params).forEach(function (k) {
      var v = params[k];
      if (v !== undefined && v !== null && v !== "") parts.push(encodeURIComponent(k) + "=" + encodeURIComponent(v));
    });
    return parts.length ? "?" + parts.join("&") : "";
  }

  var util = {
    qs: function (name) { return new URLSearchParams(window.location.search).get(name); },
    escape: function (value) {
      return String(value == null ? "" : value)
        .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    },
    money: function (amount, currency) {
      if (amount === null || amount === undefined || Number(amount) === 0) return "مجاني";
      try {
        return new Intl.NumberFormat("ar", { style: "currency", currency: currency || "USD" }).format(amount);
      } catch (e) { return amount + " " + (currency || ""); }
    },
    date: function (iso) {
      try { return new Date(iso).toLocaleDateString("ar"); } catch (e) { return iso; }
    },
    toast: function (message, type) {
      var el = document.createElement("div");
      el.textContent = message;
      el.setAttribute("role", "status");
      el.style.cssText = "position:fixed;bottom:24px;left:50%;transform:translateX(-50%);z-index:9999;padding:12px 20px;border-radius:12px;color:#fff;font:14px system-ui,sans-serif;box-shadow:0 10px 30px rgba(0,0,0,.2);background:" + (type === "error" ? "#dc2626" : "#16a34a");
      document.body.appendChild(el);
      setTimeout(function () { el.remove(); }, 3500);
    }
  };

  function revoke(token) {
    if (!token) return;
    fetch(base + "/auth/logout", { method: "POST", headers: { Authorization: "Bearer " + token } }).catch(function () {});
  }

  var auth = {
    register: function (data) {
      var previous = getSession();
      return request("POST", "/auth/register", data).then(function (r) {
        storageSet(tokenKey, JSON.stringify(r));
        if (previous && previous.token && previous.token !== r.token) revoke(previous.token);
        return r.user;
      });
    },
    login: function (data) {
      var previous = getSession();
      return request("POST", "/auth/login", data).then(function (r) {
        storageSet(tokenKey, JSON.stringify(r));
        if (previous && previous.token && previous.token !== r.token) revoke(previous.token);
        return r.user;
      });
    },
    logout: function () {
      var session = getSession();
      storageDel(tokenKey);
      if (session && session.token) revoke(session.token);
    },
    user: function () { var s = getSession(); return s ? s.user : null; },
    isLoggedIn: function () { return !!getSession(); },
    me: function () { return request("GET", "/auth/me"); },
    requireLogin: function (loginPage) {
      if (!getSession()) {
        var back = encodeURIComponent(window.location.pathname.split("/").pop() + window.location.search);
        window.location.href = (loginPage || "login.html") + "?next=" + back;
        return false;
      }
      return true;
    },
    redirectAfterLogin: function (fallback) {
      var next = util.qs("next");
      window.location.href = next && !/^(https?:)?\/\//i.test(next) ? next : (fallback || "index.html");
    }
  };

  var forms = {
    submit: function (formName, data) { return request("POST", "/forms/" + encodeURIComponent(formName), data); },
    bind: function (form, formName) {
      if (!form || form.__cascoBound) return;
      form.__cascoBound = true;
      form.addEventListener("submit", function (ev) {
        ev.preventDefault();
        var data = {};
        new FormData(form).forEach(function (v, k) { if (typeof v === "string") data[k] = v; });
        var btn = form.querySelector("[type=submit]");
        if (btn) btn.disabled = true;
        forms.submit(formName || form.getAttribute("data-casco-form") || "contact", data).then(function () {
          form.reset();
          var ok = form.querySelector("[data-casco-success]") || document.querySelector("[data-casco-success]");
          if (ok) { ok.hidden = false; ok.style.display = ""; } else { util.toast("تم الإرسال بنجاح"); }
        }).catch(function (e) {
          util.toast(e.message || "حدث خطأ، حاول مرة أخرى", "error");
        }).finally(function () { if (btn) btn.disabled = false; });
      });
    }
  };

  var courses = {
    list: function (params) { return request("GET", "/courses" + toQuery(params)); },
    get: function (id) { return request("GET", "/courses/" + encodeURIComponent(id)); },
    enroll: function (id) { return request("POST", "/courses/" + encodeURIComponent(id) + "/enroll"); },
    mine: function () { return request("GET", "/my/courses"); }
  };

  var ads = {
    list: function (params) { return request("GET", "/ads" + toQuery(params)); },
    get: function (id) { return request("GET", "/ads/" + encodeURIComponent(id)); },
    uploadImage: function (file) {
      var fd = new FormData();
      fd.append("file", file);
      return request("POST", "/uploads", fd, true).then(function (r) { return r.url; });
    },
    create: function (data) {
      var files = Array.prototype.slice.call(data.images || []).filter(function (f) { return f instanceof File; });
      return Promise.all(files.map(ads.uploadImage)).then(function (urls) {
        var payload = Object.assign({}, data, { images: urls });
        return request("POST", "/ads", payload);
      });
    },
    mine: function () { return request("GET", "/my/ads"); },
    remove: function (id) { return request("DELETE", "/ads/" + encodeURIComponent(id)); }
  };

  // ---------- Custom database (collections declared in casco.backend.json) ----------
  function collection(name) {
    var p = "/db/" + encodeURIComponent(name);
    return {
      list: function (params) {
        params = Object.assign({}, params || {});
        if (params.where && typeof params.where === "object") params.where = JSON.stringify(params.where);
        return request("GET", p + toQuery(params));
      },
      get: function (id) { return request("GET", p + "/" + encodeURIComponent(id)); },
      create: function (data) { return request("POST", p, data); },
      update: function (id, data) { return request("PATCH", p + "/" + encodeURIComponent(id), data); },
      remove: function (id) { return request("DELETE", p + "/" + encodeURIComponent(id)); }
    };
  }
  var db = {
    collection: collection,
    list: function (name, params) { return collection(name).list(params); },
    get: function (name, id) { return collection(name).get(id); },
    create: function (name, data) { return collection(name).create(data); },
    update: function (name, id, data) { return collection(name).update(id, data); },
    remove: function (name, id) { return collection(name).remove(id); }
  };

  // ---------- Store + cart ----------
  var cartKey = "casco_cart_" + (cfg.siteKey || "site");
  function readCart() {
    try { var c = JSON.parse(storageGet(cartKey) || "[]"); return Array.isArray(c) ? c : []; } catch (e) { return []; }
  }
  function writeCart(items) {
    storageSet(cartKey, JSON.stringify(items));
    var detail = { items: items, count: cart.count(), subtotal: cart.subtotal() };
    document.querySelectorAll("[data-casco-cart-count]").forEach(function (el) { el.textContent = detail.count; });
    try { window.dispatchEvent(new CustomEvent("casco:cart", { detail: detail })); } catch (e) { }
  }
  var cart = {
    items: function () { return readCart(); },
    count: function () { return readCart().reduce(function (n, i) { return n + i.quantity; }, 0); },
    subtotal: function () { return readCart().reduce(function (n, i) { return n + i.price * i.quantity; }, 0); },
    add: function (product, quantity) {
      var items = readCart();
      var qty = Math.max(1, parseInt(quantity || 1, 10));
      var existing = items.filter(function (i) { return i.productId === product.id; })[0];
      if (existing) existing.quantity = Math.min(100, existing.quantity + qty);
      else items.push({ productId: product.id, name: product.name, price: Number(product.price), image: product.image || (product.images && product.images[0]) || null, quantity: qty });
      writeCart(items);
      return items;
    },
    setQuantity: function (productId, quantity) {
      var qty = parseInt(quantity, 10) || 0;
      var items = readCart().map(function (i) { if (i.productId === productId) i.quantity = Math.min(100, qty); return i; })
        .filter(function (i) { return i.quantity > 0; });
      writeCart(items);
      return items;
    },
    remove: function (productId) {
      var items = readCart().filter(function (i) { return i.productId !== productId; });
      writeCart(items);
      return items;
    },
    clear: function () { writeCart([]); }
  };
  var store = {
    config: function () { return request("GET", "/store/config"); },
    products: function (params) { return request("GET", "/store/products" + toQuery(params)); },
    product: function (id) { return request("GET", "/store/products/" + encodeURIComponent(id)); },
    categories: function () { return request("GET", "/store/categories"); },
    checkout: function (customer) {
      var items = readCart().map(function (i) { return { productId: i.productId, quantity: i.quantity }; });
      return request("POST", "/store/orders", Object.assign({}, customer, { items: items })).then(function (order) {
        cart.clear();
        return order;
      });
    },
    order: function (id, phone) { return request("GET", "/store/orders/" + encodeURIComponent(id) + toQuery({ phone: phone })); },
    myOrders: function () { return request("GET", "/store/my/orders"); },
    cart: cart
  };

  // ---------- Bookings ----------
  var bookings = {
    services: function () { return request("GET", "/bookings/services"); },
    slots: function (serviceId, date) { return request("GET", "/bookings/slots" + toQuery({ serviceId: serviceId, date: date })); },
    book: function (data) { return request("POST", "/bookings", data); },
    mine: function () { return request("GET", "/bookings/my"); }
  };

  // ---------- Server functions (server/functions.js) ----------
  function fn(name, input) {
    return request("POST", "/fn/" + encodeURIComponent(name), input || {}).then(function (r) { return r ? r.result : null; });
  }

  window.Casco = {
    config: cfg,
    util: util,
    auth: auth,
    forms: forms,
    courses: courses,
    ads: ads,
    db: db,
    store: store,
    cart: cart,
    bookings: bookings,
    fn: fn,
    uploads: { image: ads.uploadImage },
    request: request
  };

  function autoBind() {
    document.querySelectorAll("form[data-casco-form]").forEach(function (f) { forms.bind(f); });
    var count = cart.count();
    document.querySelectorAll("[data-casco-cart-count]").forEach(function (el) { el.textContent = count; });
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", autoBind);
  else autoBind();
  // React pages render after this script. Bind forms and cart badges when they appear.
  var bindTimer = 0;
  new MutationObserver(function () {
    clearTimeout(bindTimer);
    bindTimer = setTimeout(autoBind, 50);
  }).observe(document.documentElement, { childList: true, subtree: true });
})();
