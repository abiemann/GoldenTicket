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
  const nodes = new Map(), listeners = {}, intervals = [], timeouts = new Map(), requests = [], objectUrls = [], revokedUrls = [];
  let nextTimer = 0, uuid = 0, now = Date.now();
  class Node {
    constructor(tag = 'div') { this.tag = tag; this.textContent = ''; this.hidden = false; this.value = ''; this.disabled = false; this.children = []; this.dataset = {}; this.events = {}; }
    addEventListener(event, callback) { this.events[event] = callback; }
    append(...children) { this.children.push(...children); if (this.tag === 'select' && !this.value) this.value = this.children[0]?.value || ''; }
    replaceChildren(...children) { this.children = children; this.textContent = ''; if (this.tag === 'select') this.value = this.children[0]?.value || ''; }
    setAttribute(name, value) { this[name] = value; }
    removeAttribute(name) { delete this[name]; }
    set innerHTML(value) { throw new Error('Private UI must not interpolate HTML'); }
  }
  const get = id => { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
  const state = {
    paired: options.paired !== false, pending: false, csrf: 'csrf-token', apiVersion: '1', assetsVersion: '8', handoffGeneration: 1,
    snapshot: { canControl: true, revealSeatId: 1, message: 'Pass this device to Alex.', profileId: 'classic-us', manifestHash: 'hash', routes: [],
      game: { sessionId: 'match', stateVersion: 1, activeSeatId: 1, turnNumber: 1, turnPhase: 'TurnStart', seats: [{ seatId: 1, displayName: 'Alex', symbol: 'A', color: 'Blue', routeScore: 0, trainsRemaining: 45 }], pendingClaim: null } }
  };
  const data = { view: { seatId: 1, public: state.snapshot.game, hand: [{ id: 3, kind: 'Red' }], reservedCards: [] }, heldTickets: [{id:'secret', label:'PRIVATE_DESTINATION', points:20}], offeredTickets: [], actions: { mustCommitTicketSelection: false, mustResolvePendingClaim: true } };
  function response(value, ok = true, status = 200) { return { ok, status, json: async () => structuredClone(value) }; }
  const context = vm.createContext({
    console, Promise, AbortController, Blob, Uint8Array, structuredClone,
    URL: class extends URL { static createObjectURL(blob) { const url = `blob:results-${objectUrls.length}`; objectUrls.push({url, blob}); return url; } static revokeObjectURL(url) { revokedUrls.push(url); } },
    Date: class extends Date { static now() { return now; } },
    // getRandomValues remains available in an insecure context; randomUUID does not.
    crypto: { getRandomValues: bytes => { bytes.fill(++uuid); return bytes; } },
    setTimeout: (fn, ms) => { const id = ++nextTimer; timeouts.set(id, {fn, ms}); return id; }, clearTimeout: id => timeouts.delete(id),
    setInterval: (fn, ms) => intervals.push({fn, ms}),
    document: { hidden: false, getElementById: get, createElement: tag => new Node(tag), addEventListener: (name, fn) => listeners['document:' + name] = fn },
    window: { isSecureContext: options.secure !== false, addEventListener: (name, fn) => listeners['window:' + name] = fn },
    navigator: { onLine: options.internetAvailable !== false },
    fetch: async (url, request) => {
      requests.push({url, request});
      if (options.offline) throw new Error('Offline');
      if (options.fetch) { const result = options.fetch(url, request); if (result !== undefined) return await result; }
      if (url === '/api/session') return response(state);
      if (url === '/api/reveal') { state.handoffGeneration++; return response({grant:'private-grant', handoffGeneration: state.handoffGeneration, data}); }
      if (url === '/api/command') return response({ accepted:true, message:'Choice saved on laptop.' });
      if (url === '/api/pair') return response({pending:true, identity:'1234'});
      return response({hidden:true});
    }
  });
  let source = fs.readFileSync(path.join(sourceDir, 'app.js'), 'utf8');
  source = source.replace(/\}\)\(\);\s*$/, 'globalThis.clientTest = { poll, reveal, hide, submit, clearPrivate, state: () => ({paired, busy, privateData, grant, revealGeneration, handoffGeneration, resultKey, resultUrl}) }; })();');
  vm.runInContext(source, context);
  return { context, state, data, get, requests, options, timeouts, intervals, response, objectUrls, revokedUrls, client: context.clientTest,
    event: async (scope, name, event = {}) => { await listeners[scope + ':' + name]?.(event); await flush(); },
    click: async id => { await get(id).events.click?.(); await flush(); },
    advance: ms => { now += ms; }, nodes };
}

