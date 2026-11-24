// Ollama chat gateway - static frontend. No build step, no dependencies.
// Model output is only ever inserted with textContent / text nodes (never innerHTML).
"use strict";

// Fallback backend for local development (used when backend.json has no URL).
const DEFAULT_BACKEND = "http://127.0.0.1:8787";

const $ = (sel) => document.querySelector(sel);
const state = {
  backend: null,
  auth: null,       // {username, key}
  me: null,
  convs: [],
  convId: null,
  streaming: false,
};

// ---------------------------------------------------------------- utils
function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else if (v !== false && v != null) node.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c != null) node.append(c);
  return node;
}

function store(key, value) {
  try {
    if (value == null) localStorage.removeItem(key);
    else localStorage.setItem(key, JSON.stringify(value));
  } catch {}
}
function load(key) {
  try { return JSON.parse(localStorage.getItem(key)); } catch { return null; }
}

let toastTimer;
function toast(msg) {
  const t = $("#toast");
  t.textContent = msg;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), 3500);
}

function fmt(n) { return Number(n || 0).toLocaleString(); }
function fmtTime(ts) {
  if (!ts) return "never";
  return new Date(ts * 1000).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

async function copyText(text) {
  try { await navigator.clipboard.writeText(text); toast("Copied"); }
  catch { toast("Copy failed - select the text and copy it manually"); }
}

function show(view) {
  for (const v of ["loading", "offline", "disabled", "login", "chat", "admin"]) {
    $("#view-" + v).hidden = v !== view;
  }
}

// ---------------------------------------------------------------- backend
async function resolveBackend() {
  const param = new URLSearchParams(location.search).get("backend");
  if (param) { sessionStorage.setItem("backend", param); }
  const session = sessionStorage.getItem("backend");
  if (session) return session.replace(/\/$/, "");
  try {
    const r = await fetch("backend.json?t=" + Date.now(), { cache: "no-store" });
    if (r.ok) {
      const j = await r.json();
      if (j && j.url) return j.url.replace(/\/$/, "");
    }
  } catch {}
  return DEFAULT_BACKEND;
}

class ApiError extends Error {
  constructor(status, body) {
    super((body && body.message) || "Request failed");
    this.status = status;
    this.body = body || {};
  }
}

async function api(path, { method = "GET", body, timeout = 15000 } = {}) {
  const headers = {};
  if (state.auth) headers["Authorization"] = `Bearer ${state.auth.username}:${state.auth.key}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeout);
  let res;
  try {
    res = await fetch(state.backend + path, {
      method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: ctl.signal,
    });
  } catch {
    throw new ApiError(0, { error: "offline", message: "Can't reach the server" });
  } finally {
    clearTimeout(timer);
  }
  let data = null;
  try { data = await res.json(); } catch {}
  if (!res.ok) throw new ApiError(res.status, data);
  return data;
}

// Central handling for errors that change the whole screen.
function handleError(e) {
  const code = e.body && e.body.error;
  if (e.status === 0 || code === "offline") return showOffline(e.message);
  if (code === "disabled") return showDisabled(e.body.until);
  if (code === "bad_login") { logout("Your login is no longer valid."); return; }
  toast(e.message);
}

async function checkStatus() {
  try {
    const s = await api("/status", { timeout: 8000 });
    if (!s.available) {
      showOffline(s.ollama && !s.model_available
        ? "The model isn't available on the host right now."
        : "The host's computer is offline. Try again later.");
      return false;
    }
    return true;
  } catch {
    showOffline("The host's computer is offline. Try again later.");
    return false;
  }
}

function showOffline(detail) {
  $("#offline-detail").textContent = detail || "The host's computer is offline. Try again later.";
  show("offline");
}

function showDisabled(until) {
  $("#disabled-until").textContent = until ? `It re-enables on ${fmtTime(until)}.` : "";
  show("disabled");
}

// ---------------------------------------------------------------- boot / login
async function boot() {
  show("loading");
  if (!state.backend) state.backend = await resolveBackend();
  if (!(await checkStatus())) return;
  state.auth = load("auth");
  if (!state.auth) return show("login");
  try {
    state.me = await api("/api/me");
    await enterChat();
  } catch (e) {
    if (e.status === 401) { logout(); return; }
    handleError(e);
  }
}

function logout(message) {
  state.auth = null;
  state.me = null;
  state.convId = null;
  store("auth", null);
  store("convId", null);
  $("#login-form").reset();
  const err = $("#login-error");
  err.hidden = !message;
  err.textContent = message || "";
  show("login");
}

$("#login-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const f = ev.target;
  const err = $("#login-error");
  err.hidden = true;
  state.auth = { username: f.username.value.trim(), key: f.key.value.trim() };
  try {
    state.me = await api("/api/login", { method: "POST" });
    state.auth.username = state.me.username;
    store("auth", state.auth);
    await enterChat();
  } catch (e) {
    const code = e.body && e.body.error;
    state.auth = null;
    if (code === "disabled") return showDisabled(e.body.until);
    if (e.status === 0) return showOffline();
    err.textContent = e.message;
    err.hidden = false;
  }
});

document.querySelectorAll("[data-action=logout]").forEach((b) => b.addEventListener("click", () => logout()));
$("#offline-retry").addEventListener("click", boot);
$("#disabled-retry").addEventListener("click", boot);

// ---------------------------------------------------------------- chat
async function enterChat() {
  $("#whoami").textContent = "(" + state.me.username + ")";
  $("#open-admin").hidden = !state.me.is_owner;
  updateMeter(state.me.budget);
  show("chat");
  await loadConvs();
  state.convId = state.convId || load("convId");
  if (state.convId && state.convs.some((c) => c.id === state.convId)) await openConv(state.convId);
  else newChat();
  if (location.hash === "#admin" && state.me.is_owner) openAdmin();
}

function updateMeter(b) {
  if (!b) return;
  if (b.unlimited) {
    $("#meter-label").textContent = "Unlimited tokens";
    $("#meter-fill").style.width = "100%";
    $("#meter-sub").textContent = "Owner account";
    return;
  }
  const pct = b.daily_allowance ? (b.daily_left / b.daily_allowance) * 100 : 0;
  $("#meter-label").textContent = `${fmt(b.daily_left)} / ${fmt(b.daily_allowance)} daily tokens left`;
  $("#meter-fill").style.width = Math.max(0, Math.min(100, pct)) + "%";
  $("#meter-fill").style.background = b.total_left <= 0 ? "var(--danger)" : "";
  $("#meter-sub").textContent = `Top-up: ${fmt(b.topup)} - daily resets ${fmtTime(b.resets_at)}`;
}

async function loadConvs() {
  try { state.convs = await api("/api/conversations"); }
  catch (e) { return handleError(e); }
  renderConvs();
}

function renderConvs() {
  const list = $("#conv-list");
  list.replaceChildren(...state.convs.map((c) =>
    el("button", {
      class: "conv-item" + (c.id === state.convId ? " active" : ""),
      title: c.title,
      onclick: () => { openConv(c.id); closeDrawer(); },
    }, c.title)));
}

function newChat() {
  state.convId = null;
  store("convId", null);
  $("#chat-title").textContent = "New chat";
  $("#delete-chat").hidden = true;
  $("#messages").replaceChildren(el("div", { class: "empty-hint" }, "Ask me anything."));
  renderConvs();
  closeDrawer();
  $("#input").focus();
}

async function openConv(id) {
  if (state.streaming) return;
  let conv;
  try { conv = await api("/api/conversations/" + id); }
  catch (e) { return handleError(e); }
  state.convId = id;
  store("convId", id);
  $("#chat-title").textContent = conv.title;
  $("#delete-chat").hidden = false;
  const box = $("#messages");
  box.replaceChildren(...conv.messages.map((m) => messageNode(m.role, m.content)));
  renderConvs();
  box.scrollTop = box.scrollHeight;
}

function messageNode(role, text) {
  const bubble = el("div", { class: "bubble" });
  if (role === "assistant") renderMarkdown(bubble, text);
  else bubble.textContent = text;
  return el("div", { class: "msg " + role }, bubble);
}

$("#new-chat").addEventListener("click", newChat);
$("#delete-chat").addEventListener("click", async () => {
  if (!state.convId || !confirm("Delete this chat?")) return;
  try { await api("/api/conversations/" + state.convId, { method: "DELETE" }); }
  catch (e) { return handleError(e); }
  state.convs = state.convs.filter((c) => c.id !== state.convId);
  newChat();
});

// Drawer (phone)
function openDrawer() { $("#drawer").classList.add("open"); $("#scrim").hidden = false; }
function closeDrawer() { $("#drawer").classList.remove("open"); $("#scrim").hidden = true; }
$("#menu-btn").addEventListener("click", openDrawer);
$("#scrim").addEventListener("click", closeDrawer);

// Composer
const input = $("#input");
input.addEventListener("input", () => {
  input.style.height = "auto";
  input.style.height = input.scrollHeight + "px";
});
input.addEventListener("keydown", (ev) => {
  // Enter sends on desktop; on touch devices Enter makes a newline.
  if (ev.key === "Enter" && !ev.shiftKey && !matchMedia("(pointer: coarse)").matches) {
    ev.preventDefault();
    $("#composer").requestSubmit();
  }
});
$("#composer").addEventListener("submit", (ev) => {
  ev.preventDefault();
  const text = input.value.trim();
  if (!text || state.streaming) return;
  input.value = "";
  input.style.height = "auto";
  send(text);
});

async function send(text) {
  const box = $("#messages");
  const hint = box.querySelector(".empty-hint");
  if (hint) hint.remove();
  box.append(messageNode("user", text));
  const bubble = el("div", { class: "bubble cursor" });
  const msg = el("div", { class: "msg assistant" }, bubble);
  box.append(msg);
  box.scrollTop = box.scrollHeight;
  state.streaming = true;
  $("#send").disabled = true;

  let reply = "";
  let pending = false;
  const paint = () => {
    pending = false;
    const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    renderMarkdown(bubble, reply);
    if (nearBottom) box.scrollTop = box.scrollHeight;
  };

  const fail = (message) => {
    msg.className = "msg error";
    bubble.classList.remove("cursor");
    bubble.textContent = message;
  };

  try {
    const res = await fetch(state.backend + "/api/chat", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${state.auth.username}:${state.auth.key}`,
      },
      body: JSON.stringify({ message: text, conversation_id: state.convId }),
    });
    if (!res.ok) {
      let body = null;
      try { body = await res.json(); } catch {}
      const e = new ApiError(res.status, body);
      const code = e.body.error;
      if (code === "offline" || code === "disabled" || code === "bad_login") {
        msg.remove();
        return handleError(e);
      }
      return fail(e.message);
    }
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buf = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += decoder.decode(value, { stream: true });
      let idx;
      while ((idx = buf.indexOf("\n\n")) >= 0) {
        const raw = buf.slice(0, idx);
        buf = buf.slice(idx + 2);
        if (!raw.startsWith("data: ")) continue;
        const ev = JSON.parse(raw.slice(6));
        if (ev.type === "meta") {
          if (state.convId !== ev.conversation_id) {
            state.convId = ev.conversation_id;
            store("convId", state.convId);
            $("#delete-chat").hidden = false;
            $("#chat-title").textContent = text.slice(0, 60);
          }
        } else if (ev.type === "delta") {
          reply += ev.text;
          if (!pending) { pending = true; requestAnimationFrame(paint); }
        } else if (ev.type === "done") {
          updateMeter(ev.budget);
          msg.append(el("div", { class: "meta" }, `${fmt(ev.usage.total)} tokens`));
        } else if (ev.type === "error") {
          fail(ev.message);
        }
      }
    }
    paint();
  } catch {
    if (!reply) {
      msg.remove();
      if (!(await checkStatus())) return;
      toast("Connection lost - try again.");
    }
  } finally {
    bubble.classList.remove("cursor");
    state.streaming = false;
    $("#send").disabled = false;
    loadConvs();
  }
}

