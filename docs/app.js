// Nook - static frontend for the Ollama chat gateway. No build step, no dependencies.
// Model and user text is only ever inserted with textContent / text nodes (never innerHTML).
// The only innerHTML use is for the constant SVG icon strings below.
"use strict";

// Fallback backend for local development (used when backend.json has no URL).
const DEFAULT_BACKEND = "http://127.0.0.1:8787";

const $ = (sel) => document.querySelector(sel);
const state = {
  backend: null,
  auth: null,        // {username, key}
  me: null,
  limits: { max_file_bytes: 204800, max_files_per_message: 4, max_instructions_chars: 2000, max_project_bytes: 1048576 },
  convs: [],
  conv: null,        // the open conversation {id, title, project_id, ...}
  projects: [],
  projectId: null,   // project selected in the sidebar (null = all chats)
  search: "",
  searchResults: null,
  streaming: false,
  abort: null,
  attachments: [],   // [{name, content, size}] waiting to be sent
};

// ---------------------------------------------------------------- icons
const ICONS = {
  plus: '<path d="M12 5v14M5 12h14"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="m20 20-4.2-4.2"/>',
  menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
  sidebar: '<rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M9 4v16"/>',
  edit: '<path d="M12 20h8M16.5 3.6a2.1 2.1 0 0 1 3 3L7.5 18.5 3.5 19.5l1-4Z"/>',
  trash: '<path d="M4 7h16M9.5 7V4.5h5V7M18 7l-.8 12.5a1.5 1.5 0 0 1-1.5 1.5H8.3a1.5 1.5 0 0 1-1.5-1.5L6 7"/>',
  clip: '<path d="m20.5 11.5-8.6 8.6a5.5 5.5 0 0 1-7.8-7.8l8.6-8.6a3.7 3.7 0 0 1 5.2 5.2l-8.6 8.6a1.8 1.8 0 0 1-2.6-2.6l7.9-7.9"/>',
  send: '<path d="M12 19V5M6 11l6-6 6 6"/>',
  stop: '<rect x="7" y="7" width="10" height="10" rx="2" fill="currentColor" stroke="none"/>',
  copy: '<rect x="9" y="9" width="11" height="11" rx="2.5"/><path d="M5 15V6a2 2 0 0 1 2-2h9"/>',
  download: '<path d="M12 4v11M7 10.5l5 5 5-5M5 20h14"/>',
  close: '<path d="M6 6l12 12M18 6 6 18"/>',
  back: '<path d="M19 12H5M11 18l-6-6 6-6"/>',
  settings: '<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0M16 4.6a3.5 3.5 0 0 1 0 6.8M21.5 20a6.5 6.5 0 0 0-3.5-5.8"/>',
  logout: '<path d="M14 4h4.5a1.5 1.5 0 0 1 1.5 1.5v13a1.5 1.5 0 0 1-1.5 1.5H14M10 8l-4 4 4 4M6 12h10"/>',
  pin: '<path d="M12 16v5M8.5 3.5h7l-1 5.5 3 4h-11l3-4Z"/>',
  folder: '<path d="M3.5 7a2 2 0 0 1 2-2h3.8l2 2h7.2a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2Z"/>',
  chats: '<path d="M4.5 5h15v10.5H9L4.5 19.5Z"/>',
  refresh: '<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4"/>',
  file: '<path d="M14 3.5H7a1.5 1.5 0 0 0-1.5 1.5v14A1.5 1.5 0 0 0 7 20.5h10a1.5 1.5 0 0 0 1.5-1.5V8Z"/><path d="M14 3.5V8h4.5"/>',
  more: '<circle cx="6" cy="12" r="1.3" fill="currentColor"/><circle cx="12" cy="12" r="1.3" fill="currentColor"/><circle cx="18" cy="12" r="1.3" fill="currentColor"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M4.6 4.6 6 6M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4"/>',
  moon: '<path d="M19.5 14.5A8 8 0 1 1 9.5 4.5a6.3 6.3 0 0 0 10 10Z"/>',
  auto: '<circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 1 0 16Z" fill="currentColor"/>',
  open: '<path d="M14 4h6v6M20 4l-8.5 8.5M10 5H6a1.5 1.5 0 0 0-1.5 1.5v11.5A1.5 1.5 0 0 0 6 19.5h11.5A1.5 1.5 0 0 0 19 18v-4"/>',
};

function icon(name) {
  const wrap = document.createElement("span");
  // Constant markup from ICONS only - never user or model text.
  wrap.innerHTML = `<svg class="ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
    stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name] || ""}</svg>`;
  return wrap.firstElementChild;
}

