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
  const nodes = new Map(), listeners = {}, intervals = [], timeouts = new Map(), requests = [], objectUrls = [], revokedUrls = [], streams = [];
  let nextTimer = 0, uuid = 0, now = Date.now(), focusedNode = null;
  class Node {
    constructor(tag = 'div') { this.tag = tag; this.tagName=tag.toUpperCase(); this.textContent = ''; this.hidden = false; this.value = ''; this.disabled = false; this.children = []; this.dataset = {}; this.events = {}; this.scrollLeft=0; this.style={removeProperty(name){delete this[name];}}; }
    addEventListener(event, callback) { this.events[event] = callback; }
    append(...children) { this.children.push(...children); if (this.tag === 'select' && !this.value) this.value = this.children[0]?.value || ''; }
    replaceChildren(...children) { this.children = children; this.textContent = ''; if (this.tag === 'select') this.value = this.children[0]?.value || ''; }
    setAttribute(name, value) { this[name] = value; }
    removeAttribute(name) { delete this[name]; }
    querySelectorAll() { return descendants(this).filter(node => ['button', 'input', 'select'].includes(node.tag) && !node.className?.includes('private-hide') && !node.className?.includes('private-view-control')); }
    getBoundingClientRect() { return {width:384,height:this.className==='destination-map'?244:150}; }
    focus() { focusedNode=this; }
    set innerHTML(value) { throw new Error('Private UI must not interpolate HTML'); }
  }
  const get = id => { const live=[...nodes.values()].flatMap(descendants).find(node=>node.id===id);if(live)return live;if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
  const state = {
    paired: options.paired !== false, pending: false, csrf: 'csrf-token', apiVersion: '1', assetsVersion: '15', controllerGeneration: 1, handoffGeneration: 1,
    snapshot: { canControl: true, revealSeatId: 1, message: 'Pass this device to Alex.', profileId: 'classic-us', manifestHash: 'hash', routes: [],
      game: { sessionId: 'match', stateVersion: 1, activeSeatId: 1, turnNumber: 1, turnPhase: 'TurnStart', seats: [{ seatId: 1, displayName: 'Alex', symbol: 'A', color: 'Blue', routeScore: 0, trainsRemaining: 45 }], pendingClaim: null } }
  };
  const data = { view: { seatId: 1, public: state.snapshot.game, hand: [{ id: 3, kind: 'Red' }], reservedCards: [] }, heldTickets: [{id:'secret', label:'PRIVATE_DESTINATION', points:20}], offeredTickets: [], actions: { mustCommitTicketSelection: false, mustResolvePendingClaim: true } };
  function response(value, ok = true, status = 200) { return { ok, status, json: async () => structuredClone(value) }; }
  const context = vm.createContext({
    console, Promise, AbortController, Blob, Uint8Array, TextDecoder, structuredClone,
    Image: class { async decode() { if (options.decode) await options.decode(); } },
    ResizeObserver: class { observe() {} disconnect() {this.disconnected=true;} },
    URL: class extends URL { static createObjectURL(blob) { const url = `blob:results-${objectUrls.length}`; objectUrls.push({url, blob}); return url; } static revokeObjectURL(url) { revokedUrls.push(url); } },
    Date: class extends Date { static now() { return now; } },
    // getRandomValues remains available in an insecure context; randomUUID does not.
    crypto: { getRandomValues: bytes => { bytes.fill(++uuid); return bytes; } },
    setTimeout: (fn, ms) => { const id = ++nextTimer; timeouts.set(id, {fn, ms}); return id; }, clearTimeout: id => timeouts.delete(id),
    setInterval: (fn, ms) => intervals.push({fn, ms}),
    document: { hidden: false, get activeElement(){return focusedNode;}, getElementById: get, createElement: tag => new Node(tag), createElementNS: (_, tag) => new Node(tag), addEventListener: (name, fn) => listeners['document:' + name] = fn },
    window: { isSecureContext: options.secure !== false, scrollX:0, scrollY:0, scrollTo({left,top}) {this.scrollX=left;this.scrollY=top;}, matchMedia:()=>({matches:options.reducedMotion===true}), addEventListener: (name, fn) => listeners['window:' + name] = fn },
    navigator: { onLine: options.internetAvailable !== false },
    fetch: async (url, request) => {
      requests.push({url, request});
      if (options.offline) throw new Error('Offline');
      if (options.fetch) { const result = options.fetch(url, request); if (result !== undefined) return await result; }
      if (url === '/api/events') {
        let stream;
        const body = new ReadableStream({start(controller) {
          stream = {controller, request, closed:false}; streams.push(stream);
          request.signal.addEventListener('abort', () => {
            if (!stream.closed) { stream.closed=true; controller.error(new DOMException('Aborted','AbortError')); }
          });
          controller.enqueue(new TextEncoder().encode(`event: session\ndata: ${JSON.stringify(state)}\n\n`));
        }, cancel() { stream.closed=true; }});
        return new Response(body, {headers:{'Content-Type':'text/event-stream'}});
      }
      if (url === '/api/session') throw new Error('The browser must not poll /api/session');
      if (url === '/api/reveal') { state.handoffGeneration++; return response({grant:'private-grant', handoffGeneration: state.handoffGeneration, data}); }
      if (url === '/api/command') return response({ accepted:true, message:'Choice saved on laptop.' });
      if (url === '/api/pair') { state.pending=true; return response({pending:true, identity:'1234'}); }
      return response({hidden:true});
    }
  });
  let source = fs.readFileSync(path.join(sourceDir, 'app.js'), 'utf8');
  source = source.replace(/\}\)\(\);\s*$/, 'globalThis.clientTest = { reveal, hide, submit, clearPrivate, state: () => ({paired, busy, privateData, grant, revealGeneration, handoffGeneration, resultKey, resultUrl, snapshot, eventsAbort, reconnectTimer, needsReload, boardUrl:publicMapImage?.state.url??null, boardImageId:publicMapImage?.state.id??null, boardAbort:publicMapImage?.state.abort??null, destinationView}) }; })();');
  vm.runInContext(source, context);
  async function retry() {
    const timer=context.clientTest.state().reconnectTimer;
    if (timer) {const task=timeouts.get(timer);timeouts.delete(timer);task.fn();await flush();}
  }
  async function bytes(value) {
    const stream=streams.findLast(item=>!item.closed);
    assert.ok(stream,'An active SSE connection is required');
    stream.controller.enqueue(typeof value==='string'?new TextEncoder().encode(value):value); await flush();
  }
  async function push(value=state) {
    if (options.offline) {
      const stream=streams.findLast(item=>!item.closed);
      if(stream) {stream.closed=true;stream.controller.error(new Error('Offline'));await flush();}
      return;
    }
    await retry(); await bytes(`event: session\ndata: ${JSON.stringify(value)}\n\n`);
  }
  return { context, state, data, get, requests, options, timeouts, intervals, response, objectUrls, revokedUrls, streams, push, bytes, retry, client: context.clientTest,
    event: async (scope, name, event = {}) => { await listeners[scope + ':' + name]?.(event); await flush(); },
    click: async id => { await get(id).events.click?.(); await flush(); },
    advance: ms => { now += ms; }, nodes };
}

// Advance time without interaction while keeping the laptop connection fresh.
async function elapse(p, ms) {
  while (ms > 0) {
    const step = Math.min(ms, 2000); p.advance(step); ms -= step;
    await p.bytes('event: heartbeat\ndata: {}\n\n'); p.intervals.find(t => t.ms === 500).fn(); await flush();
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
  p.state.pending=true; await p.push();
  assert.equal(p.get('pair-form').hidden,true);
  await p.get('pair-form').events.submit({preventDefault(){}});
  assert.equal(p.requests.some(r=>r.url==='/api/pair'),false);
});

