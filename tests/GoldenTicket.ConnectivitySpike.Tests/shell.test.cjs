// Behavioural checks of the shipped scripts. Run with Node's built-in test runner; no npm packages.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const shell = path.resolve(__dirname, '../../tools/GoldenTicket.ConnectivitySpike/wwwroot');

function page(options = {}) {
    const nodes = new Map();
    const timers = new Map();
    let nextTimer = 0;
    let paired = !!options.paired;
    const get = id => {
        if (!nodes.has(id)) nodes.set(id, {
            textContent: '', value: '', hidden: false, className: '', mark: {},
            addEventListener() {}, querySelector() { return this.mark; },
        });
        return nodes.get(id);
    };
    const context = vm.createContext({
        URL, console, Promise, AbortController,
        setTimeout: fn => { const id = ++nextTimer; timers.set(id, fn); return id; },
        clearTimeout: id => timers.delete(id),
        document: { getElementById: get, querySelectorAll: () => [] },
        window: { isSecureContext: true, location: { origin: 'https://gt-12345678.local:8443',
            hostname: 'gt-12345678.local', pathname: '/' }, matchMedia: () => ({matches: false}) },
        navigator: { userAgent: options.embedded ? 'Android; wv) Version/4.0 Chrome/140' : 'Android Chrome/140',
            serviceWorker: { register: () => Promise.resolve({}),
                ready: options.pendingWorker ? new Promise(() => {}) : Promise.resolve({}) } },
        caches: { open: async () => ({ match: async asset =>
            options.incompleteCache && asset !== 'index.html' ? undefined : { ok: true } }) },
        fetch: async () => {
            if (options.offline) throw new Error('offline');
            return { ok: true, json: async () => ({ paired, label: 'test' }) };
        },
    });
    context.window.navigator = context.navigator;
    const source = fs.readFileSync(path.join(shell, 'app.js'), 'utf8');
    assert.match(source, /runChecks\(\);\s*$/);
    vm.runInContext(source.replace(/runChecks\(\);\s*$/, 'globalThis.initialCheck = runChecks();'), context);
    return { context, get, options, timers, setPaired: value => { paired = value; },
        run: code => vm.runInContext(code, context) };
}

test('embedded-browser handoff is visible even when the worker never activates', async () => {
    const p = page({ embedded: true, pendingWorker: true });
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.equal(p.get('checks').hidden, true);
    assert.equal(p.get('handoff').hidden, false);
    assert.match(p.get('handoff-title').textContent, /Chrome/);
});

test('worker activation times out and leaves recheck and pairing diagnostics usable', async () => {
    const p = page({ pendingWorker: true });
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.ok(p.timers.size > 0, 'a failed worker installation must have a deadline');
    for (const fn of [...p.timers.values()]) fn();
    await p.context.initialCheck;
    assert.equal(p.run('state.serviceWorkerRegistered'), false);
    assert.match(p.get('display-detail').textContent, /timed out/i);
    assert.equal(p.get('pair-blocked').hidden, false);
});

test('a manual recheck after pairing does not prove persistence across reload', async () => {
    const p = page();
    await p.context.initialCheck;
    p.setPaired(true);
    await p.run('runChecks()');
    assert.equal(p.run('state.paired'), true);
    assert.equal(p.run('state.sessionSurvivedReload'), false);
});

test('a fresh page with a valid session does prove reload persistence', async () => {
    const p = page({ paired: true });
    await p.context.initialCheck;
    assert.equal(p.run('state.sessionSurvivedReload'), true);
});

test('losing the laptop clears stale successful report fields', async () => {
    const p = page({ paired: true });
    await p.context.initialCheck;
    p.options.offline = true;
    await p.run('runChecks()');
    assert.equal(p.run('state.paired'), false);
    assert.equal(p.run('state.sessionSurvivedReload'), false);
    assert.equal(p.get('check-origin').className, 'unknown');
});

test('an index page alone is not reported as a complete offline shell', async () => {
    const p = page({ incompleteCache: true });
    await p.context.initialCheck;
    assert.equal(p.run('state.shellCachedOffline'), false);
});

test('re-pairing in the same page clears evidence from an older loaded session', async () => {
    const p = page({ paired: true });
    await p.context.initialCheck;
    await p.run('pair({ preventDefault() {} })');
    await p.run('runChecks()');
    assert.equal(p.run('state.paired'), true);
    assert.equal(p.run('state.sessionSurvivedReload'), false);
});

