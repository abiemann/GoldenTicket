// Actual Chromium rendering/lifecycle test, with the shipped PWA and synthetic .NET bridge data.
// This localhost fixture is not evidence of LAN/certificate/phone installation acceptance.
// First run CompanionHostBrowserFixtures with GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY set.
// NODE_PATH must point to the existing bundled node_modules containing Playwright. No npm install.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const root = path.resolve(__dirname, '../..');
const shell = path.join(root, 'src/GoldenTicket.CompanionHost/wwwroot');
const fixturePath = process.env.GOLDENTICKET_COMPANION_FIXTURES || path.join(root, 'artifacts/companion-browser/fixtures.json');
const output = process.env.GOLDENTICKET_BROWSER_EVIDENCE || path.join(root, 'docs/evidence/companion-browser-2026-09-12');
const fixtures = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
fs.mkdirSync(output, { recursive: true });
const mime = { '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.webmanifest':'application/manifest+json', '.svg':'image/svg+xml', '.png':'image/png' };
const results = [];
let fixture = fixtures.setup, paired = false, pending = false, generation = 1, requests = [], offline = false, delayedReveal = null;
const send = (response, body, status=200) => { response.writeHead(status, {'Content-Type':'application/json', 'Cache-Control':'no-store'}); response.end(JSON.stringify(body)); };
const server = http.createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (url.pathname.startsWith('/api/')) {
    if (offline) { request.socket.destroy(); return; }
    let body = ''; for await(const chunk of request) body += chunk;
    body = body ? JSON.parse(body) : {};
    requests.push({url:url.pathname, body});
    if(url.pathname === '/api/session') return send(response, paired ? { paired, csrf:'test-csrf', handoffGeneration:generation, controllerGeneration:1, apiVersion:'1', assetsVersion:'2', snapshot:fixture.snapshot } : {paired,pending});
    if(url.pathname === '/api/pair') { pending=true; return send(response, {pending:true,identity:'2468'}); }
    if(url.pathname === '/api/hide') { generation++; return send(response,{hidden:true}); }
    if(url.pathname === '/api/reveal') {
      const reply = {grant:'test-private-grant', expiresAt:new Date(Date.now()+30000).toISOString(), handoffGeneration:++generation, data:fixture.data};
      if(delayedReveal) { delayedReveal.response=response; delayedReveal.reply=reply; return; }
      return send(response, reply);
    }
    if(url.pathname === '/api/command') {
      if(body.command.kind === 'keepTickets') fixture = fixtures.secondHumanSetup;
      if(body.command.kind === 'drawTrain') fixture = fixture === fixtures.turnStart ? fixtures.secondDraw : fixtures.nextHuman;
      if(body.command.kind === 'drawTickets') fixture = fixtures.ticketOffer;
      if(body.command.kind === 'planClaim') fixture = fixtures.physicalPlacement;
      generation++;
      return send(response,{accepted:true,duplicate:false,stateVersion:fixture.snapshot.game.stateVersion,message:'Choice saved on the laptop.'});
    }
    return send(response,{},404);
  }
  const asset = url.pathname === '/companion/' ? 'index.html' : url.pathname.startsWith('/companion/') ? url.pathname.slice('/companion/'.length) : '';
  if(!['index.html','app.js','app.css','sw.js','manifest.webmanifest','icon.svg','icon-192.png','icon-512.png'].includes(asset)) { response.writeHead(404); response.end(); return; }
  response.writeHead(200, {'Content-Type':mime[path.extname(asset)],'Cache-Control':'no-store','Service-Worker-Allowed':'/companion/',
    'Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; worker-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"});
  response.end(fs.readFileSync(path.join(shell,asset)));
});
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
async function main() {
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const origin='http://127.0.0.1:'+server.address().port;
  const executable=process.env.GOLDENTICKET_BROWSER_EXECUTABLE ||
    ['C:/Program Files/Google/Chrome/Application/chrome.exe','C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',chromium.executablePath()].find(file=>fs.existsSync(file));
  const browser=await chromium.launch({headless:true,executablePath:executable});
  const browserVersion=browser.version();
  try {
    for(const viewport of [{name:'pixel',width:448,height:900},{name:'small-phone',width:320,height:740},{name:'tablet',width:768,height:1024}]) {
      fixture=fixtures.setup; paired=false; pending=false; generation=1; requests=[]; offline=false; delayedReveal=null;
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
      // An OS Internet probe can fail even while local Wi-Fi and HTTPS remain available.
      await context.addInitScript(()=>Object.defineProperty(navigator,'onLine',{configurable:true,value:false}));
      const page=await context.newPage(); const errors=[]; page.on('pageerror',error=>errors.push(error.message));
      await record(viewport.name+': offline shell and browser-context pairing',async()=>{
        await page.goto(origin+'/companion/');
        await page.getByText('Offline app shell ready.',{exact:false}).waitFor();
        assert.equal(await page.evaluate(()=>isSecureContext),true);
        await noOverflow(page); await screenshot(page,viewport.name+'-connect');
        await page.getByRole('button',{name:'Continue in this browser'}).click();
        await page.getByLabel('Code shown on the laptop').fill('123456');
        await page.getByRole('button',{name:'Request connection'}).click();
        await page.getByText('Pairing identity 2468.',{exact:false}).waitFor();
        assert.equal(requests.filter(r=>r.url==='/api/pair').length,1);
        paired=true; pending=false;
        await page.locator('#curtain').waitFor({state:'visible'}); await noOverflow(page);
        await screenshot(page,viewport.name+'-curtain');
      });
      await record(viewport.name+': human opening tickets and pass-and-hide',async()=>{
        await reveal(page); await noOverflow(page);
        const choices=page.locator('#private input[type=checkbox]'); assert.equal(await choices.count(),3);
        assert.equal(await page.getByRole('button',{name:'Keep selected tickets'}).isDisabled(),true);
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
      await record(viewport.name+': Hide, focus loss and stale reveal cover private DOM',async()=>{
        await page.locator('#hide').click(); await waitCovered(page); await reveal(page);
        await page.evaluate(()=>window.dispatchEvent(new Event('blur'))); await waitCovered(page);
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
      await record(viewport.name+': disconnected cover and cached reconnect shell',async()=>{
        await page.waitForResponse(r=>r.url().endsWith('/api/session')); await reveal(page);
        offline=true;
        await page.getByText('Laptop connection unavailable',{exact:true}).waitFor({timeout:10000}); await waitCovered(page);
        await screenshot(page,viewport.name+'-disconnected');
        await context.setOffline(true); await page.reload();
        await page.getByRole('heading',{name:'Your journey starts here'}).waitFor();
        assert.equal(await page.locator('#private').textContent(),'');
        const caches=await page.evaluate(async()=>{
          const keys=await window.caches.keys(); const entries=[];
          for(const key of keys) for(const req of await (await window.caches.open(key)).keys()) entries.push(new URL(req.url).pathname);
          return entries;
        });
        assert.equal(caches.some(url=>url.startsWith('/api/')),false); assert.equal(caches.length,7);
        assert.equal(await page.evaluate(()=>localStorage.length+sessionStorage.length),0);
        assert.deepEqual([...outsideLaptop],[]);
        assert.deepEqual(errors,[]);
      });
      await context.close();
    }
    fs.writeFileSync(path.join(output,'browser-ui-results.json'),JSON.stringify({browser:'Chromium '+browserVersion,fixtureTransport:'HTTP loopback secure context; synthetic .NET bridge payloads; no certificate bypass',internetIsolation:'Page requests outside the laptop fixture origin are blocked and recorded; service-worker requests are also observed; navigator.onLine is false. Browser/OS background traffic is outside this harness.',viewports:['448×900','320×740','768×1024'],screenshots:'Synthetic player data only',results},null,2));
    console.log(`${results.length} browser UI scenarios passed.`);
  } finally { await browser.close(); await new Promise(resolve=>server.close(resolve)); }
}
main().catch(error=>{console.error(error);process.exitCode=1; server.close();});