test('one authenticated SSE connection receives bursts without session polling or private view rebuilds', async () => {
  const p=page(); await flush(); await p.client.reveal();
  const hand=p.get('private').children[3];
  const events=Array.from({length:40},(_,index)=>{
    const next=structuredClone(p.state);next.snapshot.guidance={title:'Alex',instruction:`Camera update ${index}`};
    return `event: session\ndata: ${JSON.stringify(next)}\n\n`;
  }).join('');
  await p.bytes(events); await elapse(p,120000);
  assert.equal(p.get('curtain-detail').textContent,'Camera update 39');
  assert.equal(p.get('private').children[3],hand);assert.equal(p.get('private').hidden,false);
  assert.equal(p.requests.filter(r=>r.url==='/api/events').length,1);
  assert.equal(p.requests.some(r=>r.url==='/api/session'),false);
  assert.deepEqual(p.intervals.map(timer=>timer.ms),[500],'Only the local connection watchdog runs periodically');
  const stream=p.requests.find(r=>r.url==='/api/events');
  assert.equal(stream.request.headers.Accept,'text/event-stream');
  assert.match(stream.request.headers['X-GoldenTicket-Tab'],/^[a-f0-9]{32}$/);
  assert.equal(stream.url.includes('?'),false);
});

test('SSE parses fragmented CRLF, multiline JSON and split UTF-8 without partial rendering', async () => {
  const p=page();await flush();
  const next=structuredClone(p.state);next.snapshot.guidance={title:'Montréal 🚂',instruction:'Place the train → then continue.'};
  const wire=`: keepalive\r\nid: ignored\r\nretry: 10\r\nevent: session\r\n${JSON.stringify(next,null,2).split('\n').map(line=>'data: '+line).join('\r\n')}\r\n\r\n`;
  const encoded=new TextEncoder().encode(wire);
  for(const byte of encoded.subarray(0,encoded.length-2)) await p.bytes(Uint8Array.of(byte));
  assert.notEqual(p.get('handoff').textContent,'Montréal 🚂','Incomplete events are not rendered');
  await p.bytes(encoded.subarray(encoded.length-2));
  assert.equal(p.get('handoff').textContent,'Montréal 🚂');
  assert.equal(p.get('curtain-detail').textContent,'Place the train → then continue.');
  assert.equal(p.requests.filter(r=>r.url==='/api/events').length,1);
});

test('malformed or oversized SSE events cover cards and reconnect without accepting partial data', async () => {
  for(const invalid of [
    'event: session\ndata: {bad json}\n\n',
    'event: session\ndata: {"paired":true,"pending":false,"apiVersion":"1","assetsVersion":"15"}\n\n',
    'data: '+ 'x'.repeat(1024*1024+1),
    Uint8Array.of(0xff)
  ]) {
    const p=page();await flush();await p.client.reveal();
    await p.bytes(invalid);
    assert.equal(p.get('private').hidden,true);assert.equal(p.client.state().grant,null);
    assert.equal(p.streams[0].closed,true);
    assert.equal(p.timeouts.get(p.client.state().reconnectTimer).ms,1000);
    await p.retry();assert.equal(p.get('private').hidden,true);assert.equal(p.get('reveal').disabled,false);
    assert.equal(p.streams.filter(stream=>!stream.closed).length,1);
  }
});

test('pairing replaces the initial stream once and approval arrives without another request', async () => {
  const p=page({paired:false});await flush();p.get('pair-code').value='123456';
  await p.get('pair-form').events.submit({preventDefault(){}});await flush();
  assert.equal(p.streams[0].request.signal.aborted,true);assert.equal(p.streams.filter(stream=>!stream.closed).length,1);
  assert.equal(p.requests.filter(r=>r.url==='/api/events').length,2);assert.equal(p.get('pair-form').hidden,true);
  p.state.paired=true;p.state.pending=false;await p.push();
  assert.equal(p.get('connect').hidden,true);assert.equal(p.get('reveal').disabled,false);
  assert.equal(p.requests.filter(r=>r.url==='/api/events').length,2);
});

test('closed stream reconnects covered with bounded backoff and accepts a restarted host', async () => {
  const p=page();await flush();await p.client.reveal();
  p.streams[0].closed=true;p.streams[0].controller.close();await flush();
  assert.equal(p.get('private').hidden,true);assert.equal(p.get('reveal').disabled,true);
  p.options.offline=true;
  for(const delay of [1000,2000,4000,8000,10000,10000]) {
    assert.equal(p.timeouts.get(p.client.state().reconnectTimer).ms,delay);await p.retry();
    assert.equal(p.streams.filter(stream=>!stream.closed).length,0);
  }
  p.options.offline=false;p.state.controllerGeneration=0;p.state.handoffGeneration=0;
  p.state.snapshot.game.sessionId='restarted';p.state.snapshot.game.stateVersion=0;
  await p.retry();
  assert.equal(p.get('reveal').disabled,false);assert.equal(p.get('private').hidden,true);
  assert.equal(p.client.state().snapshot.game.sessionId,'restarted');
  assert.equal(p.client.state().handoffGeneration,0);
  assert.equal(p.streams.filter(stream=>!stream.closed).length,1);
});

test('background suspends streams and retries until visible; repeated lifecycle events never overlap streams', async () => {
  const p=page();await flush();await p.client.reveal();
  p.context.document.hidden=true;await p.event('document','visibilitychange');
  assert.equal(p.streams.filter(stream=>!stream.closed).length,0);
  assert.equal(p.client.state().reconnectTimer,null);assert.equal(p.get('private').hidden,true);
  p.advance(30000);p.intervals[0].fn();await p.event('window','pageshow');
  assert.equal(p.requests.filter(r=>r.url==='/api/events').length,1);
  p.context.document.hidden=false;await p.event('document','visibilitychange');
  await p.event('window','pageshow');await p.event('document','visibilitychange');
  assert.equal(p.streams.filter(stream=>!stream.closed).length,1);
  assert.equal(p.requests.filter(r=>r.url==='/api/events').length,2);
  assert.equal(p.get('private').hidden,true);
});

test('missing heartbeat aborts the stream once; fresh heartbeats do not alter cards or request data', async () => {
  const p=page();await flush();await p.client.reveal();
  p.advance(5000);await p.bytes('event: heartbeat\ndata: {}\n\n');
  p.advance(5000);p.intervals[0].fn();assert.equal(p.get('private').hidden,false);
  p.advance(1000);p.intervals[0].fn();p.intervals[0].fn();await flush();
  assert.equal(p.streams[0].request.signal.aborted,true);assert.equal(p.get('private').hidden,true);
  assert.equal(p.timeouts.size,1);assert.equal(p.timeouts.get(p.client.state().reconnectTimer).ms,1000);
});
test('LAN gameplay remains available when the browser reports no Internet connection', async () => {
  // Browser/OS connectivity probes may report offline on a working Wi-Fi LAN without WAN access.
  // The laptop's successful responses, not navigator.onLine, decide whether gameplay is possible.
  const p = page({internetAvailable:false}); await flush();
  assert.equal(p.get('reveal').disabled,false);
  await p.client.reveal(); assert.equal(p.get('private').hidden,false);
  await p.event('window','offline'); assert.equal(p.get('private').hidden,true);
  await p.push(); assert.equal(p.get('reveal').disabled,false);
  await p.client.reveal(); await p.client.submit('drawTrain',{slot:null});
  assert.equal(p.requests.filter(r=>r.url==='/api/command').length,1);
  assert.match(p.get('notice').textContent,/saved/i);
  assert.equal(p.client.state().privateData,null);
});
test('the complete companion request flow stays on the laptop origin', async () => {
  const p=page({paired:false}); await flush();
  p.get('pair-code').value='123456';
  await p.get('pair-form').events.submit({preventDefault(){}});
  p.state.paired=true; await p.push(); await p.client.reveal();
  await p.client.submit('drawTrain',{slot:null}); await p.client.reveal(); await p.click('hide');
  assert.deepEqual([...new Set(p.requests.map(r=>r.url))].sort(),['/api/command','/api/events','/api/hide','/api/pair','/api/reveal']);
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
    p.state.paired=true; await p.push(); await p.client.reveal();
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
    p.state.snapshot.guidance={title:'Computer 1',instruction}; await p.push();
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
  p.state.snapshot.message='Pass this device to Alex.'; await p.push();
  assert.equal(p.get('handoff').textContent,'Pass this device to Alex.');
  assert.match(p.get('curtain-detail').textContent,/Reveal only when it is your turn/);
  assert.equal(p.get('public-instruction').textContent,'Turn 1 · Turn Start');
  assert.equal(p.get('private').children.length,0); assert.equal(p.get('reveal').disabled,false);
  await p.client.reveal(); assert.equal(p.get('private').hidden,false);
});