function hydrateIcons(root = document) {
  root.querySelectorAll("[data-icon]").forEach((n) => {
    if (n.querySelector(":scope > svg")) return;
    n.prepend(icon(n.dataset.icon));
  });
}

// ---------------------------------------------------------------- utils
function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else if (v !== false && v != null) node.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c != null && c !== false) node.append(c);
  return node;
}

function iconBtn(name, label, onclick, cls = "icon-btn") {
  return el("button", { class: cls, type: "button", "aria-label": label, title: label, onclick }, icon(name));
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
function fmtSize(b) { return b < 1024 ? `${b} B` : `${(b / 1024).toFixed(b < 10240 ? 1 : 0)} KB`; }
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

const isPhone = () => matchMedia("(max-width: 860px)").matches;

// ---------------------------------------------------------------- theme
function applyTheme(mode) {
  if (mode === "light" || mode === "dark") document.documentElement.dataset.theme = mode;
  else delete document.documentElement.dataset.theme;
  store("theme", mode === "system" ? null : mode);
  const btn = $("#theme-btn");
  btn.replaceChildren(icon(mode === "light" ? "sun" : mode === "dark" ? "moon" : "auto"));
  const label = `Theme: ${mode === "light" ? "light" : mode === "dark" ? "dark" : "follows system"}`;
  btn.title = label;
  btn.setAttribute("aria-label", label);
}
const currentTheme = () => load("theme") || "system";
$("#theme-btn").addEventListener("click", () => {
  const order = ["system", "light", "dark"];
  applyTheme(order[(order.indexOf(currentTheme()) + 1) % 3]);
});

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

function authHeader() {
  return `Bearer ${state.auth.username}:${state.auth.key}`;
}

async function api(path, { method = "GET", body, timeout = 20000 } = {}) {
  const headers = {};
  if (state.auth) headers["Authorization"] = authHeader();
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
  if (state.abort) state.abort.abort();
  state.auth = null;
  state.me = null;
  state.conv = null;
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

// ---------------------------------------------------------------- chat shell
async function enterChat() {
  $("#whoami").textContent = "(" + state.me.username + ")";
  $("#open-admin").hidden = !state.me.is_owner;
  updateMeter(state.me.budget);
  show("chat");
  if (load("collapsed") && !isPhone()) $("#view-chat").classList.add("collapsed");
  try { Object.assign(state.limits, await api("/api/settings")); } catch {}
  await Promise.all([loadConvs(), loadProjects()]);
  const saved = load("convId");
  if (saved && state.convs.some((c) => c.id === saved)) await openConv(saved);
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
  $("#meter-label").textContent = `${fmt(b.daily_left)} of ${fmt(b.daily_allowance)} daily tokens left`;
  $("#meter-fill").style.width = Math.max(0, Math.min(100, pct)) + "%";
  $("#meter-fill").style.background = b.total_left <= 0 ? "var(--danger)" : "";
  $("#meter-sub").textContent = `Top-up ${fmt(b.topup)}, resets at ${new Date(b.resets_at * 1000).toLocaleTimeString([], { timeStyle: "short" })}`;
}

// Sidebar (drawer on phones, collapsible column on desktop)
function openDrawer() { $("#drawer").classList.add("open"); $("#scrim").hidden = false; }
function closeDrawer() { $("#drawer").classList.remove("open"); if (!$("#scrim").classList.contains("for-panel")) $("#scrim").hidden = true; }
$("#menu-btn").addEventListener("click", () => {
  if (isPhone()) return openDrawer();
  $("#view-chat").classList.remove("collapsed");
  store("collapsed", null);
});
$("#collapse-btn").addEventListener("click", () => {
  $("#view-chat").classList.add("collapsed");
  store("collapsed", true);
});
$("#scrim").addEventListener("click", () => { closeDrawer(); closePanel(); });

// ---------------------------------------------------------------- projects
async function loadProjects() {
  try { state.projects = await api("/api/projects"); }
  catch (e) { return handleError(e); }
  if (state.projectId && !state.projects.some((p) => p.id === state.projectId)) state.projectId = null;
  renderProjects();
}

const projectById = (id) => state.projects.find((p) => p.id === id);

function renderProjects() {
  const list = $("#project-list");
  const all = el("button", {
    class: "item" + (state.projectId == null ? " active" : ""), type: "button",
    onclick: () => selectProject(null),
  }, el("span", { class: "lead" }, icon("chats")), el("span", { class: "label" }, "All chats"));
  list.replaceChildren(all, ...state.projects.map((p) => {
    const item = el("div", {
      class: "item" + (p.id === state.projectId ? " active" : ""), role: "button", tabindex: "0",
      onclick: () => selectProject(p.id),
      onkeydown: (ev) => { if (ev.key === "Enter") selectProject(p.id); },
    },
    el("span", { class: "lead" }, icon("folder")),
    el("span", { class: "label" }, p.name),
    el("span", { class: "item-actions" },
      iconBtn("more", "Project settings", (ev) => { ev.stopPropagation(); openProjectDialog(p.id); })));
    return item;
  }));
}

function selectProject(id) {
  state.projectId = id;
  renderProjects();
  renderConvs();
  if (!state.streaming) newChat();
}

// ---------------------------------------------------------------- conversation list
async function loadConvs() {
  try { state.convs = await api("/api/conversations"); }
  catch (e) { return handleError(e); }
  const open = state.conv && state.conv.id && state.convs.find((c) => c.id === state.conv.id);
  if (open && open.title !== state.conv.title) { state.conv.title = open.title; renderTitle(); }
  if (state.search) await runSearch();
  renderConvs();
}

let searchTimer;
$("#chat-search").addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(async () => {
    state.search = $("#chat-search").value.trim();
    await runSearch();
    renderConvs();
  }, 250);
});

