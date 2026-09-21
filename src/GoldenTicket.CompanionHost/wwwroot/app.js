"use strict";
(() => {
  const byId = id => document.getElementById(id);
  function randomId() {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  }
  const tab = randomId();
  let csrf = null, snapshot = null, privateData = null, grant = null;
  let revealGeneration = 0, handoffGeneration = -1, paired = false, pending = false;
  let busy = false, polling = false, lastHeartbeat = 0, revealDeadline = 0, lastInteraction = 0;
  let activityRequest = null, lastRenewalAt = 0, renewedInteraction = 0;
  let connectionGeneration = 0;
  const maxResultBytes = 16 * 1024 * 1024;
  let resultKey = null, resultGeneration = 0, resultUrl = null, resultAbort = null;
  const trainCount = count => `${count} train${count === 1 ? "" : "s"}`;

  function notice(message) { byId("notice").textContent = message; }
  function clearPrivate() {
    revealGeneration++;
    privateData = null; grant = null; revealDeadline = 0;
    activityRequest = null;
    byId("private").replaceChildren(); byId("private").hidden = true;
    byId("curtain").hidden = !paired || resultKey !== null;
  }
  function hide(notify = true) {
    clearPrivate();
    if (notify && csrf && paired) api("/api/hide", {}).catch(() => {});
  }
  function disconnect(message) {
    clearResultImage(); clearPrivate(); lastHeartbeat = 0;
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
  function recordInteraction() {
    if (!grant || !privateData || document.hidden) return;
    const now = Date.now();
    if (now >= revealDeadline || now - lastInteraction >= 30000 || now - lastHeartbeat >= 6000) { hide(); return; }
    lastInteraction = now;
    renewActivity();
  }
  async function renewActivity() {
    const now = Date.now();
    if (!grant || !privateData || busy || document.hidden || activityRequest || lastInteraction <= renewedInteraction || now - lastRenewalAt < 5000) return;
    if (now >= revealDeadline || now - lastInteraction >= 30000 || now - lastHeartbeat >= 6000) { hide(); return; }
    // Renew authorization without re-rendering the hand or losing checked destinations.
    // Coalesce touch/key events so a gesture cannot flood the laptop with requests.
    const request = { generation: revealGeneration, identity: currentIdentity(snapshot) };
    activityRequest = request; lastRenewalAt = now; renewedInteraction = lastInteraction;
    try {
      const result = await api("/api/activity", { seat: privateData.view.seatId, sessionId: privateData.view.public.sessionId, version: privateData.view.public.stateVersion, grant, handoffGeneration });
      if (activityRequest !== request || request.generation !== revealGeneration || document.hidden || request.identity !== currentIdentity(snapshot)) return;
      if (result.handoffGeneration !== handoffGeneration || Date.now() - lastHeartbeat >= 6000 || Date.now() - lastInteraction >= 30000) { hide(); return; }
      const deadline = Date.parse(result.expiresAt);
      if (!Number.isFinite(deadline) || deadline <= Date.now()) { hide(); return; }
      revealDeadline = deadline;
    } catch (error) {
      if (activityRequest === request) { hide(); notice(error.message); }
    } finally { if (activityRequest === request) activityRequest = null; }
  }
  async function poll() {
    if (polling || document.hidden) return;
    polling = true;
    const generation = connectionGeneration;
    try {
      const result = await api("/api/session");
      if (generation !== connectionGeneration || document.hidden) return;
      if (!result.paired) {
        if (paired) { clearPrivate(); notice("This controller was revoked or replaced. Request a fresh code on the laptop."); }
        clearResultImage(); snapshot = null;
        paired = false; csrf = null; handoffGeneration = -1; pending = result.pending;
        byId("connect").hidden = false; byId("curtain").hidden = true; byId("public").hidden = true;
        byId("connection").textContent = pending ? "Waiting for the laptop" : "Connected to your game";
        updatePairForm(); return;
      }
      if (result.apiVersion !== "1" || result.assetsVersion !== "5") { disconnect("The companion needs an update. Reload from the laptop before playing."); return; }
      if (currentIdentity(snapshot) !== currentIdentity(result.snapshot) || (grant && result.handoffGeneration > handoffGeneration)) clearPrivate();
      paired = true; pending = false; csrf = result.csrf; snapshot = result.snapshot;
      updatePairForm();
      handoffGeneration = Math.max(handoffGeneration, result.handoffGeneration); lastHeartbeat = Date.now();
      byId("connect").hidden = true; byId("curtain").hidden = privateData !== null;
      byId("connection").textContent = "Synchronized with laptop · Private LAN";
      renderPublic(); syncResultImage();
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
  function resultMetadata() {
    const value = snapshot?.resultImage;
    return paired && snapshot?.game && value && /^[a-zA-Z0-9_-]{1,128}$/.test(value.id) ? value : null;
  }
  function clearResultImage() {
    resultGeneration++; resultAbort?.abort(); resultAbort = null;
    resultKey = null;
    byId("result-preview").removeAttribute("src");
    byId("result-save").removeAttribute("href");
    if (resultUrl) URL.revokeObjectURL(resultUrl);
    resultUrl = null;
    for (const id of ["result", "result-preview", "result-save", "result-retry", "result-help"]) byId(id).hidden = true;
    byId("result-status").textContent = "";
  }
  function syncResultImage() {
    const metadata = resultMetadata();
    if (!metadata) { if (resultKey) clearResultImage(); return; }
    const key = `${snapshot.game.sessionId}:${metadata.id}`;
    byId("curtain").hidden = true; byId("public").hidden = true;
    if (key === resultKey) return;
    clearPrivate(); clearResultImage(); byId("curtain").hidden = true;
    resultKey = key; byId("result").hidden = false;
    loadResultImage();
  }
  async function loadResultImage() {
    const metadata = resultMetadata();
    if (!metadata || resultAbort || !resultKey) return;
    const generation = resultGeneration;
    const abort = new AbortController(); resultAbort = abort;
    const timeout = setTimeout(() => abort.abort(), 10000);
    byId("result-status").textContent = "Receiving the image…"; byId("result-retry").hidden = true;
    try {
      const response = await fetch(`/api/result-image/${encodeURIComponent(metadata.id)}`, {
        headers: { "X-GoldenTicket-Tab": tab }, credentials: "same-origin", cache: "no-store", signal: abort.signal
      });
      if (!response.ok) throw new Error("The image is no longer available. Try again, or send it again from the laptop.");
      if (response.headers.get("Content-Type")?.split(";")[0].trim().toLowerCase() !== "image/png") throw new Error("The laptop did not send a PNG image. Send the standings again.");
      if (Number(response.headers.get("Content-Length")) > maxResultBytes) throw new Error("This image is too large. Send the standings again from the laptop.");
      const reader = response.body.getReader(), chunks = [];
      let size = 0;
      try {
        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          size += value.byteLength;
          if (size > maxResultBytes) throw new Error("This image is too large. Send the standings again from the laptop.");
          chunks.push(value);
        }
      } catch (error) { await reader.cancel().catch(() => {}); throw error; }
      finally { reader.releaseLock(); }
      const blob = new Blob(chunks, { type: "image/png" });
      const signature = new Uint8Array(await blob.slice(0, 8).arrayBuffer());
      if (signature.length !== 8 || signature.some((byte, index) => byte !== [137, 80, 78, 71, 13, 10, 26, 10][index])) throw new Error("The image could not be read. Try sending it again from the laptop.");
      if (generation !== resultGeneration || abort.signal.aborted || !paired) return;
      const fileName = /^[a-zA-Z0-9_-]+\.png$/.test(metadata.fileName) ? metadata.fileName : "golden-ticket-final-standings.png";
      resultUrl = URL.createObjectURL(blob);
      byId("result-preview").src = resultUrl; byId("result-preview").hidden = false;
      byId("result-save").href = resultUrl; byId("result-save").download = fileName; byId("result-save").hidden = false;
      byId("result-help").hidden = false;
      byId("result-status").textContent = "Your results are ready.";
    } catch (error) {
      if (generation !== resultGeneration) return;
      byId("result-status").textContent = error.name === "AbortError" ? "The image took too long to arrive. Check the laptop connection and try again." : error instanceof TypeError ? "Could not receive the image. Check the laptop connection and try again." : error.message;
      byId("result-retry").hidden = false;
    } finally { clearTimeout(timeout); abort.abort(); if (generation === resultGeneration) resultAbort = null; }
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
      row.append(element("span", `${seat.symbol} ${seat.displayName} · ${seat.color}`), element("span", `${seat.routeScore} points · ${trainCount(seat.trainsRemaining)}`, "score-detail")); scores.append(row);
    }
    const claim = snapshot.game.pendingClaim;
    byId("public-instruction").textContent = claim ? `${claim.awaitingRestore ? "Restore" : "Place"} the ${claim.trainCount === 1 ? "train" : "trains"} as shown on the laptop. Only the laptop can verify the physical board.` : `Turn ${snapshot.game.turnNumber} · ${snapshot.game.turnPhase.replace(/([a-z])([A-Z])/g, "$1 $2")}`;
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
      lastRenewalAt = lastInteraction; renewedInteraction = lastInteraction;
      byId("curtain").hidden = true; renderPrivate(); notice("");
    } catch (error) { clearPrivate(); notice(error.message); }
    finally { busy = false; if (snapshot) byId("reveal").disabled = !snapshot.canControl || !lastHeartbeat; }
  }
  async function submit(kind, details = {}) {
    if (busy || !privateData || !grant || document.hidden || Date.now() >= revealDeadline || Date.now() - lastHeartbeat >= 6000) { hide(); return; }
    busy = true;
    const payload = { seat: privateData.view.seatId, grant, command: { commandId: randomId(), sessionId: privateData.view.public.sessionId, expectedStateVersion: privateData.view.public.stateVersion, kind, ...details } };
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
    target.append(element("p", "ONLY FOR YOU", "eyebrow"), element("h2", `${own.displayName}'s cards`), element("p", "Cards hide after 30 seconds without activity. Use Hide before passing the device.", "fine-print"));
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
      check.addEventListener("change", () => { recordInteraction(); if (!privateData) return; check.checked ? selected.add(ticket.id) : selected.delete(ticket.id); update(); });
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
      for (const claim of actions.claims) { const definition = snapshot.routes.find(r => r.id === claim.routeId); const option = element("option", `${definition?.label || claim.routeId} · ${trainCount(claim.length)}`); option.value = claim.routeId; route.append(option); }
      let choices = [];
      const paymentLabel = choice => `${choice.colorCards ? `${choice.colorCards} ${choice.color}` : ""}${choice.colorCards && choice.locomotives ? " + " : ""}${choice.locomotives ? `${choice.locomotives} locomotive(s)` : ""}`;
      function reviewSelection() {
        const definition = snapshot.routes.find(r => r.id === route.value);
        const choice = choices[Number(payment.value)];
        review.textContent = definition && choice ? `${definition.label} · ${trainCount(definition.length)} · Pay ${paymentLabel(choice)}.` : "Choose a route and payment.";
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
    byId("pair-form").hidden = pending || paired;
    byId("hide").hidden = !paired;
    byId("pair-button").disabled = busy;
  }
  byId("pair-form").addEventListener("submit", async event => {
    event.preventDefault(); if (busy || paired || pending) return;
    busy = true; connectionGeneration++; updatePairForm();
    try {
      const result = await api("/api/pair", { code: byId("pair-code").value.trim(), tab, label: byId("device-label").value.trim() });
      byId("pair-code").value = ""; pending = true;
      byId("pair-status").textContent = `Approve this phone on the laptop to join. Device number: ${result.identity}.`;
    } catch (error) { notice(error.message); }
    finally { busy = false; updatePairForm(); await poll(); }
  });
  byId("hide").addEventListener("click", () => hide());
  byId("reveal").addEventListener("click", reveal);
  byId("result-retry").addEventListener("click", loadResultImage);
  document.addEventListener("visibilitychange", () => { if (document.hidden) { connectionGeneration++; hide(); } else { clearPrivate(); poll(); } });
  window.addEventListener("pagehide", () => { connectionGeneration++; hide(); clearResultImage(); });
  window.addEventListener("pageshow", () => { clearPrivate(); poll(); });
  window.addEventListener("offline", () => disconnect("Reconnect to the laptop before continuing."));
  // Tapping outside a control, native pickers and scrolling can blur/cancel a
  // pointer without leaving the page. Only actual backgrounding covers it.
  document.addEventListener("pointerdown", recordInteraction);
  document.addEventListener("keydown", event => { if (event.key === "Escape") hide(); else recordInteraction(); });
  setInterval(() => {
    if (grant && (Date.now() >= revealDeadline || Date.now() - lastInteraction >= 30000)) hide();
    if (lastHeartbeat && Date.now() - lastHeartbeat >= 6000) disconnect();
    renewActivity();
  }, 500);
  setInterval(poll, 2000);
  clearPrivate(); updatePairForm(); poll();
})();