test('shared guidance updates pending placement without rebuilding or revealing private cards', async () => {
  const p=page(); await flush();
  p.state.snapshot.guidance={title:'Alex',instruction:'Place 2 Blue trains on Calgary - Helena.'};
  await p.push(); await p.client.reveal();
  const node=descendants(p.get('private')).find(node=>node.textContent===p.state.snapshot.guidance.instruction);
  assert.ok(node); const hand=p.get('private').children[3];
  p.state.snapshot.guidance.instruction='Restore the Blue trains to Calgary - Helena.'; await p.push();
  assert.equal(node.textContent,p.state.snapshot.guidance.instruction); assert.equal(p.get('private').children[3],hand);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
  delete p.state.snapshot.guidance; await p.push();
  assert.equal(node.textContent,'Follow the placement instructions on the laptop.');
  await p.click('hide');
  p.state.snapshot.guidance={title:'Alex',instruction:'Move the Blue score marker.'}; await p.push();
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
  p.state.handoffGeneration=3; await p.push();
  reply.resolve(p.response({grant:'revoked',handoffGeneration:2,data:p.data})); await operation;
  assert.equal(p.client.state().privateData,null); assert.equal(p.get('private').hidden,true);
});
test('a pushed update observing this reveal own generation does not discard its valid response', async () => {
  const reply=deferred(); const p=page({fetch:url=>url==='/api/reveal'?reply.promise:undefined}); await flush();
  const operation=p.client.reveal(); await flush(); p.state.handoffGeneration=2; await p.push();
  reply.resolve(p.response({grant:'valid',handoffGeneration:2,data:p.data})); await operation;
  assert.equal(p.get('private').hidden,false); assert.equal(p.client.state().grant,'valid');
});
test('an old pushed update cannot rewind handoff generation and re-pairing can reset it for a restarted host', async () => {
  const p=page(); await flush(); await p.client.reveal();
  p.state.handoffGeneration=1; await p.push();
  assert.equal(p.client.state().handoffGeneration,2); assert.equal(p.get('private').hidden,false);
  p.state.paired=false; await p.push(); assert.equal(p.client.state().handoffGeneration,-1);
  p.state.paired=true; p.state.handoffGeneration=1; await p.push();
  assert.equal(p.client.state().handoffGeneration,1); await p.client.reveal(); assert.equal(p.get('private').hidden,false);
});
test('turn revision change, revoke, and connection failure clear private views', async () => {
  for (const change of ['revision', 'revoked', 'offline']) {
    const p = page(); await flush(); await p.client.reveal();
    if(change === 'revision') p.state.snapshot.game.stateVersion++;
    if(change === 'revoked') p.state.paired = false;
    if(change === 'offline') p.options.offline = true;
    await p.push(); assert.equal(p.client.state().privateData,null); assert.equal(p.get('private').hidden,true);
  }
});
test('an incompatible client version covers cards and blocks reveal until reload', async () => {
  const p=page(); await flush(); await p.client.reveal(); p.state.assetsVersion='1'; await p.push();
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
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1,'Heartbeats must not fetch/rebuild the private view');
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
test('held destination cards use explicit city names and safely support older labels', async () => {
  const p=page(); await flush();
  p.data.heldTickets=[
    {id:'names',label:'Ignored display label',from:'Winston-Salem',to:'Sault St. Marie',points:11},
    {id:'legacy',label:'Denver – Pittsburgh',points:12},
    {id:'hyphen',label:'Winston-Salem',points:13},
    {id:'html',label:'<img src=x onerror=bad()>',points:14}
  ];
  await p.client.reveal();
  const row=descendants(p.get('private')).find(node=>node.className==='tickets held-tickets');
  assert.equal(row.tabIndex,0); assert.equal(row.role,'region'); assert.equal(row['aria-label'],'Your destination tickets');
  const cards=row.children;
  assert.equal(cards.length,4); assert.equal(cards[0]['aria-label'],'Winston-Salem to Sault St. Marie');
  assert.deepEqual(cards[0].children.map(node=>node.textContent),['Winston-Salem','↓','Sault St. Marie','11 points']);
  assert.equal(cards[0].children[1]['aria-hidden'],'true');
  assert.deepEqual(cards[1].children.map(node=>node.textContent),['Denver','↓','Pittsburgh','12 points']);
  assert.deepEqual(cards[2].children.map(node=>node.textContent),['Winston-Salem','13 points']);
  assert.deepEqual(cards[3].children.map(node=>node.textContent),['<img src=x onerror=bad()>','14 points']);
  assert.equal(descendants(row).some(node=>node.tag==='img'),false);
});
test('offered destinations retain their checkbox list alongside held destination cards', async () => {
  const p=page(), choices=await ticketOffer(p);
  assert.equal(choices.length,2);
  assert.equal(descendants(p.get('private')).filter(node=>node.className==='tickets held-tickets').length,1);
  assert.equal(descendants(p.get('private')).filter(node=>node.className==='tickets').length,1);
  await checkTicket(choices[1],true);
  const keep=descendants(p.get('private')).find(node=>node.textContent==='Keep selected tickets');
  assert.equal(keep.disabled,false); await keep.events.click(); await flush();
  const command=JSON.parse(p.requests.find(r=>r.url==='/api/command').request.body).command;
  assert.deepEqual(command.keptTickets,['second']); assert.deepEqual(command.returnedTickets,['first']);
});
test('camera claims wait for a detected route and pushes never reveal a hidden hand', async () => {
  const p=page(); await flush(); cameraTurn(p); await p.push(); await p.client.reveal();
  assert.equal(descendants(p.get('private')).some(node=>node.tag==='select'),false);
  assert.ok(descendants(p.get('private')).some(node=>node.textContent.includes('Place your trains on the board.')));
  await p.client.submit('planClaim',{routeId:'first-route',payment:p.data.actions.claims[0].payments[0]});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  p.client.hide(); detect(p); await p.push();
  assert.equal(p.get('private').hidden,true); assert.equal(p.get('private').children.length,0);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
});
test('camera-only pushes update payment choices while preserving the private hand and same-proposal selection', async () => {
  const p=page(); await flush(); cameraTurn(p); await p.push(); await p.client.reveal();
  const hand=descendants(p.get('private')).find(node=>node.className==='cards');
  const tickets=descendants(p.get('private')).find(node=>node.className==='tickets held-tickets');
  detect(p); await p.push();
  assert.equal(p.get('private').hidden,false); assert.ok(descendants(p.get('private')).includes(hand)); assert.ok(descendants(p.get('private')).includes(tickets));
  assert.equal(privateNode(p,'Pay with 1 Blue'),undefined,'Only payments for the detected route are offered');
  assert.equal(privateNode(p,'Draw a blind card').disabled,true); assert.equal(privateNode(p,'Draw destination tickets').disabled,true);
  assert.equal(descendants(p.get('private')).filter(node=>node.className==='market-card').every(node=>node.disabled),true);
  await p.client.submit('drawTrain',{slot:null}); await p.client.submit('drawTickets');
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  await privateNode(p,'Pay with 1 Red + 1 Locomotive').events.click();
  assert.equal(privateNode(p,'Pay for detected route').disabled,false);
  detect(p,{ready:false}); await p.push();
  assert.equal(privateNode(p,'Pay with 1 Red + 1 Locomotive')['aria-pressed'],'true'); assert.equal(privateNode(p,'Pay for detected route').disabled,true);
  await p.client.submit('payDetectedRoute',{routeId:'first-route',payment:p.data.actions.claims[0].payments[1],detectedClaimId:'placement-1'});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  detect(p); await p.push();
  assert.equal(privateNode(p,'Pay with 1 Red + 1 Locomotive')['aria-pressed'],'true');
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
  await privateNode(p,'Pay for detected route').events.click(); await flush();
  const payload=JSON.parse(p.requests.find(r=>r.url==='/api/command').request.body);
  assert.equal(payload.command.kind,'payDetectedRoute'); assert.equal(payload.command.detectedClaimId,'placement-1'); assert.equal(payload.command.routeId,'first-route');
  assert.deepEqual(payload.command.payment,{color:'Red',colorCards:1,locomotives:1}); assert.equal(p.get('private').hidden,true);
});
test('removed or replaced camera proposals clear payment choices and reject stale submissions', async () => {
  const p=page(); await flush(); cameraTurn(p); detect(p); await p.push(); await p.client.reveal();
  await privateNode(p,'Pay with 2 Red').events.click(); const oldPay=privateNode(p,'Pay for detected route');
  detect(p,{proposalId:'placement-2'}); await p.push();
  assert.equal(privateNode(p,'Pay with 2 Red')['aria-pressed'],'false'); assert.equal(privateNode(p,'Pay for detected route').disabled,true);
  await oldPay.events.click(); await p.client.submit('payDetectedRoute',{routeId:'first-route',payment:p.data.actions.claims[0].payments[0],detectedClaimId:'placement-1'});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
  await privateNode(p,'Pay with 2 Red').events.click();
  await oldPay.events.click(); await flush();
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false,'A detached Pay button must not spend a newly selected payment on its old proposal');
  p.state.snapshot.boardInteraction.detectedRoute=null; p.state.snapshot.boardInteraction.cardActionsBlocked=false; await p.push();
  assert.equal(privateNode(p,'Pay for detected route'),undefined); assert.equal(privateNode(p,'Draw a blind card').disabled,false);
  detect(p,{proposalId:'placement-2'}); await p.push();
  assert.equal(privateNode(p,'Pay for detected route').disabled,true); assert.equal(privateNode(p,'Pay with 2 Red')['aria-pressed'],'false');
});
test('camera-only pushes preserve in-progress destination ticket checkboxes', async () => {
  const p=page(), choices=await ticketOffer(p); await checkTicket(choices[0],true);
  detect(p); await p.push(); detect(p,{proposalId:'placement-2',ready:false}); await p.push();
  const current=descendants(p.get('private')).filter(node=>node.type==='checkbox');
  assert.equal(current[0],choices[0]); assert.equal(current[0].checked,true); assert.equal(privateNode(p,'Pay for detected route'),undefined);
  const keep=descendants(p.get('private')).find(node=>node.textContent==='Keep selected tickets');
  assert.equal(keep.disabled,true);
  await p.client.submit('keepTickets',{keptTickets:['first'],returnedTickets:['second']});
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false); assert.equal(current[0].checked,true);
  p.state.snapshot.boardInteraction.cardActionsBlocked=false; await p.push();
  assert.equal(keep.disabled,false); assert.equal(descendants(p.get('private')).find(node=>node.type==='checkbox'),choices[0]);
});
test('a delayed private reveal uses the newest camera proposal without requesting another hand', async () => {
  const reply=deferred(), p=page({fetch:url=>url==='/api/reveal'?reply.promise:undefined}); await flush(); cameraTurn(p); detect(p); await p.push();
  const revealing=p.client.reveal(); await flush();
  detect(p,{proposalId:'placement-2',routeId:'other-route',label:'Seattle – Vancouver',length:1}); await p.push();
  reply.resolve(p.response({grant:'latest-hand',handoffGeneration:2,data:p.data})); await revealing;
  assert.ok(privateNode(p,'Pay with 1 Blue')); assert.equal(privateNode(p,'Pay with 2 Red'),undefined);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1); assert.equal(p.get('private').hidden,false);
});
test('command keeps the hand visible during transport and duplicate tap cannot submit twice', async () => {
  const result = deferred(); const p=page({fetch:url=>url==='/api/command'?result.promise:undefined}); await flush(); await p.client.reveal();
  const sending=p.client.submit('drawTrain',{slot:null}); await flush();
  assert.ok([...p.timeouts.values()].some(timer=>timer.ms===8000),'Commands allow the laptop eight seconds for a camera check');
  assert.equal(p.get('private').hidden,false); assert.ok(p.get('private').children.length); assert.equal(p.get('private')['aria-busy'],'true');
  await p.client.submit('drawTrain',{slot:null}); assert.equal(p.requests.filter(r=>r.url==='/api/command').length,1);
  result.resolve(p.response({accepted:true,message:'Saved.'})); await sending;
  assert.equal(p.client.state().busy,false); assert.equal(p.client.state().privateData,null);
});

