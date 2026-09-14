# GoldenTicket implementation completion

Updated September 12, 2026 after the [implementation audit](docs/AUDIT-2026-09-12.md).
The complete requirements remain in [DESIGN.md](DESIGN.md). This is a partial manual desktop
implementation with a game companion and camera/reference-photo tools, not a completed automatic
camera-assisted product. See [September 12 progress and morning checks](docs/IMPLEMENTATION-2026-09-12.md).

## Game screen (implemented September 14, 2026)

See the
[game and technical layers](DESIGN.md#411-game-and-technical-layers).

- [x] Add a player-facing game layer that covers the entire application content area, including
  the current technical navigation. Keep the existing technical interface available underneath.
- [x] Slide the game layer out of view to reveal the technical interface with Shift+Escape.
  Preserve plain Escape-to-hide/cancel behavior and ignore held-key repeats.
- [x] Add a **Return to game** button at the top of the technical interface. Slide the game
  layer back over 100% of the content area, preserving the active game and technical tool state.
- [x] Add snowy-twilight launch art, Start/Reload choices, 2–5 player counts, matched human/robot
  portrait selection, keyboard and mouse input, and PLAY. All-human and all-computer setups work.
- [x] All 686 automated tests pass under a loaded Windows user profile. Synthetic WPF checks
  cover 64 render cases, selection input, repeated transitions, resizing, privacy hiding and
  shared game/camera ownership, with no binding warnings.
- [ ] Perform a hands-on desktop check of focus, maximization, animation/reduced-motion settings
  and the live camera while switching layers on the target Windows machine.

## Still missing: feature checklist

These six features remain unfinished. The milestone tasks below define their implementation and
validation requirements; mark each feature complete only after those checks pass.

- [ ] **Camera tracking and recovery** — board recognition, move verification, and automatic recovery after camera movement (M3–M5).
- [ ] **Phone/tablet PWA** — private pass-and-hide on Android and iOS/iPadOS, with laptop-hosted data
  and QR-assisted initial synchronization entirely within the LAN (M0/M2). No iPhone or iPad is
  available, so **iOS cannot be claimed as supported at release**; see
  [companion device evidence](docs/companion-device-evidence.md) for the honest wording and the
  borrowed-device checklist.
- [ ] **CPU/GPU inference selection** — actual Auto/CPU/GPU preprocessing, preference persistence,
  fallback and effective-backend status are implemented. Packaged-model execution and the full
  hardware/provider acceptance matrix remain M4/M5; preprocessing is not ML inference.
- [ ] **Photographed save and rebuild** — the state-only half is implemented (M2): named checkpoints,
  the `PreparingPackAway`/`PackedAway`/`Rebuilding` lifecycle, commit-then-readback validation,
  route-list guided reconstruction and exactly-once resume. Optional encrypted, operator-attested
  reference photos now have immutable checkpoint binding and authenticated readback. Still missing:
  the board diagram, machine-verified photograph, pending-placement mask and full evidence lifecycle.
- [ ] **Offline installer packaging** — self-contained Windows x64 distribution with required runtimes and assets included (M7).
- [ ] **Training mode, voice, story, and audio: last feature pass** — Training follows Story without effects/ambience; narration follows visual/voice/both settings. Complete photo save-and-rebuild and packaging foundation first (M6).

The user's requested order puts voice/story/audio last. Keep essential visual guidance available
earlier; perform final release checks and package refresh after the narrative features are finished.

Development now uses Visual Studio and GitHub [Windows CI](.github/workflows/windows-ci.yml).
Do not generate a personal app ZIP as a routine handoff. CI builds/tests and keeps diagnostic
evidence only; future installer/distribution work remains a separate requirement. Installed
Windows/PWA gameplay must operate on the LAN without Internet access; see [the build/runtime contract](docs/build-and-ci.md).

## Audit fixes implemented

- [x] Harden the connectivity spike's network boundary, request limits, session lifetime and local
  certificate handling; keep the full game companion and real-device evidence outstanding.
- [x] Compare complete checkpoint readback data, include supply policies in new logical hashes,
  require a fresh rebuild attestation after restart, and permit safe readback retry.
- [x] Keep packed/rebuilding games behind the privacy gate, scope operator acknowledgements to the
  current decision, and continue eligible AI turns after resolving a pause or rebuild.
- [x] Bound browser installation/network checks, prevent false reload/cache success reports, and
  constrain the service worker to its own explicitly allowed shell assets.
- [x] Confine save paths and reject linked/unsupported paths before filesystem operations.
- [x] Check snapshot/journal/version integrity, prevent competing writers, and preserve old save compatibility.
- [x] Keep unreadable saves visible, sanitize recovery errors, and block gameplay after uncertain writes.
- [x] Validate claim actors, revision, lane, manual mode, and attestation metadata.
- [x] Preserve rare depleted-supply states without silently advancing turns.
- [x] Protect complete state fingerprints, card identities, and card/ticket conservation checks.
- [x] Invalidate late private views on Hide/deactivation; require explicit manual verification.
- [x] Require whole-board attestation per placement and before continuing a restored game.
- [x] Bound AI waiting, isolate late work/randomness, honor cancellation, and validate fallback decisions.
- [x] Redact and bound fault diagnostics; stop after an unhandled application fault.
- [x] Lock transitive dependencies and add targeted regression coverage.

## Complete the product in dependency order

- [x] **GitHub CI configuration.** Windows build, locked .NET/npm restores, .NET/JavaScript tests,
  simulation and WPF/browser UI checks run for pushes to `main`, PRs and manual dispatch. Actions
  have read-only repository access; application archives and releases are not generated.
- [x] **LAN-only runtime regression gates.** Validate bundled browser resources and local certificates;
  exercise phone gameplay with Internet-unavailable browser state and block external browser origins.
  Physical WAN-disconnected phone acceptance remains outstanding.
- [x] **Embedded companion first slice.** Local HTTPS host, controller approval/CSRF/private grants,
  shared human card/ticket actions, public-only shell cache and privacy/reconnect handling are
  integrated into the Windows shell. Actual device gates and the remaining protocol requirements
  below are still open.
- [x] **Single-human laptop play.** One human's card and ticket choices open directly on the laptop,
  with **Your cards** / **Back to table** controls and no **Connect phone** step. The active or
  resumed roster determines this behavior; multiple humans retain explicit pass-and-hide and an
  optional shared companion. Board verification, AI secrecy and explicit Hide remain in force.
- [x] **Camera and photo foundation.** Windows video-only capture, selectable formats, manual
  four-corner crop and conservative scene-reference checks; optional encrypted operator-attested
  checkpoint photos with integrity/readback and stale-capture protection. No automated train
  verification or machine-verified photo checkpoint is claimed.
- [x] **Manual board-photo export.** A valid live crop can be exported to PNG without a scene
  reference, including while the scene has changed. Fresh-frame and crop/camera identity checks
  remain; encrypted checkpoint-photo capture retains its separate reference checks.
- [x] **Native-resolution preference and truthful 4K processing.** Prefer an advertised native mode
  up to 3840 × 2160; try smaller usable modes when necessary, and display the actual delivered
  dimensions separately from processing output. Shared-read-only inspection confirms the current
  Pixel UVC connection advertises 1080p at most. Physical native-4K camera acceptance remains open.
- [x] **CPU/GPU preprocessing implementation.** Real Direct3D 11 compute performs bounded
  enhancement and aspect-preserving resizing, with Auto/CPU/GPU choices, local preference
  persistence, validated hardware activation, effective status and CPU fallback. No recognition
  model was bundled with the September 12 preprocessing step. Local build, 558 tests and 53 synthetic WPF render cases passed,
  with no binding warnings/errors; see [camera processing](docs/camera-processing.md).
  Software-adapter checks now cover missing flags on Microsoft Basic Render Driver. Controlled
  frame-clock tests retain the two-second stale-evidence limit without depending on CI speed.
- [x] **Historical empty-board piece baseline.** Capture or load an empty-board crop, compare
  subsequent frames, and draw white rotated train-candidate rectangles and score-marker squares.
  Camera/crop/processor changes invalidate references and stale work. Motion, insufficient detail
  and major scene changes withhold candidates. This is a low-false-positive baseline to evaluate,
  not a measured accuracy claim or an authority to spend cards, score or commit routes. The
  September 13 ML experiment supersedes this baseline in the live preview; comparison tooling remains.
- [x] **Processing and outline automated/image-pair checks.** The integrated automated/UI pass
  succeeded. The supplied photo pair produced 15 train and 5 marker candidates in raw and enhanced
  comparisons; an unchanged empty-board comparison produced 0/0. RTX 4080 Laptop preprocessing
  matched the CPU within one channel level when upscaling and exactly in the native-4K fixture.
  See the [bounded validation record](docs/evidence/camera-processing-2026-09-12/validation.md).
- [ ] **Processing and outline physical acceptance.** Measure empty-board false positives and
  per-piece misses across printed routes, shadows, touching trains and lighting; test live
  raw/enhanced preview and native-4K input on actual hardware. Exported-reference reload and
  jog/return checks apply only when explicitly comparing the historical difference baseline.
  Complete adapter/device-loss and preference-switching acceptance. Image enhancement must remain
  separate from unsharpened checkpoint evidence. A successful photo pair is not general recognition
  accuracy or a supported native-4K camera claim.
- [ ] **Controlled glare/reference experiment.** A later enhanced comparison of image `160803`
  against old empty reference `152343` returned 20 train/19 marker candidates; the comparison
  without additional enhancement returned 24/19. Visual review sees 15 trains/5 markers, with
  false candidates on printed score numbers/tracks. Both were Ready, and the cause is
  unisolated (crop/geometry, lighting or reference age); do not attribute it to glare. Follow the
  [fixed-camera glare protocol](docs/glare-test.md), using fresh and lighting-matched empty
  references, and annotate individual misses, false positives and scene holds rather than counts.
- [x] **First fresh-reference glare series.** With unchanged detector thresholds, the nominal empty
  reference plus minimal/stronger/strongest glare images produced 8/4, 19/4 and 35/6 train/marker
  candidates. Overlay review found all 8 actual trains and 4 markers covered, with 0/0, 11/0 and
  27/2 false outlines. Every result stayed Ready; additional GPU enhancement gave identical counts.
  The [bounded glare record](docs/glare-test.md#fresh-reference-series-september-12-2026) includes
  placement and reflection limits. User photographs remain local.
- [x] **User-reported soft-light check.** After the glare experiment, the user restored soft
  lighting and reported reliable piece detection on September 12. The camera guide now recommends
  that setup. This is a user observation for that trial, without a new counted image series;
  see the [follow-up record](docs/glare-test.md#user-reported-soft-light-follow-up).
- [x] **User-reported matching-lighting glare check.** The user cleared the board, set lights with
  some glare, captured the empty-board reference, then added pieces and reported reliable detection.
  Setup guidance now puts camera/light positioning before reference capture and keeps lighting
  stable afterward. This is an uncounted user observation; see the
  [matching-lighting report](docs/glare-test.md#user-reported-matching-lighting-glare-follow-up).
- [ ] **Glare and illumination robustness.** Reduce false printed-board candidates or withhold
  doubtful results under changed lighting. Complete the counted matching-lighting reference matrix
  and direct glare over actual piece groups; repeat with other colors/placements and live motion.
  The current series showed false positives, not misses, but does not prove general glare tolerance
  or reliable scene holds. Keep any future tuning separate from the recorded measurement.
- [x] **Developer ML data preparation.** Add a local browser annotation workbench and bounded,
  reviewed-label COCO exporter with source hashes and capture-group split isolation. The
  [ML sequence](docs/piece-recognition-ml.md) starts with independent image detection; no trained
  model, inference runtime or recognition accuracy gain is included in this step.
- [x] **First learned piece-outline experiment.** Audit 29 local photos / 954 labels, preserve
  capture-date groups, train and export a two-class YOLOX-Nano detector, and integrate independent
  ONNX CPU/DirectML inference into the preview. Empty-board capture is no longer a prerequisite.
  Add exact-frame review ZIPs with model hashes and notes. See the
  [experiment and validation](docs/piece-recognition-ml.md); this is experimental visual feedback.
- [x] **Rotated train outlines.** Fit an optional display rectangle to the train pixels within each
  ML detection; uncertain fits retain the original box and markers remain square. Preserve original
  model predictions for evaluation and save fitted geometry separately for review. This is local
  image fitting, not learned rotation; live-camera angle accuracy still needs review.
- [x] **Score marker values in Piece outlines.** Show each detected marker's color and printed
  track position, preserving shared rows/columns. Clear stale readings and display missing,
  uncertain and duplicate colors explicitly. The supplied photo reads yellow 20, blue 15,
  red 11, black 11 and green 50 on CPU/DirectML; 57 focused checks and four WPF render cases
  pass. These observations do not change game scores or infer full laps. See the
  [score-marker checks](docs/evidence/score-markers-2026-09-13/validation.md).
- [ ] **Independent ML acceptance and error-driven training.** Review saved failures, correct
  labels, retrain with recorded provenance and evaluate untouched new capture sessions. Include
  empty boards and lighting changes in held-out evaluation, all colors, crowding, motion and
  occlusion. Measure whole camera-to-outline latency and provider failures on more hardware.
  Color recognition, route assignment and automatic game verification remain separate work.
- [ ] **Independent crowded-marker checks.** The black marker in photo `20260913-171132`
  had low baseline confidence when surrounded by other markers. The second model now detects it
  in all three reviewed failure photos. Collect new paired isolated/clustered examples on light
  and dark artwork to test generalization. See the
  [recorded failure](docs/evidence/clustered-markers-2026-09-13/validation.md).
- [ ] **Capture an intermittent yellow-train miss.** Photo `172806` and the yellow body
  omitted from the previous `171132` labels were reviewed in collection `03` and subsequently trained. The saved photo detects
  the train on both CPU and DirectML; collect an exact missed-frame detection example before
  attributing the live failure or changing runtime behavior. See the
  [check and label correction](docs/evidence/yellow-train-2026-09-13/validation.md).
- [ ] **Check new layouts for printed-route false positives.** The second model removes the extra prediction over the
  empty yellow Boston–New York slot in training photo `173706` and keeps the real red train.
  Check new layouts and lighting for recurrence. See the
  [reproduction and labels](docs/evidence/boston-false-positive-2026-09-13/validation.md).
- [x] **Retrain on reviewed failures.** Fine-tune on all 32 photos / 1,126 labels, compare both
  models at unchanged thresholds, verify actual CPU/DirectML outputs and install the local pair
  with rollback preserved. All saved-photo labels now match without extras; this is in-sample
  regression evidence, not independent accuracy. See the
  [training and deployment record](docs/evidence/ml-retrain-2026-09-13/validation.md).
- [x] **Retrain the Denver yellow-train miss.** Add reviewed photo `184806` and fine-tune on
  all 33 photos / 1,210 labels. The missed Salt Lake City–Denver train rises from 0.2993 to
  0.9290 at the unchanged 0.30 cutoff; all labels match without extras. Install the verified
  local pair with rollback preserved after six CPU/DirectML fixtures and a subpixel parity
  investigation. These are in-sample checks; independent acceptance remains open above. See the
  [training and deployment evidence](docs/evidence/ml-retrain-denver-2026-09-13/validation.md).
- [x] **Retrain for stronger shadows and parallel trains.** Add reviewed photos `202009`
  and `202150` to collection `06` (35 photos / 1,390 labels). Select the final checkpoint
  after rejecting an early checkpoint's older-photo false positive. All reviewed labels
  match without extras; parallel upper boxes improve from IoU about 0.51 to 0.85 / 0.84
  with no lower-edge spill. Nine CPU/DirectML fixtures pass. These are in-sample checks;
  independent sessions and live stability remain acceptance work. See the
  [training evidence](docs/evidence/ml-retrain-shadows-2026-09-13/validation.md).
- [x] **Train on shifted-light Miami example.** Add reviewed photo `202835` to collection
  `07` (36 photos / 1,481 labels), excluding printed slots and cast shadows. Reject the
  early checkpoint's two older-photo extras; the final checkpoint preserves all labels
  without extras and distinct parallel trains. Ten labeled CPU/DirectML fixtures plus
  the separate `202727` score fixture pass. The frozen model already handled this saved
  frame, so the intermittent live issue remains unverified. See
  [shifted-light evidence](docs/evidence/ml-retrain-miami-shadows-2026-09-13/validation.md).
- [x] **Train on another shadow direction.** Add reviewed photo `203020` with unchanged
  placements to collection `08` (37 photos / 1,572 labels). Reject both first-run checkpoints;
  the seed-only retry preserves every label without extras at the unchanged preview threshold.
  Twelve labeled CPU/DirectML fixtures and the separate score fixture pass. Install the verified
  pair locally with the previous pair retained for rollback. Results remain in-sample. See
  [lighting-variation evidence](docs/evidence/ml-retrain-light-variation-2026-09-13/validation.md).
- [x] **Train on major lighting adjustments.** Add reviewed photo `203656` to collection
  `09` (38 photos / 1,663 labels). Reject final epoch40 for the older marker seam miss;
  install the recorded epoch30 fallback after all labels match without extras and all
  14 native CPU/DirectML fixtures pass. Previous model pairs remain available for rollback.
  These are training-photo checks, not independent live acceptance. See
  [major-lighting evidence](docs/evidence/ml-retrain-large-lighting-2026-09-13/validation.md).
- [x] **Reconcile the saved training backlog.** All 38 reviewed piece photos across collections
  `01`–`09`, including the staged failure and lighting examples, are included in the installed
  model. No additional reviewed training batch remains. Photo `181222` was used for corner
  training; `202727` remains an intentionally untrained score-check fixture. Independent
  live captures and the runtime issues below are separate outstanding work. Coverage evidence
  is local under `artifacts/piece-training/backlog-audit-20260913/`.
- [ ] **Tile-boundary detection gap.** A rejected training checkpoint misses the red score
  marker in `130924` because adjacent tiles predict centers just across opposite sides of
  the same ownership boundary, discarding both strong proposals. Preserve the raw/native
  regression and evaluate a geometry fix separately from model training. See the
  [lighting-variation diagnosis](docs/evidence/ml-retrain-light-variation-2026-09-13/validation.md).
- [x] **Experimental learned corner selection.** A separate local corner heatmap model selects
  the outer board crop once per camera session, with an explicit retry and editable handles.
  Manual edits, camera changes and stale frames invalidate pending results. Missing or uncertain
  models preserve manual operation. See [model scope and validation](docs/board-corners-ml.md).
- [x] **Outer crop margin.** ML corner proposals expand by about 0.25% per side before setting the
  visible handles, leaving a thin border matching the user's adjusted examples. Padding stays inside the camera
  image and contains the detected board. Retries do not accumulate it; manual edits remain exact.
- [ ] **Real corner-model acceptance.** Collect uncropped, independently labeled camera sessions
  with varied backgrounds, framing, lighting, perspective and occlusion. Synthetic projective
  training and screenshot diagnostics do not establish real-camera corner accuracy or tracking.
- [x] **Adjustable photo crop.** Drag any numbered corner during or after selection, or select it
  with 1–4 and nudge with arrows (Shift for larger steps). Invalid geometry keeps all handles editable
  and disables photo capture until corrected. Synthetic view-model and WPF checks cover editing,
  crop invalidation, image-edge clamping and camera-session changes; physical mouse-drag acceptance
  with the overhead camera remains to be checked.
- [x] **Precise crop preview zoom.** Fit through 800%, pointer-anchored Ctrl + wheel, + / −,
  and automatic left-drag panning on the zoomed image. A click places the next corner during
  explicit selection; the selection button, next-corner prompt and errors sit beside zoom so the
  current mode remains visible while scrolled to the image. Dragging a numbered handle adjusts it.
  Pan/Space/middle-button dragging remain
  available. Number keys reveal offscreen corners and zoom makes
  arrow nudges finer. Crop coordinates, references and export processing do not change with
  preview zoom. Synthetic WPF checks cover transforms, overlays, bounds, keyboard behavior,
  click-versus-drag handling and cancelled gestures;
  real mouse/trackpad and overhead-camera acceptance remains to be checked.
- [x] **Visible photo capture and rebuild status.** Distinguish a digital save, a live unsaved crop
  and an attached photo. Explain disabled capture prerequisites, offer Camera setup from the photo
  page, display the saved photo inline during rebuilding, and explain zero-route saved positions.
  Capture remains explicit and requires an operator board check; a digital save alone has no photo.
- [x] **Obvious saved-match selection.** Show a checkmark, automatically select a sole save, keep
  selection across refreshes, and enable Resume only with a selected match. Display selection
  guidance and restore errors in the saved-matches panel; retain the board reconciliation gate.
- [x] **Saved-match names.** Show the entered name first, including names already stored in existing
  checkpoints. Keep the latest committed name after resuming, use readable status text, and retain
  support for older unnamed saves without a schema migration.
- [x] **Offline package build workflow.** A clean-source, locked-dependency PowerShell builder creates
  a self-contained Windows x64 ZIP with runtime/assets checks, notices, provenance and checksums.
  Actual package output is recorded separately in [packaging evidence](docs/offline-package.md);
  clean-machine acceptance and an installer remain open.
- [x] **Runtime packaging checks.** Separate reviewed Windows-runtime lock files preserve the
  development dependency locks. An explicit windowless executable diagnostic checks the loaded
  bundled runtime and native components before the ZIP is created.
- [x] **First portable package built and checked.** Normal and cache-only offline builds passed;
  the published executable passed eight component checks and every archived payload hash matched.
  Source/artifact identities are in [package evidence](docs/evidence/offline-package-2026-09-12/README.md).
- [ ] Replace companion snapshot polling with WSS/event-cursor synchronization; persist protected
  approved-device registry, implement reconnect/lease recovery without pairing after every reload,
  and add hold-to-peek plus full accessibility/device acceptance. Current plain-JS client is a
  bundled implementation deviation from the planned TypeScript build.
- [x] **Record first Pixel setup requirements.** Document the Windows Public-to-Private change,
  Administrator/UAC requirement, scoped firewall rule, successful LAN bootstrap, and verified USB
  certificate transfer in [phone setup](docs/phone-setup.md). Android certificate approval and the
  remaining PWA acceptance checks are still pending.
- [x] **M0/M2: guided connection setup first slice.** Detect the actual Windows network profile, explain a
  Public-profile block, guide consent for a trusted-network change and scoped firewall access,
  and verify each step. Handle Chrome's HTTP certificate-download warning with a verified local
  transfer path; explain Android's CA confirmation and record the outcome without treating it as
  successful HTTPS trust. Preserve laptop-only play if the user declines. The UI displays a scoped
  firewall command and links Windows network settings; applying OS changes remains explicit.
- [ ] **M0/M1: data and platform evidence.** Review every city connection, lane, color, train length,
  and all 30 tickets against the supported physical edition. Record reviewer/provenance. Supply
  measured route-cell geometry and board landmarks; retain the current unaudited status until done.
- [x] **Rare supply rules resolved.** Each paused position now offers one reviewed, versioned
  continuation the operator accepts explicitly; the acceptance is journalled with its policy version
  and holds for the match. The pass policy terminates: a full round of passes goes to final scoring.
  Documented in [docs/rules-policies.md](docs/rules-policies.md). Remaining: confirm each policy
  against an official clarification where one exists, rather than shipping them as house policy.
- [x] **M0: connectivity spike built.** `tools/GoldenTicket.ConnectivitySpike` generates a
  per-installation DPAPI-protected CA and a hostname/IP-matching leaf, serves a trusted same-origin
  HTTPS host bound to a chosen private interface, advertises `gt-<id>.local` over mDNS with an IP
  fallback, offers a closable plain-HTTP certificate bootstrap, and runs a single-use rate-limited
  pairing round-trip. It draws a locally generated connection QR on the console and to an SVG file,
  carrying only the landing address; the device page refuses to run inside an in-app browser and
  hands off to Chrome or Safari, puts installation before pairing, and tells players not to tap
  through a certificate warning. Verified laptop-side; **still needs the real-device runs** in
  [docs/companion-device-evidence.md](docs/companion-device-evidence.md), and the QR has never been
  scanned by a camera.
- [ ] **M0/M2: companion completion.** Extend the embedded HTTPS host and game PWA with the remaining
  WSS/recovery/registry protocol and accessibility requirements. Keep the implemented certificate,
  pairing, grants, CSRF, idempotent commands, shell-only cache and QR behavior covered by regression
  tests. Synchronize public data/snapshots directly from the local host after pairing and
  handle changes during synchronization without losing events. Require no inputs or services outside
  the LAN, including during first setup. Prove QR setup, local certificate trust, and initial sync
  with WAN disconnected on real devices before claiming support. Android is testable throughout;
  iOS/iPadOS depends on a single borrowed-device session, so build the M0 connectivity spike as a
  standalone fifteen-minute checklist that a borrowed device can be taken through without the game
  UI (docs/companion-device-evidence.md).
- [ ] **M3: camera completion.** Validate the implemented WinRT acquisition, bounded frame ownership,
  camera choice, native-format fallback, preview, CPU/GPU preprocessing and manual crop against
  the real board. Evaluate the experimental empty-board detector. Add printable markers, board
  landmarks, automatic calibration, detailed quality gates and recording/replay.
- [ ] **M4: verification.** Implement whole-board recognition, authorized pending evidence,
  board-first human placement, occlusion/unknown foreground rejection, jog/reconnect recovery,
  stale-epoch rejection, wake gesture, and explicit mode-change reconciliation.
- [ ] **M4/M5: model inference.** Build on the implemented preprocessing preference/status flow.
  For any required learned recognizer, default to Auto: detect adapters at launch, validate GPU execution with
  the packaged model, and fall back to CPU on absence, incompatibility, timeout, or failure. Retain
  explicit CPU/GPU preferences and distinguish them from the effective backend. Display a chip/CPU
  icon or GPU text with lightning around it, with adapter/fallback details. Package native runtimes
  and test safe switching. Evaluate a baseline first; if needed train on developer data, validate
  held-out physical sets, and ship an offline model. Users never train or download a model.
- [x] **M2: state-only pack away and rebuild.** Named checkpoints, durable packed/rebuild lifecycles,
  frozen source state, commit-then-readback validation before any safe-to-pack result, suspended
  partial operations preserved, route-list guided reconstruction with whole-target attestation,
  and exactly-once resume. Verified by `PackAwayTests` and `PackAwayDurabilityTests`.
- [x] **Desktop exit confirmation.** An unfinished match prompts before exit with No selected;
  wording distinguishes automatic digital saves from a verified pack-away checkpoint. No warning
  for an already verified packed/rebuilding game solely because its optional photo is absent.
  Pending writes block closing; storage faults show uncertainty. Cancel retains usable tools and
  covers private hands. Confirm preserves deferred cleanup and final close without WPF reentry.
- [ ] **M2/M4: persistence and pack away, remaining.** Add the geometry-based rebuild diagram, complete encrypted snapshots,
  backup-before-migration, full evidence pinning and machine-verified board photographs,
  partial-operation (pending placement) targets, and current-checkpoint success receipts for the
  companion. Add correction branches and optional encrypted portable export/import.
- [ ] Complete non-audio theme/high-contrast/screen-reader/keyboard work and adjustable privacy timing
  before the final narrative feature pass.
- [ ] Implement and evaluate the specified Challenging AI sampled lookahead; current difficulty
  choices tune a heuristic. Keep opponent hands/deck state inaccessible and report strength honestly.
- [ ] **M7: distribution completion.** Validate the self-contained x64 ZIP on clean Windows, add an
  installer, native dependency smoke checks, complete license/asset notice review and upgrades/uninstall
  that retain saves. Establish packaging before the final narrative pass, then refresh it with the
  finished audio assets. Re-run package advisories when preparing a release.
- [ ] **M6: Training, voice/story/audio last.** After photographed save-and-rebuild works, implement
  Standard/Training/Story presentation. Training uses the same story with effects and ambience off;
  speech follows Voice/Visual/Both. Add local speech/recorded fallback, original story/sound assets,
  public-event filtering, volume/interruption controls, and mode persistence. Verify zero effects
  in Training and identical game state across modes. This is unrelated to developer ML training.
- [ ] Run the full DESIGN §22 physical, crash/power-loss, privacy lifecycle, camera, CPU/GPU,
  story, iOS/Android, network-loss, and clean-machine offline acceptance matrix. Automated view-model
  tests and simulated games are not substitutes for those checks.

## Verification commands

```powershell
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln --no-restore
dotnet test GoldenTicket.sln --no-restore
node --test tests/GoldenTicket.ConnectivitySpike.Tests/shell.test.cjs
dotnet run --project tools/GoldenTicket.Simulator --no-build --no-restore -- verify-data
dotnet run --project tools/GoldenTicket.Simulator --no-build --no-restore -- simulate --games 25 --seats 4 --seed 400
dotnet list GoldenTicket.sln package --vulnerable --include-transitive --no-restore
```

Persistence tests require Windows DPAPI under a loaded user profile. Do not replace encryption with
plaintext or skip those tests to make a restricted execution context report success.
