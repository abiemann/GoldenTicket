// Shell-only service worker for the M0 spike.
//
// DESIGN 18.5: a small explicit allowlist of local shell assets. It must never cache /api/,
// authenticated responses, or anything a game command would carry. There is no game state in this
// spike at all, and this file stays that way so the behaviour observed on a device is the behaviour
// the real companion will have.

const SHELL_CACHE = 'gt-spike-shell-v3';

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
const SHELL_URLS = new Set(SHELL.map(asset => new URL(asset, self.location.href).href));

self.addEventListener('install', (event) => {
    event.waitUntil(caches.open(SHELL_CACHE).then((cache) => cache.addAll(SHELL)));
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((names) => Promise.all(
                names.filter((name) => name.startsWith('gt-spike-shell-') && name !== SHELL_CACHE)
                    .map((name) => caches.delete(name))))
            .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Never touch the API, even read-only: its responses are no-store by contract.
    if (url.pathname.includes('/api/')) return;
    if (event.request.method !== 'GET') return;
    if (url.origin !== self.location.origin) return;
    if (!SHELL_URLS.has(url.href)) return;

    // Read only this version's allowlisted shell. Unknown paths must not become successful HTML
    // responses when offline, and responses in another cache are not part of this shell contract.
    event.respondWith(
        caches.open(SHELL_CACHE).then(async cache =>
            (await cache.match(event.request)) || fetch(event.request)));
});