test('pairing during initial worker activation does not become reload evidence', async () => {
    const p = page({ pendingWorker: true });
    for (let i = 0; i < 10; i++) await Promise.resolve();
    p.setPaired(true);
    await p.run('pair({ preventDefault() {} })');
    for (const fn of [...p.timers.values()]) fn();
    await p.context.initialCheck;
    assert.equal(p.run('state.paired'), true);
    assert.equal(p.run('state.sessionSurvivedReload'), false);
});

test('a stale session read cannot overwrite successful pairing', async () => {
    const p = page();
    await p.context.initialCheck;
    let releaseRead;
    p.context.fetch = address => address.endsWith('/session')
        ? new Promise(resolve => { releaseRead = resolve; })
        : Promise.resolve({ok: true, json: async () => ({paired: true})});
    const read = p.run('checkSession()');
    await p.run('pair({ preventDefault() {} })');
    releaseRead({ok: true, json: async () => ({paired: false})});
    await read;
    assert.equal(p.run('state.paired'), true);
    assert.equal(p.get('check-paired').className, 'pass');
});

test('a double tap sends only one single-use pairing request', async () => {
    const p = page();
    await p.context.initialCheck;
    let requests = 0;
    let release;
    p.context.fetch = () => { requests++; return new Promise(resolve => { release = resolve; }); };
    const first = p.run('pair({ preventDefault() {} })');
    await p.run('pair({ preventDefault() {} })');
    assert.equal(requests, 1);
    release({ok: true, json: async () => ({ paired: true })});
    await first;
    assert.equal(p.run('state.paired'), true);
});

test('a hanging session request is aborted and does not leave Recheck locked', async () => {
    const p = page();
    await p.context.initialCheck;
    p.context.fetch = (_, {signal}) => new Promise((_, reject) =>
        signal.addEventListener('abort', () => reject(new Error('aborted'))));
    const checks = p.run('runChecks()');
    for (let i = 0; i < 30; i++) await Promise.resolve();
    assert.ok(p.timers.size > 0);
    for (const fn of [...p.timers.values()]) fn();
    await checks;
    assert.equal(p.run('checksRunning'), false);
    assert.equal(p.get('check-origin').className, 'unknown');
});

function worker() {
    const handlers = {};
    const deleted = [];
    const opened = [];
    const fetched = [];
    const context = vm.createContext({ URL, Promise,
        self: { location: { origin: 'https://gt-12345678.local:8443',
            href: 'https://gt-12345678.local:8443/sw.js' },
            addEventListener: (name, fn) => { handlers[name] = fn; },
            skipWaiting() {}, clients: { claim() {} } },
        caches: { keys: async () => ['gt-spike-shell-v1', 'unrelated-cache'],
            delete: async name => { deleted.push(name); },
            match: async () => { throw new Error('must not read every cache on this origin'); },
            open: async name => { opened.push(name); return { match: async () => 'cached shell' }; } },
        fetch: async req => { fetched.push(req); return 'network'; },
    });
    vm.runInContext(fs.readFileSync(path.join(shell, 'sw.js'), 'utf8'), context);
    function request(url, method = 'GET') {
        let response;
        handlers.fetch({ request: {url, method}, respondWith: value => { response = value; } });
        return response;
    }
    return { handlers, deleted, opened, fetched, request };
}

test('worker upgrades delete only this spike\'s old caches', async () => {
    const w = worker();
    let completion;
    w.handlers.activate({ waitUntil: value => { completion = value; } });
    await completion;
    assert.deepEqual(w.deleted, ['gt-spike-shell-v1']);
});

test('only allowlisted shell GETs use the current shell cache', async () => {
    const w = worker();
    assert.equal(await w.request('https://gt-12345678.local:8443/app.js'), 'cached shell');
    assert.equal(w.opened.length, 1);
    assert.match(w.opened[0], /^gt-spike-shell-v/);
});

test('API, unknown paths, query strings, POSTs and foreign origins bypass shell handling', () => {
    const w = worker();
    for (const url of [
        'https://gt-12345678.local:8443/api/spike/session',
        'https://gt-12345678.local:8443/private-data.json',
        'https://gt-12345678.local:8443/app.js?credential=secret',
        'https://elsewhere.example/app.js',
    ]) assert.equal(w.request(url), undefined, url);
    assert.equal(w.request('https://gt-12345678.local:8443/', 'POST'), undefined);
});