// ---------------------------------------------------------------- markdown-ish
// Supports ``` code blocks, `inline code`, **bold**, *italic*, # headings, - / 1. lists.
// Builds DOM nodes only; text is always inserted as text nodes.
function renderMarkdown(container, text) {
  const out = [];
  const fence = /```([^\n`]*)\n?([\s\S]*?)(?:```|$)/g;
  let last = 0, m;
  while ((m = fence.exec(text))) {
    if (m.index > last) out.push(...renderBlocks(text.slice(last, m.index)));
    out.push(codeBlock(m[1].trim(), m[2].replace(/\n$/, "")));
    last = fence.lastIndex;
    if (m[0].length === 0) break;
  }
  if (last < text.length) out.push(...renderBlocks(text.slice(last)));
  container.replaceChildren(...out);
}

function codeBlock(lang, code) {
  const copy = el("button", { class: "btn small copy", type: "button", onclick: () => copyText(code) }, "Copy");
  return el("div", { class: "codeblock" },
    el("div", { class: "lang" }, lang || "code"),
    el("pre", {}, el("code", {}, code)),
    copy);
}

function renderBlocks(text) {
  const nodes = [];
  for (const para of text.split(/\n{2,}/)) {
    const lines = para.split("\n").filter((l) => l.trim() !== "");
    if (!lines.length) continue;
    let list = null;
    let p = null;
    for (const line of lines) {
      const h = line.match(/^#{1,6}\s+(.*)/);
      const ul = line.match(/^\s*[-*+]\s+(.*)/);
      const ol = line.match(/^\s*\d+[.)]\s+(.*)/);
      if (h) {
        list = p = null;
        nodes.push(el("h3", {}, ...inline(h[1])));
      } else if (ul || ol) {
        p = null;
        const tag = ul ? "ul" : "ol";
        if (!list || list.tagName.toLowerCase() !== tag) { list = el(tag); nodes.push(list); }
        list.append(el("li", {}, ...inline((ul || ol)[1])));
      } else {
        list = null;
        if (!p) { p = el("p"); nodes.push(p); }
        else p.append(el("br"));
        p.append(...inline(line));
      }
    }
  }
  return nodes;
}

function inline(text) {
  const parts = [];
  const re = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*\s][^*]*\*)/g;
  let last = 0, m;
  while ((m = re.exec(text))) {
    if (m.index > last) parts.push(document.createTextNode(text.slice(last, m.index)));
    const t = m[0];
    if (t.startsWith("`")) parts.push(el("code", {}, t.slice(1, -1)));
    else if (t.startsWith("**")) parts.push(el("strong", {}, t.slice(2, -2)));
    else parts.push(el("em", {}, t.slice(1, -1)));
    last = re.lastIndex;
  }
  if (last < text.length) parts.push(document.createTextNode(text.slice(last)));
  return parts;
}

