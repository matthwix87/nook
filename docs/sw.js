// Service worker: caches the app shell so the PWA opens instantly.
// backend.json and all backend (cross-origin) requests always go to the network.
const CACHE = "nook-shell-v6";
const SHELL = ["./", "index.html", "style.css?v=6", "app.js?v=6", "manifest.webmanifest",
  "icons/icon.svg", "icons/icon-192.png", "icons/icon-512.png"];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (e) => {
  e.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== "GET" || url.origin !== location.origin || url.pathname.endsWith("backend.json")) return;
  // Network first (so updates show up), cache as offline fallback.
  // Asset URLs in index.html carry ?v=N; bump it together with CACHE on every release.
  e.respondWith(
    // no-cache: always revalidate with the server, so a stale HTTP-cached app.js/style.css
    // can never be mixed with a newer index.html.
    fetch(e.request, { cache: "no-cache" })
      .then((res) => {
        const copy = res.clone();
        caches.open(CACHE).then((c) => c.put(e.request, copy));
        return res;
      })
      .catch(() => caches.match(e.request, { ignoreSearch: true }))
  );
});