// Advance time without interaction while keeping the laptop connection fresh.
async function elapse(p, ms) {
  while (ms > 0) {
    const step = Math.min(ms, 2000); p.advance(step); ms -= step;
    await p.client.poll(); p.intervals.find(t => t.ms === 500).fn(); await flush();
  }
}
function descendants(node) { return [node, ...node.children.flatMap(descendants)]; }
async function ticketOffer(p) {
  await flush();
  p.data.offeredTickets = [{id:'first',label:'First destination',points:5},{id:'second',label:'Second destination',points:7}];
  p.data.minimumKeep = 1; p.data.actions = {mustCommitTicketSelection:true};
  await p.client.reveal();
  return descendants(p.get('private')).filter(node => node.type === 'checkbox');
}
async function checkTicket(node, checked) { node.checked = checked; node.events.change(); await flush(); }
function cameraTurn(p) {
  p.state.snapshot.game.faceUp=['Red','Green','White','Pink','Blue'];
  p.state.snapshot.routes=[{id:'first-route',label:'Calgary – Helena',length:2},{id:'other-route',label:'Seattle – Vancouver',length:1}];
  p.state.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:false,message:null,detectedRoute:null};
  p.data.actions={mustCommitTicketSelection:false,mustResolvePendingClaim:false,canDrawBlindTrainCard:true,canRequestTicketOffer:true,drawableFaceUpSlots:[0,1,2,3,4],claims:[
    {routeId:'first-route',length:2,payments:[{color:'Red',colorCards:2,locomotives:0},{color:'Red',colorCards:1,locomotives:1}]},
    {routeId:'other-route',length:1,payments:[{color:'Blue',colorCards:1,locomotives:0}]}
  ]};
}
function detect(p, overrides={}) {
  p.state.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:null,detectedRoute:{proposalId:'placement-1',routeId:'first-route',label:'Calgary – Helena',length:2,ready:true,...overrides}};
}
function privateNode(p, name) { return descendants(p.get('private')).find(node=>node['aria-label']===name); }