async function runSearch() {
  if (!state.search) { state.searchResults = null; return; }
  try { state.searchResults = await api("/api/conversations?q=" + encodeURIComponent(state.search)); }
  catch (e) { handleError(e); }
}

function renderConvs() {
  const list = $("#conv-list");
  let items;
  if (state.searchResults) {
    items = state.searchResults;
    $("#chats-head").textContent = `Results for "${state.search}"`;
  } else {
    items = state.projectId == null ? state.convs : state.convs.filter((c) => c.project_id === state.projectId);
    const p = projectById(state.projectId);
    $("#chats-head").textContent = p ? `Chats in ${p.name}` : "Chats";
  }
  if (!items.length) {
    list.replaceChildren(el("div", { class: "list-empty" },
      state.searchResults ? "No chats match." : state.projectId ? "No chats in this project yet." : "No chats yet."));
    return;
  }
  list.replaceChildren(...items.map(convItem));
}

function convItem(c) {
  const active = state.conv && c.id === state.conv.id;
  const open = () => { openConv(c.id); closeDrawer(); };
  const p = state.searchResults && c.project_id ? projectById(c.project_id) : null;
  return el("div", {
    class: "item" + (active ? " active" : ""), role: "button", tabindex: "0", title: c.title,
    onclick: open, onkeydown: (ev) => { if (ev.key === "Enter" && ev.target === ev.currentTarget) open(); },
  },
  c.pinned ? el("span", { class: "lead pin-mark", title: "Pinned" }, icon("pin")) : null,
  el("span", { class: "label" }, c.title,
    c.snippet ? el("span", { class: "sub" }, c.snippet) : p ? el("span", { class: "sub" }, p.name) : null),
  el("span", { class: "item-actions" },
    iconBtn("pin", c.pinned ? "Unpin" : "Pin", (ev) => { ev.stopPropagation(); togglePin(c); }, "icon-btn" + (c.pinned ? " on" : "")),
    iconBtn("edit", "Rename", (ev) => { ev.stopPropagation(); renameConv(c); }),
    iconBtn("trash", "Delete", (ev) => { ev.stopPropagation(); deleteConv(c); })));
}

async function togglePin(c) {
  try { await api("/api/conversations/" + c.id, { method: "PATCH", body: { pinned: !c.pinned } }); }
  catch (e) { return handleError(e); }
  loadConvs();
}

function renameConv(c) {
  const dlg = $("#rename-dialog");
  const form = dlg.querySelector("form");
  form.title.value = c.title;
  dlg.showModal();
  form.title.select();
  form.onsubmit = async (ev) => {
    ev.preventDefault();
    if (ev.submitter && ev.submitter.value === "cancel") return dlg.close();
    try { await api("/api/conversations/" + c.id, { method: "PATCH", body: { title: form.title.value } }); }
    catch (e) { return handleError(e); }
    dlg.close();
    if (state.conv && state.conv.id === c.id) { state.conv.title = form.title.value.trim(); renderTitle(); }
    loadConvs();
  };
}

async function deleteConv(c) {
  if (!confirm(`Delete "${c.title}"?`)) return;
  try { await api("/api/conversations/" + c.id, { method: "DELETE" }); }
  catch (e) { return handleError(e); }
  if (state.conv && state.conv.id === c.id) newChat();
  loadConvs();
}

$("#rename-chat").addEventListener("click", () => state.conv && state.conv.id && renameConv(state.conv));
$("#delete-chat").addEventListener("click", () => state.conv && state.conv.id && deleteConv(state.conv));
$("#new-chat").addEventListener("click", () => { if (!state.streaming) newChat(); });

