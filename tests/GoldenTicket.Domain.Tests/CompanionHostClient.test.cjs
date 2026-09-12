// Execute the shipped browser scripts against controlled DOM/network/lifecycle fixtures.
// Node's built-in runner only: node --test tests/GoldenTicket.Domain.Tests/CompanionHostClient.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const sourceDir = path.resolve(__dirname, '../../src/GoldenTicket.CompanionHost/wwwroot');
const flush = async () => { for (let i = 0; i < 30; i++) await Promise.resolve(); };
function deferred() { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; }
function page(options = {}) {
  const nodes = new Map(), listeners = {}, intervals = [], timeouts = new Map(), requests = [];
  let nextTimer = 0, uuid = 0, now = Date.now();
  class Node {
    constructor(tag = 'div') { this.tag = tag; this.textContent = ''; this.hidden = false; this.value = ''; this.disabled = false; this.children = []; this.dataset = {}; this.events = {}; }
    addEventListener(event, callback) { this.events[event] = callback; }
    append(...children) { this.children.push(...children); if (this.tag === 'select' && !this.value) this.value = this.children[0]?.value || ''; }
    replaceChildren(...children) { this.children = children; this.textContent = ''; if (this.tag === 'select') this.value = this.children[0]?.value || ''; }
    setAttribute(name, value) { this[name] = value; }
    set innerHTML(value) { throw new Error('Private UI must not interpolate HTML'); }
  }
  const get = id => { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
  const state = {
    paired: options.paired !== false, pending: false, csrf: 'csrf-token', apiVersion: '1', assetsVersion: '2', handoffGeneration: 1,
    snapshot: { canControl: true, revealSeatId: 1, message: 'Pass this device to Alex.', profileId: 'classic-us', manifestHash: 'hash', routes: [],
      game: { sessionId: 'match', stateVersion: 1, activeSeatId: 1, turnNumber: 1, turnPhase: 'TurnStart', seats: [{ seatId: 1, displayName: 'Alex', symbol: 'A', color: 'Blue', routeScore: 0, trainsRemaining: 45 }], pendingClaim: null } }
  };
  const data = { view: { seatId: 1, public: state.snapshot.game, hand: [{ id: 3, kind: 'Red' }], reservedCards: [] }, heldTickets: [{id:'secret', label:'PRIVATE_DESTINATION', points:20}], offeredTickets: [], actions: { mustCommitTicketSelection: false, mustResolvePendingClaim: true } };
  function response(value, ok = true, status = 200) { return { ok, status, json: async () => structuredClone(value) }; }
  const context = vm.createContext({
    console, Promise, AbortController, URL, structuredClone,
    Date: class extends Date { static now() { return now; } },
    crypto: { randomUUID: () => `${String(++uuid).padStart(8, '0')}-abcd-4321-aaaa-bbbbbbbbbbbb` },
    matchMedia: () => ({ matches: !!options.standalone }),
    setTimeout: (fn, ms) => { const id = ++nextTimer; timeouts.set(id, {fn, ms}); return id; }, clearTimeout: id => timeouts.delete(id),
    setInterval: (fn, ms) => intervals.push({fn, ms}),
    document: { hidden: false, getElementById: get, createElement: tag => new Node(tag), addEventListener: (name, fn) => listeners['document:' + name] = fn },
    window: { isSecureContext: options.secure !== false, addEventListener: (name, fn) => listeners['window:' + name] = fn },
    navigator: { serviceWorker: { register: async () => ({}), ready: options.workerPending ? new Promise(() => {}) : Promise.resolve({}) } },
    caches: { open: async () => ({ match: async asset => options.missingAsset === asset ? undefined : {ok:true} }) },
    fetch: async (url, request) => {
      requests.push({url, request});
      if (options.offline) throw new Error('Offline');
      if (options.fetch) { const result = options.fetch(url, request); if (result !== undefined) return await result; }
      if (url === '/api/session') return response(state);
      if (url === '/api/reveal') { state.handoffGeneration++; return response({grant:'private-grant', expiresAt:new Date(now + 30000).toISOString(), handoffGeneration: state.handoffGeneration, data}); }
      if (url === '/api/command') return response({ accepted:true, message:'Choice saved on laptop.' });
      if (url === '/api/pair') return response({pending:true, identity:'1234'});
      return response({hidden:true});
    }
  });
  let source = fs.readFileSync(path.join(sourceDir, 'app.js'), 'utf8');
  source = source.replace(/\}\)\(\);\s*$/, 'globalThis.clientTest = { poll, reveal, hide, submit, clearPrivate, state: () => ({paired, busy, privateData, grant, revealGeneration, handoffGeneration, shellReady}) }; })();');
  vm.runInContext(source, context);
  return { context, state, data, get, requests, options, timeouts, intervals, response, client: context.clientTest,
    event: async (scope, name) => { await listeners[scope + ':' + name]?.(); await flush(); },
    click: async id => { await get(id).events.click?.(); await flush(); },
    advance: ms => { now += ms; }, nodes };
}

