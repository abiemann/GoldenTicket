// Actual Chromium rendering/lifecycle tests for HTTP Quick play on phone-size viewports.
// Synthetic hosts and .NET payloads do not prove real-device/LAN acceptance.
// First run CompanionHostBrowserFixtures with GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY set.
// NODE_PATH must point to the existing bundled node_modules containing Playwright. No npm install.
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
fs.mkdirSync(output, { recursive: true });
const mime = { '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.svg':'image/svg+xml' };
const results = [];
let fixture = fixtures.setup, paired = false, pending = false, generation = 1, requests = [], offline = false, delayedReveal = null;
let resultImageBytes = null, activeGrant = null, controlledNow = null;
const serverNow = () => controlledNow ?? Date.now();
const send = (response, body, status=200) => { response.writeHead(status, {'Content-Type':'application/json', 'Cache-Control':'no-store'}); response.end(JSON.stringify(body)); };
const requestHandler = async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (url.pathname.startsWith('/api/')) {
    if (offline) { request.socket.destroy(); return; }
    let body = ''; for await(const chunk of request) body += chunk;
    body = body ? JSON.parse(body) : {};
    requests.push({url:url.pathname, body, headers:request.headers});
    if(url.pathname === '/api/session') return send(response, paired ? { paired, csrf:'test-csrf', handoffGeneration:generation, controllerGeneration:1, apiVersion:'1', assetsVersion:'5', snapshot:fixture.snapshot } : {paired,pending});
    if(url.pathname.startsWith('/api/result-image/')) {
      if(!paired || !resultImageBytes || url.pathname!==`/api/result-image/${fixture.snapshot.resultImage?.id}` || !request.headers['x-goldenticket-tab']) return send(response,{},404);
      response.writeHead(200,{'Content-Type':'image/png','Content-Length':resultImageBytes.length,'Cache-Control':'no-store'}); response.end(resultImageBytes); return;
    }
    if(url.pathname === '/api/pair') { pending=true; return send(response, {pending:true,identity:'2468'}); }
    if(url.pathname === '/api/hide') { generation++; activeGrant=null; return send(response,{hidden:true}); }
    if(url.pathname === '/api/reveal') {
      const reply = {grant:'test-private-grant', expiresAt:new Date(serverNow()+30000).toISOString(), handoffGeneration:++generation, data:fixture.data};
      activeGrant={seat:body.seat,sessionId:body.sessionId,version:body.version,grant:reply.grant,handoffGeneration:reply.handoffGeneration,expiresAt:reply.expiresAt};
      if(delayedReveal) { delayedReveal.response=response; delayedReveal.reply=reply; return; }
      return send(response, reply);
    }
    if(url.pathname === '/api/activity') {
      if(!paired || !activeGrant || generation!==activeGrant.handoffGeneration || serverNow()>=Date.parse(activeGrant.expiresAt) ||
        ['seat','sessionId','version','grant','handoffGeneration'].some(key=>body[key]!==activeGrant[key])) return send(response,{},409);
      activeGrant.expiresAt=new Date(serverNow()+30000).toISOString();
      return send(response,{expiresAt:activeGrant.expiresAt,handoffGeneration:generation});
    }
    if(url.pathname === '/api/command') {
      if(body.command.kind === 'keepTickets') fixture = fixtures.secondHumanSetup;
      if(body.command.kind === 'drawTrain') fixture = fixture === fixtures.turnStart ? fixtures.secondDraw : fixtures.nextHuman;
      if(body.command.kind === 'drawTickets') fixture = fixtures.ticketOffer;
      if(body.command.kind === 'planClaim') fixture = fixtures.physicalPlacement;
      generation++; activeGrant=null;
      return send(response,{accepted:true,duplicate:false,stateVersion:fixture.snapshot.game.stateVersion,message:'Choice saved on the laptop.'});
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
async function record(name, action) { const start=Date.now(); await action(); results.push({name,passed:true,milliseconds:Date.now()-start}); console.log('PASS '+name); }
async function waitCovered(page) { await page.locator('#curtain').waitFor({state:'visible'}); assert.equal(await page.locator('#private').textContent(),''); }
async function reveal(page) { await page.locator('#reveal').waitFor({state:'visible'}); await page.locator('#reveal').click(); await page.locator('#private').waitFor({state:'visible'}); }
async function changeFixture(page, next) {
  fixture = fixtures[next]; generation++;
  await page.locator('#hide').click();
  await page.waitForFunction(expected => document.getElementById('handoff').textContent === expected, fixture.snapshot.message);
  // Same player but changed phase can have identical handoff text. Await an actual later poll.
  await page.waitForResponse(r => r.url().endsWith('/api/session') && r.ok());
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
    for(const viewport of [{name:'pixel',width:448,height:900},{name:'small-phone',width:320,height:740},{name:'tablet',width:768,height:1024}]) {
      fixture=fixtures.setup; paired=false; pending=false; generation=1; requests=[]; offline=false; delayedReveal=null; activeGrant=null; controlledNow=null;
      const context=await browser.newContext({viewport:{width:viewport.width,height:viewport.height},deviceScaleFactor:1,isMobile:viewport.name!=='tablet',hasTouch:true});
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
        paired=true; pending=false;
        await page.locator('#curtain').waitFor({state:'visible'}); await noOverflow(page);
        await screenshot(page,viewport.name+'-curtain');
      });
      await record(viewport.name+': destination checks and outside taps preserve the private hand',async()=>{
        await reveal(page); await noOverflow(page);
        const choices=page.locator('#private input[type=checkbox]'); assert.equal(await choices.count(),3);
        assert.equal(await page.getByRole('button',{name:'Keep selected tickets'}).isDisabled(),true);
        const initialGrant={...activeGrant}, hideCount=requests.filter(r=>r.url==='/api/hide').length;
        const renewed=page.waitForResponse(r=>r.url().endsWith('/api/activity') && r.ok());
        await choices.nth(0).check(); await choices.nth(1).check();
        await choices.nth(0).uncheck();
        await page.getByRole('heading',{name:'Golden Ticket',exact:true}).tap();
        await page.evaluate(()=>{window.dispatchEvent(new Event('blur'));document.dispatchEvent(new PointerEvent('pointercancel',{bubbles:true}));});
        const response=await renewed;
        assert.ok(Date.parse((await response.json()).expiresAt)>Date.parse(initialGrant.expiresAt));
        assert.equal(activeGrant.handoffGeneration,initialGrant.handoffGeneration);
        assert.equal(activeGrant.grant,initialGrant.grant);
        assert.equal(await page.locator('#private').isVisible(),true);
        assert.deepEqual(await choices.evaluateAll(nodes=>nodes.map(node=>node.checked)),[false,true,false]);
        assert.equal(requests.filter(r=>r.url==='/api/hide').length,hideCount);
        const activity=requests.filter(r=>r.url==='/api/activity').at(-1);
        assert.deepEqual(activity.body,Object.fromEntries(['seat','sessionId','version','grant','handoffGeneration'].map(key=>[key,initialGrant[key]])));
        assert.equal(activity.headers['x-goldenticket-csrf'],'test-csrf');
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
        await screenshot(page,viewport.name+'-private-turn');
        await page.getByRole('button',{name:'Draw a blind card'}).click(); await waitCovered(page); await reveal(page);
        await page.getByText('Choose your second card.',{exact:false}).waitFor();
        assert.equal(await page.getByRole('button',{name:/Locomotive · slot/}).count(),0);
        await page.getByRole('button',{name:'Draw a blind card'}).click(); await waitCovered(page);
        await page.getByText('Pass this device to Jordan.',{exact:true}).waitFor();
      });
      await record(viewport.name+': ticket offer return ordering',async()=>{
        await changeFixture(page,'turnStart'); await reveal(page);
        await page.getByRole('button',{name:'Draw destination tickets'}).click(); await waitCovered(page); await reveal(page);
        await page.locator('#private input[type=checkbox]').nth(0).check();
        const before=await page.getByText('Return order:',{exact:false}).textContent();
        await page.getByRole('button',{name:'Reverse return order'}).click();
        const after=await page.getByText('Return order:',{exact:false}).textContent(); assert.notEqual(before,after);
        await noOverflow(page); await screenshot(page,viewport.name+'-ticket-offer');
        await page.getByRole('button',{name:'Keep selected tickets'}).click(); await waitCovered(page);
      });
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
        await page.getByRole('button',{name:'Authorize this route and payment'}).click(); await waitCovered(page);
        const command=requests.filter(r=>r.url==='/api/command').at(-1).body.command;
        assert.equal(command.kind,'planClaim'); assert.ok(command.routeId); assert.ok(command.payment.colorCards+command.payment.locomotives>0);
        await reveal(page); await page.getByRole('heading',{name:'Follow the placement instructions on the laptop.'}).waitFor();
        assert.equal(await page.getByRole('button',{name:/verify|confirm placement/i}).count(),0);
      });
      await record(viewport.name+': Hide, leaving the page and stale reveal cover private DOM',async()=>{
        await page.locator('#hide').click(); await waitCovered(page); await reveal(page);
        await page.evaluate(()=>window.dispatchEvent(new Event('pagehide'))); await waitCovered(page);
        delayedReveal={};
        const clicked=page.locator('#reveal').click(); await clicked;
        while(!delayedReveal.response) await new Promise(resolve=>setTimeout(resolve,20));
        await page.locator('#hide').click();
        send(delayedReveal.response,delayedReveal.reply); delayedReveal=null;
        await page.waitForResponse(r=>r.url().endsWith('/api/reveal')); await waitCovered(page);
        await page.waitForResponse(r=>r.url().endsWith('/api/session')); await reveal(page);
        await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
        await waitCovered(page);
        await page.evaluate(()=>{delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));});
        await waitCovered(page);
        await page.waitForResponse(r=>r.url().endsWith('/api/session'));
        delayedReveal={}; await page.locator('#reveal').click();
        while(!delayedReveal.response) await new Promise(resolve=>setTimeout(resolve,20));
        generation++; // Laptop Hide after granting, while the private response is still in transit.
        const latest=await page.waitForResponse(r=>r.url().endsWith('/api/session')); await latest.finished();
        await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
        const oldResponse=page.waitForResponse(r=>r.url().endsWith('/api/reveal'));
        send(delayedReveal.response,delayedReveal.reply); delayedReveal=null; await oldResponse;
        await waitCovered(page);
      });
      await record(viewport.name+': WAN unavailable with all gameplay requests confined to the laptop',async()=>{
        assert.equal(await page.evaluate(()=>navigator.onLine),false);
        assert.ok(browserRequests.includes('/companion/app.js'));
        assert.ok(browserRequests.includes('/api/command'));
        assert.deepEqual([...outsideLaptop],[],'The companion must never request an Internet destination.');
      });
      await record(viewport.name+': published standings preview and exact PNG download',async()=>{
        assert.equal(await page.locator('#result').isVisible(),false);
        resultImageBytes=await syntheticStandingsPng(page);
        fixture=structuredClone(fixtures.turnStart);fixture.snapshot.canControl=false;fixture.snapshot.game.lifecycle='Finished';
        fixture.snapshot.message='Game finished. The final standings are ready.';
        fixture.snapshot.resultImage={id:'finished-image-1',fileName:'golden-ticket-final-standings.png'};
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
        fixture.snapshot.resultImage.id='finished-image-2';
        await page.waitForResponse(r=>r.url().endsWith('/api/result-image/finished-image-2'));
        await page.getByText('Your results are ready.',{exact:true}).waitFor();
        assert.equal(await page.locator('#result-share').count(),0);assert.equal(await page.locator('#result-save').isVisible(),true);
        await page.getByText('On iPhone, downloaded images may be in Files.',{exact:false}).waitFor();
      });
      await record(viewport.name+': revocation removes standings and a new game starts covered',async()=>{
        paired=false;
        await page.locator('#connect').waitFor({state:'visible'});assert.equal(await page.locator('#result').isVisible(),false);
        assert.equal(await page.locator('#result-preview').getAttribute('src'),null);assert.equal(await page.locator('#result-save').getAttribute('href'),null);
        fixture=fixtures.turnStart;paired=true;
        await page.locator('#curtain').waitFor({state:'visible'});assert.equal(await page.locator('#result').isVisible(),false);await waitCovered(page);
      });
      await record(viewport.name+': disconnected cover and browser reconnect',async()=>{
        await page.waitForResponse(r=>r.url().endsWith('/api/session')); await reveal(page);
        offline=true;
        await page.getByText('Laptop connection unavailable',{exact:true}).waitFor({timeout:10000}); await waitCovered(page);
        await screenshot(page,viewport.name+'-disconnected');
        assert.equal(await page.evaluate(()=>typeof window.caches),'undefined');
        assert.equal(browserRequests.includes('/companion/sw.js'),false);
        offline=false;
        await page.waitForResponse(r=>r.url().endsWith('/api/session')&&r.ok());
        await waitCovered(page);
        await reveal(page);
        assert.equal(await page.evaluate(()=>localStorage.length+sessionStorage.length),0);
        assert.deepEqual([...outsideLaptop],[]);
        assert.deepEqual(errors,[]);
      });
      if(viewport.name==='pixel') await record('pixel: destination activity renews idle time, then inactivity covers the hand',async()=>{
        await changeFixture(page,'ticketOffer'); await reveal(page);
        const choices=page.locator('#private input[type=checkbox]');
        const initialDeadline=Date.parse(activeGrant.expiresAt), initialGeneration=activeGrant.handoffGeneration;
        const start=Date.now(); controlledNow=start; await page.clock.setFixedTime(start);
        // Only Date is controlled: real Chromium input, the 500 ms watchdog, polling and
        // HTTP requests still run. Advance in five-second steps so heartbeat checks remain
        // meaningful, awaiting a fresh poll before changing each actual checkbox.
        for(let step=1;step<=7;step++) {
          controlledNow=start+step*5000; await page.clock.setFixedTime(controlledNow);
          await page.waitForResponse(r=>r.url().endsWith('/api/session') && r.ok());
          const renewed=page.waitForResponse(r=>r.url().endsWith('/api/activity') && r.ok());
          await choices.nth(0).setChecked(step%2===1); await renewed;
          assert.equal(await choices.nth(0).isChecked(),step%2===1);
          assert.equal(await page.locator('#private').isVisible(),true);
        }
        assert.ok(controlledNow>initialDeadline,'Selections must remain visible beyond the original reveal deadline.');
        assert.equal(activeGrant.handoffGeneration,initialGeneration);
        const renewals=requests.filter(r=>r.url==='/api/activity').length;
        for(let step=8;step<=12;step++) {
          controlledNow=start+step*5000; await page.clock.setFixedTime(controlledNow);
          await page.waitForResponse(r=>r.url().endsWith('/api/session') && r.ok());
          assert.equal(await choices.nth(0).isChecked(),true);
          assert.equal(await page.locator('#private').isVisible(),true);
        }
        assert.equal(requests.filter(r=>r.url==='/api/activity').length,renewals,'Polling must not renew idle time.');
        controlledNow=start+65000; await page.clock.setFixedTime(controlledNow); await waitCovered(page);
        assert.deepEqual(errors,[]);
      });
      await context.close();
    }
    fs.writeFileSync(path.join(output,'browser-ui-results.json'),JSON.stringify({browser:'Chromium '+browserVersion,fixtureTransport:'Real insecure HTTP goldenticket.test origin mapped to loopback; no browser security overrides or certificate dependencies; no real-phone acceptance claim.',internetIsolation:'Page requests outside the laptop fixture origin are blocked and recorded; navigator.onLine is false. Browser/OS background traffic is outside this harness.',viewports:['448×900','320×740','768×1024'],screenshots:'Synthetic player data only',results},null,2));
    console.log(`${results.length} browser UI scenarios passed.`);
  } finally { await browser.close(); await new Promise(resolve=>server.close(resolve)); }
}
main().catch(error=>{console.error(error);process.exitCode=1; server.close();});