// ---------------------------------------------------------------- open / new chat
function renderTitle() {
  const c = state.conv;
  $("#chat-title").textContent = c && c.id ? c.title : "New chat";
  const p = projectById(c ? c.project_id : null);
  $("#chat-project").hidden = !p;
  $("#chat-project").textContent = p ? p.name : "";
  $("#rename-chat").hidden = $("#delete-chat").hidden = !(c && c.id);
}

function greeting() {
  const h = new Date().getHours();
  const part = h < 5 ? "Up late" : h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
  return `${part}, ${state.me.username}.`;
}

function emptyState() {
  const p = projectById(state.conv && state.conv.project_id);
  const pick = (text) => { input.value = text; autoGrow(); input.focus(); };
  const files = p ? p.files.length : 0;
  return el("div", { class: "empty" },
    el("h1", { class: "greet" }, p ? p.name : greeting()),
    el("p", { class: "lede" }, p
      ? `New chat in this project. Nook will use its instructions${files ? ` and ${files} file${files > 1 ? "s" : ""}` : ""}.`
      : "What would you like to talk about?"),
    el("div", { class: "suggestions" },
      ["Explain something simply", "Help me write a message", "Review a piece of code"].map((s) =>
        el("button", { class: "btn", type: "button", onclick: () => pick(s + ": ") }, s))));
}

function newChat() {
  state.conv = { id: null, title: "New chat", project_id: state.projectId };
  store("convId", null);
  renderTitle();
  $("#messages").replaceChildren(emptyState());
  renderConvs();
  closeDrawer();
  if (!isPhone()) input.focus();
}

async function openConv(id) {
  if (state.streaming) return;
  let conv;
  try { conv = await api("/api/conversations/" + id); }
  catch (e) { return handleError(e); }
  state.conv = conv;
  store("convId", id);
  renderTitle();
  const box = $("#messages");
  box.replaceChildren(...conv.messages.map(messageNode));
  markLast();
  renderConvs();
  box.scrollTop = box.scrollHeight;
}

// ---------------------------------------------------------------- messages
function chip(name, size, onremove) {
  return el("span", { class: "chip" + (onremove ? "" : " static"), title: name },
    icon("file"), el("span", { class: "name" }, name), el("span", { class: "size" }, fmtSize(size)),
    onremove ? el("button", { type: "button", "aria-label": "Remove " + name, onclick: onremove }, icon("close")) : null);
}

function messageNode(m) {
  const node = el("div", { class: "msg " + m.role });
  if (m.id) node.dataset.id = m.id;
  const bubble = el("div", { class: "bubble" });
  node._text = m.content;
  if (m.role === "assistant") {
    renderMarkdown(bubble, m.content);
    node.append(bubble, el("div", { class: "msg-tools" },
      iconBtn("copy", "Copy", () => copyText(node._text)),
      iconBtn("refresh", "Regenerate", () => regenerate(), "icon-btn regen")));
  } else {
    if (m.attachments && m.attachments.length) {
      node.append(el("div", { class: "chips" }, m.attachments.map((a) => chip(a.name, a.size))));
    }
    bubble.textContent = m.content;
    node.append(bubble, el("div", { class: "msg-tools" },
      iconBtn("copy", "Copy", () => copyText(node._text)),
      iconBtn("edit", "Edit", () => startEdit(node), "icon-btn edit")));
  }
  return node;
}

// Regenerate is offered only on the final reply; tools stay visible on the last message.
function markLast() {
  const msgs = [...$("#messages").querySelectorAll(".msg")];
  msgs.forEach((m, i) => {
    const last = i === msgs.length - 1;
    m.classList.toggle("last", last);
    const regen = m.querySelector(".regen");
    if (regen) regen.hidden = !last;
  });
}

// Tap an assistant reply on phones to reveal its tools.
$("#messages").addEventListener("click", (ev) => {
  const msg = ev.target.closest(".msg.assistant");
  if (msg && !ev.target.closest("button")) msg.classList.toggle("show-tools");
});