test('pairing waits for a fully cached shell and chosen final launch context', async () => {
  const p = page({paired:false}); await flush();
  assert.equal(p.client.state().shellReady, true); assert.equal(p.get('pair-form').hidden, true);
  await p.click('browser-mode'); assert.equal(p.get('pair-form').hidden, false);
});
test('missing cached asset and insecure contexts never offer pairing', async () => {
  for (const options of [{missingAsset:'/companion/app.js'}, {secure:false}]) {
    const p = page({...options, paired:false, standalone:true}); await flush();
    assert.equal(p.client.state().shellReady, false); assert.equal(p.get('pair-form').hidden, true);
  }
});
test('private reveal renders only the permitted view and Hide clears DOM and memory', async () => {
  const p = page(); await flush(); await p.client.reveal();
  assert.equal(p.get('private').hidden, false); assert.ok(p.get('private').children.length);
  assert.equal(p.client.state().privateData.heldTickets[0].label, 'PRIVATE_DESTINATION');
  await p.click('hide'); assert.equal(p.get('private').hidden, true); assert.equal(p.get('private').children.length, 0);
  assert.equal(p.client.state().privateData, null); assert.equal(p.client.state().grant, null);
  assert.ok(p.requests.some(r => r.url === '/api/hide'));
});
test('delayed private response cannot uncover a hand after Hide', async () => {
  const reply = deferred(); const p = page({fetch:url => url === '/api/reveal' ? reply.promise : undefined}); await flush();
  const operation = p.client.reveal(); await flush(); p.client.hide();
  reply.resolve(p.response({grant:'late',expiresAt:new Date(Date.now()+30000).toISOString(),handoffGeneration:2,data:p.data})); await operation;
  assert.equal(p.client.state().privateData, null); assert.equal(p.get('private').hidden, true);
});
test('background then foreground always returns covered even with delayed reveal', async () => {
  const reply = deferred(); const p = page({fetch:url => url === '/api/reveal' ? reply.promise : undefined}); await flush();
  const operation = p.client.reveal(); await flush(); p.context.document.hidden = true; await p.event('document','visibilitychange');
  reply.resolve(p.response({grant:'late',expiresAt:new Date(Date.now()+30000).toISOString(),handoffGeneration:2,data:p.data})); await operation;
  p.context.document.hidden = false; await p.event('document','visibilitychange');
  assert.equal(p.client.state().grant, null); assert.equal(p.get('private').children.length,0);
});
test('a newer laptop handoff observed during reveal rejects the delayed old private response', async () => {
  const reply=deferred(); const p=page({fetch:url=>url==='/api/reveal'?reply.promise:undefined}); await flush();
  const operation=p.client.reveal(); await flush();
  // Server granted generation2, then laptop Hide revoked it as generation3 before the response arrived.
  p.state.handoffGeneration=3; await p.client.poll();
  reply.resolve(p.response({grant:'revoked',expiresAt:new Date(Date.now()+30000).toISOString(),handoffGeneration:2,data:p.data})); await operation;
  assert.equal(p.client.state().privateData,null); assert.equal(p.get('private').hidden,true);
});
test('a poll observing this reveal own generation does not discard its valid response', async () => {
  const reply=deferred(); const p=page({fetch:url=>url==='/api/reveal'?reply.promise:undefined}); await flush();
  const operation=p.client.reveal(); await flush(); p.state.handoffGeneration=2; await p.client.poll();
  reply.resolve(p.response({grant:'valid',expiresAt:new Date(Date.now()+30000).toISOString(),handoffGeneration:2,data:p.data})); await operation;
  assert.equal(p.get('private').hidden,false); assert.equal(p.client.state().grant,'valid');
});
test('an old poll cannot rewind handoff generation and re-pairing can reset it for a restarted host', async () => {
  const p=page(); await flush(); await p.client.reveal();
  p.state.handoffGeneration=1; await p.client.poll();
  assert.equal(p.client.state().handoffGeneration,2); assert.equal(p.get('private').hidden,false);
  p.state.paired=false; await p.client.poll(); assert.equal(p.client.state().handoffGeneration,-1);
  p.state.paired=true; p.state.handoffGeneration=1; await p.client.poll();
  assert.equal(p.client.state().handoffGeneration,1); await p.client.reveal(); assert.equal(p.get('private').hidden,false);
});
test('turn revision change, revoke, and connection failure clear private views', async () => {
  for (const change of ['revision', 'revoked', 'offline']) {
    const p = page(); await flush(); await p.client.reveal();
    if(change === 'revision') p.state.snapshot.game.stateVersion++;
    if(change === 'revoked') p.state.paired = false;
    if(change === 'offline') p.options.offline = true;
    await p.client.poll(); assert.equal(p.client.state().privateData,null); assert.equal(p.get('private').hidden,true);
  }
});
test('an incompatible shell version covers cards and blocks reveal until reload', async () => {
  const p=page(); await flush(); await p.client.reveal(); p.state.assetsVersion='1'; await p.client.poll();
  assert.equal(p.client.state().privateData,null); assert.equal(p.get('reveal').disabled,true);
  assert.match(p.get('notice').textContent,/needs an update/);
});
test('blur and pointer cancellation clear privacy immediately', async () => {
  for(const [scope,event] of [['window','blur'],['window','pagehide'],['document','pointercancel']]) {
    const p = page(); await flush(); await p.client.reveal(); await p.event(scope,event);
    assert.equal(p.get('private').children.length,0); assert.equal(p.client.state().grant,null);
  }
});
test('heartbeat timeout and grant expiry cover without waiting for network', async () => {
  for(const elapsed of [6000,30000]) {
    const p=page(); await flush(); await p.client.reveal(); p.advance(elapsed);
    p.intervals.find(t=>t.ms===500).fn(); assert.equal(p.client.state().privateData,null);
  }
});
test('command clears private UI before transport and duplicate tap cannot submit twice', async () => {
  const result = deferred(); const p=page({fetch:url=>url==='/api/command'?result.promise:undefined}); await flush(); await p.client.reveal();
  const sending=p.client.submit('drawTrain',{slot:null}); await flush();
  assert.equal(p.get('private').children.length,0); assert.equal(p.client.state().grant,null);
  await p.client.submit('drawTrain',{slot:null}); assert.equal(p.requests.filter(r=>r.url==='/api/command').length,1);
  result.resolve(p.response({accepted:true,message:'Saved.'})); await sending;
  assert.equal(p.client.state().busy,false); assert.equal(p.client.state().privateData,null);
});
test('untrusted player text is assigned as text and never interpreted as HTML', async () => {
  const p=page(); p.state.snapshot.game.seats[0].displayName='<img src=x onerror=alert(1)>'; await flush(); await p.client.reveal();
  assert.equal(p.get('private').hidden,false); // Node.innerHTML setter would throw on interpolation.
});
test('worker caches only named shell assets and never intercepts APIs or arbitrary navigation', async () => {
  const handlers={}, calls=[], deleted=[];
  const context=vm.createContext({URL, Promise,
    self:{location:{origin:'https://local.test'},addEventListener:(name,fn)=>handlers[name]=fn,skipWaiting:async()=>{},clients:{claim:async()=>{}}},
    caches:{open:async()=>({addAll:async paths=>calls.push(...paths),match:async()=>({cached:true})}),keys:async()=>['goldenticket-companion-shell-v1','goldenticket-companion-shell-v2','other-app'],delete:async key=>deleted.push(key)},fetch:async()=>({network:true})});
  vm.runInContext(fs.readFileSync(path.join(sourceDir,'sw.js'),'utf8'),context);
  let installation; handlers.install({waitUntil:promise=>installation=promise}); await installation;
  let activation; handlers.activate({waitUntil:promise=>activation=promise}); await activation;
  assert.deepEqual(deleted,['goldenticket-companion-shell-v1']);
  assert.ok(calls.includes('/companion/app.js')); assert.equal(calls.some(p=>p.startsWith('/api')),false);
  for(const [url,method] of [['https://local.test/api/reveal','POST'],['https://local.test/api/session','GET'],['https://local.test/companion/private','GET'],['https://local.test/companion/?secret=1','GET'],['https://evil.test/companion/','GET']]) {
    let intercepted=false; handlers.fetch({request:{url,method},respondWith:()=>intercepted=true}); assert.equal(intercepted,false,url);
  }
  let shell; handlers.fetch({request:{url:'https://local.test/companion/',method:'GET'},respondWith:p=>shell=p}); assert.equal((await shell).cached,true);
});
