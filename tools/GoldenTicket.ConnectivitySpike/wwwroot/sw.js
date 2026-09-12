// Shell-only service worker for the M0 spike.
//
// DESIGN 18.5: a small explicit allowlist of local shell assets. It must never cache /api/,
// authenticated responses, or anything a game command would carry. There is no game state in this
// spike at all, and this file stays that way so the behaviour observed on a device is the behaviour
// the real companion will have.

const SHELL_CACHE = 'gt-spike-shell-v2';

const SHELL = [
    './',
    'index.html',
    'app.js',
    'styles.css',
    'icon.svg',
    'icon-192.png',
    'icon-512.png',
    'manifest.webmanifest',
];

self.addEventListener('install', (event) => {
    event.waitUntil(caches.open(SHELL_CACHE).then((cache) => cache.addAll(SHELL)));
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((names) => Promise.all(
                names.filter((name) => name !== SHELL_CACHE).map((name) => caches.delete(name))))
            .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Never touch the API, even read-only: its responses are no-store by contract.
    if (url.pathname.includes('/api/')) return;
    if (event.request.method !== 'GET') return;
    if (url.origin !== self.location.origin) return;

    event.respondWith(
        caches.match(event.request).then((cached) => cached || fetch(event.request).catch(() =>
            caches.match('index.html'))));
});
