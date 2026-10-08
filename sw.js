/* On repeat, phone version: keeps the page and its scripts for offline opening.
   Only same-origin GETs are handled here; YouTube and Google requests never pass
   through this worker, so they always go to the network. */
"use strict";
const CACHE = "on-repeat-phone-v1";
const SHELL = ["./", "index.html", "local-api.js", "manifest.webmanifest", "icon-192.png", "icon-512.png"];

self.addEventListener("install", e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)));
  self.skipWaiting();
});

self.addEventListener("activate", e => {
  e.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))));
  self.clients.claim();
});

self.addEventListener("fetch", e => {
  const url = new URL(e.request.url);
  if (e.request.method !== "GET" || url.origin !== self.location.origin) return;
  // network first, so an update arrives as soon as the phone is online;
  // the cached copy answers when it isn't (the query string is ignored: ?v= ids)
  e.respondWith(
    fetch(e.request)
      .then(r => { caches.open(CACHE).then(c => c.put(e.request, r.clone())); return r; })
      .catch(() => caches.match(e.request, {ignoreSearch: true}))
  );
});
