// GoldenTicket M0 connectivity spike, device side.
//
// Four questions (DESIGN 22.7): is this a trusted secure context, was the laptop reached by its
// local name, did the shell install and cache offline, and does a pairing round-trip work in the
// context the device finally launches from?
//
// No game state, no private information, no persistence beyond the pairing cookie the laptop sets.

const SHELL_CACHE = 'gt-spike-shell-v1';

const state = {
    secureContext: false,
    reachedByName: false,
    serviceWorkerSupported: false,
    serviceWorkerRegistered: false,
    shellCachedOffline: false,
    launchedStandalone: false,
    displayMode: 'browser',
    paired: false,
    sessionSurvivedReload: false,
};

function mark(id, ok, unknown) {
    const item = document.getElementById(id);
    if (!item) return;

    const glyph = item.querySelector('.mark');
    glyph.textContent = unknown ? '?' : ok ? '✓' : '✗';
    item.className = unknown ? 'unknown' : ok ? 'pass' : 'fail';
}

function detail(id, text) {
    const node = document.getElementById(id);
    if (node) node.textContent = text;
}

function currentDisplayMode() {
    // iOS reports standalone separately from the display-mode media query.
    if (window.navigator.standalone === true) return 'standalone-ios';

    for (const mode of ['standalone', 'minimal-ui', 'fullscreen', 'window-controls-overlay']) {
        try {
            if (window.matchMedia(`(display-mode: ${mode})`).matches) return mode;
        } catch {
            // Some embedded browsers throw on an unknown media feature.
        }
    }
    return 'browser';
}

// A modern iPad reports itself as a Mac; without the touch-point check it is mistaken for a desktop
// and the Add to Home Screen guidance is never shown. Learned from a shipped PWA, not from the spec.
function isIosDevice() {
    const agent = String(navigator.userAgent || '');
    const platform = String(navigator.platform || '');
    return /iPad|iPhone|iPod/.test(agent) ||
        (platform === 'MacIntel' && Number(navigator.maxTouchPoints || 0) > 1);
}

