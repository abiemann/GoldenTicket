// Actual Chromium rendering/lifecycle tests for HTTP Quick play on phone-size viewports.
// Synthetic hosts and .NET payloads do not prove real-device/LAN acceptance.
// First run CompanionHostBrowserFixtures with GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY set.
// NODE_PATH must point to the existing bundled node_modules containing Playwright. No npm install.
// Optional visual iteration: GOLDENTICKET_BROWSER_DRAW_ONLY=1 and
// GOLDENTICKET_BROWSER_VIEWPORTS=pixel,small-phone,tablet,wide-tablet.
// Camera rejection/retry checks: GOLDENTICKET_BROWSER_BLOCKED_DRAW_ONLY=1.
// Computer-map checks: GOLDENTICKET_BROWSER_MAP_ONLY=1; optionally provide an upright
// PNG without overlays through GOLDENTICKET_BOARD_IMAGE_FIXTURE for visual evidence.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const root = path.resolve(__dirname, '../..');
const assets = path.join(root, 'src/GoldenTicket.CompanionHost/wwwroot');
const fixturePath = process.env.GOLDENTICKET_COMPANION_FIXTURES || path.join(root, 'artifacts/companion-browser/fixtures.json');
const output = process.env.GOLDENTICKET_BROWSER_EVIDENCE || path.join(root, 'docs/evidence/companion-browser-2026-09-12');
const fixtures = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
// The seeded .NET market need not include a locomotive. Explicitly supply one to
// exercise second-draw legality while retaining its real surrounding payload.
fixtures.secondDraw.snapshot.game.faceUp[2]='Locomotive';
fixtures.secondDraw.data.view.public.faceUp[2]='Locomotive';
fixtures.secondDraw.data.actions.drawableFaceUpSlots=fixtures.secondDraw.data.actions.drawableFaceUpSlots.filter(slot=>slot!==2);
fs.mkdirSync(output, { recursive: true });
const mime = { '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.svg':'image/svg+xml' };
const results = [];
const drawOnly=process.env.GOLDENTICKET_BROWSER_DRAW_ONLY==='1';
const blockedDrawOnly=process.env.GOLDENTICKET_BROWSER_BLOCKED_DRAW_ONLY==='1';
const mapOnly=process.env.GOLDENTICKET_BROWSER_MAP_ONLY==='1';
const destinationMapOnly=process.env.GOLDENTICKET_BROWSER_DESTINATION_MAP_ONLY==='1';
const viewports=[{name:'pixel',width:448,height:900},{name:'small-phone',width:320,height:740},{name:'tablet',width:768,height:1024},{name:'wide-tablet',width:1024,height:768}]
  .filter(viewport=>process.env.GOLDENTICKET_BROWSER_VIEWPORTS ? process.env.GOLDENTICKET_BROWSER_VIEWPORTS.split(',').includes(viewport.name) : viewport.name!=='wide-tablet');
