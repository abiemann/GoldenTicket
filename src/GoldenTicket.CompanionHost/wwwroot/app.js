"use strict";
(() => {
  const byId = id => document.getElementById(id);
  const tab = crypto.randomUUID().replaceAll("-", "");
  const shellPaths = ["/companion/", "/companion/app.js", "/companion/app.css", "/companion/manifest.webmanifest", "/companion/icon.svg", "/companion/icon-192.png", "/companion/icon-512.png"];
  let csrf = null, snapshot = null, privateData = null, grant = null;
  let revealGeneration = 0, handoffGeneration = -1, paired = false, pending = false;
  let shellReady = false, browserMode = false, installPrompt = null;
  let busy = false, polling = false, lastHeartbeat = 0, revealDeadline = 0, lastInteraction = 0;
  let connectionGeneration = 0;
  const standalone = () => matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;

  function notice(message) { byId("notice").textContent = message; }
  function clearPrivate() {
    revealGeneration++;
    privateData = null; grant = null; revealDeadline = 0;
    byId("private").replaceChildren(); byId("private").hidden = true;
    byId("curtain").hidden = !paired;
  }
  function hide(notify = true) {
    clearPrivate();
    if (notify && csrf && paired) api("/api/hide", {}).catch(() => {});
  }
  function disconnect(message) {
    clearPrivate(); lastHeartbeat = 0;
    byId("connection").textContent = "Laptop connection unavailable";
    byId("curtain-detail").textContent = "Return to the same private network. Your game is saved on the laptop. No actions are queued offline.";
    byId("reveal").disabled = true;
    if (message) notice(message);
  }
  async function api(path, body) {
    const abort = new AbortController();
    const timeout = setTimeout(() => abort.abort(), 5000);
    try {
      const headers = { "X-GoldenTicket-Tab": tab };
      if (body !== undefined) { headers["Content-Type"] = "application/json"; if (csrf) headers["X-GoldenTicket-CSRF"] = csrf; }
      const response = await fetch(path, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: "same-origin", cache: "no-store", signal: abort.signal });
      if (!response.ok) throw new Error(response.status === 429 ? "The laptop is busy. Wait a moment and try again." : "The request was refused or the turn changed. Hide and reveal again; check the laptop if needed.");
      return await response.json();
    } catch (error) {
      if (error.name === "AbortError") throw new Error("The laptop did not respond in time. Check it before continuing.");
      if (error instanceof TypeError) throw new Error("Cannot reach the laptop. Check that it is running and both devices use the same private network.");
      throw error;
    } finally { clearTimeout(timeout); }
  }
  function currentIdentity(value) { return value?.game ? `${value.game.sessionId}:${value.game.stateVersion}:${value.revealSeatId}:${value.canControl}` : "none"; }
  async function poll() {
    if (polling || document.hidden) return;
    polling = true;
    const generation = connectionGeneration;
    try {
      const result = await api("/api/session");
      if (generation !== connectionGeneration || document.hidden) return;
      if (!result.paired) {
        if (paired) { clearPrivate(); notice("This controller was revoked or replaced. Request a fresh code on the laptop."); }
        paired = false; csrf = null; handoffGeneration = -1; pending = result.pending;
        byId("connect").hidden = false; byId("curtain").hidden = true; byId("public").hidden = true;
        byId("connection").textContent = pending ? "Waiting for laptop approval" : "Connected securely · Pair to play";
        updatePairForm(); return;
      }
      if (result.apiVersion !== "1" || result.assetsVersion !== "2") { disconnect("The app shell needs an update. Reload from the laptop before playing."); return; }
      if (currentIdentity(snapshot) !== currentIdentity(result.snapshot) || (grant && result.handoffGeneration > handoffGeneration)) clearPrivate();
      paired = true; pending = false; csrf = result.csrf; snapshot = result.snapshot;
      handoffGeneration = Math.max(handoffGeneration, result.handoffGeneration); lastHeartbeat = Date.now();
      byId("connect").hidden = true; byId("curtain").hidden = privateData !== null;
      byId("connection").textContent = "Synchronized with laptop · Private LAN";
      renderPublic();
    } catch (error) { if (generation === connectionGeneration) disconnect(error.message); }
    finally { polling = false; }
  }
  function element(tag, text, className) {
    const node = document.createElement(tag); if (text !== undefined) node.textContent = text;
    if (className) node.className = className; return node;
  }
  function button(text, callback, className) {
    const node = element("button", text, className); node.type = "button"; node.addEventListener("click", callback); return node;
  }
  function renderPublic() {
    byId("handoff").textContent = snapshot.message;
    byId("curtain-detail").textContent = snapshot.canControl ? "Keep this device private. Reveal only when it is your turn." : "Follow the public instructions on the laptop.";
    byId("reveal").disabled = !snapshot.canControl || busy || !lastHeartbeat;
    byId("public").hidden = !snapshot.game;
    const scores = byId("scoreboard"); scores.replaceChildren();
    if (!snapshot.game) return;
    for (const seat of snapshot.game.seats) {
      const row = element("div", undefined, `score-row${seat.seatId === snapshot.game.activeSeatId ? " active" : ""}`);
      row.append(element("span", `${seat.symbol} ${seat.displayName} · ${seat.color}`), element("span", `${seat.routeScore} points · ${seat.trainsRemaining} trains`, "score-detail")); scores.append(row);
    }
    const claim = snapshot.game.pendingClaim;
    byId("public-instruction").textContent = claim ? `${claim.awaitingRestore ? "Restore" : "Place"} the trains as shown on the laptop. Only the laptop can verify the physical board.` : `Turn ${snapshot.game.turnNumber} · ${snapshot.game.turnPhase.replace(/([a-z])([A-Z])/g, "$1 $2")}`;
  }
  async function reveal() {
    if (busy || !paired || !snapshot?.canControl || document.hidden || !lastHeartbeat) return;
    busy = true; byId("reveal").disabled = true;
    const generation = ++revealGeneration;
    const identity = currentIdentity(snapshot);
    try {
      const result = await api("/api/reveal", { seat: snapshot.revealSeatId, sessionId: snapshot.game.sessionId, version: snapshot.game.stateVersion, handoffGeneration });
      if (generation !== revealGeneration || document.hidden || identity !== currentIdentity(snapshot) || Date.now() - lastHeartbeat >= 6000 || result.handoffGeneration < handoffGeneration) return;
      grant = result.grant; privateData = result.data; handoffGeneration = result.handoffGeneration;
      revealDeadline = Date.parse(result.expiresAt); lastInteraction = Date.now();
      byId("curtain").hidden = true; renderPrivate(); notice("");
    } catch (error) { clearPrivate(); notice(error.message); }
    finally { busy = false; if (snapshot) byId("reveal").disabled = !snapshot.canControl || !lastHeartbeat; }
  }
  async function submit(kind, details = {}) {
    if (busy || !privateData || !grant || document.hidden || Date.now() >= revealDeadline || Date.now() - lastHeartbeat >= 6000) { hide(); return; }
    busy = true;
    const payload = { seat: privateData.view.seatId, grant, command: { commandId: crypto.randomUUID().replaceAll("-", ""), sessionId: privateData.view.public.sessionId, expectedStateVersion: privateData.view.public.stateVersion, kind, ...details } };
    // Remove private DOM immediately, but do not revoke the grant authorizing this in-flight choice.
    clearPrivate(); byId("reveal").disabled = true;
    notice("Saving your choice on the laptop…");
    try {
      const result = await api("/api/command", payload);
      notice(result.message);
    } catch (error) { disconnect("The result is not confirmed on this device. Check the current turn on the laptop before choosing again."); }
    finally { busy = false; await poll(); }
  }
  function card(kind, count) {
    const node = element("div", undefined, "card"); node.dataset.color = kind;
    node.append(element("span", kind), element("strong", String(count))); return node;
  }
  function renderPrivate() {
    const target = byId("private"); target.replaceChildren();
    const own = privateData.view.public.seats.find(s => s.seatId === privateData.view.seatId);
    target.append(element("p", "ONLY FOR YOU", "eyebrow"), element("h2", `${own.displayName}'s cards`), element("p", "Private view closes after 30 seconds. Use Hide before passing the device.", "fine-print"));
    const cards = element("div", undefined, "cards");
    for (const kind of ["Pink", "White", "Blue", "Yellow", "Orange", "Black", "Red", "Green", "Locomotive"]) {
      const count = privateData.view.hand.filter(c => c.kind === kind).length;
      if (count) cards.append(card(kind, count));
    }
    if (!privateData.view.hand.length) cards.append(element("p", "Your train-card hand is empty.", "empty"));
    target.append(cards);
    if (privateData.view.reservedCards.length) target.append(element("p", `${privateData.view.reservedCards.length} cards are reserved for the pending route. Complete placement on the laptop.`, "badge"));
    target.append(element("h3", "Your destination tickets"));
    const tickets = element("div", undefined, "tickets");
    for (const ticket of privateData.heldTickets) { const item = element("div", ticket.label, "ticket"); item.append(element("span", `${ticket.points} points`)); tickets.append(item); }
    if (!privateData.heldTickets.length) tickets.append(element("p", "Choose your opening tickets below.", "empty"));
    target.append(tickets);
    if (privateData.actions.mustCommitTicketSelection) renderOffer(target);
    else if (privateData.actions.mustResolvePendingClaim) target.append(element("h3", "Follow the placement instructions on the laptop."));
    else renderActions(target);
    target.append(button("Hide my cards", () => hide(), "secondary"));
    target.hidden = false;
  }
  function renderOffer(target) {
    const offered = privateData.offeredTickets;
    const selected = new Set();
    let reversed = false;
    target.append(element("h3", "Choose destination tickets"), element("p", `Keep at least ${privateData.minimumKeep}. Kept tickets stay with you for the entire game; unfinished tickets lose their points.`));
    const list = element("div", undefined, "tickets");
    const kept = button("Keep selected tickets", () => submit("keepTickets", { keptTickets: offered.filter(t => selected.has(t.id)).map(t => t.id), returnedTickets: returns().map(t => t.id) }));
    kept.disabled = true;
    const returned = element("p", "", "fine-print");
    const returns = () => { const values = offered.filter(t => !selected.has(t.id)); return reversed ? values.reverse() : values; };
    function update() { kept.disabled = selected.size < privateData.minimumKeep; returned.textContent = `Return order: ${returns().map(t => t.label).join("; ") || "keep all"}`; }
    for (const ticket of offered) {
      const label = element("label", undefined, "ticket ticket-choice");
      const check = document.createElement("input"); check.type = "checkbox";
      check.addEventListener("change", () => { if (!privateData) return; check.checked ? selected.add(ticket.id) : selected.delete(ticket.id); update(); });
      label.append(check, element("span", `${ticket.label} · ${ticket.points} points`)); list.append(label);
    }
    const actions = element("div", undefined, "actions"); actions.append(kept, button("Reverse return order", () => { reversed = !reversed; update(); }, "secondary"));
    target.append(list, returned, actions); update();
  }
  function renderActions(target) {
    const actions = privateData.actions;
    if (actions.canDrawBlindTrainCard || actions.drawableFaceUpSlots.length) {
      target.append(element("h3", "Draw train cards"));
      if (privateData.view.public.turnPhase === "AwaitingSecondTrainCard") target.append(element("p", "Choose your second card. A visible locomotive cannot be the second draw."));
      else target.append(element("p", "Take two cards, one at a time. A visible locomotive uses the whole turn."));
      const market = element("div", undefined, "market");
      for (const slot of actions.drawableFaceUpSlots) { const kind = privateData.view.public.faceUp[slot]; const option = button(`${kind} · slot ${slot + 1}`, () => submit("drawTrain", { slot }), "card"); option.dataset.color = kind; market.append(option); }
      target.append(market);
      if (actions.canDrawBlindTrainCard) target.append(button("Draw a blind card", () => submit("drawTrain", { slot: null }), "secondary"));
    }
    if (actions.canRequestTicketOffer) { target.append(element("h3", "Find a new destination"), element("p", "Draw destination tickets. You must keep at least one."), button("Draw destination tickets", () => submit("drawTickets"), "secondary")); }
    if (actions.claims.length) {
      target.append(element("h3", "Claim a route"), element("p", "Choose the route and exact payment. The laptop will then ask for physical train placement."));
      const route = document.createElement("select"); route.setAttribute("aria-label", "Route to claim");
      const payment = document.createElement("select"); payment.setAttribute("aria-label", "Cards to spend");
      const review = element("p", "", "badge"); review.setAttribute("aria-live", "polite");
      for (const claim of actions.claims) { const definition = snapshot.routes.find(r => r.id === claim.routeId); const option = element("option", `${definition?.label || claim.routeId} · ${claim.length} trains`); option.value = claim.routeId; route.append(option); }
      let choices = [];
      const paymentLabel = choice => `${choice.colorCards ? `${choice.colorCards} ${choice.color}` : ""}${choice.colorCards && choice.locomotives ? " + " : ""}${choice.locomotives ? `${choice.locomotives} locomotive(s)` : ""}`;
      function reviewSelection() {
        const definition = snapshot.routes.find(r => r.id === route.value);
        const choice = choices[Number(payment.value)];
        review.textContent = definition && choice ? `${definition.label} · ${definition.length} trains · Pay ${paymentLabel(choice)}.` : "Choose a route and payment.";
      }
      function update() {
        const claim = actions.claims.find(c => c.routeId === route.value); choices = claim?.payments || []; payment.replaceChildren();
        for (const [index, choice] of choices.entries()) { const option = element("option", paymentLabel(choice)); option.value = String(index); payment.append(option); }
        reviewSelection();
      }
      route.addEventListener("change", update); payment.addEventListener("change", reviewSelection); update();
      target.append(route, payment, review, button("Authorize this route and payment", () => submit("planClaim", { routeId: route.value, payment: choices[Number(payment.value)] })));
    }
    target.append(element("div", "", "actions"));
  }
  function updatePairForm() {
    byId("pair-form").hidden = !shellReady || (!standalone() && !browserMode) || pending || paired;
    byId("install-help").hidden = standalone() || browserMode || paired;
    byId("pair-button").disabled = busy;
  }
  async function setupShell() {
    if (!window.isSecureContext || !("serviceWorker" in navigator)) { byId("shell-status").textContent = "Trusted HTTPS is required. Install the laptop certificate and reopen its HTTPS address. Do not bypass a browser warning."; return; }
    try {
      await navigator.serviceWorker.register("/companion/sw.js", { scope: "/companion/" });
      let timeout;
      try { await Promise.race([navigator.serviceWorker.ready, new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error("App shell activation timed out.")), 10000); })]); }
      finally { clearTimeout(timeout); }
      const cache = await caches.open("goldenticket-companion-shell-v2");
      const cached = await Promise.all(shellPaths.map(path => cache.match(path)));
      if (cached.some(value => !value)) throw new Error("Some app files have not been saved yet.");
      shellReady = true; byId("shell-status").textContent = "Offline app shell ready. Gameplay still needs the laptop on your LAN."; updatePairForm();
    } catch (error) { byId("shell-status").textContent = `${error.message} Reload while connected to the laptop to try again.`; }
  }
  byId("pair-form").addEventListener("submit", async event => {
    event.preventDefault(); if (busy || !shellReady || (!standalone() && !browserMode)) return;
    busy = true; connectionGeneration++; updatePairForm();
    try {
      const result = await api("/api/pair", { code: byId("pair-code").value.trim(), tab, label: byId("device-label").value.trim() });
      byId("pair-code").value = ""; pending = true;
      byId("pair-status").textContent = `Pairing identity ${result.identity}. Compare it with the laptop, then approve this device there.`;
    } catch (error) { notice(error.message); }
    finally { busy = false; updatePairForm(); await poll(); }
  });
  byId("hide").addEventListener("click", () => hide());
  byId("reveal").addEventListener("click", reveal);
  byId("browser-mode").addEventListener("click", () => { browserMode = true; updatePairForm(); });
  byId("install").addEventListener("click", async () => { if (installPrompt) { await installPrompt.prompt(); installPrompt = null; byId("install").hidden = true; } });
  window.addEventListener("beforeinstallprompt", event => { event.preventDefault(); installPrompt = event; byId("install").hidden = false; });
  document.addEventListener("visibilitychange", () => { if (document.hidden) { connectionGeneration++; hide(); } else { clearPrivate(); poll(); } });
  window.addEventListener("blur", () => hide());
  window.addEventListener("pagehide", () => { connectionGeneration++; hide(); });
  window.addEventListener("pageshow", () => { clearPrivate(); poll(); });
  window.addEventListener("offline", () => disconnect("Reconnect to the laptop before continuing."));
  document.addEventListener("pointercancel", () => hide());
  document.addEventListener("pointerdown", () => { lastInteraction = Date.now(); });
  document.addEventListener("keydown", event => { lastInteraction = Date.now(); if (event.key === "Escape") hide(); });
  setInterval(() => {
    if (grant && (Date.now() >= revealDeadline || Date.now() - lastInteraction >= 30000)) hide();
    if (lastHeartbeat && Date.now() - lastHeartbeat >= 6000) disconnect();
  }, 500);
  setInterval(poll, 2000);
  clearPrivate(); updatePairForm(); setupShell(); poll();
})();
