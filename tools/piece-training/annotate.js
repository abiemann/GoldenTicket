"use strict";

(() => {
  const MAX_FILE_BYTES = 48 * 1024 * 1024;
  const MAX_TOTAL_BYTES = 512 * 1024 * 1024;
  const MAX_IMAGES = 200;
  const MAX_BOXES = 1000;
  const MAX_LABEL_BYTES = 64 * 1024 * 1024;
  const kinds = new Set(["train", "player-marker"]);
  const colors = new Set(["black", "blue", "green", "red", "yellow", "unknown"]);
  const splits = new Set(["train", "validation", "test"]);
  const byId = id => document.getElementById(id);
  const canvas = byId("annotation-canvas");
  const context = canvas.getContext("2d");
  const state = { images: [], active: -1, selectedBox: -1, dirty: false, busy: false, draft: null, image: null, generation: 0 };

  function active() { return state.images[state.active] ?? null; }
  function tell(message) { byId("status").textContent = message; }
  function error(message) { byId("error").textContent = message; byId("error").hidden = !message; }
  function markDirty() {
    state.dirty = true;
    byId("save-state").textContent = "Unexported changes. Export labels before closing; there is no autosave.";
  }
  function changed() {
    const image = active();
    if (image) image.reviewed = false;
    markDirty();
    renderLists();
    renderMetadata();
    draw();
  }
  function setBusy(value) {
    state.busy = value;
    byId("image-files").disabled = value;
    byId("label-file").disabled = value;
    byId("export-labels").disabled = value || state.images.length === 0;
    byId("clear-images").disabled = value || state.images.length === 0;
    renderMetadata();
  }
  function renderMetadata() {
    const image = active();
    for (const id of ["capture-group", "dataset-split", "image-reviewed"])
      byId(id).disabled = !image || state.busy;
    byId("capture-group").value = image?.group ?? "";
    byId("dataset-split").value = image?.split ?? "train";
    byId("image-reviewed").checked = image?.reviewed ?? false;
    const box = image?.boxes[state.selectedBox];
    byId("deselect-box").disabled = !box || state.busy;
    byId("delete-box").disabled = !box || state.busy;
    byId("piece-kind").disabled = state.busy;
    byId("piece-color").disabled = state.busy;
    if (box) { byId("piece-kind").value = box.kind; byId("piece-color").value = box.color; }
    byId("photo-details").textContent = image
      ? `${image.fileName} · ${image.width} × ${image.height} original pixels. Boxes use original photo coordinates.`
      : "Boxes are exported in original photo pixels, regardless of preview size.";
    canvas.setAttribute("aria-disabled", String(!image || state.busy || !state.image));
  }
  function button(label, selected, click) {
    const element = document.createElement("button");
    element.type = "button";
    element.textContent = label;
    element.setAttribute("aria-pressed", String(selected));
    element.addEventListener("click", click);
    const item = document.createElement("li");
    item.append(element);
    return item;
  }
  function renderLists() {
    byId("image-count").textContent = state.images.length ? `${state.images.length} photos · ${state.images.filter(image => image.reviewed).length} reviewed` : "No photos open.";
    byId("image-list").replaceChildren(...state.images.map((image, index) => {
      const item = button(`${image.reviewed ? "✓ " : ""}${image.fileName}`, index === state.active, () => {
        if (!state.busy) void selectImage(index);
      });
      const details = document.createElement("small");
      details.textContent = `${image.boxes.length} boxes · ${image.split} · ${image.group || "group needed"}`;
      item.firstChild.append(details);
      return item;
    }));
    const image = active();
    byId("box-count").textContent = image ? `${image.boxes.length} boxes${image.reviewed ? " · reviewed" : " · review needed"}` : "No photo selected.";
    byId("box-list").replaceChildren(...(image?.boxes ?? []).map((box, index) => button(
      `${index + 1} · ${box.kind === "train" ? "Train" : "Player marker"} · ${box.color} (${Math.round(box.x)}, ${Math.round(box.y)}; ${Math.round(box.width)} × ${Math.round(box.height)})`,
      index === state.selectedBox, () => {
        if (state.busy) return;
        cancelDraft();
        state.selectedBox = index;
        renderLists(); renderMetadata(); draw();
      })));
  }
  function drawBox(box, label, selected, draft = false) {
    const image = active();
    if (!image) return;
    const sx = canvas.width / image.width;
    const sy = canvas.height / image.height;
    const x = box.x * sx, y = box.y * sy, width = box.width * sx, height = box.height * sy;
    context.lineWidth = 5;
    context.strokeStyle = "#241b16";
    context.strokeRect(x, y, width, height);
    context.lineWidth = 2;
    context.strokeStyle = selected || draft ? "#ffdc32" : "#ffffff";
    context.setLineDash(draft ? [6, 4] : []);
    context.strokeRect(x, y, width, height);
    context.setLineDash([]);
    if (label) {
      context.font = "bold 15px Segoe UI, Arial, sans-serif";
      const textWidth = context.measureText(label).width;
      const textY = Math.max(18, y);
      const textX = Math.min(Math.max(0, x), Math.max(0, canvas.width - textWidth - 8));
      context.fillStyle = "#241b16";
      context.fillRect(textX, textY - 18, textWidth + 8, 20);
      context.fillStyle = selected ? "#ffdc32" : "#ffffff";
      context.fillText(label, textX + 4, textY - 3);
    }
  }
  function draw() {
    const image = active();
    context.clearRect(0, 0, canvas.width, canvas.height);
    canvas.hidden = !state.image;
    byId("canvas-placeholder").hidden = Boolean(state.image);
    if (!image || !state.image) return;
    context.drawImage(state.image, 0, 0, canvas.width, canvas.height);
    image.boxes.forEach((box, index) => drawBox(box, String(index + 1), index === state.selectedBox));
    if (state.draft) drawBox(rectangle(state.draft.start, state.draft.end), "", false, true);
  }
  function cancelDraft() {
    const draft = state.draft;
    state.draft = null;
    if (draft && canvas.hasPointerCapture(draft.pointerId)) canvas.releasePointerCapture(draft.pointerId);
    draw();
  }
  async function selectImage(index) {
    cancelDraft();
    state.active = index;
    state.selectedBox = -1;
    state.image = null;
    const generation = ++state.generation;
    const photo = active();
    renderLists(); renderMetadata(); draw();
    if (!photo) return;
    try {
      const decoded = await decode(photo.url);
      if (generation !== state.generation) return;
      state.image = decoded;
      const scale = Math.min(1, 1600 / photo.width, 1000 / photo.height);
      canvas.width = Math.max(1, Math.round(photo.width * scale));
      canvas.height = Math.max(1, Math.round(photo.height * scale));
      canvas.setAttribute("aria-label", `${photo.fileName}. Drag to outline a physical piece. Escape cancels drawing. Delete removes the selected box when the photo has focus.`);
      renderMetadata(); draw();
    } catch (failure) { if (generation === state.generation) error(failure.message); }
  }
  function decode(url) {
    return new Promise((resolve, reject) => {
      const image = new Image();
      image.onload = () => resolve(image);
      image.onerror = () => reject(new Error("The browser could not decode this PNG or JPEG photo."));
      image.src = url;
    });
  }
  function fileNameValid(name) {
    if (typeof name !== "string" || name.length === 0 || name.length > 240 ||
        /[<>:"/\\|?*\u0000-\u001f]/u.test(name) || /[. ]$/u.test(name) || !/\.(png|jpe?g)$/iu.test(name)) return false;
    const stem = name.split(".", 1)[0].replace(/[. ]+$/u, "").toUpperCase();
    return !/^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$/u.test(stem);
  }
  function checkExifOrientation(bytes, view, start, length) {
    if (length < 6 || ![69, 120, 105, 102, 0, 0].every((value, index) => bytes[start + index] === value)) return;
    const tiff = start + 6, end = start + length;
    const invalid = () => { throw new Error("JPEG EXIF orientation must be valid and upright. Export an upright PNG before labeling."); };
    if (tiff + 8 > end) return invalid();
    const little = bytes[tiff] === 0x49 && bytes[tiff + 1] === 0x49;
    if (!little && !(bytes[tiff] === 0x4d && bytes[tiff + 1] === 0x4d)) return invalid();
    if (view.getUint16(tiff + 2, little) !== 42) return invalid();
    const directory = tiff + view.getUint32(tiff + 4, little);
    if (directory < tiff + 8 || directory + 2 > end) return invalid();
    const count = view.getUint16(directory, little);
    if (directory + 2 + count * 12 + 4 > end) return invalid();
    for (let index = 0; index < count; index++) {
      const entry = directory + 2 + index * 12;
      if (view.getUint16(entry, little) !== 0x0112) continue;
      if (view.getUint16(entry + 2, little) !== 3 || view.getUint32(entry + 4, little) !== 1 ||
          view.getUint16(entry + 8, little) !== 1) return invalid();
    }
  }
  function imageDimensions(buffer) {
    const bytes = new Uint8Array(buffer);
    const view = new DataView(buffer);
    const png = [137, 80, 78, 71, 13, 10, 26, 10];
    if (bytes.length >= 24 && png.every((value, index) => bytes[index] === value) &&
        view.getUint32(8) === 13 && view.getUint32(12) === 0x49484452)
      return [view.getUint32(16), view.getUint32(20)];
    if (bytes.length >= 4 && bytes[0] === 0xff && bytes[1] === 0xd8) {
      let position = 2;
      let dimensions = null;
      while (position + 4 <= bytes.length) {
        if (bytes[position++] !== 0xff) break;
        while (position < bytes.length && bytes[position] === 0xff) position++;
        const marker = bytes[position++];
        if (marker === 0xda || marker === 0xd9) break;
        if (marker === 0x01 || marker >= 0xd0 && marker <= 0xd7) continue;
        if (position + 2 > bytes.length) break;
        const length = view.getUint16(position);
        if (length < 2 || position + length > bytes.length) break;
        if (marker === 0xe1) checkExifOrientation(bytes, view, position + 2, length - 2);
        if ([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf].includes(marker)) {
          if (length < 8) break;
          dimensions = [view.getUint16(position + 5), view.getUint16(position + 3)];
        }
        position += length;
      }
      if (dimensions) return dimensions;
    }
    throw new Error("Choose a valid PNG or JPEG photo.");
  }
  async function sha256(buffer) {
    if (!globalThis.crypto?.subtle) return null;
    const hash = await crypto.subtle.digest("SHA-256", buffer);
    return Array.from(new Uint8Array(hash), value => value.toString(16).padStart(2, "0")).join("");
  }
  async function addImages(files) {
    if (!files.length || state.busy) return;
    error(""); cancelDraft(); setBusy(true);
    const staged = [];
    try {
      if (state.images.length + files.length > MAX_IMAGES) throw new Error(`Open at most ${MAX_IMAGES} photos in one workbench.`);
      if (state.images.reduce((sum, image) => sum + image.file.size, 0) + files.reduce((sum, file) => sum + file.size, 0) > MAX_TOTAL_BYTES)
        throw new Error("This batch exceeds the 512 MB total photo limit. Use a smaller set.");
      const names = new Set(state.images.map(image => image.fileName.toLowerCase()));
      for (const file of files) {
        if (!fileNameValid(file.name) || !/\.(png|jpe?g)$/iu.test(file.name)) throw new Error("Choose PNG or JPEG filenames without path components.");
        if (names.has(file.name.toLowerCase())) throw new Error(`Duplicate filename: ${file.name}. Use unique filenames for every photo.`);
        names.add(file.name.toLowerCase());
        if (file.size <= 0 || file.size > MAX_FILE_BYTES) throw new Error(`${file.name}: each photo must be nonempty and no larger than 48 MB.`);
        tell(`Checking ${file.name}…`);
        const buffer = await file.arrayBuffer();
        const [width, height] = imageDimensions(buffer);
        if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1 || width > 16384 || height > 16384)
          throw new Error(`${file.name}: photo dimensions must be between 1 and 16,384 pixels per side.`);
        const url = URL.createObjectURL(file);
        try {
          const decoded = await decode(url);
          if (decoded.naturalWidth !== width || decoded.naturalHeight !== height)
            throw new Error(`${file.name}: decoded dimensions differ from the stored image. Export an upright PNG before labeling.`);
          staged.push({ fileName: file.name, width, height, group: "", split: "train", reviewed: false, boxes: [], file, url, sha256: await sha256(buffer) });
        } catch (failure) { URL.revokeObjectURL(url); throw failure; }
      }
      const first = state.images.length;
      state.images.push(...staged);
      markDirty();
      await selectImage(first);
      tell(`Opened ${staged.length} photos. Outline physical pieces and assign a capture group.`);
    } catch (failure) {
      staged.forEach(image => URL.revokeObjectURL(image.url));
      error(failure.message);
      tell("Photo batch was not added. Existing annotations are unchanged.");
    } finally { setBusy(false); byId("image-files").value = ""; }
  }
  function coordinate(event) {
    const image = active();
    const bounds = canvas.getBoundingClientRect();
    return { x: Math.min(image.width, Math.max(0, (event.clientX - bounds.left) / bounds.width * image.width)),
      y: Math.min(image.height, Math.max(0, (event.clientY - bounds.top) / bounds.height * image.height)) };
  }
  function rectangle(start, end) {
    return { x: Math.min(start.x, end.x), y: Math.min(start.y, end.y), width: Math.abs(end.x - start.x), height: Math.abs(end.y - start.y) };
  }
  canvas.addEventListener("pointerdown", event => {
    const image = active();
    if (!image || !state.image || state.busy || event.button !== 0 || state.draft) return;
    if (image.boxes.length >= MAX_BOXES) { error(`A photo can contain at most ${MAX_BOXES} boxes.`); return; }
    event.preventDefault();
    error(""); canvas.focus();
    state.selectedBox = -1;
    const point = coordinate(event);
    state.draft = { start: point, end: point, pointerId: event.pointerId };
    canvas.setPointerCapture(event.pointerId);
    renderLists(); renderMetadata(); draw();
  });
  canvas.addEventListener("pointermove", event => {
    if (state.draft?.pointerId !== event.pointerId) return;
    state.draft.end = coordinate(event);
    draw();
  });
  canvas.addEventListener("pointerup", event => {
    if (state.draft?.pointerId !== event.pointerId) return;
    const box = rectangle(state.draft.start, coordinate(event));
    cancelDraft();
    if (box.width < 1 || box.height < 1) { tell("Drag a box at least one original pixel wide and high."); return; }
    active().boxes.push({ kind: byId("piece-kind").value, color: byId("piece-color").value, ...box });
    state.selectedBox = active().boxes.length - 1;
    changed();
    tell("Box added. Select New box or drag again to label the next piece.");
  });
  canvas.addEventListener("pointercancel", cancelDraft);
  canvas.addEventListener("lostpointercapture", () => { if (state.draft) cancelDraft(); });
  canvas.addEventListener("keydown", event => {
    if (event.key === "Escape") { event.preventDefault(); cancelDraft(); }
    else if (event.key === "Delete" && !state.busy) { event.preventDefault(); deleteBox(); }
  });
  function deleteBox() {
    const image = active();
    if (!image || state.selectedBox < 0 || state.busy) return;
    cancelDraft(); image.boxes.splice(state.selectedBox, 1); state.selectedBox = -1;
    changed(); tell("Selected box deleted. Review the photo again when labeling is complete.");
  }
  function validateGroup(group) {
    return typeof group === "string" && group.trim().length > 0 && group.trim().toLowerCase() !== "unassigned" && group.length <= 128 && !/[\u0000-\u001f]/u.test(group);
  }
  function validDraftGroup(group) {
    return typeof group === "string" && group.length <= 128 && !/[\u0000-\u001f]/u.test(group);
  }
  function validateBox(box, image) {
    if (!box || typeof box !== "object" || Array.isArray(box) || !kinds.has(box.kind) || !colors.has(box.color))
      throw new Error(`${image.fileName}: every box needs a supported kind and physical color.`);
    for (const property of ["x", "y", "width", "height"])
      if (typeof box[property] !== "number" || !Number.isFinite(box[property])) throw new Error(`${image.fileName}: box coordinates must be finite numbers.`);
    if (box.x < 0 || box.y < 0 || box.width < 1 || box.height < 1 || box.x + box.width > image.width + 0.000001 || box.y + box.height > image.height + 0.000001)
      throw new Error(`${image.fileName}: a box is empty or extends outside the photo.`);
    return { kind: box.kind, color: box.color, x: box.x, y: box.y, width: box.width, height: box.height };
  }
  function checkGroups(images) {
    const groups = new Map();
    for (const image of images) {
      if (!validateGroup(image.group)) throw new Error(`${image.fileName}: enter a capture group before exporting or importing labels.`);
      if (!splits.has(image.split)) throw new Error(`${image.fileName}: choose train, validation, or test.`);
      const key = image.group.trim().toLowerCase();
      if (groups.has(key) && groups.get(key) !== image.split) throw new Error(`Capture group ${image.group} spans multiple splits. Keep the entire group in one split.`);
      groups.set(key, image.split);
    }
  }
  async function importLabels(file) {
    if (!file || state.busy) return;
    error(""); cancelDraft(); setBusy(true);
    try {
      if (!state.images.length) throw new Error("Open the matching board photos before importing labels.");
      if (file.size <= 0 || file.size > MAX_LABEL_BYTES) throw new Error("Choose a labels JSON file no larger than 64 MB.");
      let data;
      try { data = JSON.parse(await file.text()); } catch { throw new Error("The labels file is not valid JSON."); }
      if (!data || data.version !== 1 || !Array.isArray(data.images) || !data.images.length || data.images.length > MAX_IMAGES)
        throw new Error("Expected labels version 1 with a nonempty images array of at most 200 entries.");
      const staged = new Map();
      const filenames = new Set();
      for (const entry of data.images) {
        if (!entry || typeof entry !== "object" || !fileNameValid(entry.fileName)) throw new Error("Every image needs a valid filename without path components.");
        const filename = entry.fileName.toLowerCase();
        if (filenames.has(filename)) throw new Error(`Duplicate image labels: ${entry.fileName}.`);
        filenames.add(filename);
        const index = state.images.findIndex(image => image.fileName === entry.fileName);
        if (index < 0) throw new Error(`Open the matching photo first: ${entry.fileName}. Filenames must match exactly.`);
        const image = state.images[index];
        if (entry.width !== image.width || entry.height !== image.height) throw new Error(`${entry.fileName}: photo dimensions do not match the labels.`);
        if (entry.sha256 !== undefined && (typeof entry.sha256 !== "string" || !/^[a-f0-9]{64}$/u.test(entry.sha256) || entry.sha256 !== image.sha256))
          throw new Error(`${entry.fileName}: photo SHA-256 does not match, or hashing is unavailable in this browser.`);
        if (!validDraftGroup(entry.group) || !splits.has(entry.split) || typeof entry.reviewed !== "boolean" || !Array.isArray(entry.boxes) || entry.boxes.length > MAX_BOXES)
          throw new Error(`${entry.fileName}: invalid capture group, split, review flag, or boxes array.`);
        staged.set(index, { ...image, group: entry.group.trim(), split: entry.split, reviewed: entry.reviewed,
          boxes: entry.boxes.map(box => validateBox(box, image)) });
      }
      // Drafts can contain missing groups or split conflicts. Training export enforces those gates.
      state.images = state.images.map((image, index) => staged.get(index) ?? image);
      state.selectedBox = -1;
      markDirty(); renderLists(); renderMetadata(); draw();
      tell(`Imported labels for ${staged.size} photos. Review any new edits and export to preserve them.`);
    } catch (failure) { error(failure.message); tell("Labels were not imported. Existing annotations are unchanged."); }
    finally { setBusy(false); byId("label-file").value = ""; }
  }
  function exportLabels() {
    if (state.busy || !state.images.length) return;
    error(""); cancelDraft();
    try {
      let readyForTraining = state.images.every(image => image.reviewed);
      try { checkGroups(state.images); } catch { readyForTraining = false; }
      const data = { version: 1, images: state.images.map(image => {
        const entry = { fileName: image.fileName, width: image.width, height: image.height, group: image.group.trim(), split: image.split,
          reviewed: image.reviewed, boxes: image.boxes.map(box => validateBox(box, image)) };
        if (image.sha256) entry.sha256 = image.sha256;
        return entry;
      }) };
      const blob = new Blob([JSON.stringify(data, null, 2) + "\n"], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `GoldenTicket-piece-labels-${new Date().toISOString().replace(/[:.]/gu, "-")}.json`;
      document.body.append(anchor); anchor.click(); anchor.remove();
      setTimeout(() => URL.revokeObjectURL(url), 30000);
      state.dirty = false;
      byId("save-state").textContent = "Export download requested. Check your Downloads folder before closing; there is no autosave.";
      tell(`Exported labels for ${data.images.length} photos (${data.images.filter(image => image.reviewed).length} reviewed). ` +
        (readyForTraining ? "The training exporter will validate the complete dataset separately." : "Draft saved: training export still requires reviewed photos, assigned groups, and consistent group splits."));
    } catch (failure) { error(failure.message); }
  }
  byId("image-files").addEventListener("change", event => { void addImages(Array.from(event.target.files)); });
  byId("label-file").addEventListener("change", event => { void importLabels(event.target.files[0]); });
  byId("export-labels").addEventListener("click", exportLabels);
  byId("delete-box").addEventListener("click", deleteBox);
  byId("deselect-box").addEventListener("click", () => { cancelDraft(); state.selectedBox = -1; renderLists(); renderMetadata(); draw(); });
  for (const id of ["piece-kind", "piece-color"]) byId(id).addEventListener("change", event => {
    const box = active()?.boxes[state.selectedBox];
    if (!box || state.busy) return;
    box[id === "piece-kind" ? "kind" : "color"] = event.target.value;
    changed();
  });
  byId("capture-group").addEventListener("input", event => {
    const image = active();
    if (!image || state.busy) return;
    image.group = event.target.value;
    image.reviewed = false;
    markDirty(); renderLists();
    byId("image-reviewed").checked = false;
  });
  byId("dataset-split").addEventListener("change", event => {
    if (!active() || state.busy) return;
    active().split = event.target.value; changed();
  });
  byId("image-reviewed").addEventListener("change", event => {
    const image = active();
    if (!image || state.busy) return;
    error("");
    if (event.target.checked && !validateGroup(image.group)) {
      event.target.checked = false; error("Enter a capture group before marking this photo reviewed."); return;
    }
    image.reviewed = event.target.checked;
    markDirty(); renderLists();
  });
  byId("clear-images").addEventListener("click", () => {
    if (state.busy || state.dirty && !confirm("There are unexported labels. Clear the workbench and discard them?")) return;
    cancelDraft(); state.generation++;
    state.images.forEach(image => URL.revokeObjectURL(image.url));
    state.images = []; state.active = -1; state.selectedBox = -1; state.image = null; state.dirty = false;
    error(""); renderLists(); renderMetadata(); draw(); setBusy(false);
    byId("save-state").textContent = "Labels are held in memory. Export before closing; there is no autosave.";
    tell("Workbench cleared. Open board photos to begin.");
  });
  window.addEventListener("beforeunload", event => { if (state.dirty) { event.preventDefault(); event.returnValue = ""; } });
  renderLists(); renderMetadata(); draw();
})();