test('a pending face-up draw names the chosen card beside the picker until the board check finishes', async () => {
  const reply=deferred();const p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
  await flush();cameraTurn(p);await p.push();await p.client.reveal();
  assert.equal(privateNode(p,'Red · slot 1').disabled,false,'Reveal itself must not leave draw controls busy');
  const help=p.get('train-draw-help');const sending=p.client.submit('drawTrain',{slot:0});await flush();
  assert.equal(help.textContent,'Checking the board before drawing your Red card…');assert.equal(help.style.minHeight,'150px');
  assert.equal(p.get('private').hidden,false);assert.equal(help.role,'status');
  assert.ok(descendants(p.get('private')).filter(node=>node.className==='market-card').every(node=>node.disabled));
  const continuation=secondDrawContinuation(p);reply.resolve(p.response(continuation));await sending;
  assert.equal(help.textContent,'Choose your second card. A visible locomotive cannot be the second draw.');
  assert.equal(p.get('private').hidden,false);
});

test('a camera-blocked draw explains the actual reason beside the disabled face-up cards', async () => {
  const p=page();await flush();cameraTurn(p);await p.push();await p.client.reveal();
  p.state.snapshot.boardInteraction.cardActionsBlocked=true;
  p.state.snapshot.boardInteraction.message='The camera sees an unclaimed Yellow train. Remove it before drawing cards.';
  await p.push();
  assert.equal(p.get('train-draw-help').textContent,p.state.snapshot.boardInteraction.message);
  assert.equal(p.get('private').hidden,false);
  assert.ok(descendants(p.get('private')).filter(node=>node.className==='market-card').every(node=>node.disabled));
  assert.equal(p.requests.some(r=>r.url==='/api/command'),false);
});

function secondDrawContinuation(p) {
  p.state.snapshot.game.stateVersion++;
  p.state.snapshot.game.turnPhase='AwaitingSecondTrainCard';
  p.state.snapshot.game.faceUp=['Blue','Green','Locomotive','Pink','Blue'];
  p.state.handoffGeneration++;
  p.data.view.public=p.state.snapshot.game;
  p.data.view.hand.push({id:4,kind:'Red'});
  p.data.actions.canRequestTicketOffer=false;
  p.data.actions.drawableFaceUpSlots=[0,1,3,4];
  p.data.actions.claims=[];
  return {accepted:true,message:'Saved.',continuation:{grant:'second-draw-grant',handoffGeneration:p.state.handoffGeneration,snapshot:structuredClone(p.state.snapshot),data:structuredClone(p.data)}};
}

function boardCheckContinuation(p) {
  p.state.handoffGeneration++;
  p.state.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:'The camera sees an unclaimed Yellow train.',detectedRoute:null};
  return {accepted:false,code:'BoardCheckRequired',stateVersion:p.state.snapshot.game.stateVersion,message:p.state.snapshot.boardInteraction.message,
    continuation:{grant:'retry-draw-grant',handoffGeneration:p.state.handoffGeneration,snapshot:structuredClone(p.state.snapshot),data:structuredClone(p.data)}};
}

test('a refused first or second draw keeps its unchanged hand and turn with an explicit no-card message', async () => {
  for(const second of [false,true]) {
    const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
    await flush();cameraTurn(p);if(second)secondDrawContinuation(p);await p.push();await p.client.reveal();
    const hand=p.get('private').children[3],view=p.client.state().destinationView;
    const before=structuredClone(p.client.state().privateData.view),version=p.state.snapshot.game.stateVersion;
    const sending=p.client.submit('drawTrain',{slot:0});await flush();const result=boardCheckContinuation(p);await p.push();
    reply.resolve(p.response(result));await sending;
    assert.equal(p.get('private').hidden,false);assert.equal(p.get('private').children[3],hand);assert.equal(p.client.state().destinationView,view);
    assert.deepEqual(p.client.state().privateData.view.hand,before.hand);assert.equal(p.client.state().privateData.view.public.turnPhase,before.public.turnPhase);
    assert.equal(p.client.state().snapshot.game.stateVersion,version);assert.equal(p.client.state().grant,'retry-draw-grant');
    assert.equal(p.get('train-draw-help').textContent,'No card drawn. It is still your turn. The camera sees an unclaimed Yellow train.');
    assert.equal(p.requests.filter(r=>r.url==='/api/command').length,1,'Rejection never silently retries the draw');
    p.state.snapshot.boardInteraction.message='The camera sees an unclaimed Blue train.';await p.push();
    assert.equal(p.get('train-draw-help').textContent,'No card drawn. It is still your turn. The camera sees an unclaimed Blue train.');
    p.state.snapshot.boardInteraction.cardActionsBlocked=false;p.state.snapshot.boardInteraction.message=null;await p.push();
    assert.equal(p.get('train-draw-help').textContent,'No card drawn. It is still your turn. Choose a card to try again.');
    p.options.fetch=url=>url==='/api/command'?p.response({accepted:true,message:'Card added to your hand.'}):undefined;
    await p.client.submit('drawTrain',{slot:0});
    const retry=JSON.parse(p.requests.filter(r=>r.url==='/api/command').at(-1).request.body);
    assert.equal(retry.grant,'retry-draw-grant');assert.equal(retry.command.expectedStateVersion,version);
  }
});