test('joining is ready immediately and a pending request cannot be submitted again', async () => {
  const p = page({paired:false}); await flush();
  assert.equal(p.get('pair-form').hidden, false);
  p.state.pending=true; await p.client.poll();
  assert.equal(p.get('pair-form').hidden,true);
  await p.get('pair-form').events.submit({preventDefault(){}});
  assert.equal(p.requests.some(r=>r.url==='/api/pair'),false);
});
test('LAN gameplay remains available when the browser reports no Internet connection', async () => {
  // Browser/OS connectivity probes may report offline on a working Wi-Fi LAN without WAN access.
  // The laptop's successful responses, not navigator.onLine, decide whether gameplay is possible.
  const p = page({internetAvailable:false}); await flush();
  assert.equal(p.get('reveal').disabled,false);
  await p.client.reveal(); assert.equal(p.get('private').hidden,false);
  await p.event('window','offline'); assert.equal(p.get('private').hidden,true);
  await p.client.poll(); assert.equal(p.get('reveal').disabled,false);
  await p.client.reveal(); await p.client.submit('drawTrain',{slot:null});
  assert.equal(p.requests.filter(r=>r.url==='/api/command').length,1);
  assert.match(p.get('notice').textContent,/saved/i);
  assert.equal(p.client.state().privateData,null);
});
test('the complete companion request flow stays on the laptop origin', async () => {
  const p=page({paired:false}); await flush();
  p.get('pair-code').value='123456';
  await p.get('pair-form').events.submit({preventDefault(){}});
  p.state.paired=true; await p.client.poll(); await p.client.reveal();
  await p.client.submit('drawTrain',{slot:null}); await p.client.reveal(); await p.click('hide');
  assert.deepEqual([...new Set(p.requests.map(r=>r.url))].sort(),['/api/command','/api/hide','/api/pair','/api/reveal','/api/session']);
  for(const {url,request} of p.requests) {
    const destination=new URL(url,'http://192.168.50.2:8080');
    assert.equal(destination.origin,'http://192.168.50.2:8080');
    assert.equal(request.credentials,'same-origin'); assert.equal(request.cache,'no-store');
  }
});
test('HTTP Quick play offers joining immediately without secure-context APIs or installation', async () => {
  for (const secure of [false, true]) {
    const p = page({paired:false, secure}); await flush();
    assert.equal(p.get('pair-form').hidden,false);
    p.get('pair-code').value='123456';
    await p.get('pair-form').events.submit({preventDefault(){}});
    const request = p.requests.find(r=>r.url==='/api/pair');
    assert.equal(JSON.parse(request.request.body).code,'123456');
    assert.match(JSON.parse(request.request.body).tab,/^[a-f0-9]{32}$/);
    p.state.paired=true; await p.client.poll(); await p.client.reveal();
    assert.equal(p.get('private').hidden,false);
    assert.equal(p.get('hide').hidden,false);
    await p.client.submit('drawTrain',{slot:null});
    const command = p.requests.find(r=>r.url==='/api/command');
    assert.match(JSON.parse(command.request.body).command.commandId,/^[a-f0-9]{32}$/);
    assert.notEqual(JSON.parse(command.request.body).command.commandId,JSON.parse(request.request.body).tab);
    assert.equal(p.get('private').hidden,true);
    await p.client.reveal(); await p.click('hide');
    assert.equal(p.client.state().privateData,null);
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

test('public placement, correction and scoring instructions update while cards remain covered', async () => {
  const p=page(); await flush(); await p.client.reveal();
  p.state.snapshot.canControl=false; p.state.snapshot.revealSeatId=null;
  p.state.snapshot.message='Follow the current instructions on the laptop.';
  const version=p.state.snapshot.game.stateVersion, reveals=p.requests.filter(r=>r.url==='/api/reveal').length;
  for(const instruction of [
    "Place Computer 1's 1 Green train on Dallas - Houston (lane A). The camera will check its position and continue automatically.",
    "Remove the extra Green train from Dallas - Oklahoma City (lane A).",
    "Move Computer 1's Green score marker to 1."
  ]) {
    p.state.snapshot.guidance={title:'Computer 1',instruction}; await p.client.poll();
    assert.equal(p.state.snapshot.game.stateVersion,version,'Camera guidance can change without a game revision');
    assert.equal(p.get('handoff').textContent,'Computer 1');
    assert.equal(p.get('curtain-detail').textContent,instruction);
    assert.equal(p.get('public-instruction').textContent,instruction);
    assert.equal(p.get('private').children.length,0); assert.equal(p.client.state().grant,null);
    assert.equal(p.get('reveal').disabled,true); await p.client.reveal();
  }
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,reveals);
  assert.equal(p.requests.filter(r=>r.url==='/api/command').length,0);
  p.state.snapshot.guidance=null; p.state.snapshot.canControl=true; p.state.snapshot.revealSeatId=1;
  p.state.snapshot.message='Pass this device to Alex.'; await p.client.poll();
  assert.equal(p.get('handoff').textContent,'Pass this device to Alex.');
  assert.match(p.get('curtain-detail').textContent,/Reveal only when it is your turn/);
  assert.equal(p.get('public-instruction').textContent,'Turn 1 · Turn Start');
  assert.equal(p.get('private').children.length,0); assert.equal(p.get('reveal').disabled,false);
  await p.client.reveal(); assert.equal(p.get('private').hidden,false);
});

test('shared guidance updates pending placement without rebuilding or revealing private cards', async () => {
  const p=page(); await flush();
  p.state.snapshot.guidance={title:'Alex',instruction:'Place 2 Blue trains on Calgary - Helena.'};
  await p.client.poll(); await p.client.reveal();
  const node=descendants(p.get('private')).find(node=>node.textContent===p.state.snapshot.guidance.instruction);
  assert.ok(node); const hand=p.get('private').children[3];
  p.state.snapshot.guidance.instruction='Restore the Blue trains to Calgary - Helena.'; await p.client.poll();
  assert.equal(node.textContent,p.state.snapshot.guidance.instruction); assert.equal(p.get('private').children[3],hand);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
  delete p.state.snapshot.guidance; await p.client.poll();
  assert.equal(node.textContent,'Follow the placement instructions on the laptop.');
  await p.click('hide');
  p.state.snapshot.guidance={title:'Alex',instruction:'Move the Blue score marker.'}; await p.client.poll();
  assert.equal(p.get('private').children.length,0); assert.equal(p.get('curtain-detail').textContent,'Move the Blue score marker.');
});
test('delayed private response cannot uncover a hand after Hide', async () => {
  const reply = deferred(); const p = page({fetch:url => url === '/api/reveal' ? reply.promise : undefined}); await flush();
  const operation = p.client.reveal(); await flush(); p.client.hide();
  assert.ok([...p.timeouts.values()].some(timer=>timer.ms===5000),'Reveal keeps its five-second timeout');
  reply.resolve(p.response({grant:'late',handoffGeneration:2,data:p.data})); await operation;
  assert.equal(p.client.state().privateData, null); assert.equal(p.get('private').hidden, true);
});
test('background then foreground always returns covered even with delayed reveal', async () => {
  const reply = deferred(); const p = page({fetch:url => url === '/api/reveal' ? reply.promise : undefined}); await flush();
  const operation = p.client.reveal(); await flush(); p.context.document.hidden = true; await p.event('document','visibilitychange');
  reply.resolve(p.response({grant:'late',handoffGeneration:2,data:p.data})); await operation;
  p.context.document.hidden = false; await p.event('document','visibilitychange');
  assert.equal(p.client.state().grant, null); assert.equal(p.get('private').children.length,0);
});
test('a newer laptop handoff observed during reveal rejects the delayed old private response', async () => {
  const reply=deferred(); const p=page({fetch:url=>url==='/api/reveal'?reply.promise:undefined}); await flush();
  const operation=p.client.reveal(); await flush();
  // Server granted generation2, then laptop Hide revoked it as generation3 before the response arrived.
  p.state.handoffGeneration=3; await p.client.poll();
  reply.resolve(p.response({grant:'revoked',handoffGeneration:2,data:p.data})); await operation;
  assert.equal(p.client.state().privateData,null); assert.equal(p.get('private').hidden,true);
});
test('a poll observing this reveal own generation does not discard its valid response', async () => {
  const reply=deferred(); const p=page({fetch:url=>url==='/api/reveal'?reply.promise:undefined}); await flush();
  const operation=p.client.reveal(); await flush(); p.state.handoffGeneration=2; await p.client.poll();
  reply.resolve(p.response({grant:'valid',handoffGeneration:2,data:p.data})); await operation;
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
test('an incompatible client version covers cards and blocks reveal until reload', async () => {
  const p=page(); await flush(); await p.client.reveal(); p.state.assetsVersion='1'; await p.client.poll();
  assert.equal(p.client.state().privateData,null); assert.equal(p.get('reveal').disabled,true);
  assert.match(p.get('notice').textContent,/needs an update/);
});
test('ordinary focus changes and scroll pointer cancellation keep the current hand visible', async () => {
  for(const [scope,event] of [['window','blur'],['document','pointercancel']]) {
    const p = page(); await flush(); await p.client.reveal(); await p.event(scope,event);
    assert.equal(p.get('private').hidden,false); assert.equal(p.client.state().grant,'private-grant');
    assert.equal(p.requests.some(r=>r.url==='/api/hide'),false);
  }
});
test('leaving the page still clears the private view immediately', async () => {
  const p=page(); await flush(); await p.client.reveal(); await p.event('window','pagehide');
  assert.equal(p.get('private').children.length,0); assert.equal(p.client.state().grant,null);
});

test('untouched destination selections remain available after ten minutes and can still be submitted', async () => {
  const p=page(), choices=await ticketOffer(p);
  await checkTicket(choices[0],true); await checkTicket(choices[1],true); await checkTicket(choices[0],false);
  await elapse(p,600000);
  assert.equal(p.get('private').hidden,false,'Time alone must not hide the hand or discard ticket selections');
  const current=descendants(p.get('private')).filter(node=>node.type==='checkbox');
  assert.equal(current[0],choices[0]); assert.equal(current[0].checked,false); assert.equal(current[1].checked,true);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1,'Polling must not fetch/rebuild the private view');
  assert.equal(p.requests.some(r=>r.url==='/api/activity'),false,'Reading cards must not require activity renewals');
  const keep=descendants(p.get('private')).find(node=>node.textContent==='Keep selected tickets');
  assert.equal(keep.disabled,false); await keep.events.click(); await flush();
  const payload=JSON.parse(p.requests.find(r=>r.url==='/api/command').request.body);
  assert.deepEqual(payload.command.keptTickets,['second']); assert.equal(payload.grant,'private-grant');
});

test('a hand remains visible without interaction and its original grant still permits a command', async () => {
  const p=page(); await flush(); await p.client.reveal();
  const originalData=p.client.state().privateData;
  await elapse(p,300000);
  assert.equal(p.get('private').hidden,false); assert.equal(p.client.state().privateData,originalData);
  assert.equal(p.client.state().grant,'private-grant'); assert.equal(p.client.state().handoffGeneration,2);
  assert.equal(p.requests.some(r=>r.url==='/api/activity'),false);
  await p.client.submit('drawTrain',{slot:null});
  const command=JSON.parse(p.requests.find(r=>r.url==='/api/command').request.body);
  assert.equal(command.grant,'private-grant'); assert.equal(command.command.kind,'drawTrain');
  assert.equal(p.get('private').hidden,true);
});

test('Escape still hides a private hand after a long read', async () => {
  const p=page(); await flush(); await p.client.reveal(); await elapse(p,120000);
  await p.event('document','keydown',{key:'Escape'});
  assert.equal(p.get('private').hidden,true); assert.equal(p.client.state().grant,null);
  assert.ok(p.requests.some(r=>r.url==='/api/hide'));
});

test('heartbeat timeout still covers the hand without waiting for a network request', async () => {
  const disconnected=page(); await flush(); await disconnected.client.reveal(); disconnected.advance(6000);
  disconnected.intervals.find(t=>t.ms===500).fn(); assert.equal(disconnected.client.state().privateData,null);
});
test('camera claims wait for a detected route and polls never reveal a hidden hand', async () => {
  const p=page(); await flush(); cameraTurn(p); await p.client.poll(); await p.client.reveal();
  assert.equal(descendants(p.get('private')).some(node=>node.tag==='select'),false);
  assert.ok(descendants(p.get('private')).some(node=>node.textContent.includes('Place your trains on the board.')));
  await p.client.submit('planClaim',{routeId:'first-route',payment:p.data.actions.claims[0].payments[0]});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  p.client.hide(); detect(p); await p.client.poll();
  assert.equal(p.get('private').hidden,true); assert.equal(p.get('private').children.length,0);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
});
test('camera-only polls update payment choices while preserving the private hand and same-proposal selection', async () => {
  const p=page(); await flush(); cameraTurn(p); await p.client.poll(); await p.client.reveal();
  const hand=descendants(p.get('private')).find(node=>node.className==='cards');
  const tickets=descendants(p.get('private')).find(node=>node.className==='tickets');
  detect(p); await p.client.poll();
  assert.equal(p.get('private').hidden,false); assert.ok(descendants(p.get('private')).includes(hand)); assert.ok(descendants(p.get('private')).includes(tickets));
  assert.equal(privateNode(p,'Pay with 1 Blue'),undefined,'Only payments for the detected route are offered');
  assert.equal(privateNode(p,'Draw a blind card').disabled,true); assert.equal(privateNode(p,'Draw destination tickets').disabled,true);
  assert.equal(descendants(p.get('private')).filter(node=>node.className==='market-card').every(node=>node.disabled),true);
  await p.client.submit('drawTrain',{slot:null}); await p.client.submit('drawTickets');
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  await privateNode(p,'Pay with 1 Red + 1 Locomotive').events.click();
  assert.equal(privateNode(p,'Pay for detected route').disabled,false);
  detect(p,{ready:false}); await p.client.poll();
  assert.equal(privateNode(p,'Pay with 1 Red + 1 Locomotive')['aria-pressed'],'true'); assert.equal(privateNode(p,'Pay for detected route').disabled,true);
  await p.client.submit('payDetectedRoute',{routeId:'first-route',payment:p.data.actions.claims[0].payments[1],detectedClaimId:'placement-1'});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  detect(p); await p.client.poll();
  assert.equal(privateNode(p,'Pay with 1 Red + 1 Locomotive')['aria-pressed'],'true');
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
  await privateNode(p,'Pay for detected route').events.click(); await flush();
  const payload=JSON.parse(p.requests.find(r=>r.url==='/api/command').request.body);
  assert.equal(payload.command.kind,'payDetectedRoute'); assert.equal(payload.command.detectedClaimId,'placement-1'); assert.equal(payload.command.routeId,'first-route');
  assert.deepEqual(payload.command.payment,{color:'Red',colorCards:1,locomotives:1}); assert.equal(p.get('private').hidden,true);
});
test('removed or replaced camera proposals clear payment choices and reject stale submissions', async () => {
  const p=page(); await flush(); cameraTurn(p); detect(p); await p.client.poll(); await p.client.reveal();
  await privateNode(p,'Pay with 2 Red').events.click(); const oldPay=privateNode(p,'Pay for detected route');
  detect(p,{proposalId:'placement-2'}); await p.client.poll();
  assert.equal(privateNode(p,'Pay with 2 Red')['aria-pressed'],'false'); assert.equal(privateNode(p,'Pay for detected route').disabled,true);
  await oldPay.events.click(); await p.client.submit('payDetectedRoute',{routeId:'first-route',payment:p.data.actions.claims[0].payments[0],detectedClaimId:'placement-1'});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  await privateNode(p,'Pay with 2 Red').events.click();
  await oldPay.events.click(); await flush();
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false,'A detached Pay button must not spend a newly selected payment on its old proposal');
  p.state.snapshot.boardInteraction.detectedRoute=null; p.state.snapshot.boardInteraction.cardActionsBlocked=false; await p.client.poll();
  assert.equal(privateNode(p,'Pay for detected route'),undefined); assert.equal(privateNode(p,'Draw a blind card').disabled,false);
  detect(p,{proposalId:'placement-2'}); await p.client.poll();
  assert.equal(privateNode(p,'Pay for detected route').disabled,true); assert.equal(privateNode(p,'Pay with 2 Red')['aria-pressed'],'false');
});
test('camera-only polls preserve in-progress destination ticket checkboxes', async () => {
  const p=page(), choices=await ticketOffer(p); await checkTicket(choices[0],true);
  detect(p); await p.client.poll(); detect(p,{proposalId:'placement-2',ready:false}); await p.client.poll();
  const current=descendants(p.get('private')).filter(node=>node.type==='checkbox');
  assert.equal(current[0],choices[0]); assert.equal(current[0].checked,true); assert.equal(privateNode(p,'Pay for detected route'),undefined);
  const keep=descendants(p.get('private')).find(node=>node.textContent==='Keep selected tickets');
  assert.equal(keep.disabled,true);
  await p.client.submit('keepTickets',{keptTickets:['first'],returnedTickets:['second']});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false); assert.equal(current[0].checked,true);
  p.state.snapshot.boardInteraction.cardActionsBlocked=false; await p.client.poll();
  assert.equal(keep.disabled,false); assert.equal(descendants(p.get('private')).find(node=>node.type==='checkbox'),choices[0]);
});
test('a delayed private reveal uses the newest camera proposal without requesting another hand', async () => {
  const reply=deferred(), p=page({fetch:url=>url==='/api/reveal'?reply.promise:undefined}); await flush(); cameraTurn(p); detect(p); await p.client.poll();
  const revealing=p.client.reveal(); await flush();
  detect(p,{proposalId:'placement-2',routeId:'other-route',label:'Seattle – Vancouver',length:1}); await p.client.poll();
  reply.resolve(p.response({grant:'latest-hand',handoffGeneration:2,data:p.data})); await revealing;
  assert.ok(privateNode(p,'Pay with 1 Blue')); assert.equal(privateNode(p,'Pay with 2 Red'),undefined);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1); assert.equal(p.get('private').hidden,false);
});
test('command clears private UI before transport and duplicate tap cannot submit twice', async () => {
  const result = deferred(); const p=page({fetch:url=>url==='/api/command'?result.promise:undefined}); await flush(); await p.client.reveal();
  const sending=p.client.submit('drawTrain',{slot:null}); await flush();
  assert.ok([...p.timeouts.values()].some(timer=>timer.ms===8000),'Commands allow the laptop eight seconds for a camera check');
  assert.equal(p.get('private').children.length,0); assert.equal(p.client.state().grant,null);
  await p.client.submit('drawTrain',{slot:null}); assert.equal(p.requests.filter(r=>r.url==='/api/command').length,1);
  result.resolve(p.response({accepted:true,message:'Saved.'})); await sending;
  assert.equal(p.client.state().busy,false); assert.equal(p.client.state().privateData,null);
});
test('untrusted player text is assigned as text and never interpreted as HTML', async () => {
  const p=page(); p.state.snapshot.game.seats[0].displayName='<img src=x onerror=alert(1)>'; await flush(); await p.client.reveal();
  assert.equal(p.get('private').hidden,false); // Node.innerHTML setter would throw on interpolation.
});
// Synthetic one-pixel PNG; independent of product branding assets.
const resultPng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==','base64');
const imageResponse = (bytes = resultPng, contentType = 'image/png') => new Response(bytes, {headers:{'Content-Type':contentType}});
async function receiveResults(p, id = 'image-1') {
  p.state.snapshot.resultImage = {id, fileName:'golden-ticket-final-standings.png'};
  p.state.snapshot.canControl = false;
  await p.client.poll();
  for(let i=0; i<5; i++) { await new Promise(resolve=>setImmediate(resolve)); await flush(); }
}