// An in-app browser cannot install a PWA and often cannot register a service worker, so a failure
// here would be blamed on the laptop rather than on the app the link was opened inside.
function embeddedBrowserName() {
    const agent = String(navigator.userAgent || '');
    const signatures = [
        [/Messenger|FBAN|FBAV|FB_IAB|FBIOS/i, 'Messenger or Facebook'],
        [/Instagram/i, 'Instagram'],
        [/TikTok|BytedanceWebview/i, 'TikTok'],
        [/LinkedInApp/i, 'LinkedIn'],
        [/Line\//i, 'LINE'],
        [/Snapchat/i, 'Snapchat'],
        [/\bGSA\//i, 'the Google app'],
    ];

    for (const [pattern, name] of signatures) {
        if (pattern.test(agent)) return name;
    }

    if (/Android/i.test(agent) && (/;\s*wv\)/i.test(agent) || /\bVersion\/4\.0\b.*\bChrome\//i.test(agent))) {
        return 'another app';
    }
    return null;
}

async function checkConnection() {
    state.secureContext = window.isSecureContext === true;
    mark('check-secure', state.secureContext);

    const origin = window.location.origin;
    state.reachedByName = /^https:\/\/gt-[0-9a-f]{8}\.local/i.test(origin);

    mark('check-origin', true);
    mark('check-name', state.reachedByName);

    detail('origin-detail', state.reachedByName
        ? `Reached by name: ${origin}`
        : `Reached by address: ${origin}. The .local name did not resolve, so the IP fallback is in use.`);
}

async function checkInstallation() {
    state.serviceWorkerSupported = 'serviceWorker' in navigator;
    mark('check-sw-support', state.serviceWorkerSupported);

    if (state.serviceWorkerSupported) {
        try {
            // A service worker needs a secure context, which is exactly what this is testing.
            await navigator.serviceWorker.register('sw.js', { scope: './' });
            await navigator.serviceWorker.ready;
            state.serviceWorkerRegistered = true;
        } catch (error) {
            state.serviceWorkerRegistered = false;
            detail('display-detail', `Service worker refused: ${error && error.message ? error.message : error}`);
        }
    }
    mark('check-sw', state.serviceWorkerRegistered);

    try {
        const cache = await caches.open(SHELL_CACHE);
        const cached = await cache.match('index.html') || await cache.match('./');
        state.shellCachedOffline = !!cached;
    } catch {
        state.shellCachedOffline = false;
    }
    mark('check-cache', state.shellCachedOffline);

    state.displayMode = currentDisplayMode();
    state.launchedStandalone = state.displayMode !== 'browser';
    mark('check-standalone', state.launchedStandalone);

    if (!document.getElementById('display-detail').textContent) {
        const embedded = embeddedBrowserName();
        if (embedded && !state.launchedStandalone) {
            detail('display-detail',
                `This page was opened inside ${embedded}. Open it in Safari or Chrome instead: ` +
                'an in-app browser cannot install a home-screen app.');
        } else if (isIosDevice() && !state.launchedStandalone) {
            detail('display-detail',
                'Display mode: browser. On iPhone or iPad use Share → Add to Home Screen, ' +
                'then open it from there and run these checks again.');
        } else {
            detail('display-detail', `Display mode: ${state.displayMode}`);
        }
    }
}

async function checkSession() {
    try {
        const response = await fetch('api/spike/session', { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) throw new Error(`status ${response.status}`);

        const body = await response.json();
        state.paired = body.paired === true;

        // Reaching this on a fresh load means the cookie survived the reload or relaunch, which is
        // the thing DESIGN 18.5 warns must not be assumed between a tab and a home-screen app.
        state.sessionSurvivedReload = state.paired;

        mark('check-paired', state.paired);
        mark('check-session', state.sessionSurvivedReload);

        if (state.paired) detail('pair-detail', `Paired as "${body.label}".`);
    } catch (error) {
        // Offline is an expected state here; the cached shell should still have rendered.
        mark('check-paired', false, true);
        mark('check-session', false, true);
        detail('pair-detail', 'The laptop could not be reached just now.');
    }
}

async function pair(event) {
    event.preventDefault();

    const code = document.getElementById('code').value.replace(/\s+/g, '');
    const label = document.getElementById('label').value;

    try {
        const response = await fetch('api/spike/pair', {
            method: 'POST',
            credentials: 'same-origin',
            cache: 'no-store',
            headers: { 'Content-Type': 'application/json', 'X-GoldenTicket-Spike': '1' },
            body: JSON.stringify({ code, label }),
        });

        const body = await response.json();
        detail('pair-detail', body.message || (body.paired ? 'Paired.' : 'Pairing refused.'));

        state.paired = body.paired === true;
        mark('check-paired', state.paired);

        if (state.paired) document.getElementById('code').value = '';
    } catch (error) {
        detail('pair-detail', 'The laptop could not be reached.');
    }
}

async function send() {
    const payload = {
        label: document.getElementById('label').value || 'unnamed device',
        userAgent: navigator.userAgent,
        origin: window.location.origin,
        secureContext: state.secureContext,
        serviceWorkerSupported: state.serviceWorkerSupported,
        serviceWorkerRegistered: state.serviceWorkerRegistered,
        shellCachedOffline: state.shellCachedOffline,
        displayMode: state.displayMode,
        launchedStandalone: state.launchedStandalone,
        sessionSurvivedReload: state.sessionSurvivedReload,
        notes: document.getElementById('notes').value,
    };

    try {
        const response = await fetch('api/spike/report', {
            method: 'POST',
            credentials: 'same-origin',
            cache: 'no-store',
            headers: { 'Content-Type': 'application/json', 'X-GoldenTicket-Spike': '1' },
            body: JSON.stringify(payload),
        });

        detail('send-detail', response.ok
            ? 'Sent. The laptop has recorded this run.'
            : `The laptop refused the report (status ${response.status}).`);
    } catch (error) {
        detail('send-detail', 'The laptop could not be reached. Reconnect and try again.');
    }
}

async function runChecks() {
    await checkConnection();
    await checkInstallation();
    await checkSession();
}

document.getElementById('pair-form').addEventListener('submit', pair);
document.getElementById('recheck').addEventListener('click', runChecks);
document.getElementById('send').addEventListener('click', send);

runChecks();