test('a rejected draw continuation never restores after manual Hide, disconnect, background or handoff', async () => {
  for(const reason of ['hide','offline','background','turn','revoked','handoff']) {
    const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
    await flush();cameraTurn(p);await p.push();await p.client.reveal();
    const sending=p.client.submit('drawTrain',{slot:0});await flush();const result=boardCheckContinuation(p);
    if(reason==='hide')p.client.hide();
    if(reason==='offline'){p.options.offline=true;await p.push();p.options.offline=false;}
    if(reason==='background'){p.context.document.hidden=true;await p.event('document','visibilitychange');}
    if(reason==='turn'){p.state.snapshot.game.turnNumber++;p.state.snapshot.game.activeSeatId=2;p.state.snapshot.revealSeatId=2;await p.push();}
    if(reason==='revoked'){p.state.paired=false;await p.push();}
    if(reason==='handoff'){p.state.handoffGeneration++;await p.push();}
    reply.resolve(p.response(result));await sending;
    assert.equal(p.get('private').hidden,true,reason);assert.equal(p.client.state().grant,null,reason);
    assert.equal(p.client.state().privateData,null,reason);
  }
});

test('only unchanged BoardCheckRequired receipts can carry a rejected private continuation', async () => {
  for(const change of ['other-code','advanced-receipt','advanced-snapshot','older-snapshot','changed-private-turn','newer-stream']) {
    const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
    await flush();cameraTurn(p);await p.push();await p.client.reveal();
    const sending=p.client.submit('drawTrain',{slot:0});await flush();const result=boardCheckContinuation(p);
    if(change==='other-code')result.code='LaptopBusy';
    if(change==='advanced-receipt')result.stateVersion++;
    if(change==='advanced-snapshot')result.continuation.snapshot.game.stateVersion++;
    if(change==='older-snapshot')result.continuation.snapshot.game.stateVersion--;
    if(change==='changed-private-turn')result.continuation.data.view.public.turnNumber++;
    if(change==='newer-stream'){p.state.snapshot.game.stateVersion++;await p.push();}
    reply.resolve(p.response(result));await sending;
    assert.equal(p.get('private').hidden,true,change);assert.equal(p.client.state().grant,null,change);
  }
});

test('a rejected receipt does not rewind newer same-version camera status received through SSE', async () => {
  const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
  await flush();cameraTurn(p);await p.push();await p.client.reveal();
  const sending=p.client.submit('drawTrain',{slot:0});await flush();const result=boardCheckContinuation(p);
  p.state.snapshot.boardInteraction.cardActionsBlocked=false;p.state.snapshot.boardInteraction.message='The board is now clear.';await p.push();
  reply.resolve(p.response(result));await sending;
  assert.equal(p.get('private').hidden,false);assert.equal(p.client.state().snapshot.boardInteraction.message,'The board is now clear.');
  assert.equal(privateNode(p,'Red · slot 1').disabled,false);
  assert.equal(p.get('train-draw-help').textContent,'No card drawn. It is still your turn. Choose a card to try again.');
});

test('a rejected receipt explains its camera refusal even when the last streamed status still says checking', async () => {
  const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
  await flush();cameraTurn(p);await p.push();await p.client.reveal();
  const sending=p.client.submit('drawTrain',{slot:0});await flush();
  p.state.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:'Checking the board before drawing cards.',detectedRoute:null};await p.push();
  const result=boardCheckContinuation(p);reply.resolve(p.response(result));await sending;
  assert.equal(p.get('private').hidden,false);
  assert.equal(p.get('train-draw-help').textContent,'No card drawn. It is still your turn. The camera sees an unclaimed Yellow train.');
});

test('a rejected ticket choice preserves checked destinations and uses choice-specific feedback', async () => {
  const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
  const choices=await ticketOffer(p);assert.equal(choices[0].disabled,false,'A fresh reveal leaves checkboxes interactive');
  await checkTicket(choices[1],true);const sending=p.client.submit('keepTickets',{keptTickets:['second'],returnedTickets:['first']});await flush();
  const result=boardCheckContinuation(p);reply.resolve(p.response(result));await sending;
  assert.equal(p.get('private').hidden,false);assert.equal(choices[1].checked,true);assert.equal(choices[1].disabled,false);
  assert.ok(descendants(p.get('private')).some(node=>node.textContent==='Choice not saved. It is still your turn. The camera sees an unclaimed Yellow train.'));
  p.state.snapshot.boardInteraction.cardActionsBlocked=false;await p.push();
  assert.equal(descendants(p.get('private')).find(node=>node.textContent==='Keep selected tickets').disabled,false);
  assert.equal(descendants(p.get('private')).find(node=>node.textContent==='Reverse return order').disabled,false);
});

test('first face-up draw refreshes in place and the next draw uses the new version and grant', async () => {
  const reply=deferred(), p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
  await flush(); cameraTurn(p); await p.push(); await p.client.reveal();
  const hand=p.get('private').children[3], ticketRow=descendants(p.get('private')).find(n=>n.className==='tickets held-tickets');
  const first=privateNode(p,'Red · slot 1'), locomotive=privateNode(p,'White · slot 3'); ticketRow.scrollLeft=87;
  const sending=p.client.submit('drawTrain',{slot:0}); await flush();
  assert.equal(first.disabled,true); assert.equal(p.get('curtain').hidden,true);
  const result=secondDrawContinuation(p); await p.push();
  assert.equal(p.get('private').hidden,false,'The command revision may be pushed before the receipt');
  reply.resolve(p.response(result)); await sending;
  assert.equal(p.get('private').hidden,false); assert.equal(p.get('curtain').hidden,true);
  assert.equal(p.get('private').children[3],hand); assert.equal(hand.children[0].children[1].textContent,'2');
  assert.equal(descendants(p.get('private')).find(n=>n.className==='tickets held-tickets'),ticketRow); assert.equal(ticketRow.scrollLeft,87);
  assert.equal(privateNode(p,'Blue · slot 1'),first); assert.equal(first.disabled,false);
  assert.equal(privateNode(p,'Locomotive · slot 3'),locomotive); assert.equal(locomotive.disabled,true);
  assert.equal(privateNode(p,'Draw destination tickets').disabled,true);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
  p.options.fetch=url=>{if(url==='/api/command') {p.state.snapshot.game.turnNumber++;p.state.snapshot.game.activeSeatId=2;p.state.snapshot.revealSeatId=2;return p.response({accepted:true,message:'Next player.'});}};
  await p.client.submit('drawTrain',{slot:0});
  const command=JSON.parse(p.requests.filter(r=>r.url==='/api/command').at(-1).request.body);
  assert.equal(command.grant,'second-draw-grant'); assert.equal(command.command.expectedStateVersion,2);
  assert.equal(p.get('private').hidden,true); assert.equal(p.client.state().grant,null);
});

test('late continuation cannot uncover after Hide, background, revoke, disconnect, or a turn change', async () => {
  for(const reason of ['hide','background','revoked','offline','turn','handoff']) {
    const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
    await flush(); cameraTurn(p); await p.push(); await p.client.reveal();
    const sending=p.client.submit('drawTrain',{slot:0}); await flush(); const result=secondDrawContinuation(p);
    if(reason==='hide') p.client.hide();
    if(reason==='background') {p.context.document.hidden=true;await p.event('document','visibilitychange');}
    if(reason==='revoked') {p.state.paired=false;await p.push();}
    if(reason==='offline') {p.options.offline=true;await p.push();p.options.offline=false;}
    if(reason==='turn') {p.state.snapshot.game.turnNumber++;p.state.snapshot.game.activeSeatId=2;p.state.snapshot.revealSeatId=2;await p.push();}
    if(reason==='handoff') {p.state.handoffGeneration++;await p.push();}
    reply.resolve(p.response(result)); await sending;
    assert.equal(p.get('private').hidden,true,reason); assert.equal(p.client.state().grant,null,reason);
  }
});

