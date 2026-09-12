// GoldenTicket M0 connectivity spike, device side.
//
// Four questions (DESIGN 22.7): is this a trusted secure context, was the laptop reached by its
// local name, did the shell install and cache offline, and does a pairing round-trip work in the
// context the device finally launches from?
//
// No game state, no private information, no persistence beyond the pairing cookie the laptop sets.

const SHELL_CACHE = 'gt-spike-shell-v2';

const state = {
    secureContext: false,
    reachedByName: false,
    serviceWorkerSupported: false,
    serviceWorkerRegistered: false,
    shellCachedOffline: false,
    launchedStandalone: false,
    displayMode: 'browser',
    embeddedBrowser: '',
    paired: false,
    sessionSurvivedReload: false,
    pairAnyway: false,
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

function show(id, visible) {
    const node = document.getElementById(id);
    if (node) node.hidden = !visible;
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

function isAndroidDevice() {
    return /Android/i.test(String(navigator.userAgent || ''));
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
        [/Pinterest/i, 'Pinterest'],
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

// The address a player should end up on, without any fragment or query. This is the only thing the
// laptop's QR code carries (DESIGN 18.5), and the only thing worth copying between browsers.
function landingAddress() {
    return window.location.origin + window.location.pathname;
}

// Android's intent scheme hands a URL to Chrome specifically. There is no iOS equivalent, which is
// why the iOS path below is written as instructions rather than a button.
function androidChromeIntentUrl(address) {
    const target = new URL(address);
    if (!/^https?:$/.test(target.protocol)) throw new TypeError('The handoff target must be HTTP or HTTPS.');

    target.hash = '';
    const scheme = target.protocol.slice(0, -1);
    return `intent://${target.host}${target.pathname}${target.search}` +
        `#Intent;scheme=${scheme};package=com.android.chrome;end`;
}

async function copyAddress(button) {
    const target = button.getAttribute('data-copy');
    const address = landingAddress();

    try {
        await navigator.clipboard.writeText(address);
        detail(target, 'Copied. Paste it into Chrome or Safari.');
    } catch {
        // Clipboard access needs a secure context and, in some browsers, a very recent tap. Selecting
        // the text by hand always works.
        detail(target, `Copying was refused. Press and hold ${address} to select and copy it.`);
    }
}

// ---- The handoff gate ---------------------------------------------------------------------------

function renderHandoff() {
    for (const node of document.querySelectorAll('.address')) {
        node.textContent = landingAddress();
    }

    if (!state.embeddedBrowser || state.launchedStandalone) {
        show('handoff', false);
        show('checks', true);
        return;
    }

    show('handoff', true);
    show('checks', false);

    const open = document.getElementById('handoff-open');

    if (isAndroidDevice()) {
        detail('handoff-title', 'Open this in Chrome');
        detail('handoff-message',
            `This page was opened inside ${state.embeddedBrowser}. That browser cannot add an app to ` +
            'the home screen, so the checks below would fail for a reason that has nothing to do with ' +
            'the laptop.');
        detail('handoff-instructions',
            'Tap Open in Chrome. If it stays inside this app, use the app’s own menu and choose ' +
            'Open in Chrome or Open in external browser.');

        try {
            open.href = androidChromeIntentUrl(landingAddress());
            open.hidden = false;
        } catch {
            open.hidden = true;
        }
    } else {
        detail('handoff-title', isIosDevice() ? 'Open this in Safari' : 'Open this in a real browser');
        detail('handoff-message',
            `This page was opened inside ${state.embeddedBrowser}. That browser cannot add an app to ` +
            'the home screen, so the checks below would fail for a reason that has nothing to do with ' +
            'the laptop.');
        detail('handoff-instructions', isIosDevice()
            ? 'Use this app’s menu or its Share control and choose Open in Safari. If neither ' +
              'offers it, copy the address below and paste it into Safari.'
            : 'Copy the address below and paste it into Chrome or Safari.');
        open.hidden = true;
    }
}

// ---- The four questions --------------------------------------------------------------------------

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
        detail('display-detail', `Display mode: ${state.displayMode}`);
    }

    detail('install-instructions', installInstructions());
}

function installInstructions() {
    if (state.launchedStandalone) return 'Installed and launched from the home screen. Pair here.';

    if (state.embeddedBrowser) {
        return `This page is running inside ${state.embeddedBrowser}, which cannot install an app. ` +
            'Open it in Chrome or Safari first.';
    }

    if (isIosDevice()) {
        return 'iPhone and iPad: tap Share, then Add to Home Screen. Close this tab, open the new ' +
            'icon from the home screen, and carry on there. Safari and the installed app do not ' +
            'share cookies, so pairing has to happen in the installed app.';
    }

    if (isAndroidDevice()) {
        return 'Android: accept the install prompt, or open the browser menu and choose Install app ' +
            'or Add to Home screen. Then open the new icon and carry on there.';
    }

    return 'Use the browser’s install control, usually in or beside the address bar, then open ' +
        'the installed app and carry on there.';
}

function renderPairGate() {
    const blocked = !state.launchedStandalone && !state.pairAnyway;
    show('pair-blocked', blocked);
    show('pair-area', !blocked);
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
        embeddedBrowser: state.embeddedBrowser,
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
    // Detected first: the installation guidance below reads it, and a page opened inside another
    // app needs to say so rather than offering an install control that browser does not have.
    state.embeddedBrowser = embeddedBrowserName() || '';

    await checkConnection();
    await checkInstallation();
    renderHandoff();
    renderPairGate();
    await checkSession();
}

for (const button of document.querySelectorAll('button[data-copy]')) {
    button.addEventListener('click', () => copyAddress(button));
}

document.getElementById('pair-form').addEventListener('submit', pair);
document.getElementById('recheck').addEventListener('click', runChecks);
document.getElementById('send').addEventListener('click', send);

document.getElementById('handoff-continue').addEventListener('click', () => {
    show('handoff', false);
    show('checks', true);
});

document.getElementById('pair-anyway').addEventListener('click', () => {
    state.pairAnyway = true;
    renderPairGate();
});

runChecks();