test('standings stay absent until published and load once with authenticated uncached transport', async () => {
  const p=page({fetch:url=>url.startsWith('/api/result-image/')?imageResponse():undefined}); await flush();
  assert.equal(p.requests.some(r=>r.url.startsWith('/api/result-image/')),false);
  await receiveResults(p); assert.equal(p.get('result').hidden,false); assert.equal(p.get('curtain').hidden,true); assert.equal(p.get('public').hidden,true);
  assert.equal(p.get('result-preview').hidden,false); assert.equal(p.get('result-save').hidden,false);
  assert.equal(p.get('result-save').download,'golden-ticket-final-standings.png');
  assert.equal(p.objectUrls.at(-1).blob.type,'image/png');
  assert.deepEqual(Buffer.from(await p.objectUrls.at(-1).blob.arrayBuffer()),resultPng);
  const request=p.requests.find(r=>r.url==='/api/result-image/image-1').request;
  assert.equal(request.cache,'no-store'); assert.equal(request.credentials,'same-origin'); assert.ok(request.headers['X-GoldenTicket-Tab']);
  await p.client.poll(); assert.equal(p.requests.filter(r=>r.url.startsWith('/api/result-image/')).length,1);
  await p.event('window','blur'); assert.equal(p.get('curtain').hidden,true); assert.equal(p.get('result-preview').hidden,false);
});