test('an old pushed update cannot replace the continuation with an earlier game revision', async () => {
  const commandReply=deferred();
  const p=page({fetch:url=>url==='/api/command'?commandReply.promise:undefined});
  await flush();cameraTurn(p);await p.push();await p.client.reveal();
  const sending=p.client.submit('drawTrain',{slot:0});await flush();
  const oldState=structuredClone(p.state);
  commandReply.resolve(p.response(secondDrawContinuation(p)));await sending;
  await p.push(oldState);
  assert.equal(p.get('private').hidden,false); assert.equal(p.client.state().grant,'second-draw-grant');
  assert.equal(p.client.state().snapshot.game.stateVersion,2);
  await p.push();assert.equal(p.get('private').hidden,false);
});

test('a delayed continuation preserves newer public guidance streamed for its resulting revision', async () => {
  const reply=deferred(),p=page({fetch:url=>url==='/api/command'?reply.promise:undefined});
  await flush();cameraTurn(p);await p.push();await p.client.reveal();
  const sending=p.client.submit('drawTrain',{slot:0});await flush();
  const receipt=secondDrawContinuation(p);
  p.state.snapshot.guidance={title:'Alex',instruction:'The camera now sees an extra train.'};
  p.state.snapshot.boardInteraction.message='Latest camera observation';await p.push();
  reply.resolve(p.response(receipt));await sending;
  assert.equal(p.get('private').hidden,false);assert.equal(p.client.state().grant,'second-draw-grant');
  assert.equal(p.client.state().snapshot.game.stateVersion,2);
  assert.equal(p.client.state().snapshot.boardInteraction.message,'Latest camera observation');
  assert.equal(p.get('public-instruction').textContent,'The camera now sees an extra train.');
  assert.equal(p.requests.filter(request=>request.url==='/api/events').length,1,'A periodic fetch must not be needed to restore the latest guidance');
});

test('untrusted player text is assigned as text and never interpreted as HTML', async () => {
  const p=page(); p.state.snapshot.game.seats[0].displayName='<img src=x onerror=alert(1)>'; await flush(); await p.client.reveal();
  assert.equal(p.get('private').hidden,false); // Node.innerHTML setter would throw on interpolation.
});
// Synthetic one-pixel PNG; independent of product branding assets.
const resultPng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==','base64');
const imageResponse = (bytes = resultPng, contentType = 'image/png') => new Response(bytes, {headers:{'Content-Type':contentType}});
const boardIds = ['a'.repeat(32), 'b'.repeat(32), 'c'.repeat(32)];
const boardJpeg = Uint8Array.of(255,216,255,224,0,16,255,217);
const boardResponse = () => imageResponse(boardJpeg, 'image/jpeg');
const settleImages = async () => { await new Promise(resolve => setImmediate(resolve)); await flush(); };
async function privateDestinationMap(p) {
  await flush(); cameraTurn(p);
  p.data.heldTickets=[
    {id:'ticket-ab',from:'A',to:'B',fromCityId:'a',toCityId:'b',label:'A – B',points:8},
    {id:'ticket-cd',from:'C',to:'D',fromCityId:'c',toCityId:'d',label:'C – D',points:11}
  ];
  p.state.snapshot.boardMap={imageId:boardIds[0],targets:[],cities:[
    {id:'a',name:'A',x:100,y:100},{id:'b',name:'B',x:500,y:400},
    {id:'c',name:'C',x:200,y:200},{id:'d',name:'D',x:800,y:100},
    {id:'unheld',name:'Another city',x:40,y:560}
  ]};
  await p.push();await p.client.reveal();return p.client.state().destinationView;
}
async function openDestinationMap(p, index=0) {
  const view=p.client.state().destinationView;view.row.children[index].events.click();await settleImages();return view;
}
function finishMapAnimation(p, view=p.client.state().destinationView) {
  const timer=p.timeouts.get(view.animation);
  if(timer) {p.timeouts.delete(view.animation);timer.fn();}
}
function overlayNodes(view,className) {return view.overlay.children.filter(node=>node.class===className);}

test('any held ticket reveals all and only its owner’s destination connections inside the private hand', async () => {
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined});
  const view=await privateDestinationMap(p);
  assert.equal(p.requests.some(r=>r.url.startsWith('/api/board-image/')),false,'A closed ticket row does not download camera images');
  await openDestinationMap(p,1);finishMapAnimation(p);
  assert.equal(view.open,true);assert.equal(view.row.hidden,true);assert.equal(view.map.hidden,false);
  assert.deepEqual(overlayNodes(view,'destination-connection').map(node=>node['data-ticket-id']),['ticket-ab','ticket-cd']);
  assert.deepEqual(overlayNodes(view,'destination-city-marker').map(node=>node['data-city-id']),['a','b','c','d']);
  const first=overlayNodes(view,'destination-connection')[0].children[0];
  assert.ok(Math.abs(Math.hypot(Number(first.x1)-100,Number(first.y1)-100)-22)<1e-9);
  assert.ok(Math.abs(Math.hypot(Number(first.x2)-500,Number(first.y2)-400)-22)<1e-9);
  assert.equal(p.client.state().boardUrl,null);assert.equal(p.get('board-targets').children.length,0);
  assert.equal(p.get('curtain').hidden,true);assert.equal(p.get('private').hidden,false);
  assert.equal(p.requests.filter(r=>r.url==='/api/reveal').length,1);
  const original=overlayNodes(view,'destination-connection')[0];await p.push();await elapse(p,30000);
  assert.equal(overlayNodes(view,'destination-connection')[0],original,'Same-frame SSE updates leave destination overlays intact');
  assert.equal(p.requests.filter(r=>r.url.startsWith('/api/board-image/')).length,1);
});

test('destination map Back restores ticket focus and horizontal position; rapid toggles cancel old transitions', async () => {
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined});const view=await privateDestinationMap(p);
  view.row.scrollLeft=128;await openDestinationMap(p,1);
  assert.equal(p.context.document.activeElement,view.back);assert.equal(p.timeouts.get(view.animation).ms,300);
  view.back.events.click();const closing=view.animation;
  view.row.children[0].events.click();assert.equal(p.timeouts.has(closing),false);
  finishMapAnimation(p);assert.equal(view.map.hidden,false);assert.equal(view.row.hidden,true);
  view.back.events.click();finishMapAnimation(p);
  assert.equal(view.map.hidden,true);assert.equal(view.row.hidden,false);assert.equal(view.row.scrollLeft,128);
  assert.equal(p.context.document.activeElement,view.row.children[0]);
  assert.equal(view.loader.state.url,null);assert.equal(view.overlay.children.length,0);
  assert.equal(view.viewport.style.height,undefined);assert.equal(view.viewport.dataset.animating,undefined);
});

test('reduced motion switches destination maps immediately while preserving keyboard focus', async () => {
  const p=page({reducedMotion:true,fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined});const view=await privateDestinationMap(p);
  await openDestinationMap(p);assert.equal(view.animation,null);assert.equal(view.row.hidden,true);assert.equal(view.map.hidden,false);
  assert.equal(p.context.document.activeElement,view.back);
  view.back.events.click();assert.equal(view.animation,null);assert.equal(view.row.hidden,false);assert.equal(view.map.hidden,true);
  assert.equal(p.context.document.activeElement,view.row.children[0]);
});