assert.ok(viewports.length,'GOLDENTICKET_BROWSER_VIEWPORTS must select an existing viewport.');
let fixture = fixtures.setup, paired = false, pending = false, generation = 1, requests = [], offline = false, delayedReveal = null;
let resultImageBytes = null, boardImageBytes = null, delayedBoardImage = null, activeGrant = null, delayedCommand = null;
let sessionRevision=0, controllerGeneration=1, suppressHeartbeats=false;
const eventStreams=new Set();
function sessionEnvelope() {
  return paired ? {paired,pending:false,csrf:'test-csrf',handoffGeneration:generation,controllerGeneration,apiVersion:'1',assetsVersion:'15',snapshot:fixture.snapshot} : {paired,pending,apiVersion:'1',assetsVersion:'15'};
}
function publishSession(target) {
  const revision=++sessionRevision, frame=`id: ${revision}\nevent: session\ndata: ${JSON.stringify(sessionEnvelope())}\n\n`;
  for(const response of target ? [target] : eventStreams) response.write(frame);
  return revision;
}
function publishHeartbeat() {
  for(const response of eventStreams) response.write('event: heartbeat\ndata: {}\n\n');
}
function disconnectStreams() { for(const response of eventStreams) response.destroy(); }
const heartbeatTimer=setInterval(()=>{if(!offline && !suppressHeartbeats) publishHeartbeat();},2000);
heartbeatTimer.unref();
function sameTurnFixture(next, previous) {
  const value=structuredClone(next), before=previous.snapshot.game;
  for(const game of [value.snapshot.game,value.data.view.public]) {
    game.sessionId=before.sessionId;
    game.activeSeatId=before.activeSeatId;
    game.turnNumber=before.turnNumber;
    game.stateVersion=before.stateVersion+1;
  }
  value.snapshot.revealSeatId=previous.snapshot.revealSeatId;
  value.snapshot.boardInteraction=structuredClone(previous.snapshot.boardInteraction);
  value.snapshot.boardMap=structuredClone(previous.snapshot.boardMap);
  value.data.view.seatId=previous.data.view.seatId;
  value.data.actions.stateVersion=before.stateVersion+1;
  value.data.heldTickets=structuredClone(previous.data.heldTickets);
  return value;
}
const send = (response, body, status=200) => { response.writeHead(status, {'Content-Type':'application/json', 'Cache-Control':'no-store'}); response.end(JSON.stringify(body)); };
const requestHandler = async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (url.pathname.startsWith('/api/')) {
    if (offline) { request.socket.destroy(); return; }
    let body = ''; for await(const chunk of request) body += chunk;
    body = body ? JSON.parse(body) : {};
    requests.push({url:url.pathname, body, headers:request.headers});
    if(url.pathname === '/api/session') return send(response, {error:'The browser must use the event stream.'},410);
    if(url.pathname === '/api/events') {
      assert.ok(request.headers['x-goldenticket-tab'],'The event stream must retain the per-tab identity.');
      response.writeHead(200,{'Content-Type':'text/event-stream','Cache-Control':'no-store'});
      response.flushHeaders(); eventStreams.add(response);
      response.on('close',()=>eventStreams.delete(response));
      publishSession(response);
      return;
    }
    if(url.pathname.startsWith('/api/result-image/')) {
      if(!paired || !resultImageBytes || url.pathname!==`/api/result-image/${fixture.snapshot.resultImage?.id}` || !request.headers['x-goldenticket-tab']) return send(response,{},404);
      response.writeHead(200,{'Content-Type':'image/png','Content-Length':resultImageBytes.length,'Cache-Control':'no-store'}); response.end(resultImageBytes); return;
    }
    if(url.pathname.startsWith('/api/board-image/')) {
      if(!paired || !boardImageBytes || url.pathname!==`/api/board-image/${fixture.snapshot.boardMap?.imageId}` || !request.headers['x-goldenticket-tab']) return send(response,{},404);
      if(delayedBoardImage) { delayedBoardImage.response=response; delayedBoardImage.bytes=boardImageBytes; return; }
      sendBoardImage(response,boardImageBytes);return;
    }
    if(url.pathname === '/api/pair') { pending=true; publishSession(); return send(response, {pending:true,identity:'2468'}); }
    if(url.pathname === '/api/hide') { generation++; activeGrant=null; publishSession(); return send(response,{hidden:true}); }
    if(url.pathname === '/api/reveal') {
      const reply = {grant:'test-private-grant', handoffGeneration:++generation, data:fixture.data};
      activeGrant={seat:body.seat,sessionId:body.sessionId,version:body.version,grant:reply.grant,handoffGeneration:reply.handoffGeneration};
      publishSession();
      if(delayedReveal) { delayedReveal.response=response; delayedReveal.reply=reply; return; }
      return send(response, reply);
    }
    if(url.pathname === '/api/command') {
      if(!paired || !activeGrant || body.grant!==activeGrant.grant || body.seat!==activeGrant.seat || generation!==activeGrant.handoffGeneration ||
        body.command.sessionId!==activeGrant.sessionId || body.command.expectedStateVersion!==activeGrant.version) return send(response,{},409);
      const previous=fixture, board=fixture.snapshot.boardInteraction;
      if(board?.cardActionsBlocked && ['drawTrain','drawTickets','keepTickets'].includes(body.command.kind)) return send(response,{},409);
      if(delayedCommand?.rejectBoardCheck && body.command.kind==='drawTrain') {
        fixture=structuredClone(previous);generation++;
        fixture.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:'The camera sees an unclaimed Yellow train. Remove it before drawing cards.',detectedRoute:null};
        const continuation={grant:`test-retry-grant-${generation}`,handoffGeneration:generation,snapshot:structuredClone(fixture.snapshot),data:structuredClone(fixture.data)};
        activeGrant={seat:body.seat,sessionId:fixture.snapshot.game.sessionId,version:fixture.snapshot.game.stateVersion,grant:continuation.grant,handoffGeneration:generation};
        publishSession();
        delayedCommand.response=response;
        delayedCommand.reply={accepted:false,code:'BoardCheckRequired',stateVersion:fixture.snapshot.game.stateVersion,message:fixture.snapshot.boardInteraction.message,continuation};
        return;
      }
      if(body.command.kind === 'payDetectedRoute') {
        const detected=board?.detectedRoute;
        const choices=fixture.data.actions.claims.find(claim=>claim.routeId===detected?.routeId)?.payments || [];
        if(!board?.useCameraClaims || !detected?.ready || body.command.detectedClaimId!==detected.proposalId || body.command.routeId!==detected.routeId ||
          !choices.some(choice=>['color','colorCards','locomotives'].every(key=>choice[key]===body.command.payment?.[key]))) return send(response,{},409);
        fixture=fixtures.nextHuman;
      }
      if(body.command.kind === 'keepTickets') fixture = fixtures.secondHumanSetup;
      if(body.command.kind === 'drawTrain') {
        fixture=previous.snapshot.game.turnPhase==='TurnStart' ? sameTurnFixture(fixtures.secondDraw,previous) : fixtures.nextHuman;
        if(fixture!==fixtures.nextHuman && body.command.slot!==null) {
          const kind=previous.snapshot.game.faceUp[body.command.slot];
          fixture.data.view.hand=[...structuredClone(previous.data.view.hand),{id:999,kind}];
          fixture.data.view.available=structuredClone(fixture.data.view.hand);
          for(const game of [fixture.snapshot.game,fixture.data.view.public]) game.faceUp[body.command.slot]='Orange';
        }
      }
      if(body.command.kind === 'drawTickets') fixture = sameTurnFixture(fixtures.ticketOffer,previous);
      if(body.command.kind === 'planClaim') fixture = sameTurnFixture(fixtures.physicalPlacement,previous);
      generation++; activeGrant=null;
      const reply={accepted:true,duplicate:false,stateVersion:fixture.snapshot.game.stateVersion,message:'Choice saved on the laptop.',continuation:null};
      if(body.command.kind==='drawTrain') reply.message=body.command.slot===null?'Card added to your hand.':`${previous.snapshot.game.faceUp[body.command.slot]} card added to your hand.`;
      if(fixture.snapshot.canControl && fixture.snapshot.revealSeatId===body.seat &&
        fixture.snapshot.game.sessionId===previous.snapshot.game.sessionId && fixture.snapshot.game.turnNumber===previous.snapshot.game.turnNumber) {
        reply.continuation={grant:`test-private-grant-${generation}`,handoffGeneration:generation,snapshot:fixture.snapshot,data:fixture.data};
        activeGrant={seat:body.seat,sessionId:fixture.snapshot.game.sessionId,version:fixture.snapshot.game.stateVersion,grant:reply.continuation.grant,handoffGeneration:generation};
      }
      publishSession();
      if(delayedCommand) { delayedCommand.response=response; delayedCommand.reply=reply; return; }
      return send(response,reply);
    }
    return send(response,{},404);
  }
  const asset = url.pathname === '/companion/' ? 'index.html' : url.pathname.startsWith('/companion/') ? url.pathname.slice('/companion/'.length) : '';
  if(!['index.html','app.js','app.css','icon.svg'].includes(asset)) { response.writeHead(404); response.end(); return; }
  response.writeHead(200, {'Content-Type':mime[path.extname(asset)],'Cache-Control':'no-store',
    'Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; worker-src 'none'; manifest-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"});
  response.end(fs.readFileSync(path.join(assets,asset)));
};
const server = http.createServer(requestHandler);
function sendBoardImage(response,bytes) {
  response.writeHead(200,{'Content-Type':'image/jpeg','Content-Length':bytes.length,'Cache-Control':'no-store'});response.end(bytes);
}
async function record(name, action) { const start=Date.now(); await action(); results.push({name,passed:true,milliseconds:Date.now()-start}); console.log('PASS '+name); }
async function waitCovered(page) { await page.locator('#curtain').waitFor({state:'visible'}); assert.equal(await page.locator('#private').textContent(),''); }
async function reveal(page) { await page.locator('#reveal').waitFor({state:'visible'}); await page.locator('#reveal').click(); await page.locator('#private').waitFor({state:'visible'}); }
async function sessionDelivered(page,revision=sessionRevision) {
  await page.waitForFunction(expected=>window.fixtureEvents.session>=expected,revision);
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
}
async function heartbeatDelivered(page) {
  const previous=await page.evaluate(()=>window.fixtureEvents.heartbeat);
  await page.waitForFunction(expected=>window.fixtureEvents.heartbeat>expected,previous);
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
}
async function changeFixture(page, next) {
  // Independent scenarios can return to an earlier seeded turn. Model a new
  // controller so the production client's stale-event guard still stays active.
  fixture = fixtures[next]; generation++; controllerGeneration++;
  publishSession();
  await page.locator('#hide').click();
  await page.waitForFunction(expected => document.getElementById('handoff').textContent === expected, fixture.snapshot.message);
  // Same player but changed phase can have identical handoff text. Await the actual stream frame.
  await sessionDelivered(page);
}
async function screenshot(page,name) {
  // Normalize scroll before a full-page capture so a sticky header is not composited halfway
  // down the artifact. The separate scrolled viewport assertion checks actual Hide visibility.
  await page.evaluate(()=>scrollTo(0,0));
  await page.screenshot({path:path.join(output,name+'.png'),fullPage:true});
}
async function noOverflow(page) {
  const metrics=await page.evaluate(()=>({width:innerWidth,body:document.body.scrollWidth,html:document.documentElement.scrollWidth}));
  assert.ok(metrics.body<=metrics.width+1 && metrics.html<=metrics.width+1, JSON.stringify(metrics));
}
async function expectLastDestination(row) {
  const last=row.locator('.destination-ticket').last();
  assert.equal(await last.locator('.destination-city').first().textContent(),'Kansas City');
  assert.equal(await last.locator('.destination-city').last().textContent(),'Nashville');
  const bounds=await row.boundingBox(), card=await last.boundingBox();
  assert.ok(card.x>=bounds.x && card.x+card.width<=bounds.x+bounds.width+1,'The last ticket must be reachable by horizontal scrolling.');
}
async function drawPicker(page, name) {
  const picker=page.locator('#private .draw-picker'); await picker.waitFor({state:'visible'});
  await picker.getByRole('heading',{name:'DRAW PILES',exact:true}).waitFor();
  await picker.getByRole('heading',{name:'FACE-UP TRAIN CARDS',exact:true}).waitFor();
  const market=picker.locator('.face-up-panel').getByRole('button');
  const faceUp=fixture.data.view.public.faceUp, actions=fixture.data.actions;
  assert.equal(await market.count(),faceUp.length);
  for(const [slot,kind] of faceUp.entries()) {
    assert.equal(await market.nth(slot).getAttribute('aria-label'),`${kind} · slot ${slot+1}`);
    assert.equal(await market.nth(slot).isDisabled(),!actions.drawableFaceUpSlots.includes(slot));
    assert.equal(await market.nth(slot).isVisible(),true);
  }
  assert.equal(await picker.getByRole('button',{name:'Draw a blind card',exact:true}).isDisabled(),!actions.canDrawBlindTrainCard);
  assert.equal(await picker.getByRole('button',{name:'Draw destination tickets',exact:true}).isDisabled(),!actions.canRequestTicketOffer);
  const help=page.locator('#private .draw-help');
  assert.match(await help.textContent(),/Take two cards|Choose your second card/);
  if(actions.canRequestTicketOffer) assert.match(await help.textContent(),/Draw destination tickets\. You must keep at least one\./);
  await page.locator('#private .draw-area').screenshot({path:path.join(output,name+'-draw-area.png')});
  const bounds=await picker.boundingBox(), helpBounds=await help.boundingBox();
  assert.ok(helpBounds.y>=bounds.y+bounds.height-1,'Draw instructions must follow the complete picker.');
  const slots=await market.evaluateAll(buttons=>buttons.map(button=>{const r=button.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height};}));
  for(const [index,box] of slots.entries()) {
    assert.ok(box.x>=bounds.x-1 && box.x+box.width<=bounds.x+bounds.width+1,'Every market slot must fit inside the picker.');
    assert.ok(box.y>=bounds.y-1 && box.y+box.height<=bounds.y+bounds.height+1,'Every market slot must fit inside the picker.');
    for(const other of slots.slice(index+1)) assert.ok(box.x+box.width<=other.x+1 || other.x+other.width<=box.x+1 || box.y+box.height<=other.y+1 || other.y+other.height<=box.y+1,`Market slots must not overlap: ${JSON.stringify({box,other})}`);
  }
  const labels=await market.locator('.market-card-label').evaluateAll(nodes=>nodes.map(node=>{
    const range=document.createRange();range.selectNodeContents(node);
    const text=range.getBoundingClientRect(), label=node.getBoundingClientRect();
    return {name:node.textContent,left:text.left,right:text.right,labelLeft:label.left,labelRight:label.right};
  }));
  for(const label of labels) assert.ok(label.left>=label.labelLeft-1 && label.right<=label.labelRight+1,`The complete ${label.name} label must fit on its card: ${JSON.stringify(label)}`);
  const pileLabels=await picker.locator('.pile-label').evaluateAll(nodes=>nodes.map(node=>{
    const range=document.createRange();range.selectNodeContents(node);
    const text=range.getBoundingClientRect(), panel=node.closest('.draw-piles').getBoundingClientRect();
    return {name:node.textContent,lines:range.getClientRects().length,left:text.left,right:text.right,panelLeft:panel.left,panelRight:panel.right};
  }));
  for(const label of pileLabels) assert.ok(label.lines===1 && label.left>=label.panelLeft && label.right<=label.panelRight,`The complete ${label.name} pile label must fit on one line: ${JSON.stringify(label)}`);
  await noOverflow(page);
}
async function blockedDrawCases(page, viewport) {
  await record(viewport.name+': camera-rejected first and second draws retain the hand and allow an explicit retry',async()=>{
    fixtures.blockedDraw=structuredClone(fixtures.turnStart);
    fixtures.blockedDraw.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:false,message:null,detectedRoute:null};
    fixtures.blockedDraw.snapshot.boardMap=null;
    fixtures.blockedDraw.data.heldTickets=Array.from({length:8},(_,index)=>({...fixtures.turnStart.data.heldTickets[index%fixtures.turnStart.data.heldTickets.length],id:`blocked-draw-ticket-${index}`}));
    await changeFixture(page,'blockedDraw');await reveal(page);
    const row=page.locator('.held-tickets'),help=page.locator('#train-draw-help');
    await row.evaluate(node=>{node.scrollLeft=node.scrollWidth;node.dataset.identity='retained-draw-tickets';});
    await page.locator('#private h2').evaluate(node=>node.dataset.identity='retained-draw-heading');
    const reveals=requests.filter(request=>request.url==='/api/reveal').length;
    for(const draw of [0,1]) {
      const version=fixture.snapshot.game.stateVersion,phase=fixture.snapshot.game.turnPhase;
      const slot=fixture.data.actions.drawableFaceUpSlots.find(index=>fixture.snapshot.game.faceUp[index]!=='Locomotive');
      const kind=fixture.snapshot.game.faceUp[slot],button=page.getByRole('button',{name:`${kind} · slot ${slot+1}`,exact:true});
      const handCount=()=>page.locator('.cards .card').evaluateAll(nodes=>Object.fromEntries(nodes.map(node=>[node.dataset.color,Number(node.querySelector('strong').textContent)])));
      const hand=await handCount();
      await button.evaluate(node=>node.scrollIntoView({block:'center',inline:'nearest'}));
      const before=await page.evaluate(()=>({y:scrollY,x:document.querySelector('.held-tickets').scrollLeft}));
      delayedCommand={rejectBoardCheck:true};await button.tap();
      await help.filter({hasText:`Checking the board before drawing your ${kind} card…`}).waitFor();
      await sessionDelivered(page);
      assert.equal(await page.locator('#private').isVisible(),true);
      assert.deepEqual(await handCount(),hand,'A pending check has not awarded a card.');
      assert.ok(await page.locator('.market-card').evaluateAll(nodes=>nodes.every(node=>node.disabled)));
      const rejected=delayedCommand;assert.ok(rejected.response);
      const receipt=page.waitForResponse(response=>response.url().endsWith('/api/command'));
      send(rejected.response,rejected.reply);delayedCommand=null;await receipt;
      await help.filter({hasText:'No card drawn. It is still your turn. The camera sees an unclaimed Yellow train.'}).waitFor();
      assert.equal(fixture.snapshot.game.stateVersion,version);assert.equal(fixture.snapshot.game.turnPhase,phase);
      assert.equal(await page.locator('#private h2').getAttribute('data-identity'),'retained-draw-heading');
      assert.equal(await row.getAttribute('data-identity'),'retained-draw-tickets');
      assert.deepEqual(await handCount(),hand,draw===1?'The first awarded card survives rejection of the second draw.':'The rejected first draw leaves the hand unchanged.');
      assert.equal(await page.locator('#curtain').isVisible(),false);
      const after=await page.evaluate(()=>({y:scrollY,max:document.documentElement.scrollHeight-innerHeight,help:document.getElementById('train-draw-help').getBoundingClientRect().height}));
      assert.ok(Math.abs(after.y-before.y)<2,`Rejected drawing keeps vertical scroll: ${JSON.stringify({draw,before,after})}`);
      assert.ok(Math.abs(await row.evaluate(node=>node.scrollLeft)-before.x)<2,'Rejected drawing keeps destination scroll.');
      assert.equal(requests.filter(request=>request.url==='/api/reveal').length,reveals);
      await page.screenshot({path:path.join(output,`${viewport.name}-rejected-draw-${draw+1}.png`)});
      fixture.snapshot.boardInteraction.message='The camera sees an unclaimed Blue train.';
      await sessionDelivered(page,publishSession());
      await help.filter({hasText:'No card drawn. It is still your turn. The camera sees an unclaimed Blue train.'}).waitFor();
      fixture.snapshot.boardInteraction.cardActionsBlocked=false;fixture.snapshot.boardInteraction.message=null;
      await sessionDelivered(page,publishSession());
      await help.filter({hasText:'No card drawn. It is still your turn. Choose a card to try again.'}).waitFor();
      assert.equal(await button.isEnabled(),true);
      const retry=page.waitForResponse(response=>response.url().endsWith('/api/command'));
      await button.tap();await retry;
      const submitted=requests.filter(request=>request.url==='/api/command').at(-1).body;
      assert.equal(submitted.grant,rejected.reply.continuation.grant);
      assert.equal(submitted.command.expectedStateVersion,version);
      if(draw===0) {
        await help.filter({hasText:'Choose your second card.'}).waitFor();
        assert.equal((await handCount())[kind],(hand[kind]||0)+1);
        assert.equal(await page.locator('#private').isVisible(),true);
      } else {
        await waitCovered(page);await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
        assert.equal(await page.locator('#notice').textContent(),`${kind} card added to your hand.`);
      }
    }
    await noOverflow(page);
  });
}
async function syntheticStandingsPng(page) {
  if(process.env.GOLDENTICKET_RESULT_IMAGE_FIXTURE) return fs.readFileSync(process.env.GOLDENTICKET_RESULT_IMAGE_FIXTURE);
  // Synthetic public results only; no player photos, private hands, or saved matches.
  const data=await page.evaluate(()=>{
    const canvas=document.createElement('canvas');canvas.width=1280;canvas.height=960;const ctx=canvas.getContext('2d');
    ctx.fillStyle='#15232d';ctx.fillRect(0,0,1280,960);ctx.fillStyle='#ffdc87';ctx.textAlign='center';ctx.font='bold 46px Georgia';ctx.fillText('THE FINAL STANDINGS',640,92);
    ctx.fillStyle='#f4e7ce';ctx.font='27px sans-serif';ctx.fillText('Alex wins. A journey to remember.',640,147);
    for(const [index,name,score,time] of [[0,'Alex','92','59:25'],[1,'Jordan','82','51:03']]) {
      const x=90+index*570;ctx.fillStyle='#233744';ctx.fillRect(x,205,530,660);ctx.strokeStyle='#d6af5c';ctx.lineWidth=4;ctx.strokeRect(x,205,530,660);
      ctx.fillStyle='#ffdc87';ctx.font='bold 36px Georgia';ctx.fillText(name,x+265,280);ctx.fillStyle='#f4e7ce';ctx.fillRect(x+25,320,480,120);
      ctx.fillStyle='#30291e';ctx.font='bold 62px sans-serif';ctx.fillText(score+' points',x+265,401);
      ctx.fillStyle='#f4e7ce';ctx.font='26px sans-serif';ctx.fillText('Claimed routes · '+(index?73:64),x+265,495);ctx.fillText('Completed destinations · +18',x+265,547);ctx.fillText('Longest-route bonus · '+(index?0:10),x+265,599);
      ctx.fillStyle='#ffdc87';ctx.fillText('Turn time · '+time,x+265,688);ctx.font='21px sans-serif';ctx.fillText('Includes placement; excludes pauses.',x+265,731);ctx.fillText('Longest continuous route · '+(index?19:41)+' trains',x+265,804);
    }
    return canvas.toDataURL('image/png').split(',')[1];
  });
  return Buffer.from(data,'base64');
}
async function boardJpeg(page) {
  const source=process.env.GOLDENTICKET_BOARD_IMAGE_FIXTURE;
  const image=source ? 'data:image/png;base64,'+fs.readFileSync(source).toString('base64') : null;
  const encoded=await page.evaluate(async image=>{
    const canvas=document.createElement('canvas');canvas.width=960;canvas.height=600;const context=canvas.getContext('2d');
    if(image) {
      const board=new Image();board.src=image;await board.decode();context.drawImage(board,0,0,960,600);
    } else {
      // Public synthetic calibration board for CI. Local visual review supplies
      // an upright camera frame without baked overlays through the variable above.
      context.fillStyle='#dbcba6';context.fillRect(0,0,960,600);context.strokeStyle='#8e7656';context.lineWidth=2;
      for(let x=0;x<=960;x+=80) {context.beginPath();context.moveTo(x,0);context.lineTo(x,600);context.stroke();}
      for(let y=0;y<=600;y+=60) {context.beginPath();context.moveTo(0,y);context.lineTo(960,y);context.stroke();}
      context.fillStyle='#3f3427';context.font='24px sans-serif';context.fillText('Public board camera fixture',40,45);
    }
    return canvas.toDataURL('image/jpeg',0.85).split(',')[1];
  },image);
  return Buffer.from(encoded,'base64');
}
async function showComputerMap(page, imageAvailable=true) {
  fixture=structuredClone(fixtures.computerMap);generation++;controllerGeneration++;
  if(!imageAvailable) fixture.snapshot.boardMap.imageId=null;
  await sessionDelivered(page,publishSession());await waitCovered(page);
  await page.locator('#board-map').waitFor({state:'visible'});
  if(imageAvailable) await page.waitForFunction(()=>document.getElementById('board-image').naturalWidth===960);
}
async function checkMapTargets(page,targets) {
  const markers=page.locator('#board-targets .board-target');assert.equal(await markers.count(),targets.length);
  const expected=targets.map(target=>({number:String(target.number),transform:`translate(${target.x} ${target.y})`}));
  const actual=await markers.evaluateAll(nodes=>nodes.map(node=>({number:node.dataset.number,transform:node.getAttribute('transform')})));
  assert.deepEqual(actual,expected);
  const layout=await page.evaluate(()=>{
    const image=document.getElementById('board-image').getBoundingClientRect(),svg=document.getElementById('board-targets').getBoundingClientRect();
    return {image:{x:image.x,y:image.y,width:image.width,height:image.height},svg:{x:svg.x,y:svg.y,width:svg.width,height:svg.height}};
  });
  for(const key of ['x','y','width','height']) assert.ok(Math.abs(layout.image[key]-layout.svg[key])<1,`Map and gold targets must share ${key}.`);
  assert.ok(Math.abs(layout.image.width/layout.image.height-1.6)<0.02,'The map must preserve its 960×600 proportions.');
}
async function computerMapCases(page,viewport) {
  assert.ok(fixtures.computerMap,'Export the current .NET computerMap fixture before browser checks.');
  boardImageBytes=await boardJpeg(page);
  await record(viewport.name+': computer placement replaces Reveal with the public map and aligned gold targets',async()=>{
    // Destination-map scenarios can request this same seeded frame beforehand.
    // Count only this transition into the computer map, not the shared request log.
    const requestStart=requests.length;
    const reveals=requests.filter(request=>request.url==='/api/reveal').length;
    await showComputerMap(page,false);
    assert.equal(await page.locator('#reveal').isVisible(),false);
    assert.equal(await page.locator('#board-stage').isVisible(),false);
    assert.match(await page.locator('#board-status').textContent(),/Waiting for the laptop/);
    fixture.snapshot.boardMap.imageId=fixtures.computerMap.snapshot.boardMap.imageId;await sessionDelivered(page,publishSession());
    await page.waitForFunction(()=>document.getElementById('board-image').naturalWidth===960);
    assert.equal(await page.locator('#handoff').textContent(),'Computer 1');
    assert.equal(await page.locator('#curtain-detail').textContent(),fixture.snapshot.guidance.instruction);
    await checkMapTargets(page,fixture.snapshot.boardMap.targets);await noOverflow(page);
    const received=requests.slice(requestStart).filter(request=>request.url===`/api/board-image/${fixture.snapshot.boardMap.imageId}`);
    assert.equal(received.length,1);assert.ok(received[0].headers['x-goldenticket-tab']);
    assert.equal(requests.filter(request=>request.url==='/api/reveal').length,reveals);
    await screenshot(page,viewport.name+'-computer-map-placement');
    await page.locator('#board-map').screenshot({path:path.join(output,viewport.name+'-computer-map-detail.png')});
    const imageCount=requests.filter(request=>request.url.startsWith('/api/board-image/')).length;
    await heartbeatDelivered(page);await heartbeatDelivered(page);
    assert.equal(requests.filter(request=>request.url.startsWith('/api/board-image/')).length,imageCount,'Unchanged heartbeats must not download the board again.');
  });
  await record(viewport.name+': camera correction updates only remaining gold dots and new frames replace the image',async()=>{
    const before=requests.filter(request=>request.url.startsWith('/api/board-image/')).length;
    fixture.snapshot.boardMap.targets=[fixture.snapshot.boardMap.targets[2]];
    fixture.snapshot.guidance.instruction='Place the remaining Blue train on Duluth - Chicago.';
    await sessionDelivered(page,publishSession());await checkMapTargets(page,fixture.snapshot.boardMap.targets);
    assert.equal(requests.filter(request=>request.url.startsWith('/api/board-image/')).length,before,'Target changes reuse the current camera frame.');
    await screenshot(page,viewport.name+'-computer-map-correction');
    const oldSource=await page.locator('#board-image').getAttribute('src');
    fixture.snapshot.boardMap.imageId='00112233445566778899aabbccddee01';publishSession();
    await page.waitForFunction(previous=>{const image=document.getElementById('board-image');return image.naturalWidth===960&&image.getAttribute('src')!==previous;},oldSource);
    await checkMapTargets(page,fixture.snapshot.boardMap.targets);
    assert.equal(requests.filter(request=>request.url.startsWith('/api/board-image/')).length,before+1);
  });
  await record(viewport.name+': stale image delivery cannot replace a newer frame or restore a removed map',async()=>{
    delayedBoardImage={};fixture.snapshot.boardMap.imageId='00112233445566778899aabbccddee02';publishSession();
    while(!delayedBoardImage.response) await new Promise(resolve=>setTimeout(resolve,20));
    const old=delayedBoardImage;delayedBoardImage=null;
    fixture.snapshot.boardMap.imageId='00112233445566778899aabbccddee03';
    const source=await page.locator('#board-image').getAttribute('src');publishSession();
    await page.waitForFunction(previous=>{const image=document.getElementById('board-image');return image.naturalWidth===960&&image.getAttribute('src')!==previous;},source);
    const current=await page.locator('#board-image').getAttribute('src');sendBoardImage(old.response,old.bytes);
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    assert.equal(await page.locator('#board-image').getAttribute('src'),current);
    delayedBoardImage={};fixture.snapshot.boardMap.imageId='00112233445566778899aabbccddee04';publishSession();
    while(!delayedBoardImage.response) await new Promise(resolve=>setTimeout(resolve,20));
    const removed=delayedBoardImage;delayedBoardImage=null;
    fixture=structuredClone(fixtures.nextHuman);generation++;controllerGeneration++;await sessionDelivered(page,publishSession());
    sendBoardImage(removed.response,removed.bytes);
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    assert.equal(await page.locator('#board-map').isVisible(),false);
    assert.equal(await page.locator('#board-image').getAttribute('src'),null);
    assert.equal(await page.locator('#reveal').isVisible(),true);assert.equal(await page.locator('#reveal').isDisabled(),false);
  });
  await record(viewport.name+': pause, background and revocation erase the computer map',async()=>{
    await showComputerMap(page);
    fixture.snapshot.boardMap=null;fixture.snapshot.guidance=null;fixture.snapshot.message='The game is paused.';
    await sessionDelivered(page,publishSession());
    assert.equal(await page.locator('#board-map').isVisible(),false);assert.equal(await page.locator('#board-image').getAttribute('src'),null);
    await showComputerMap(page);
    await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
    assert.equal(await page.locator('#board-map').isVisible(),false);assert.equal(await page.locator('#board-image').getAttribute('src'),null);
    const next=sessionRevision+1;
    await page.evaluate(()=>{delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));});await sessionDelivered(page,next);
    await page.waitForFunction(()=>document.getElementById('board-image').naturalWidth===960);
    paired=false;await sessionDelivered(page,publishSession());
    assert.equal(await page.locator('#board-map').isVisible(),false);assert.equal(await page.locator('#board-image').getAttribute('src'),null);
    fixture=fixtures.setup;paired=true;generation++;controllerGeneration++;await sessionDelivered(page,publishSession());await waitCovered(page);
  });
}
async function showDestinationHand(page) {
  fixture=structuredClone(fixtures.destinationMap);generation++;controllerGeneration++;
  await sessionDelivered(page,publishSession());await waitCovered(page);await reveal(page);
  assert.equal(await page.locator('#board-map').isVisible(),false,'Human public map metadata must not display the computer panel.');
  await page.locator('.held-tickets').waitFor({state:'visible'});
}
async function waitDestinationMap(page) {
  await page.locator('#destination-map').waitFor({state:'visible'});
  await page.waitForFunction(()=>document.getElementById('destination-map-image')?.naturalWidth===960);
}
async function destinationMapCases(page,viewport) {
  assert.ok(fixtures.destinationMap,'Export the destinationMap .NET fixture before browser checks.');
  boardImageBytes=await boardJpeg(page);
  const back=page.getByRole('button',{name:'Back to destination tickets',exact:true});
  await record(viewport.name+': a held ticket opens all private destinations and smoothly moves route controls',async()=>{
    await showDestinationHand(page);
    const row=page.locator('.held-tickets');
    await row.evaluate(node=>node.scrollLeft=node.scrollWidth);
    const cards=row.locator('.destination-ticket');const chosen=cards.last();
    await chosen.evaluate(node=>{node.scrollIntoView({block:'center'});node.dataset.focusOrigin='destination-test';node.focus({preventScroll:true});});
    const rowScroll=await row.evaluate(node=>node.scrollLeft);
    const samples=await chosen.evaluate(async node=>{
      const view=document.querySelector('.destination-viewport'),claim=document.querySelector('.detected-route');
      const values=[],start=performance.now();
      const sample=()=>values.push({time:performance.now()-start,height:view.getBoundingClientRect().height,claim:claim.getBoundingClientRect().top+scrollY});
      sample();node.click();
      await new Promise(resolve=>{function frame(){sample();if(performance.now()-start<500) requestAnimationFrame(frame);else resolve();}requestAnimationFrame(frame);});
      return values;
    });
    await waitDestinationMap(page);
    const start=samples[0],end=samples.at(-1),distance=end.height-start.height;
    assert.ok(Math.abs((end.claim-start.claim)-distance)<2,`The claim panel must move by the animated destination-height change (${JSON.stringify({start,end,distance})}).`);
    if(Math.abs(distance)>8) assert.ok(samples.some(sample=>Math.abs(sample.height-start.height)>2&&Math.abs(sample.height-end.height)>2),'Destination height must pass through intermediate positions, not jump.');
    const tickets=fixture.data.heldTickets,cities=new Map(fixture.snapshot.boardMap.cities.map(city=>[city.id,city]));
    assert.ok(cities.size>new Set(tickets.flatMap(ticket=>[ticket.fromCityId,ticket.toCityId])).size);
    const connections=page.locator('#destination-map-targets .destination-connection');
    assert.equal(await connections.count(),tickets.length,'One click must show every held destination.');
    for(const ticket of tickets) {
      const found=page.locator(`#destination-map-targets .destination-connection[data-ticket-id="${ticket.id}"]`);
      assert.equal(await found.count(),1);
      const endpoints=await found.locator('line').last().evaluate(line=>({x1:Number(line.getAttribute('x1')),y1:Number(line.getAttribute('y1')),x2:Number(line.getAttribute('x2')),y2:Number(line.getAttribute('y2')),dash:getComputedStyle(line).strokeDasharray}));
      const from=cities.get(ticket.fromCityId),to=cities.get(ticket.toCityId),length=Math.hypot(to.x-from.x,to.y-from.y);
      const dx=(to.x-from.x)/length*22,dy=(to.y-from.y)/length*22;
      for(const [key,value] of Object.entries({x1:from.x+dx,y1:from.y+dy,x2:to.x-dx,y2:to.y-dy})) assert.ok(Math.abs(endpoints[key]-value)<0.1,`${ticket.id} ${key} must connect its private city endpoints.`);
      assert.notEqual(endpoints.dash,'none');
    }
    const markers=page.locator('#destination-map-targets .destination-city-marker');
    const ids=await markers.evaluateAll(nodes=>nodes.map(node=>node.dataset.cityId).sort());
    assert.deepEqual(ids,[...new Set(tickets.flatMap(ticket=>[ticket.fromCityId,ticket.toCityId]))].sort(),'Only held-ticket cities may be highlighted.');
    await noOverflow(page);await screenshot(page,viewport.name+'-destination-map');
    const backBounds=await back.boundingBox();
    await page.touchscreen.tap(backBounds.x+backBounds.width/2,backBounds.y+backBounds.height+7);
    await row.waitFor({state:'visible'});
    await page.waitForFunction(()=>document.activeElement?.dataset.focusOrigin==='destination-test');
    assert.ok(Math.abs(await row.evaluate(node=>node.scrollLeft)-rowScroll)<1,'Back must restore the ticket row scroll.');
  });
  if(viewport.name==='pixel') await record('phone widths 360/375/390/414: Back does not reflow the destination heading',async()=>{
    for(const width of [360,375,390,414]) {
      await page.setViewportSize({width,height:viewport.height});
      await page.waitForTimeout(350);
      const heading=page.locator('.destination-heading');
      const closed=await heading.evaluate(node=>node.getBoundingClientRect().height);
      await page.locator('.destination-ticket').first().click();await waitDestinationMap(page);
      const open=await heading.evaluate(node=>node.getBoundingClientRect().height);
      assert.ok(Math.abs(open-closed)<1,`Back must not reflow the heading at ${width}px (${closed} to ${open}).`);
      await back.click();await page.locator('.held-tickets').waitFor({state:'visible'});
    }
    await page.setViewportSize({width:viewport.width,height:viewport.height});await page.waitForTimeout(350);
  });
  await record(viewport.name+': destination map stays open through camera frames and the first train draw',async()=>{
    await page.locator('.held-tickets .destination-ticket').first().click();await waitDestinationMap(page);
    await page.locator('#destination-map').evaluate(node=>node.dataset.identity='open-destination-map');
    await page.waitForTimeout(350);
    const cameraScroll=await page.evaluate(()=>scrollY);
    const oldSource=await page.locator('#destination-map-image').getAttribute('src');
    fixture.snapshot.boardMap.imageId='00112233445566778899aabbccddee10';publishSession();
    await page.waitForFunction(previous=>{const image=document.getElementById('destination-map-image');return image.naturalWidth===960&&image.getAttribute('src')!==previous;},oldSource);
    assert.equal(await page.locator('#destination-map').getAttribute('data-identity'),'open-destination-map');
    assert.ok(Math.abs(await page.evaluate(()=>scrollY)-cameraScroll)<2,'New camera frames must preserve the open destination view and page scroll.');
    const requestCount=requests.filter(request=>request.url.startsWith('/api/board-image/')).length;
    await heartbeatDelivered(page);
    assert.equal(requests.filter(request=>request.url.startsWith('/api/board-image/')).length,requestCount);
    const slot=fixture.data.actions.drawableFaceUpSlots.find(index=>fixture.snapshot.game.faceUp[index]!=='Locomotive');
    const card=page.getByRole('button',{name:`${fixture.snapshot.game.faceUp[slot]} · slot ${slot+1}`,exact:true});
    await card.evaluate(node=>node.scrollIntoView({block:'center'}));
    const scrollBefore=await page.evaluate(()=>scrollY);await card.click();
    await page.getByText('Choose your second card.',{exact:false}).waitFor();
    assert.equal(await page.locator('#destination-map').getAttribute('data-identity'),'open-destination-map');
    assert.equal(await page.locator('#destination-map').isVisible(),true);
    assert.ok(Math.abs(await page.evaluate(()=>scrollY)-scrollBefore)<2,'First draw must not disturb an open destination map or scrolling.');
    await noOverflow(page);
  });
  await record(viewport.name+': destination map handles rapid reversal, resizing and reduced motion',async()=>{
    await back.click();await page.locator('.held-tickets').waitFor({state:'visible'});
    await page.evaluate(()=>{const card=document.querySelector('.destination-ticket');card.click();document.querySelector('.destination-map-back').click();card.click();});
    await waitDestinationMap(page);
    await page.setViewportSize({width:viewport.width+64,height:viewport.height});
    await page.waitForTimeout(350);await noOverflow(page);
    const metrics=await page.locator('.destination-viewport').evaluate(node=>({height:node.getBoundingClientRect().height,content:document.getElementById('destination-map').getBoundingClientRect().height}));
    assert.ok(Math.abs(metrics.height-metrics.content)<3,'Resizing must fit the full destination map inside its animated area.');
    await page.setViewportSize({width:viewport.width,height:viewport.height});await page.waitForTimeout(350);
    await back.click();await page.locator('.held-tickets').waitFor({state:'visible'});
    await page.emulateMedia({reducedMotion:'reduce'});
    await page.locator('.destination-ticket').first().click();await waitDestinationMap(page);
    const movement=await page.locator('.destination-viewport').evaluate(async node=>{
      const start=node.getBoundingClientRect().height;await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));return Math.abs(node.getBoundingClientRect().height-start);
    });
    assert.ok(movement<1,'Reduced-motion mode must settle without a height animation.');
    await back.click();await page.emulateMedia({reducedMotion:'no-preference'});await noOverflow(page);
  });
  await record(viewport.name+': private destination overlays disappear immediately on Hide, background and handoff',async()=>{
    await showDestinationHand(page);await page.locator('.destination-ticket').first().click();await waitDestinationMap(page);
    await page.locator('#hide').click();await waitCovered(page);
    assert.equal(await page.locator('.destination-city-marker,.destination-connection').count(),0);
    await reveal(page);await page.locator('.destination-ticket').first().click();await waitDestinationMap(page);
    await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
    assert.equal(await page.locator('.destination-city-marker,.destination-connection').count(),0);
    const next=sessionRevision+1;await page.evaluate(()=>{delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));});await sessionDelivered(page,next);await reveal(page);
    await page.locator('.destination-ticket').first().click();await waitDestinationMap(page);
    delayedBoardImage={};fixture.snapshot.boardMap.imageId='00112233445566778899aabbccddee11';publishSession();
    while(!delayedBoardImage.response) await new Promise(resolve=>setTimeout(resolve,20));
    const old=delayedBoardImage;delayedBoardImage=null;
    fixture=structuredClone(fixtures.nextHuman);generation++;controllerGeneration++;await sessionDelivered(page,publishSession());await waitCovered(page);
    sendBoardImage(old.response,old.bytes);await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    assert.equal(await page.locator('#destination-map,.destination-city-marker,.destination-connection').count(),0,'An old image response must not restore the previous player’s private destinations.');
    fixture=fixtures.setup;generation++;controllerGeneration++;await sessionDelivered(page,publishSession());await waitCovered(page);
  });
}
async function main() {
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const origin='http://goldenticket.test:'+server.address().port;
  const executable=process.env.GOLDENTICKET_BROWSER_EXECUTABLE ||
    ['C:/Program Files/Google/Chrome/Application/chrome.exe','C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',chromium.executablePath()].find(file=>fs.existsSync(file));
  const browser=await chromium.launch({headless:true,executablePath:executable,args:[
    '--host-resolver-rules=MAP goldenticket.test 127.0.0.1','--no-proxy-server'
  ]});
  const browserVersion=browser.version();
  try {
    for(const viewport of viewports) {
      fixture=fixtures.setup; paired=false; pending=false; generation=1; controllerGeneration=1; requests=[]; offline=false; suppressHeartbeats=false; delayedReveal=null; delayedCommand=null; delayedBoardImage=null; activeGrant=null;
      const context=await browser.newContext({viewport:{width:viewport.width,height:viewport.height},deviceScaleFactor:1,isMobile:!viewport.name.includes('tablet'),hasTouch:true});
      const outsideLaptop=new Set(), browserRequests=[];
      // Keep the local host reachable while denying Internet destinations. Full browser offline
      // mode would also disconnect the LAN, which is a different acceptance condition.
      context.on('request',request=>{
        const destination=new URL(request.url());
        browserRequests.push(destination.pathname);
        if(['http:','https:'].includes(destination.protocol) && destination.origin!==origin) outsideLaptop.add(destination.href);
      });
      await context.route('**/*',async route=>{
        const destination=new URL(route.request().url());
        if(destination.origin!==origin) { outsideLaptop.add(destination.href); await route.abort('blockedbyclient'); }
        else await route.continue();
      });
      // An OS Internet probe can fail even while the local network remains available.
      await context.addInitScript(()=>{
        Object.defineProperty(navigator,'onLine',{configurable:true,value:false});
        // Observe delivery without altering chunks, timing, or application parsing. This lets
        // tests await events whose effects are intentionally invisible (for example, a camera
        // update while the hand is covered), instead of using a session polling surrogate.
        window.fixtureEvents={session:0,heartbeat:0};
        const fetch=window.fetch.bind(window);
        window.fetch=async(...args)=>{
          const response=await fetch(...args);
          if(new URL(args[0],location.href).pathname==='/api/events' && response.ok) {
            const getReader=response.body.getReader.bind(response.body);
            response.body.getReader=(...readerArgs)=>{
              const reader=getReader(...readerArgs),read=reader.read.bind(reader),decoder=new TextDecoder();
              let buffer='';
              reader.read=async(...readArgs)=>{
                const result=await read(...readArgs);
                if(result.value) {
                  buffer+=decoder.decode(result.value,{stream:true}).replace(/\r\n/g,'\n');
                  let boundary;
                  while((boundary=buffer.indexOf('\n\n'))!==-1) {
                    const frame=buffer.slice(0,boundary);buffer=buffer.slice(boundary+2);
                    if(/^event: session$/m.test(frame)) window.fixtureEvents.session=Number(/^id: (\d+)$/m.exec(frame)?.[1]||0);
                    if(/^event: heartbeat$/m.test(frame)) window.fixtureEvents.heartbeat++;
                  }
                }
                return result;
              };
              return reader;
            };
          }
          return response;
        };
      });
      const page=await context.newPage(); const errors=[]; page.on('pageerror',error=>errors.push(error.message));
      await record(viewport.name+': joining immediately in an ordinary LAN browser',async()=>{
        await page.goto(origin+'/companion/');
        assert.equal(await page.evaluate(()=>isSecureContext),false);
        await page.getByText('QUICK PLAY · NO INSTALL',{exact:true}).waitFor();
        assert.equal(await page.evaluate(()=>typeof crypto.randomUUID),'undefined');
        assert.equal(await page.evaluate(()=>typeof navigator.serviceWorker),'undefined');
        assert.equal(await page.locator('link[rel=manifest]').count(),0);
        assert.equal(browserRequests.includes('/companion/sw.js'),false);
        await noOverflow(page); await screenshot(page,viewport.name+'-connect');
        await page.getByLabel('Code shown on the laptop').fill('123456');
        await page.getByRole('button',{name:'Join game'}).click();
        await page.getByText('Approve this phone on the laptop to join.',{exact:false}).waitFor();
        assert.equal(requests.filter(r=>r.url==='/api/pair').length,1);
        paired=true; pending=false; publishSession();
        await page.locator('#curtain').waitFor({state:'visible'}); await noOverflow(page);
        await screenshot(page,viewport.name+'-curtain');
      });
      if(!mapOnly&&!destinationMapOnly) await blockedDrawCases(page,viewport);
      if(blockedDrawOnly) {assert.deepEqual(errors,[]);assert.deepEqual([...outsideLaptop],[]);await context.close();continue;}
      if(!mapOnly&&!destinationMapOnly) await changeFixture(page,'setup');
      if(!drawOnly&&!mapOnly) await destinationMapCases(page,viewport);
      if(destinationMapOnly) {assert.deepEqual(errors,[]);assert.deepEqual([...outsideLaptop],[]);await context.close();continue;}
      if(!drawOnly) await computerMapCases(page,viewport);
      if(mapOnly) {assert.deepEqual(errors,[]);assert.deepEqual([...outsideLaptop],[]);await context.close();continue;}
      await record(viewport.name+': destination checks and outside taps preserve the private hand',async()=>{
        await reveal(page); await noOverflow(page);
        const choices=page.locator('#private input[type=checkbox]'); assert.equal(await choices.count(),3);
        assert.equal(await page.getByRole('button',{name:'Keep selected tickets'}).isDisabled(),true);
        const initialGrant={...activeGrant}, hideCount=requests.filter(r=>r.url==='/api/hide').length;
        await choices.nth(0).check(); await choices.nth(1).check();
        await choices.nth(0).uncheck();
        await page.getByRole('heading',{name:'Golden Ticket',exact:true}).tap();
        await page.evaluate(()=>{window.dispatchEvent(new Event('blur'));document.dispatchEvent(new PointerEvent('pointercancel',{bubbles:true}));});
        assert.equal(activeGrant.handoffGeneration,initialGrant.handoffGeneration);
        assert.equal(activeGrant.grant,initialGrant.grant);
        assert.equal(await page.locator('#private').isVisible(),true);
        assert.deepEqual(await choices.evaluateAll(nodes=>nodes.map(node=>node.checked)),[false,true,false]);
        assert.equal(requests.filter(r=>r.url==='/api/hide').length,hideCount);
        assert.equal(requests.some(r=>r.url==='/api/activity'),false);
      });
      await record(viewport.name+': one SSE connection stays live without polling or repeated snapshots',async()=>{
        await sessionDelivered(page);
        const revision=sessionRevision, streams=requests.filter(r=>r.url==='/api/events').length;
        await heartbeatDelivered(page); await heartbeatDelivered(page);
        assert.equal(sessionRevision,revision,'Unchanged games send only stream heartbeats, not fresh session snapshots.');
        assert.equal(requests.filter(r=>r.url==='/api/events').length,streams,'Heartbeats must reuse the open connection.');
        assert.equal(requests.some(r=>r.url==='/api/session'),false,'The browser must not poll session data.');
        assert.equal(await page.locator('#private').isVisible(),true);
        assert.equal(await page.locator('#private input[type=checkbox]').nth(1).isChecked(),true);
      });
      await record(viewport.name+': human opening tickets and pass-and-hide',async()=>{
        const choices=page.locator('#private input[type=checkbox]');
        await choices.nth(0).check(); await choices.nth(1).check();
        await screenshot(page,viewport.name+'-opening-tickets');
        await page.getByRole('button',{name:'Keep selected tickets'}).click();
        await waitCovered(page); await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
        const command=requests.filter(r=>r.url==='/api/command').at(-1).body.command;
        assert.equal(command.kind,'keepTickets'); assert.equal(command.keptTickets.length,2); assert.equal(command.returnedTickets.length,1);
      });
      await record(viewport.name+': card draw, second draw and next human',async()=>{
        await changeFixture(page,'turnStart'); await reveal(page); await noOverflow(page);
        await drawPicker(page,viewport.name);
        await screenshot(page,viewport.name+'-private-turn');
        await page.getByRole('button',{name:'Draw a blind card'}).click();
        await page.getByText('Choose your second card.',{exact:false}).waitFor();
        assert.equal(requests.filter(r=>r.url==='/api/command').at(-1).body.command.slot,null);
        await drawPicker(page,viewport.name+'-second-card');
        const locomotive=page.getByRole('button',{name:'Locomotive · slot 3',exact:true});
        assert.equal(await locomotive.isDisabled(),true);
        const count=requests.filter(r=>r.url==='/api/command').length;
        await locomotive.scrollIntoViewIfNeeded();const box=await locomotive.boundingBox();
        await page.touchscreen.tap(box.x+box.width/2,box.y+box.height/2);
        assert.equal(requests.filter(r=>r.url==='/api/command').length,count,'Tapping a disabled locomotive must not submit a draw.');
        assert.equal(await page.locator('#private').isVisible(),true);
        await page.getByRole('button',{name:'Draw a blind card'}).click(); await waitCovered(page);
        await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
      });
      await record(viewport.name+': destination cards remain one scrollable row as the hand grows',async()=>{
        fixtures.manyDestinations=structuredClone(fixtures.turnStart);
        const cityPairs=[['Denver','Pittsburgh'],['Portland','Phoenix'],['Winnipeg','Little Rock'],['Sault St. Marie','Oklahoma City'],['San Francisco','New York'],['Saint Louis','Los Angeles'],['Seattle','Vancouver'],['Montréal','Santa Fe'],['Boston','Miami'],['Calgary','Salt Lake City'],['Duluth','Houston'],['Kansas City','Nashville']];
        const held=cityPairs.map(([from,to],index)=>({id:`many-${index}`,label:`${from} – ${to}`,from,to,points:index%3+11}));
        fixtures.manyDestinations.data.heldTickets=held.slice(0,3);
        await changeFixture(page,'manyDestinations'); await reveal(page);
        const row=page.getByRole('region',{name:'Your destination tickets',exact:true});
        const initialHeight=(await row.boundingBox()).height;
        fixtures.manyDestinations.data.heldTickets=held;
        await changeFixture(page,'manyDestinations'); await reveal(page);
        assert.equal(await row.locator('.destination-ticket').count(),12);
        const metrics=await row.evaluate(node=>({height:node.getBoundingClientRect().height,width:node.clientWidth,scrollWidth:node.scrollWidth,top:[...node.children].map(card=>card.getBoundingClientRect().top),firstWidth:node.firstElementChild.getBoundingClientRect().width,overflow:getComputedStyle(node).overflowX,scrollbar:getComputedStyle(node).scrollbarWidth}));
        assert.ok(Math.abs(metrics.height-initialHeight)<1,'More tickets must not make the row taller.');
        assert.ok(metrics.top.every(top=>Math.abs(top-metrics.top[0])<1),'All cards must remain on the same row.');
        assert.ok(metrics.scrollWidth>metrics.width && metrics.firstWidth<metrics.width-20,'The next card peeks into the horizontal scrolling area.');
        assert.equal(metrics.overflow,'auto'); assert.notEqual(metrics.scrollbar,'none');
        await noOverflow(page); await screenshot(page,viewport.name+'-destination-cards');
        await row.screenshot({path:path.join(output,viewport.name+'-destination-cards-row.png')});
        await row.focus(); assert.equal(await row.evaluate(node=>node===document.activeElement),true);
        await page.keyboard.press('ArrowRight'); await page.waitForFunction(()=>document.querySelector('.held-tickets').scrollLeft>0);
        await row.evaluate(node=>node.scrollLeft=node.scrollWidth);
        await expectLastDestination(row);
        const savedScroll=await row.evaluate(node=>{node.dataset.identity='original-destination-row';return node.scrollLeft;});
        fixture.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:'Keep the board still while the camera checks the trains.',detectedRoute:null};
        publishSession();
        await page.locator('#train-draw-help').filter({hasText:'Keep the board still while the camera checks the trains.'}).waitFor();
        assert.equal(await row.getAttribute('data-identity'),'original-destination-row');
        assert.ok(Math.abs(await row.evaluate(node=>node.scrollLeft)-savedScroll)<1,'Camera-only updates must preserve destination scroll position.');
        await noOverflow(page);
      });
      await record(viewport.name+': face-up card keeps its actual slot number',async()=>{
        await changeFixture(page,'turnStart'); await reveal(page);
        const slot=3, kind=fixture.data.view.public.faceUp[slot];
        await page.getByRole('button',{name:`${kind} · slot ${slot+1}`,exact:true}).click();
        await page.getByText('Choose your second card.',{exact:false}).waitFor();
        const command=requests.filter(r=>r.url==='/api/command').at(-1).body.command;
        assert.equal(command.kind,'drawTrain'); assert.equal(command.slot,slot);
        // Slot 3 is a disabled locomotive; slot 5 must still send index 4 rather
        // than an index compressed around the disabled slot.
        const secondSlot=4, secondKind=fixture.data.view.public.faceUp[secondSlot];
        await page.getByRole('button',{name:`${secondKind} · slot ${secondSlot+1}`,exact:true}).click(); await waitCovered(page);
        const secondCommand=requests.filter(r=>r.url==='/api/command').at(-1).body.command;
        assert.equal(secondCommand.kind,'drawTrain'); assert.equal(secondCommand.slot,secondSlot);
        await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
      });
      await record(viewport.name+': first face-up draw keeps the hand and scroll positions undisturbed until the next turn',async()=>{
        fixtures.continuousTurn=structuredClone(fixtures.turnStart);
        fixtures.continuousTurn.data.heldTickets=structuredClone(fixtures.manyDestinations.data.heldTickets);
        fixtures.continuousTurn.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:false,message:null,detectedRoute:null};
        await changeFixture(page,'continuousTurn'); await reveal(page);
        const row=page.getByRole('region',{name:'Your destination tickets',exact:true});
        await row.evaluate(node=>{node.scrollLeft=node.scrollWidth;node.dataset.identity='same-turn-destinations';});
        const slot=3, kind=fixture.snapshot.game.faceUp[slot];
        const chosen=page.getByRole('button',{name:`${kind} · slot ${slot+1}`,exact:true});
        // Center the card below the sticky header before measuring. A merely
        // in-viewport card can be covered by that header on a narrow screen,
        // causing Playwright itself to scroll it before delivering the click.
        await chosen.evaluate(node=>node.scrollIntoView({block:'center',inline:'nearest'}));
        await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
        const originalCardColor=await chosen.evaluate(node=>getComputedStyle(node).backgroundColor);
        const before=await page.evaluate(()=>{
          const privateView=document.getElementById('private'), row=privateView.querySelector('.held-tickets');
          const heading=privateView.querySelector('h2');
          window.turnContinuityViolations=[];
          window.turnContinuityObserver=new MutationObserver(()=>{
            if(privateView.hidden || !document.getElementById('curtain').hidden) window.turnContinuityViolations.push('covered');
            if(!row.isConnected || !heading.isConnected) window.turnContinuityViolations.push('replaced private content');
          });
          window.turnContinuityObserver.observe(privateView,{childList:true,attributes:true,subtree:true});
          window.turnContinuityObserver.observe(document.getElementById('curtain'),{attributes:true});
          return {y:scrollY,x:row.scrollLeft};
        });
        const reveals=requests.filter(r=>r.url==='/api/reveal').length, commands=requests.filter(r=>r.url==='/api/command').length;
        const handCount=Number(await page.locator(`.card[data-color="${kind}"] strong`).textContent());
        delayedCommand={}; await chosen.click();
        await page.waitForFunction(()=>[...document.querySelectorAll('.market-card')].every(button=>button.disabled));
        while(!delayedCommand.response) await new Promise(resolve=>setTimeout(resolve,20));
        assert.equal(await page.locator('#private').isVisible(),true); assert.equal(await page.locator('#curtain').isVisible(),false);
        await chosen.evaluate(node=>{node.click();node.click();});
        assert.equal(requests.filter(r=>r.url==='/api/command').length,commands+1,'Repeated taps while submitting must not draw another card.');
        // Let the public event observe the advanced game while the continuation is still in flight.
        await sessionDelivered(page);
        assert.equal(await page.locator('#private').isVisible(),true);
        const response=page.waitForResponse(r=>r.url().endsWith('/api/command'));
        send(delayedCommand.response,delayedCommand.reply); delayedCommand=null; await response;
        await page.getByText('Choose your second card.',{exact:false}).waitFor();
        assert.equal(await page.locator(`.card[data-color="${kind}"] strong`).textContent(),String(handCount+1));
        const replacement=page.getByRole('button',{name:`Orange · slot ${slot+1}`,exact:true});
        assert.equal(await replacement.isDisabled(),false);
        assert.equal(await replacement.getAttribute('data-color'),'Orange');
        await page.waitForFunction(()=>getComputedStyle(document.querySelector('.market-card[data-color="Orange"]')).backgroundColor==='rgb(222, 127, 43)');
        assert.notEqual(await replacement.evaluate(node=>getComputedStyle(node).backgroundColor),originalCardColor,'The replacement card must update its color as well as its label.');
        assert.equal(await page.getByRole('button',{name:'Locomotive · slot 3',exact:true}).isDisabled(),true);
        assert.equal(await row.getAttribute('data-identity'),'same-turn-destinations');
        assert.ok(Math.abs(await row.evaluate(node=>node.scrollLeft)-before.x)<1,'Drawing a card must preserve horizontal destination scrolling.');
        const afterScroll=await page.evaluate(()=>scrollY);
        assert.ok(Math.abs(afterScroll-before.y)<2,`Drawing a card must preserve vertical page scrolling (${before.y} before, ${afterScroll} after).`);
        assert.equal(requests.filter(r=>r.url==='/api/reveal').length,reveals,'The first card must not require another reveal.');
        assert.deepEqual(await page.evaluate(()=>{window.turnContinuityObserver.disconnect();return window.turnContinuityViolations;}),[]);
        await noOverflow(page);
        await page.screenshot({path:path.join(output,viewport.name+'-first-face-up-stays-open.png')});
        await page.getByRole('button',{name:`Orange · slot ${slot+1}`,exact:true}).click(); await waitCovered(page);
        await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
      });
      await record(viewport.name+': ticket offer return ordering',async()=>{
        await changeFixture(page,'turnStart'); await reveal(page);
        await page.getByRole('button',{name:'Draw destination tickets'}).click();
        await page.locator('#private input[type=checkbox]').first().waitFor({state:'visible'});
        assert.equal(requests.filter(r=>r.url==='/api/command').at(-1).body.command.kind,'drawTickets');
        await page.locator('#private input[type=checkbox]').nth(0).check();
        const before=await page.getByText('Return order:',{exact:false}).textContent();
        await page.getByRole('button',{name:'Reverse return order'}).click();
        const after=await page.getByText('Return order:',{exact:false}).textContent(); assert.notEqual(before,after);
        await noOverflow(page); await screenshot(page,viewport.name+'-ticket-offer');
        await page.getByRole('button',{name:'Keep selected tickets'}).click(); await waitCovered(page);
      });
      if(drawOnly) { assert.deepEqual(errors,[]); assert.deepEqual([...outsideLaptop],[]); await context.close(); continue; }
      await record(viewport.name+': route payment and laptop physical verification boundary',async()=>{
        await changeFixture(page,'turnStart'); await reveal(page);
        const routes=page.getByRole('combobox',{name:'Route to claim'}); assert.ok(await routes.locator('option').count()>0);
        await routes.selectOption({index:0});
        const payments=page.getByRole('combobox',{name:'Cards to spend'}); await payments.selectOption({index:0});
        const selectedId=await routes.inputValue();
        const fullRoute=fixture.snapshot.routes.find(route=>route.id===selectedId).label;
        const review=page.locator('#private [aria-live=polite]');
        assert.ok((await review.textContent()).includes(fullRoute),'Full route/lane must be readable even if the native select truncates');
        assert.match(await review.textContent(),/Pay .+\./);
        await payments.scrollIntoViewIfNeeded();
        const hideBox=await page.locator('#hide').boundingBox();
        assert.ok(hideBox.y>=0 && hideBox.y+hideBox.height<=viewport.height,'Hide must remain visible while scrolling private choices');
        await noOverflow(page); await screenshot(page,viewport.name+'-route-payment');
        await page.getByRole('button',{name:'Authorize this route and payment'}).click();
        await page.getByText('Follow the placement instructions on the laptop.',{exact:true}).waitFor();
        const command=requests.filter(r=>r.url==='/api/command').at(-1).body.command;
        assert.equal(command.kind,'planClaim'); assert.ok(command.routeId); assert.ok(command.payment.colorCards+command.payment.locomotives>0);
        assert.equal(await page.getByRole('button',{name:/verify|confirm placement/i}).count(),0);
      });
      await record(viewport.name+': shared AI instructions update through placement, correction, scoring and human handoff',async()=>{
        const reveals=requests.filter(r=>r.url==='/api/reveal').length, commands=requests.filter(r=>r.url==='/api/command').length;
        fixture=structuredClone(fixtures.physicalPlacement);
        fixture.snapshot.canControl=false; fixture.snapshot.revealSeatId=null;
        fixture.snapshot.message='Follow the current instructions on the laptop.';
        const version=fixture.snapshot.game.stateVersion;
        for(const [name,instruction] of [
          ['placement',"Place Computer 1's 1 Green train on Dallas - Houston (lane A). The camera will check its position and continue automatically."],
          ['correction','Remove the extra Green train from Dallas - Oklahoma City (lane A).'],
          ['scoring',"Move Computer 1's Green score marker to 1."]
        ]) {
          fixture.snapshot.guidance={title:'Computer 1',instruction};
          publishSession();
          await page.waitForFunction(expected=>document.getElementById('curtain-detail').textContent===expected,instruction);
          assert.equal(await page.locator('#handoff').textContent(),'Computer 1');
          assert.equal(await page.locator('#public-instruction').textContent(),instruction);
          assert.equal(fixture.snapshot.game.stateVersion,version);
          await waitCovered(page); assert.equal(await page.locator('#reveal').isDisabled(),true);
          await noOverflow(page); await screenshot(page,viewport.name+'-ai-'+name);
        }
        assert.equal(requests.filter(r=>r.url==='/api/reveal').length,reveals);
        assert.equal(requests.filter(r=>r.url==='/api/command').length,commands);
        fixture=fixtures.nextHuman; publishSession();
        await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor(); await waitCovered(page);
        assert.equal(await page.locator('#reveal').isDisabled(),false);
        assert.match(await page.locator('#curtain-detail').textContent(),/Reveal only when it is your turn/);
        assert.doesNotMatch(await page.locator('#public-instruction').textContent(),/Move Computer 1/);
        await reveal(page); assert.equal(await page.locator('#private').isVisible(),true);
      });
      await record(viewport.name+': detected camera route brings only its payments to the current hand',async()=>{
        fixtures.cameraTurn=structuredClone(fixtures.turnStart);
        fixtures.cameraTurn.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:false,message:null,detectedRoute:null};
        await changeFixture(page,'cameraTurn'); await reveal(page);
        assert.equal(await page.getByRole('combobox',{name:'Route to claim'}).count(),0);
        await page.getByText('Place your trains on the board.',{exact:false}).waitFor();
        await page.locator('#private .cards').evaluate(node=>node.dataset.identity='original-hand');
        await page.locator('#private .tickets').first().evaluate(node=>node.dataset.identity='original-tickets');
        const claim=fixture.data.actions.claims.find(candidate=>candidate.payments.length>1);
        assert.ok(claim,'Camera test needs a route with a real choice of payments.');
        const definition=fixture.snapshot.routes.find(route=>route.id===claim.routeId);
        const proposal={proposalId:'camera-placement-1',routeId:claim.routeId,label:definition.label,length:claim.length,ready:true};
        const initialVersion=fixture.snapshot.game.stateVersion, reveals=requests.filter(r=>r.url==='/api/reveal').length, commandCount=requests.filter(r=>r.url==='/api/command').length;
        fixture.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:null,detectedRoute:proposal};
        const pushedAt=Date.now(); publishSession();
        await page.locator('.detected-route').getByRole('heading',{name:definition.label,exact:true}).waitFor();
        assert.ok(Date.now()-pushedAt<1500,'Camera detection should arrive immediately rather than waiting for a two-second poll.');
        assert.equal(fixture.snapshot.game.stateVersion,initialVersion);
        assert.equal(await page.locator('#private .cards').getAttribute('data-identity'),'original-hand');
        assert.equal(await page.locator('#private .tickets').first().getAttribute('data-identity'),'original-tickets');
        assert.equal(await page.locator('.payment-choice').count(),claim.payments.length);
        assert.equal(await page.getByRole('button',{name:'Draw a blind card',exact:true}).isDisabled(),true);
        assert.equal(await page.getByRole('button',{name:'Draw destination tickets',exact:true}).isDisabled(),true);
        assert.equal(await page.locator('.market-card:enabled').count(),0);
        const pay=page.getByRole('button',{name:'Pay for detected route',exact:true}); assert.equal(await pay.isDisabled(),true);
        const paymentIndex=1; await page.locator('.payment-choice').nth(paymentIndex).click();
        assert.equal(await pay.isDisabled(),false);
        const panelBounds=await page.locator('.detected-route').boundingBox(), drawBounds=await page.locator('.draw-area').boundingBox();
        assert.ok(panelBounds.y+panelBounds.height<=drawBounds.y,'Detected payment must be above the draw controls.');
        await noOverflow(page); await page.locator('.detected-route').screenshot({path:path.join(output,viewport.name+'-detected-payment-panel.png')});
        await screenshot(page,viewport.name+'-detected-payment');
        fixture.snapshot.boardInteraction.detectedRoute={...proposal,ready:false};
        fixture.snapshot.boardInteraction.message='Confirming the trains on the board.';
        publishSession();
        await page.getByRole('region',{name:'Detected route payment',exact:true}).getByText('Confirming the trains on the board.',{exact:true}).waitFor();
        assert.equal(await pay.isDisabled(),true); assert.equal(await page.locator('.payment-choice').nth(paymentIndex).getAttribute('aria-pressed'),'true');
        fixture.snapshot.boardInteraction.detectedRoute={...proposal}; fixture.snapshot.boardInteraction.message=null;
        publishSession();
        await page.waitForFunction(()=>document.querySelector('.detected-pay')?.disabled===false);
        assert.equal(await page.locator('.payment-choice').nth(paymentIndex).getAttribute('aria-pressed'),'true');
        assert.equal(requests.filter(r=>r.url==='/api/reveal').length,reveals); assert.equal(requests.filter(r=>r.url==='/api/command').length,commandCount);
        await pay.click(); await waitCovered(page); await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
        const command=requests.filter(r=>r.url==='/api/command').at(-1).body.command;
        assert.equal(command.kind,'payDetectedRoute'); assert.equal(command.routeId,proposal.routeId); assert.equal(command.detectedClaimId,proposal.proposalId);
        assert.deepEqual(command.payment,claim.payments[paymentIndex]);
      });
      await record(viewport.name+': replacing camera proposals clears payment selection without revealing hidden cards',async()=>{
        await changeFixture(page,'cameraTurn');
        const reveals=requests.filter(r=>r.url==='/api/reveal').length;
        fixture.snapshot.boardInteraction.detectedRoute={...fixture.snapshot.boardInteraction.detectedRoute,proposalId:'camera-placement-2'};
        await sessionDelivered(page,publishSession()); await waitCovered(page);
        assert.equal(requests.filter(r=>r.url==='/api/reveal').length,reveals);
        await reveal(page); await page.locator('.payment-choice').first().click();
        fixture.snapshot.boardInteraction.detectedRoute={...fixture.snapshot.boardInteraction.detectedRoute,proposalId:'camera-placement-3'};
        publishSession();
        await page.waitForFunction(()=>document.querySelector('.detected-pay')?.disabled===true && !document.querySelector('.payment-choice[aria-pressed=true]'));
        await page.locator('.payment-choice').first().click();
        fixture.snapshot.boardInteraction.detectedRoute=null; fixture.snapshot.boardInteraction.cardActionsBlocked=false;
        publishSession();
        await page.locator('.detected-route').getByRole('heading',{name:'Claim a route',exact:true}).waitFor();
        assert.equal(await page.locator('.payment-choice').count(),0); assert.equal(await page.getByRole('button',{name:'Draw a blind card',exact:true}).isDisabled(),false);
        await page.locator('#hide').click(); await waitCovered(page);
        const claim=fixture.data.actions.claims[0], definition=fixture.snapshot.routes.find(route=>route.id===claim.routeId);
        fixture.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:null,detectedRoute:{proposalId:'camera-placement-4',routeId:claim.routeId,label:definition.label,length:claim.length,ready:true}};
        await sessionDelivered(page,publishSession()); await waitCovered(page);
      });
      await record(viewport.name+': a camera check blocks keeping tickets without losing checked destinations',async()=>{
        fixtures.cameraTicketOffer=structuredClone(fixtures.ticketOffer);
        await changeFixture(page,'cameraTicketOffer'); await reveal(page);
        const choices=page.locator('#private input[type=checkbox]'); await choices.first().check();
        await choices.first().evaluate(node=>node.dataset.identity='original-ticket-checkbox');
        const keep=page.getByRole('button',{name:'Keep selected tickets',exact:true}); assert.equal(await keep.isDisabled(),false);
        fixture.snapshot.boardInteraction={useCameraClaims:true,cardActionsBlocked:true,message:'Camera is checking the board.',detectedRoute:null};
        publishSession();
        await page.getByText('Camera is checking the board.',{exact:true}).waitFor();
        assert.equal(await keep.isDisabled(),true); assert.equal(await choices.first().isChecked(),true);
        assert.equal(await choices.first().getAttribute('data-identity'),'original-ticket-checkbox');
        fixture.snapshot.boardInteraction.cardActionsBlocked=false; fixture.snapshot.boardInteraction.message=null;
        publishSession();
        await page.waitForFunction(()=>[...document.querySelectorAll('#private button')].some(button=>button.textContent==='Keep selected tickets' && !button.disabled));
        assert.equal(await choices.first().isChecked(),true); assert.equal(await choices.first().getAttribute('data-identity'),'original-ticket-checkbox');
        await keep.click(); await waitCovered(page);
      });
      await record(viewport.name+': Hide, leaving the page and stale reveal cover private DOM',async()=>{
        await page.locator('#hide').click(); await waitCovered(page); await reveal(page);
        await page.evaluate(()=>window.dispatchEvent(new Event('pagehide'))); await waitCovered(page);
        const pageShowRevision=sessionRevision+1;
        await page.evaluate(()=>window.dispatchEvent(new Event('pageshow'))); await sessionDelivered(page,pageShowRevision);
        delayedReveal={};
        const clicked=page.locator('#reveal').click(); await clicked;
        while(!delayedReveal.response) await new Promise(resolve=>setTimeout(resolve,20));
        await page.locator('#hide').click();
        const revealed=page.waitForResponse(r=>r.url().endsWith('/api/reveal'));
        send(delayedReveal.response,delayedReveal.reply); delayedReveal=null;
        await revealed; await waitCovered(page);
        await sessionDelivered(page); await reveal(page);
        await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
        await waitCovered(page);
        await page.evaluate(()=>{delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));});
        await waitCovered(page);
        await page.waitForFunction(()=>!document.getElementById('reveal').disabled);
        delayedReveal={}; await page.locator('#reveal').click();
        while(!delayedReveal.response) await new Promise(resolve=>setTimeout(resolve,20));
        generation++; // Laptop Hide after granting, while the private response is still in transit.
        await sessionDelivered(page,publishSession());
        const oldResponse=page.waitForResponse(r=>r.url().endsWith('/api/reveal'));
        send(delayedReveal.response,delayedReveal.reply); delayedReveal=null; await oldResponse;
        await waitCovered(page);
      });
      await record(viewport.name+': WAN unavailable with all gameplay requests confined to the laptop',async()=>{
        assert.equal(await page.evaluate(()=>navigator.onLine),false);
        assert.ok(browserRequests.includes('/companion/app.js'));
        assert.ok(browserRequests.includes('/api/command'));
        assert.ok(browserRequests.includes('/api/events'));
        assert.equal(browserRequests.includes('/api/session'),false,'Session updates must use SSE without background polling.');
        assert.deepEqual([...outsideLaptop],[],'The companion must never request an Internet destination.');
      });
      await record(viewport.name+': published standings preview and exact PNG download',async()=>{
        assert.equal(await page.locator('#result').isVisible(),false);
        resultImageBytes=await syntheticStandingsPng(page);
        fixture=structuredClone(fixtures.turnStart);fixture.snapshot.canControl=false;fixture.snapshot.game.lifecycle='Finished';
        fixture.snapshot.message='Game finished. The final standings are ready.';
        fixture.snapshot.resultImage={id:'finished-image-1',fileName:'golden-ticket-final-standings.png'};
        publishSession();
        await page.locator('#result-preview').waitFor({state:'visible'});
        await page.waitForFunction(()=>document.getElementById('result-preview').naturalWidth>0);
        assert.equal(await page.locator('#curtain').isVisible(),false);assert.equal(await page.locator('#private').textContent(),'');
        assert.equal(await page.locator('#public').isVisible(),false);await noOverflow(page);await screenshot(page,viewport.name+'-final-standings');
        const delivery=requests.filter(r=>r.url==='/api/result-image/finished-image-1');assert.equal(delivery.length,1);assert.ok(delivery[0].headers['x-goldenticket-tab']);
        const downloaded=page.waitForEvent('download');await page.locator('#result-save').click();const file=await downloaded;
        assert.equal(file.suggestedFilename(),'golden-ticket-final-standings.png');const filePath=path.join(output,viewport.name+'-download.png');await file.saveAs(filePath);assert.deepEqual(fs.readFileSync(filePath),resultImageBytes);
      });
      await record(viewport.name+': replaced standings remain downloadable without native sharing APIs',async()=>{
        assert.equal(await page.evaluate(()=>typeof navigator.share),'undefined');
        const replacement=page.waitForResponse(r=>r.url().endsWith('/api/result-image/finished-image-2'));
        fixture.snapshot.resultImage.id='finished-image-2'; publishSession();
        await replacement;
        await page.getByText('Your results are ready.',{exact:true}).waitFor();
        assert.equal(await page.locator('#result-share').count(),0);assert.equal(await page.locator('#result-save').isVisible(),true);
        await page.getByText('On iPhone, downloaded images may be in Files.',{exact:false}).waitFor();
      });
      await record(viewport.name+': revocation removes standings and a new game starts covered',async()=>{
        paired=false; publishSession();
        await page.locator('#connect').waitFor({state:'visible'});assert.equal(await page.locator('#result').isVisible(),false);
        assert.equal(await page.locator('#result-preview').getAttribute('src'),null);assert.equal(await page.locator('#result-save').getAttribute('href'),null);
        fixture=fixtures.turnStart;paired=true; publishSession();
        await page.locator('#curtain').waitFor({state:'visible'});assert.equal(await page.locator('#result').isVisible(),false);await waitCovered(page);
      });
      await record(viewport.name+': a stalled event stream covers the hand and reconnects',async()=>{
        await reveal(page); await heartbeatDelivered(page);
        const connections=requests.filter(r=>r.url==='/api/events').length;
        suppressHeartbeats=true;
        await page.getByText('Laptop connection unavailable',{exact:true}).waitFor({timeout:10000}); await waitCovered(page);
        const nextRevision=sessionRevision+1; suppressHeartbeats=false;
        await sessionDelivered(page,nextRevision); await waitCovered(page);
        assert.ok(requests.filter(r=>r.url==='/api/events').length>connections,'A stalled stream must establish a fresh connection.');
        assert.equal(requests.some(r=>r.url==='/api/session'),false);
      });
      await record(viewport.name+': disconnected cover and browser reconnect',async()=>{
        await sessionDelivered(page); await reveal(page);
        offline=true; disconnectStreams();
        await page.getByText('Laptop connection unavailable',{exact:true}).waitFor({timeout:10000}); await waitCovered(page);
        await screenshot(page,viewport.name+'-disconnected');
        assert.equal(await page.evaluate(()=>typeof window.caches),'undefined');
        assert.equal(browserRequests.includes('/companion/sw.js'),false);
        const nextRevision=sessionRevision+1;
        offline=false;
        await sessionDelivered(page,nextRevision);
        await waitCovered(page);
        await reveal(page);
        assert.equal(await page.evaluate(()=>localStorage.length+sessionStorage.length),0);
        assert.deepEqual([...outsideLaptop],[]);
        assert.deepEqual(errors,[]);
      });
      if(viewport.name==='pixel') await record('pixel: untouched destinations remain visible for two minutes and can still be submitted',async()=>{
        await changeFixture(page,'ticketOffer'); await reveal(page);
        const choices=page.locator('#private input[type=checkbox]');
        await choices.nth(0).check(); await choices.nth(1).check(); await choices.nth(0).uncheck();
        const selectedTicket=fixture.data.offeredTickets[1].id;
        const originalGrant={...activeGrant}, revealCount=requests.filter(r=>r.url==='/api/reveal').length, hideCount=requests.filter(r=>r.url==='/api/hide').length;
        const start=Date.now(); await page.clock.setFixedTime(start);
        // Only Date is controlled: real Chromium input, the 500 ms watchdog, event streaming and
        // HTTP requests still run. Advance in five-second steps so heartbeat checks remain
        // meaningful. No pointer, keyboard or checkbox interaction occurs during this wait.
        for(let step=1;step<=24;step++) {
          await page.clock.setFixedTime(start+step*5000);
          await heartbeatDelivered(page);
          assert.equal(await choices.nth(0).isChecked(),false); assert.equal(await choices.nth(1).isChecked(),true);
          assert.equal(await page.locator('#private').isVisible(),true);
        }
        assert.deepEqual(activeGrant,originalGrant);
        assert.equal(requests.filter(r=>r.url==='/api/reveal').length,revealCount);
        assert.equal(requests.filter(r=>r.url==='/api/hide').length,hideCount);
        assert.equal(requests.some(r=>r.url==='/api/activity'),false);
        await page.getByRole('button',{name:'Keep selected tickets'}).click(); await waitCovered(page);
        await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
        const payload=requests.filter(r=>r.url==='/api/command').at(-1).body;
        assert.equal(payload.grant,originalGrant.grant); assert.deepEqual(payload.command.keptTickets,[selectedTicket]);
        assert.deepEqual(errors,[]);
      });
      await context.close();
    }
    fs.writeFileSync(path.join(output,'browser-ui-results.json'),JSON.stringify({browser:'Chromium '+browserVersion,scope:blockedDrawOnly?'Focused camera-rejected draws, explicit retry and hand continuity over SSE':destinationMapOnly?'Focused private destination map transitions and lifecycle':mapOnly?'Focused computer map and image lifecycle over SSE':drawOnly?'Focused draw controls and opening-ticket flow over SSE':'Complete companion workflow over SSE',fixtureFile:fixturePath,fixtureTransport:'Real insecure HTTP and fetch-streamed SSE on goldenticket.test mapped to loopback; no browser security overrides or certificate dependencies; no real-phone acceptance claim.',eventDelivery:'Session snapshots are published only on fixture or authorization changes. Two-second heartbeats contain no game snapshot. The former session polling endpoint returns 410.',internetIsolation:'Page requests outside the laptop fixture origin are blocked and recorded; navigator.onLine is false. Browser/OS background traffic is outside this harness.',viewports:viewports.map(v=>`${v.width}×${v.height}`),screenshots:process.env.GOLDENTICKET_BOARD_IMAGE_FIXTURE?'Synthetic player data with local upright board camera fixture; gold targets rendered by the browser':'Synthetic player data and public calibration board only',results},null,2));
    console.log(`${results.length} browser UI scenarios passed.`);
  } finally { await browser.close(); clearInterval(heartbeatTimer); disconnectStreams(); await new Promise(resolve=>server.close(resolve)); }
}
main().catch(error=>{console.error(error);process.exitCode=1;clearInterval(heartbeatTimer);disconnectStreams();server.close();});