test('replacement, revocation, game change, disconnect and page departure erase result URLs and files', async () => {
  for(const action of ['replacement','revoked','new-game','removed','offline','pagehide','timeout']) {
    const p=page({fetch:url=>url.startsWith('/api/result-image/')?imageResponse():undefined}); await flush(); await receiveResults(p);
    const old=p.client.state().resultUrl;
    if(action==='replacement') await receiveResults(p,'image-2');
    if(action==='revoked') {p.state.paired=false; await p.client.poll();}
    if(action==='new-game') {p.state.snapshot.game.sessionId='next-match'; delete p.state.snapshot.resultImage; await p.client.poll();}
    if(action==='removed') {delete p.state.snapshot.resultImage; await p.client.poll();}
    if(action==='offline') await p.event('window','offline');
    if(action==='pagehide') await p.event('window','pagehide');
    if(action==='timeout') {p.advance(6000);p.intervals.find(t=>t.ms===500).fn();}
    assert.ok(p.revokedUrls.includes(old),action);
    if(action!=='replacement') {
      assert.equal(p.client.state().resultUrl,null,action); assert.equal(p.get('result').hidden,true,action);
      assert.equal(p.get('result-preview').src,undefined,action); assert.equal(p.get('result-save').href,undefined,action);
    } else assert.notEqual(p.client.state().resultUrl,old);
  }
});