test('destination connections stay aligned with the displayed JPEG until the next frame is decoded', async () => {
  const delayed=deferred();
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?(url.endsWith(boardIds[1])?delayed.promise:boardResponse()):undefined});
  const view=await privateDestinationMap(p);await openDestinationMap(p);finishMapAnimation(p);
  const old=view.loader.state.url,marker=overlayNodes(view,'destination-city-marker')[0];
  p.state.snapshot.boardMap.imageId=boardIds[1];p.state.snapshot.boardMap.cities[0].x=120;await p.push();
  assert.equal(view.loader.state.url,old);assert.equal(overlayNodes(view,'destination-city-marker')[0],marker);
  assert.equal(marker.transform,'translate(100 100)');
  delayed.resolve(boardResponse());await settleImages();
  assert.notEqual(view.loader.state.url,old);assert.equal(overlayNodes(view,'destination-city-marker')[0].transform,'translate(120 100)');
  assert.equal(p.revokedUrls.includes(old),true);assert.equal(p.client.state().destinationView,view);
});

test('the first train-card draw preserves an open destination map and its local controls', async () => {
  const reply=deferred();
  const p=page({fetch:url=>url==='/api/command'?reply.promise:url.startsWith('/api/board-image/')?boardResponse():undefined});
  const view=await privateDestinationMap(p);view.row.scrollLeft=64;await openDestinationMap(p);finishMapAnimation(p);
  const image=view.loader.state.url,overlay=view.overlay.children[0];const sending=p.client.submit('drawTrain',{slot:0});await flush();
  assert.equal(view.back.disabled,false);assert.equal(view.row.children[0].disabled,false);
  const result=secondDrawContinuation(p);await p.push();reply.resolve(p.response(result));await sending;
  assert.equal(p.client.state().destinationView,view);assert.equal(view.open,true);assert.equal(view.loader.state.url,image);
  assert.equal(view.overlay.children[0],overlay);assert.equal(view.row.scrollLeft,64);assert.equal(p.get('private').hidden,false);
});

test('private destination maps and pending downloads are erased on Hide, background, disconnect and handoff', async () => {
  for(const action of ['hide','background','disconnect','handoff','revoke']) {
    const delayed=deferred();const p=page({fetch:url=>url.startsWith('/api/board-image/')?(url.endsWith(boardIds[1])?delayed.promise:boardResponse()):undefined});
    const view=await privateDestinationMap(p);await openDestinationMap(p);finishMapAnimation(p);const url=view.loader.state.url;
    p.state.snapshot.boardMap.imageId=boardIds[1];await p.push();const active=view.loader.state.abort;
    if(action==='hide')p.client.hide();
    if(action==='background'){p.context.document.hidden=true;await p.event('document','visibilitychange');}
    if(action==='disconnect'){p.options.offline=true;await p.push();}
    if(action==='handoff'){p.state.snapshot.game.turnNumber++;p.state.snapshot.game.activeSeatId=2;p.state.snapshot.revealSeatId=2;await p.push();}
    if(action==='revoke'){p.state.paired=false;await p.push();}
    assert.equal(active.signal.aborted,true,action);assert.equal(view.observer.disconnected,true,action);
    assert.equal(p.revokedUrls.includes(url),true,action);assert.equal(view.overlay.children.length,0,action);
    delayed.resolve(boardResponse());await settleImages();
    assert.equal(p.client.state().destinationView,null,action);assert.equal(view.loader.state.url,null,action);
    assert.equal(p.get('private').hidden,true,action);assert.equal(p.get('private').children.length,0,action);
  }
});

test('destination overlays require private endpoint IDs and reject missing or invalid public coordinates', async () => {
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined});await privateDestinationMap(p);
  p.client.hide();p.data.heldTickets.push({id:'legacy',from:'A',to:'Another city',label:'A – Another city',points:7});
  p.state.snapshot.boardMap.cities[3].x=-1;await p.push();await p.client.reveal();const view=await openDestinationMap(p);
  assert.deepEqual(overlayNodes(view,'destination-connection').map(node=>node['data-ticket-id']),['ticket-ab']);
  assert.deepEqual(overlayNodes(view,'destination-city-marker').map(node=>node['data-city-id']),['a','b']);
  p.state.snapshot.boardMap=null;await p.push();assert.equal(view.overlay.children.length,0);assert.equal(view.loader.state.url,null);
  assert.equal(view.map.hidden,false,'Unavailable camera retains the map area without reflowing the hand');
});

async function computerBoard(p, imageId = boardIds[0], targets = [{x:524,y:340,number:1},{x:515,y:365,number:2}]) {
  p.state.snapshot.canControl = false; p.state.snapshot.revealSeatId = null;
  p.state.snapshot.guidance = {title:'Computer 1',instruction:'Place 2 Green trains on Kansas City - Oklahoma City.'};
  p.state.snapshot.boardMap = {imageId, targets};
  await p.push(); await settleImages();
}

test('computer map replaces Reveal with authenticated image and canonical gold indicator positions', async () => {
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined}); await flush(); await p.client.reveal();
  await computerBoard(p);
  assert.equal(p.get('private').hidden,true); assert.equal(p.get('private').children.length,0);
  assert.equal(p.get('board-map').hidden,false); assert.equal(p.get('board-stage').hidden,false);
  assert.equal(p.get('reveal').hidden,true); assert.equal(p.get('curtain-privacy').hidden,true);
  assert.equal(p.get('curtain-detail').textContent,'Place 2 Green trains on Kansas City - Oklahoma City.');
  assert.equal(p.get('board-targets').children[0].transform,'translate(524 340)');
  assert.equal(p.get('board-targets').children[0].children[0].r,'13');
  assert.equal(p.get('board-targets').children[0].children[1].r,'7.5');
  assert.deepEqual(Buffer.from(await p.objectUrls[0].blob.arrayBuffer()),Buffer.from(boardJpeg));
  const request=p.requests.find(r=>r.url.startsWith('/api/board-image/'));
  assert.equal(request.url,`/api/board-image/${boardIds[0]}`); assert.equal(request.request.credentials,'same-origin');
  assert.equal(request.request.cache,'no-store'); assert.match(request.request.headers['X-GoldenTicket-Tab'],/^[a-f0-9]{32}$/);
  const marker=p.get('board-targets').children[0]; await p.push(); await elapse(p,30000);
  assert.equal(p.requests.filter(r=>r.url.startsWith('/api/board-image/')).length,1);
  assert.equal(p.get('board-targets').children[0],marker,'Identical updates keep the pulse and image undisturbed');
});

test('camera corrections update only the target overlay at the same game revision', async () => {
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined}); await flush(); await computerBoard(p);
  const image=p.get('board-image').src, version=p.state.snapshot.game.stateVersion;
  await computerBoard(p,boardIds[0],[{x:515,y:365,number:2}]);
  assert.equal(p.get('board-targets').children.length,1); assert.equal(p.get('board-targets').children[0].dataset.number,'2');
  assert.equal(p.get('board-image').src,image); assert.equal(p.state.snapshot.game.stateVersion,version);
  await computerBoard(p,boardIds[0],[]); assert.equal(p.get('board-targets').children.length,0);
  assert.equal(p.requests.filter(r=>r.url.startsWith('/api/board-image/')).length,1);
});

test('computer scoring keeps the map through digital turn advance until the actual human handoff', async () => {
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined}); await flush(); await computerBoard(p);
  const image=p.get('board-image').src;
  p.state.snapshot.game.activeSeatId=2;p.state.snapshot.game.turnNumber++;p.state.snapshot.game.stateVersion++;
  p.state.snapshot.guidance={title:'Computer 1',instruction:'Move the Green score marker to 2.'};
  p.state.snapshot.boardMap.targets=[];await p.push();await settleImages();
  assert.equal(p.get('board-image').src,image);assert.equal(p.get('board-stage').hidden,false);
  assert.equal(p.get('reveal').hidden,true);assert.equal(p.get('board-targets').children.length,0);
  assert.equal(p.requests.filter(r=>r.url.startsWith('/api/board-image/')).length,1);
  assert.equal(p.revokedUrls.length,0,'Finishing the computer instruction does not discard the current frame');
  p.state.snapshot.canControl=true;p.state.snapshot.revealSeatId=2;p.state.snapshot.boardMap=null;await p.push();
  assert.equal(p.get('board-map').hidden,true);assert.equal(p.get('board-image').src,undefined);
  assert.equal(p.get('reveal').hidden,false);assert.equal(p.get('reveal').disabled,false);
  assert.deepEqual(p.revokedUrls,[image]);
});

