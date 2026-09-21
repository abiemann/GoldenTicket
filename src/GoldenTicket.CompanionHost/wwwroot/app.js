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
  let busy = false, polling = false, lastHeartbeat = 0;
  let privateActionsTarget = null, ticketOfferControls = null, renderedBoardInteraction = null, detectedPayment = null;
  let pendingClaimInstruction = null;
  let connectionGeneration = 0;
  const maxResultBytes = 16 * 1024 * 1024;
  let resultKey = null, resultGeneration = 0, resultUrl = null, resultAbort = null;
  const trainCount = count => `${count} train${count === 1 ? "" : "s"}`;

  function notice(message) { byId("notice").textContent = message; }
  function clearPrivate() {
    revealGeneration++;
    privateData = null; grant = null;
    privateActionsTarget = null; ticketOfferControls = null; renderedBoardInteraction = null; detectedPayment = null;
    pendingClaimInstruction = null;
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
    const timeout = setTimeout(() => abort.abort(), path === "/api/command" ? 8000 : 5000);
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
        if (paired) { clearPrivate(); notice("This controller was revoked or replaced. Join again using the code shown on the laptop."); }
        clearResultImage(); snapshot = null;
        paired = false; csrf = null; handoffGeneration = -1; pending = result.pending;
        byId("connect").hidden = false; byId("curtain").hidden = true; byId("public").hidden = true;
        byId("connection").textContent = pending ? "Waiting for the laptop" : "Connected to your game";
        updatePairForm(); return;
      }
      if (result.apiVersion !== "1" || result.assetsVersion !== "8") { disconnect("The companion needs an update. Reload from the laptop before playing."); return; }
      if (currentIdentity(snapshot) !== currentIdentity(result.snapshot) || (grant && result.handoffGeneration > handoffGeneration)) clearPrivate();
      paired = true; pending = false; csrf = result.csrf; snapshot = result.snapshot;
      updatePairForm();
      handoffGeneration = Math.max(handoffGeneration, result.handoffGeneration); lastHeartbeat = Date.now();
      byId("connect").hidden = true; byId("curtain").hidden = privateData !== null;
      byId("connection").textContent = "Synchronized with laptop · Private LAN";
      renderPublic(); syncResultImage(); syncBoardActions();
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
    const guidance = snapshot.guidance?.instruction ? snapshot.guidance : null;
    byId("handoff").textContent = guidance?.title || snapshot.message;
    byId("curtain-detail").textContent = guidance?.instruction || (snapshot.canControl ? "Keep this device private. Reveal only when it is your turn." : "Follow the public instructions on the laptop.");
    if (pendingClaimInstruction) pendingClaimInstruction.textContent = guidance?.instruction || "Follow the placement instructions on the laptop.";
    byId("reveal").disabled = !snapshot.canControl || busy || !lastHeartbeat;
    byId("public").hidden = !snapshot.game;
    const scores = byId("scoreboard"); scores.replaceChildren();
    if (!snapshot.game) return;
    for (const seat of snapshot.game.seats) {
      const row = element("div", undefined, `score-row${seat.seatId === snapshot.game.activeSeatId ? " active" : ""}`);
      row.append(element("span", `${seat.symbol} ${seat.displayName} · ${seat.color}`), element("span", `${seat.routeScore} points · ${trainCount(seat.trainsRemaining)}`, "score-detail")); scores.append(row);
    }
    const claim = snapshot.game.pendingClaim;
    byId("public-instruction").textContent = guidance?.instruction || (claim ? `${claim.awaitingRestore ? "Restore" : "Place"} the ${claim.trainCount === 1 ? "train" : "trains"} as shown on the laptop. Only the laptop can verify the physical board.` : `Turn ${snapshot.game.turnNumber} · ${snapshot.game.turnPhase.replace(/([a-z])([A-Z])/g, "$1 $2")}`);
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
      byId("curtain").hidden = true; renderPrivate(); notice("");
    } catch (error) { clearPrivate(); notice(error.message); }
    finally { busy = false; if (snapshot) byId("reveal").disabled = !snapshot.canControl || !lastHeartbeat; }
  }
  async function submit(kind, details = {}) {
    if (busy || !privateData || !grant || document.hidden || Date.now() - lastHeartbeat >= 6000) { hide(); return; }
    const board = snapshot?.boardInteraction;
    if ((kind === "drawTrain" || kind === "drawTickets" || kind === "keepTickets") && board?.cardActionsBlocked) {
      notice(board.message || "Finish the train placement before drawing cards."); return;
    }
    if (kind === "planClaim" && board?.useCameraClaims) { notice("Place your trains on the board to choose their payment."); return; }
    if (kind === "payDetectedRoute") {
      const route = board?.detectedRoute;
      const payments = privateData.actions.claims?.find(claim => claim.routeId === route?.routeId)?.payments || [];
      if (!board?.useCameraClaims || !route?.ready || details.detectedClaimId !== route.proposalId || details.routeId !== route.routeId || !payments.some(payment => paymentKey(payment) === paymentKey(details.payment))) {
        notice("The detected route changed. Check its current payment choices."); return;
      }
    }
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
    privateActionsTarget = null; ticketOfferControls = null; detectedPayment = null;
    pendingClaimInstruction = null;
    const own = privateData.view.public.seats.find(s => s.seatId === privateData.view.seatId);
    target.append(element("p", "ONLY FOR YOU", "eyebrow"), element("h2", `${own.displayName}'s cards`), element("p", "Use Hide before passing the device.", "fine-print"));
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
    else if (privateData.actions.mustResolvePendingClaim) {
      pendingClaimInstruction = element("h3", snapshot.guidance?.instruction || "Follow the placement instructions on the laptop.");
      target.append(pendingClaimInstruction);
    }
    else {
      privateActionsTarget = element("div", undefined, "private-actions");
      target.append(privateActionsTarget); renderActions(privateActionsTarget);
    }
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
    const boardWarning = element("p", "", "detected-route-status"); boardWarning.setAttribute("role", "status");
    const returns = () => { const values = offered.filter(t => !selected.has(t.id)); return reversed ? values.reverse() : values; };
    function update() {
      const board = snapshot.boardInteraction;
      kept.disabled = selected.size < privateData.minimumKeep || Boolean(board?.cardActionsBlocked);
      boardWarning.hidden = !board?.cardActionsBlocked;
      boardWarning.textContent = board?.cardActionsBlocked ? board.message || "Finish the train placement before keeping tickets." : "";
      returned.textContent = `Return order: ${returns().map(t => t.label).join("; ") || "keep all"}`;
    }
    for (const ticket of offered) {
      const label = element("label", undefined, "ticket ticket-choice");
      const check = document.createElement("input"); check.type = "checkbox";
      check.addEventListener("change", () => { if (!privateData) return; check.checked ? selected.add(ticket.id) : selected.delete(ticket.id); update(); });
      label.append(check, element("span", `${ticket.label} · ${ticket.points} points`)); list.append(label);
    }
    const actions = element("div", undefined, "actions"); actions.append(kept, button("Reverse return order", () => { reversed = !reversed; update(); }, "secondary"));
    target.append(list, returned, boardWarning, actions);
    ticketOfferControls = { update }; renderedBoardInteraction = JSON.stringify(snapshot.boardInteraction || null); update();
  }
  function renderActions(target) {
    target.replaceChildren();
    const actions = privateData.actions;
    const board = snapshot.boardInteraction;
    renderedBoardInteraction = JSON.stringify(board || null);
    if (board?.useCameraClaims) renderDetectedRoute(target, board);
    else detectedPayment = null;
    if (actions.canDrawBlindTrainCard || actions.drawableFaceUpSlots.length || actions.canRequestTicketOffer) renderDrawPicker(target, actions, Boolean(board?.cardActionsBlocked));
    if (!board?.useCameraClaims && actions.claims.length) {
      target.append(element("h3", "Claim a route"), element("p", "Choose the route and exact payment. The laptop will then ask for physical train placement."));
      const route = document.createElement("select"); route.setAttribute("aria-label", "Route to claim");
      const payment = document.createElement("select"); payment.setAttribute("aria-label", "Cards to spend");
      const review = element("p", "", "badge"); review.setAttribute("aria-live", "polite");
      for (const claim of actions.claims) { const definition = snapshot.routes.find(r => r.id === claim.routeId); const option = element("option", `${definition?.label || claim.routeId} · ${trainCount(claim.length)}`); option.value = claim.routeId; route.append(option); }
      let choices = [];
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
  function syncBoardActions() {
    const boardKey = JSON.stringify(snapshot.boardInteraction || null);
    if (!privateData || boardKey === renderedBoardInteraction) return;
    if (ticketOfferControls) { ticketOfferControls.update(); renderedBoardInteraction = boardKey; }
    else if (privateActionsTarget) renderActions(privateActionsTarget);
  }
  function paymentKey(choice) { return choice ? `${choice.color || ""}:${choice.colorCards}:${choice.locomotives}` : ""; }
  function paymentLabel(choice) {
    return `${choice.colorCards ? `${choice.colorCards} ${choice.color}` : ""}${choice.colorCards && choice.locomotives ? " + " : ""}${choice.locomotives ? `${choice.locomotives} ${choice.locomotives === 1 ? "Locomotive" : "Locomotives"}` : ""}`;
  }
  function renderDetectedRoute(target, board) {
    const panel = element("section", undefined, "detected-route");
    panel.setAttribute("aria-label", "Detected route payment");
    const route = board.detectedRoute;
    if (!route) {
      detectedPayment = null;
      panel.append(element("h3", "Claim a route"), element("p", board.message || "Place your trains on the board. Your payment choices will appear here."));
      target.append(panel); return;
    }
    const choices = privateData.actions.claims.find(claim => claim.routeId === route.routeId)?.payments || [];
    if (detectedPayment?.proposalId !== route.proposalId || detectedPayment?.routeId !== route.routeId || !choices.some(choice => paymentKey(choice) === detectedPayment.key)) detectedPayment = null;
    panel.append(element("p", "ROUTE DETECTED", "eyebrow"), element("h3", route.label), element("p", trainCount(route.length), "detected-route-length"));
    const status = element("p", !route.ready ? board.message || "Waiting for the camera to confirm the trains." : choices.length ? "Choose the cards to spend." : "You don't have a matching payment for this route.", "detected-route-status");
    status.setAttribute("role", "status"); panel.append(status);
    const payments = element("div", undefined, "detected-payments"); payments.setAttribute("role", "group"); payments.setAttribute("aria-label", "Cards to spend on the detected route");
    const pay = button("Pay", () => {
      const payment = choices.find(choice => paymentKey(choice) === detectedPayment?.key);
      if (payment) submit("payDetectedRoute", { routeId: route.routeId, payment, detectedClaimId: route.proposalId });
    }, "detected-pay");
    pay.setAttribute("aria-label", "Pay for detected route");
    const options = [];
    function updateSelection() {
      for (const {option, key} of options) option.setAttribute("aria-pressed", String(key === detectedPayment?.key));
      pay.disabled = !route.ready || !detectedPayment;
    }
    for (const choice of choices) {
      const key = paymentKey(choice);
      const option = button(paymentLabel(choice), () => {
        const current = snapshot.boardInteraction?.detectedRoute;
        if (!current?.ready || current.proposalId !== route.proposalId || current.routeId !== route.routeId) return;
        detectedPayment = { proposalId: route.proposalId, routeId: route.routeId, key }; updateSelection();
      }, "payment-choice");
      option.setAttribute("aria-label", `Pay with ${paymentLabel(choice)}`); option.disabled = !route.ready;
      options.push({option, key}); payments.append(option);
    }
    updateSelection(); panel.append(payments, pay); target.append(panel);
  }
  function renderDrawPicker(target, actions, blocked = false) {
    const area = element("div", undefined, "draw-area");
    const picker = element("div", undefined, "draw-picker");
    const piles = element("section", undefined, "draw-panel draw-piles");
    const pileTitle = element("h3", "DRAW PILES"); pileTitle.id = "draw-piles-title";
    piles.setAttribute("aria-labelledby", pileTitle.id);
    const pileButtons = element("div", undefined, "draw-pile-list");
    for (const pile of [
      { letter: "T", label: "TRAIN", name: "Draw a blind card", className: "train-pile", enabled: actions.canDrawBlindTrainCard && !blocked, help: "train-draw-help", draw: () => submit("drawTrain", { slot: null }) },
      { letter: "D", label: "DESTINATIONS", name: "Draw destination tickets", className: "destination-pile", enabled: actions.canRequestTicketOffer && !blocked, help: "destination-draw-help", draw: () => submit("drawTickets") }
    ]) {
      const option = button(undefined, pile.draw, `draw-pile ${pile.className}`);
      option.setAttribute("aria-label", pile.name);
      if (pile.enabled) option.setAttribute("aria-describedby", pile.help);
      option.disabled = !pile.enabled;
      option.append(element("span", pile.letter, "pile-back"), element("span", pile.label, "pile-label"));
      pileButtons.append(option);
    }
    piles.append(pileTitle, pileButtons);
    const faceUp = element("section", undefined, "draw-panel face-up-panel");
    const marketTitle = element("h3", "FACE-UP TRAIN CARDS"); marketTitle.id = "face-up-title";
    faceUp.setAttribute("aria-labelledby", marketTitle.id);
    const market = element("div", undefined, "draw-market");
    for (const [slot, kind] of privateData.view.public.faceUp.entries()) {
      const option = button(undefined, () => submit("drawTrain", { slot }), "market-card");
      option.dataset.color = kind; option.disabled = blocked || !actions.drawableFaceUpSlots.includes(slot);
      option.setAttribute("aria-label", `${kind} · slot ${slot + 1}`);
      option.setAttribute("aria-describedby", "train-draw-help");
      option.append(element("span", kind, "market-card-label")); market.append(option);
    }
    if (!market.children.length) market.append(element("p", "No face-up cards available.", "empty"));
    faceUp.append(marketTitle, market); picker.append(piles, faceUp);
    const help = element("div", undefined, "draw-help");
    const trainHelp = element("p", privateData.view.public.turnPhase === "AwaitingSecondTrainCard"
      ? "Choose your second card. A visible locomotive cannot be the second draw."
      : "Take two cards, one at a time. A visible locomotive uses the whole turn.");
    trainHelp.id = "train-draw-help"; help.append(trainHelp);
    if (actions.canRequestTicketOffer) {
      const destinationHelp = element("p", "Draw destination tickets. You must keep at least one.");
      destinationHelp.id = "destination-draw-help"; help.append(destinationHelp);
    }
    area.append(picker, help); target.append(area);
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
  document.addEventListener("keydown", event => { if (event.key === "Escape") hide(); });
  setInterval(() => {
    if (lastHeartbeat && Date.now() - lastHeartbeat >= 6000) disconnect();
  }, 500);
  setInterval(poll, 2000);
  clearPrivate(); updatePairForm(); poll();
})();