// ---------------------------------------------------------------- admin
function openAdmin() { history.replaceState(null, "", "#admin"); show("admin"); loadAccounts(); }
$("#open-admin").addEventListener("click", openAdmin);
$("#admin-back").addEventListener("click", () => {
  history.replaceState(null, "", location.pathname + location.search);
  show("chat");
  refreshMe();
});

async function refreshMe() {
  try { state.me = await api("/api/me"); updateMeter(state.me.budget); } catch (e) { handleError(e); }
}

let searchTimer;
$("#search").addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(loadAccounts, 250);
});

async function loadAccounts() {
  const term = $("#search").value.trim();
  try {
    const accounts = term
      ? await api("/api/admin/accounts/search", { method: "POST", body: { q: term } })
      : await api("/api/admin/accounts");
    renderAccounts(accounts);
  } catch (e) { handleError(e); }
}

function showKey(title, key) {
  $("#keybox-title").textContent = title;
  $("#keybox-key").textContent = key;
  $("#keybox").hidden = false;
  $("#keybox").scrollIntoView({ behavior: "smooth", block: "center" });
}
$("#keybox-copy").addEventListener("click", () => copyText($("#keybox-key").textContent));
$("#keybox-close").addEventListener("click", () => {
  $("#keybox-key").textContent = "";
  $("#keybox").hidden = true;
});