test('late image response cannot restore results after revocation or session replacement', async () => {
  for(const action of ['revoked','new-game','offline']) {
    const pending=deferred(); const p=page({fetch:url=>url.startsWith('/api/result-image/')?pending.promise:undefined}); await flush(); await receiveResults(p);
    if(action==='revoked') {p.state.paired=false;await p.client.poll();}
    if(action==='new-game') {p.state.snapshot.game.sessionId='next';delete p.state.snapshot.resultImage;await p.client.poll();}
    if(action==='offline') await p.event('window','offline');
    pending.resolve(imageResponse()); await new Promise(resolve=>setImmediate(resolve)); await flush();
    assert.equal(p.client.state().resultUrl,null); assert.equal(p.objectUrls.length,0); assert.equal(p.get('result').hidden,true);
  }
});

test('failed image download has a working retry without polling indefinitely', async () => {
  let attempts=0;
  const p=page({fetch:url=>url.startsWith('/api/result-image/')?(++attempts===1?new Response('',{status:404}):imageResponse()):undefined}); await flush(); await receiveResults(p);
  assert.equal(p.get('result-retry').hidden,false); assert.equal(p.client.state().resultUrl,null);
  await p.client.poll(); assert.equal(attempts,1,'Polling must not retry the failed image indefinitely.');
  await p.click('result-retry'); assert.equal(attempts,2); assert.equal(p.get('result-save').hidden,false); assert.equal(p.get('result-retry').hidden,true);
});