test('camera frames finish one at a time and fetch only the latest queued frame without flicker', async () => {
  const delayed=deferred();
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?(url.endsWith(boardIds[1])?delayed.promise:boardResponse()):undefined});
  await flush(); await computerBoard(p); const old=p.get('board-image').src;
  await computerBoard(p,boardIds[1]); await computerBoard(p,boardIds[2]);
  assert.equal(p.get('board-image').src,old); assert.equal(p.get('board-stage').hidden,false);
  assert.equal(p.requests.filter(r=>r.url.startsWith('/api/board-image/')).length,2);
  delayed.resolve(boardResponse()); await settleImages(); await settleImages();
  assert.equal(p.client.state().boardImageId,boardIds[2]);
  assert.equal(p.requests.filter(r=>r.url.startsWith('/api/board-image/')).length,3);
  assert.equal(p.revokedUrls.includes(old),true); assert.equal(p.revokedUrls.length,2);
});

test('human turn, dismissal, background and disconnect discard map images and ignore late downloads', async () => {
  for(const action of ['human','removed','background','disconnect','revoked','new-game']) {
    const delayed=deferred();
    const p=page({fetch:url=>url.startsWith('/api/board-image/')?(url.endsWith(boardIds[1])?delayed.promise:boardResponse()):undefined});
    await flush(); await computerBoard(p); const old=p.get('board-image').src; await computerBoard(p,boardIds[1]);
    const active=p.requests.find(r=>r.url.endsWith(boardIds[1])).request.signal;
    if(action==='human') {p.state.snapshot.boardMap=null;p.state.snapshot.canControl=true;p.state.snapshot.revealSeatId=1;await p.push();}
    if(action==='removed') {p.state.snapshot.boardMap=null;await p.push();}
    if(action==='background') {p.context.document.hidden=true;await p.event('document','visibilitychange');}
    if(action==='disconnect') {p.options.offline=true;await p.push();}
    if(action==='revoked') {p.state.paired=false;await p.push();}
    if(action==='new-game') {p.state.snapshot.game.sessionId='replacement';p.state.snapshot.boardMap=null;await p.push();}
    assert.equal(active.aborted,true,action); assert.equal(p.revokedUrls.includes(old),true,action);
    delayed.resolve(boardResponse()); await settleImages();
    assert.equal(p.client.state().boardUrl,null,action); assert.equal(p.get('board-image').src,undefined,action);
    assert.equal(p.get('board-map').hidden,true,action); assert.equal(p.get('board-targets').children.length,0,action);
    if(action==='human') {assert.equal(p.get('reveal').hidden,false);assert.equal(p.get('reveal').disabled,false);}
  }
});

test('a map decoded after leaving its turn is never displayed and its object URL is released', async () => {
  const decode=deferred();
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined,decode:()=>decode.promise});
  await flush(); await computerBoard(p); assert.equal(p.objectUrls.length,1);
  p.state.snapshot.boardMap=null; await p.push();
  assert.deepEqual(p.revokedUrls,[p.objectUrls[0].url],'An in-progress decode releases its URL immediately on dismissal');
  decode.resolve(); await settleImages();
  assert.equal(p.get('board-map').hidden,true); assert.equal(p.get('board-image').src,undefined);
  assert.deepEqual(p.revokedUrls,[p.objectUrls[0].url]);
});

test('missing or failed map images keep Reveal hidden and recover on a later SSE update', async () => {
  let attempts=0;
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?(++attempts===1?new Response('',{status:404}):boardResponse()):undefined});
  await flush(); await computerBoard(p,null);
  assert.equal(p.get('board-map').hidden,false); assert.equal(p.get('reveal').hidden,true);
  assert.match(p.get('board-status').textContent,/Waiting/); assert.equal(attempts,0);
  await computerBoard(p); assert.equal(attempts,1); assert.equal(p.get('board-stage').hidden,true);
  await elapse(p,30000); assert.equal(attempts,1,'Heartbeats never fetch or retry map images');
  await p.push(); await settleImages(); assert.equal(attempts,2); assert.equal(p.get('board-stage').hidden,false);
  await computerBoard(p,null); assert.equal(p.get('board-stage').hidden,true); assert.equal(p.client.state().boardUrl,null);
});

test('map metadata cannot fetch external URLs or inject invalid marker coordinates', async () => {
  const p=page(); await flush();
  await computerBoard(p,'https://example.test/map.jpg',[null,{x:'1);bad',y:10,number:1},{x:-1,y:2,number:2},{x:2,y:601,number:3},{x:2,y:2,number:1.5},{x:480,y:300,number:1}]);
  assert.equal(p.requests.some(r=>r.url.startsWith('/api/board-image/')),false);
  assert.equal(p.get('board-targets').children.length,1); assert.equal(p.get('board-targets').children[0].transform,'translate(480 300)');
  p.state.snapshot.canControl=true;await p.push();assert.equal(p.get('board-map').hidden,true,'A human turn never displays a computer board');
});

test('map image reception rejects wrong formats, oversized bodies and decode errors', async () => {
  for(const makeResponse of [()=>imageResponse(resultPng),()=>imageResponse('bad','image/jpeg'),()=>imageResponse(new Uint8Array(4*1024*1024+1),'image/jpeg'),()=>new Response(null,{headers:{'Content-Type':'image/jpeg','Content-Length':String(4*1024*1024+1)}})]) {
    const p=page({fetch:url=>url.startsWith('/api/board-image/')?makeResponse():undefined}); await flush(); await computerBoard(p);
    assert.equal(p.client.state().boardUrl,null); assert.equal(p.objectUrls.length,0); assert.equal(p.get('board-stage').hidden,true);
    assert.equal(p.get('reveal').hidden,true);
  }
  const p=page({fetch:url=>url.startsWith('/api/board-image/')?boardResponse():undefined,decode:()=>{throw new Error('Corrupt image');}});
  await flush(); await computerBoard(p); assert.equal(p.client.state().boardUrl,null); assert.equal(p.objectUrls.length,1); assert.equal(p.revokedUrls.length,1);
});

async function receiveResults(p, id = 'image-1') {
  p.state.snapshot.resultImage = {id, fileName:'golden-ticket-final-standings.png'};
  p.state.snapshot.canControl = false;
  await p.push();
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
  await p.push(); assert.equal(p.requests.filter(r=>r.url.startsWith('/api/result-image/')).length,1);
  await p.event('window','blur'); assert.equal(p.get('curtain').hidden,true); assert.equal(p.get('result-preview').hidden,false);
});

test('replacement, revocation, game change, disconnect and page departure erase result URLs and files', async () => {
  for(const action of ['replacement','revoked','new-game','removed','offline','pagehide','timeout']) {
    const p=page({fetch:url=>url.startsWith('/api/result-image/')?imageResponse():undefined}); await flush(); await receiveResults(p);
    const old=p.client.state().resultUrl;
    if(action==='replacement') await receiveResults(p,'image-2');
    if(action==='revoked') {p.state.paired=false; await p.push();}
    if(action==='new-game') {p.state.snapshot.game.sessionId='next-match'; delete p.state.snapshot.resultImage; await p.push();}
    if(action==='removed') {delete p.state.snapshot.resultImage; await p.push();}
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
    if(action==='revoked') {p.state.paired=false;await p.push();}
    if(action==='new-game') {p.state.snapshot.game.sessionId='next';delete p.state.snapshot.resultImage;await p.push();}
    if(action==='offline') await p.event('window','offline');
    pending.resolve(imageResponse()); await new Promise(resolve=>setImmediate(resolve)); await flush();
    assert.equal(p.client.state().resultUrl,null); assert.equal(p.objectUrls.length,0); assert.equal(p.get('result').hidden,true);
  }
});

test('failed image download has a working retry without polling indefinitely', async () => {
  let attempts=0;
  const p=page({fetch:url=>url.startsWith('/api/result-image/')?(++attempts===1?new Response('',{status:404}):imageResponse()):undefined}); await flush(); await receiveResults(p);
  assert.equal(p.get('result-retry').hidden,false); assert.equal(p.client.state().resultUrl,null);
  await p.push(); assert.equal(attempts,1,'Polling must not retry the failed image indefinitely.');
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
  p.state.snapshot.resultImage={id:'safe-id',fileName:'../../bad.html'};await p.push();await new Promise(resolve=>setImmediate(resolve));await flush();
  assert.equal(p.get('result-save').download,'golden-ticket-final-standings.png');
});