$("#create-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const f = ev.target;
  const err = $("#create-error");
  err.hidden = true;
  try {
    const r = await api("/api/admin/accounts", {
      method: "POST",
      body: {
        username: f.username.value.trim(),
        daily_allowance: parseInt(f.daily_allowance.value || "0", 10),
        topup: parseInt(f.topup.value || "0", 10),
      },
    });
    f.username.value = "";
    showKey(`Key for ${r.account.username}`, r.key);
    loadAccounts();
  } catch (e) {
    if (e.status === 400 || e.status === 409) { err.textContent = e.message; err.hidden = false; }
    else handleError(e);
  }
});

async function adminAction(path, method, body, confirmMsg) {
  if (confirmMsg && !confirm(confirmMsg)) return null;
  try {
    const r = await api("/api/admin/accounts/" + path, { method, body });
    loadAccounts();
    return r;
  } catch (e) { handleError(e); return null; }
}

function renderAccounts(accounts) {
  const box = $("#accounts");
  if (!accounts.length) { box.replaceChildren(el("p", { class: "muted" }, "No accounts found.")); return; }
  box.replaceChildren(...accounts.map(accountCard));
}

function accountCard(a) {
  const now = Date.now() / 1000;
  const disabled = a.disabled_until > now;
  const head = el("div", { class: "acc-head" },
    el("span", { class: "acc-name" }, a.username),
    a.is_owner ? el("span", { class: "badge owner" }, "owner") : null,
    disabled ? el("span", { class: "badge off" }, "disabled until " + fmtTime(a.disabled_until)) : null);

  const stat = (label, value) => el("div", {}, el("span", { class: "muted" }, label + ": "), el("b", {}, value));
  const b = a.budget;
  const stats = el("div", { class: "acc-stats" },
    stat("Today", fmt(a.tokens_today)),
    stat("Total", fmt(a.total_tokens)),
    stat("Requests", fmt(a.total_requests)),
    stat("Last active", fmtTime(a.last_active)),
    ...(a.is_owner ? [stat("Budget", "unlimited")] : [
      stat("Daily left", `${fmt(b.daily_left)} / ${fmt(b.daily_allowance)}`),
      stat("Top-up", fmt(b.topup)),
    ]));

  const card = el("div", { class: "card" }, head, stats);
  const id = a.id;
  const actions = el("div", { class: "acc-actions" });

  if (!a.is_owner) {
    const allowance = el("input", { type: "number", min: "0", step: "1000", value: a.daily_allowance, "aria-label": "Daily tokens" });
    const topup = el("input", { type: "number", step: "1000", placeholder: "+ tokens", "aria-label": "Add top-up tokens" });
    const amount = el("input", { type: "number", min: "1", value: "1", "aria-label": "Disable duration" });
    const unit = el("select", { "aria-label": "Unit" },
      el("option", { value: "1" }, "minutes"),
      el("option", { value: "60", selected: "" }, "hours"),
      el("option", { value: "1440" }, "days"));

    actions.append(
      el("div", { class: "row gap" }, el("span", { class: "muted" }, "Daily"), allowance,
        el("button", { class: "btn small", onclick: () =>
          adminAction(id, "PATCH", { daily_allowance: parseInt(allowance.value || "0", 10) }) }, "Save")),
      el("div", { class: "row gap" }, el("span", { class: "muted" }, "Top-up"), topup,
        el("button", { class: "btn small", onclick: () => {
          const n = parseInt(topup.value || "0", 10);
          if (n) adminAction(id, "PATCH", { add_topup: n });
        } }, "Add")),
      el("div", { class: "row gap" },
        disabled
          ? el("button", { class: "btn small", onclick: () => adminAction(id + "/enable", "POST") }, "Re-enable now")
          : [el("span", { class: "muted" }, "Disable for"), amount, unit,
             el("button", { class: "btn small danger", onclick: () => {
               const minutes = parseFloat(amount.value || "0") * parseInt(unit.value, 10);
               if (minutes > 0) adminAction(id + "/disable", "POST", { minutes });
             } }, "Disable")]
      ),
    );
  }
  const keyRow = el("div", { class: "row gap" },
    el("button", { class: "btn small", onclick: async () => {
      if (!confirm(`Rotate the key for ${a.username}? The old key stops working immediately.`)) return;
      let r;
      try { r = await api("/api/admin/accounts/" + id + "/rotate", { method: "POST" }); }
      catch (e) { return handleError(e); }
      if (a.is_owner) { state.auth.key = r.key; store("auth", state.auth); }
      showKey(`New key for ${a.username}`, r.key);
      loadAccounts();
    } }, "Rotate key"));
  if (!a.is_owner) {
    keyRow.append(el("button", { class: "btn small danger", onclick: () =>
      adminAction(id, "DELETE", undefined, `Delete ${a.username} and all their chats? This can't be undone.`) }, "Delete"));
  }
  actions.append(keyRow);
  card.append(actions);
  return card;
}

// ---------------------------------------------------------------- start
if ("serviceWorker" in navigator && (location.protocol === "https:" || location.hostname === "localhost")) {
  navigator.serviceWorker.register("sw.js").catch(() => {});
}
boot();