test('result download rejects non-PNG, invalid signatures and oversized streamed bodies', async () => {
  for(const makeResponse of [()=>imageResponse(resultPng,'text/html'),()=>imageResponse('not-a-png'),()=>imageResponse(new Uint8Array(16*1024*1024+1)),()=>new Response(null,{headers:{'Content-Type':'image/png','Content-Length':String(16*1024*1024+1)}})]) {
    const p=page({fetch:url=>url.startsWith('/api/result-image/')?makeResponse():undefined}); await flush(); await receiveResults(p);
    assert.equal(p.client.state().resultUrl,null); assert.equal(p.objectUrls.length,0); assert.equal(p.get('result-retry').hidden,false);
  }
});

test('result metadata cannot fetch external URLs and unsafe filenames use a PNG basename', async () => {
  const p=page({fetch:url=>url.startsWith('/api/result-image/')?imageResponse():undefined}); await flush(); await receiveResults(p,'https://elsewhere.test/private');
  assert.equal(p.requests.some(r=>r.url.startsWith('/api/result-image/')),false);
  p.state.snapshot.resultImage={id:'safe-id',fileName:'../../bad.html'};await p.client.poll();await new Promise(resolve=>setImmediate(resolve));await flush();
  assert.equal(p.get('result-save').download,'golden-ticket-final-standings.png');
});