function startEdit(node) {
  if (state.streaming || !node.dataset.id) return;
  const bubble = node.querySelector(".bubble");
  const tools = node.querySelector(".msg-tools");
  const ta = el("textarea", { rows: "3", "aria-label": "Edit message" });
  ta.value = node._text;
  const cancel = () => { box.remove(); bubble.hidden = tools.hidden = false; };
  const box = el("div", { class: "edit-box" }, ta,
    el("div", { class: "row gap" },
      el("button", { class: "btn small ghost", type: "button", onclick: cancel }, "Cancel"),
      el("button", { class: "btn small primary", type: "button", onclick: () => {
        const text = ta.value.trim();
        if (!text) return;
        box.remove();
        bubble.hidden = tools.hidden = false;
        bubble.textContent = node._text = text;
        while (node.nextElementSibling) node.nextElementSibling.remove();
        stream({ conversation_id: state.conv.id, edit_message_id: Number(node.dataset.id), message: text }, node);
      } }, "Save and send")));
  ta.addEventListener("keydown", (ev) => {
    if (ev.key === "Escape") { ev.stopPropagation(); cancel(); }
    if (ev.key === "Enter" && !ev.shiftKey && !matchMedia("(pointer: coarse)").matches) {
      ev.preventDefault(); box.querySelector(".primary").click();
    }
  });
  bubble.hidden = tools.hidden = true;
  node.append(box);
  ta.focus();
  ta.setSelectionRange(ta.value.length, ta.value.length);
}

function regenerate() {
  if (state.streaming || !state.conv.id) return;
  const box = $("#messages");
  const msgs = [...box.querySelectorAll(".msg")];
  const lastUser = msgs.map((m) => m.classList.contains("user")).lastIndexOf(true);
  if (lastUser < 0) return;
  msgs.slice(lastUser + 1).forEach((m) => m.remove());
  stream({ conversation_id: state.conv.id, regenerate: true }, msgs[lastUser]);
}

// ---------------------------------------------------------------- composer
const input = $("#input");
function autoGrow() {
  input.style.height = "auto";
  input.style.height = input.scrollHeight + "px";
}
input.addEventListener("input", autoGrow);

// Preload the model when the user starts typing (fire-and-forget, at most every 5 minutes).
let lastWarm = 0;
input.addEventListener("input", () => {
  if (!input.value.trim() || Date.now() - lastWarm < 5 * 60 * 1000 || !state.auth) return;
  lastWarm = Date.now();
  fetch(state.backend + "/api/warm", { method: "POST", headers: { Authorization: authHeader() } }).catch(() => {});
});
input.addEventListener("keydown", (ev) => {
  // Enter sends on desktop; on touch devices Enter makes a newline. Shift+Enter is always a newline.
  if (ev.key === "Enter" && !ev.shiftKey && !ev.isComposing && !matchMedia("(pointer: coarse)").matches) {
    ev.preventDefault();
    $("#composer").requestSubmit();
  }
});
$("#composer").addEventListener("submit", (ev) => {
  ev.preventDefault();
  const text = input.value.trim();
  if (!text || state.streaming) return;
  const attachments = state.attachments;
  input.value = "";
  autoGrow();
  state.attachments = [];
  renderAttachments();
  sendNew(text, attachments);
});
$("#stop").addEventListener("click", () => { if (state.abort) state.abort.abort(); });

function setStreaming(on) {
  state.streaming = on;
  $("#send").hidden = on;
  $("#stop").hidden = !on;
  $("#attach-btn").disabled = on;
}

// File attachments: text/code only, checked here and again on the server.
$("#attach-btn").addEventListener("click", () => $("#file-input").click());
$("#file-input").addEventListener("change", async (ev) => {
  await addFiles([...ev.target.files]);
  ev.target.value = "";
});
const composer = $("#composer");
composer.addEventListener("dragover", (ev) => { ev.preventDefault(); composer.classList.add("dragging"); });
composer.addEventListener("dragleave", () => composer.classList.remove("dragging"));
composer.addEventListener("drop", (ev) => {
  ev.preventDefault();
  composer.classList.remove("dragging");
  if (!state.streaming) addFiles([...ev.dataTransfer.files]);
});

async function readTextFile(file) {
  const max = state.limits.max_file_bytes;
  if (file.size > max) throw new Error(`${file.name} is too large (max ${Math.round(max / 1024)} KB per file)`);
  if (/^(image|audio|video)\//.test(file.type)) throw new Error(`${file.name} isn't a text file. Images aren't supported.`);
  let content;
  try { content = new TextDecoder("utf-8", { fatal: true }).decode(await file.arrayBuffer()); }
  catch { throw new Error(`${file.name} isn't a UTF-8 text file`); }
  if (content.includes("\0")) throw new Error(`${file.name} isn't a text file`);
  if (!content.trim()) throw new Error(`${file.name} is empty`);
  return { name: file.name, content: content.replace(/^﻿/, ""), size: file.size };
}

async function addFiles(files) {
  for (const f of files) {
    if (state.attachments.length >= state.limits.max_files_per_message) {
      toast(`Attach at most ${state.limits.max_files_per_message} files per message`);
      break;
    }
    try { state.attachments.push(await readTextFile(f)); }
    catch (e) { toast(e.message); }
  }
  renderAttachments();
}

function renderAttachments() {
  const box = $("#attach-chips");
  box.hidden = !state.attachments.length;
  box.replaceChildren(...state.attachments.map((a, i) =>
    chip(a.name, a.size, () => { state.attachments.splice(i, 1); renderAttachments(); })));
}

function sendNew(text, attachments) {
  const box = $("#messages");
  box.querySelectorAll(".empty, .msg.error").forEach((n) => n.remove());
  const userNode = messageNode({ role: "user", content: text, attachments });
  box.append(userNode);
  const body = {
    message: text, conversation_id: state.conv.id,
    attachments: attachments.map(({ name, content }) => ({ name, content })),
  };
  if (!state.conv.id && state.conv.project_id) body.project_id = state.conv.project_id;
  stream(body, userNode, { text, attachments });
}

// Streams one reply. `restore` (new messages only) puts text + files back in the composer
// when the server refuses the message before answering (e.g. it doesn't fit).
async function stream(body, userNode, restore) {
  const box = $("#messages");
  const bubble = el("div", { class: "bubble cursor" });
  const tools = el("div", { class: "msg-tools" });
  const msg = el("div", { class: "msg assistant" }, bubble, tools);
  box.append(msg);
  markLast();
  box.scrollTop = box.scrollHeight;
  setStreaming(true);
  const ctl = new AbortController();
  state.abort = ctl;

  let reply = "", pending = false, failed = false, stopped = false, usage = null;
  const paint = () => {
    pending = false;
    const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 120;
    renderMarkdown(bubble, reply);
    if (nearBottom) box.scrollTop = box.scrollHeight;
  };
  const fail = (message) => {
    failed = true;
    msg.className = "msg error";
    bubble.classList.remove("cursor");
    bubble.textContent = message;
    tools.remove();
  };

  try {
    const res = await fetch(state.backend + "/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: authHeader() },
      body: JSON.stringify(body),
      signal: ctl.signal,
    });
    if (!res.ok) {
      let data = null;
      try { data = await res.json(); } catch {}
      const e = new ApiError(res.status, data);
      const code = e.body.error;
      if (code === "offline" || code === "disabled" || code === "bad_login") {
        msg.remove();
        return handleError(e);
      }
      if (restore) {
        userNode.remove();
        input.value = restore.text;
        autoGrow();
        state.attachments = restore.attachments;
        renderAttachments();
      }
      if (body.edit_message_id || body.regenerate) openConv(state.conv.id);  // resync with the server
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
          userNode.dataset.id = ev.user_message_id;
          if (state.conv.id !== ev.conversation_id) {
            state.conv.id = ev.conversation_id;
            state.conv.title = body.message.split(/\s+/).join(" ").slice(0, 50);
            store("convId", state.conv.id);
            renderTitle();
          }
        } else if (ev.type === "notice") {
          msg.insertBefore(el("div", { class: "notice" }, ev.message), bubble);
        } else if (ev.type === "delta") {
          reply += ev.text;
          if (!pending) { pending = true; requestAnimationFrame(paint); }
        } else if (ev.type === "done") {
          usage = ev.usage;
          if (ev.assistant_message_id) msg.dataset.id = ev.assistant_message_id;
          updateMeter(ev.budget);
        } else if (ev.type === "error") {
          fail(ev.message);
        }
      }
    }
    if (!failed) paint();
  } catch (e) {
    if (ctl.signal.aborted) {
      stopped = true;
      if (!failed) paint();
    } else if (!reply) {
      msg.remove();
      if (!(await checkStatus())) return;
      toast("Connection lost - try again.");
    }
  } finally {
    state.abort = null;
    bubble.classList.remove("cursor");
    setStreaming(false);
    if (!failed && msg.isConnected) {
      if (!reply && stopped) bubble.append(el("span", { class: "muted" }, "Stopped before any reply."));
      msg._text = reply;
      tools.append(
        iconBtn("copy", "Copy", () => copyText(msg._text)),
        iconBtn("refresh", "Regenerate", () => regenerate(), "icon-btn regen"),
        el("span", { class: "meta" }, stopped ? "Stopped" : usage ? `${fmt(usage.total)} tokens` : ""));
    }
    markLast();
    loadConvs();
    if (stopped) setTimeout(refreshMe, 1000);  // the server charges once it notices the disconnect
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
  const small = (name, label, onclick) =>
    el("button", { class: "btn", type: "button", onclick, title: label }, icon(name), label);
  return el("div", { class: "codeblock" },
    el("div", { class: "codeblock-head" },
      el("span", { class: "lang" }, lang || "code"),
      small("copy", "Copy", () => copyText(code)),
      small("open", "Open", () => openPanel(lang, code))),
    el("pre", {}, el("code", {}, code)));
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

// ---------------------------------------------------------------- code panel
const EXT = {
  python: "py", py: "py", javascript: "js", js: "js", jsx: "jsx", typescript: "ts", ts: "ts", tsx: "tsx",
  html: "html", xml: "xml", svg: "svg", css: "css", scss: "scss", json: "json", yaml: "yml", yml: "yml",
  toml: "toml", ini: "ini", bash: "sh", sh: "sh", shell: "sh", zsh: "sh", powershell: "ps1", ps1: "ps1",
  c: "c", h: "h", cpp: "cpp", "c++": "cpp", csharp: "cs", cs: "cs", java: "java", kotlin: "kt", go: "go",
  rust: "rs", rs: "rs", ruby: "rb", rb: "rb", php: "php", swift: "swift", lua: "lua", sql: "sql",
  markdown: "md", md: "md", dockerfile: "Dockerfile", makefile: "Makefile", r: "r", dart: "dart", text: "txt",
};
const panel = { code: "", name: "" };

function openPanel(lang, code) {
  const l = (lang || "").toLowerCase();
  const ext = EXT[l] || "txt";
  panel.code = code;
  panel.name = /^[A-Z]/.test(ext) ? ext : `snippet.${ext}`;
  const html = ext === "html" || ext === "svg" || /^\s*<(!doctype html|html)/i.test(code);
  $("#panel-title").textContent = panel.name;
  $("#panel-code").firstElementChild.textContent = code;
  $("#panel-tabs").hidden = !html;
  showTab("code");
  $("#panel").hidden = false;
  if (isPhone()) { $("#scrim").hidden = false; $("#scrim").classList.add("for-panel"); }
}

function closePanel() {
  $("#panel").hidden = true;
  $("#panel-preview").srcdoc = "";
  $("#scrim").classList.remove("for-panel");
  if (!$("#drawer").classList.contains("open")) $("#scrim").hidden = true;
}

function showTab(tab) {
  document.querySelectorAll("#panel-tabs .tab").forEach((t) => t.classList.toggle("active", t.dataset.tab === tab));
  $("#panel-code").hidden = tab !== "code";
  const frame = $("#panel-preview");
  frame.hidden = tab !== "preview";
  // Sandboxed without allow-same-origin: the preview can't reach this page, its storage, or the key.
  if (tab === "preview") frame.srcdoc = panel.code;
}

$("#panel-tabs").addEventListener("click", (ev) => { const t = ev.target.closest(".tab"); if (t) showTab(t.dataset.tab); });
$("#panel-close").addEventListener("click", closePanel);
$("#panel-copy").addEventListener("click", () => copyText(panel.code));
$("#panel-download").addEventListener("click", () => {
  const url = URL.createObjectURL(new Blob([panel.code], { type: "text/plain;charset=utf-8" }));
  const a = el("a", { href: url, download: panel.name });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});

// ---------------------------------------------------------------- keyboard
document.addEventListener("keydown", (ev) => {
  if (ev.key !== "Escape" || document.querySelector("dialog[open]")) return;
  if (!$("#panel").hidden) return closePanel();
  if ($("#drawer").classList.contains("open")) return closeDrawer();
});

// ---------------------------------------------------------------- dialogs
function bindCounter(form, name) {
  const field = form.elements[name];
  const out = form.querySelector(`[data-counter-for="${name}"]`);
  const update = () => {
    const max = state.limits.max_instructions_chars;
    out.textContent = `${field.value.length} / ${max}`;
    out.classList.toggle("over", field.value.length > max);
  };
  field.addEventListener("input", update);
  return update;
}

// Settings: custom instructions + theme
const settingsForm = $("#settings-form");
const updateSettingsCounter = bindCounter(settingsForm, "instructions");
$("#open-settings").addEventListener("click", async () => {
  let s;
  try { s = await api("/api/settings"); } catch (e) { return handleError(e); }
  settingsForm.instructions.value = s.instructions;
  settingsForm.instructions.maxLength = s.max_instructions_chars;
  settingsForm.theme.value = currentTheme();
  updateSettingsCounter();
  $("#settings-error").hidden = true;
  $("#settings-dialog").showModal();
});
settingsForm.addEventListener("change", (ev) => { if (ev.target.name === "theme") applyTheme(ev.target.value); });
settingsForm.addEventListener("submit", async (ev) => {
  ev.preventDefault();
  if (ev.submitter && ev.submitter.value === "cancel") return $("#settings-dialog").close();
  try { await api("/api/settings", { method: "PATCH", body: { instructions: settingsForm.instructions.value } }); }
  catch (e) {
    if (e.status === 400) { $("#settings-error").textContent = e.message; $("#settings-error").hidden = false; return; }
    return handleError(e);
  }
  $("#settings-dialog").close();
  toast("Settings saved");
});

// Projects
const projectForm = $("#project-form");
const updateProjectCounter = bindCounter(projectForm, "instructions");
let editingProject = null;

function projectError(msg) {
  $("#project-error").textContent = msg || "";
  $("#project-error").hidden = !msg;
}

function fillProjectDialog(p) {
  editingProject = p;
  $("#project-dialog-title").textContent = p ? "Project settings" : "New project";
  projectForm.name.value = p ? p.name : "";
  projectForm.instructions.value = p ? p.instructions : "";
  updateProjectCounter();
  $("#project-delete").hidden = !p;
  $("#project-files-section").hidden = !p;
  if (p) {
    $("#project-files-total").textContent = `${fmtSize(p.total_bytes)} of ${fmtSize(state.limits.max_project_bytes)}`;
    $("#project-files").replaceChildren(...p.files.map((f) => el("li", {},
      icon("file"), el("span", { class: "name", title: f.name }, f.name), el("span", { class: "muted" }, fmtSize(f.size)),
      iconBtn("trash", "Remove " + f.name, () => removeProjectFile(f), "icon-btn tiny"))));
  }
}

function openProjectDialog(id) {
  projectError();
  fillProjectDialog(id ? projectById(id) : null);
  $("#project-dialog").showModal();
  if (!id) projectForm.name.focus();
}
$("#new-project").addEventListener("click", () => openProjectDialog(null));

projectForm.addEventListener("submit", async (ev) => {
  ev.preventDefault();
  if (ev.submitter && ev.submitter.value === "cancel") return $("#project-dialog").close();
  const body = { name: projectForm.name.value, instructions: projectForm.instructions.value };
  let p;
  try {
    p = editingProject
      ? await api("/api/projects/" + editingProject.id, { method: "PATCH", body })
      : await api("/api/projects", { method: "POST", body });
  } catch (e) {
    if (e.status === 400) return projectError(e.message);
    return handleError(e);
  }
  const created = !editingProject;
  await loadProjects();
  renderTitle();
  if (created) {
    // Stay open so files can be added right away, and switch to the new project.
    fillProjectDialog(projectById(p.id));
    selectProject(p.id);
    toast("Project created - add files if you like");
  } else {
    $("#project-dialog").close();
    if (!state.conv.id) newChat();
  }
});

$("#project-add-file").addEventListener("click", () => $("#project-file-input").click());
$("#project-file-input").addEventListener("change", async (ev) => {
  const files = [...ev.target.files];
  ev.target.value = "";
  projectError();
  for (const f of files) {
    try {
      const { name, content } = await readTextFile(f);
      const p = await api(`/api/projects/${editingProject.id}/files`, { method: "POST", body: { name, content }, timeout: 60000 });
      fillProjectDialog(p);
    } catch (e) {
      if (e instanceof ApiError && e.status !== 400) return handleError(e);
      projectError(e.message);
    }
  }
  loadProjects();
});

async function removeProjectFile(f) {
  try { fillProjectDialog(await api(`/api/projects/${editingProject.id}/files/${f.id}`, { method: "DELETE" })); }
  catch (e) { return handleError(e); }
  loadProjects();
}

$("#project-delete").addEventListener("click", () => {
  const dlg = $("#delete-project-dialog");
  const form = dlg.querySelector("form");
  form.reset();
  dlg.showModal();
  form.onsubmit = async (ev) => {
    ev.preventDefault();
    if (!ev.submitter || ev.submitter.value !== "delete") return dlg.close();
    const id = editingProject.id;
    try { await api("/api/projects/" + id, { method: "DELETE", body: { chats: form.chats.value } }); }
    catch (e) { return handleError(e); }
    dlg.close();
    $("#project-dialog").close();
    if (state.projectId === id) state.projectId = null;
    await Promise.all([loadProjects(), loadConvs()]);
    if (!state.conv.id || !state.convs.some((c) => c.id === state.conv.id)) newChat();
    else renderTitle();
    toast("Project deleted");
  };
});

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

let accSearchTimer;
$("#search").addEventListener("input", () => {
  clearTimeout(accSearchTimer);
  accSearchTimer = setTimeout(loadAccounts, 250);
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
hydrateIcons();
applyTheme(currentTheme());
if ("serviceWorker" in navigator && (location.protocol === "https:" || location.hostname === "localhost")) {
  navigator.serviceWorker.register("sw.js").catch(() => {});
}
boot();
