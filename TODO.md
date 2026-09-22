# GoldenTicket implementation completion

Updated September 12, 2026 after the [implementation audit](docs/AUDIT-2026-09-12.md).
The complete requirements remain in [DESIGN.md](DESIGN.md). This is a partial manual desktop
implementation with a game companion and camera/reference-photo tools, not a completed automatic
camera-assisted product. See [September 12 progress and morning checks](docs/IMPLEMENTATION-2026-09-12.md).

## Architecture (September 21, 2026)

See the implemented [architecture and tradeoffs](docs/architecture.md).

- [x] Protect referee collections with genuine read-only views and mutation regressions.
- [x] Align in-memory/SQLite store contracts and share logical timing-snapshot validation.
- [x] Stage timing transitions until storage acknowledges a command; preserve clock controls during I/O.
- [x] Inject a coherent camera-capture lifecycle and replace private-field capture test fixtures.
- [x] Separate portable core tests from Windows integration; guard project dependencies in tests.
- [x] Test shipped companion QR/networking code and document contributor boundaries.
- [ ] Confirm the new portable-core job on Linux CI after these changes are pushed.
- [ ] Extract further physical-board workflows from the desktop orchestration when extending them,
  preserving privacy, operation/epoch gates and existing acceptance checks.

## Game screen (implemented September 14, 2026)

See the
[game and technical layers](DESIGN.md#411-game-and-technical-layers).

- [x] Add a player-facing game layer that covers the entire application content area, including
  the current technical navigation. Keep the existing technical interface available underneath.
- [x] Slide the game layer out of view to reveal the technical interface with Shift+Escape.
  Plain Escape opens the save/quit dialog during play without clearing an unresolved solo opening
  choice. Ignore held-key repeats.
- [x] Add a **Return to game** button at the top of the technical interface. Slide the game
  layer back over 100% of the content area, preserving the active game and technical tool state.
- [x] Add snowy-twilight launch art, Start/Reload choices, and five portraits with grayscale faces
  and scenery but colored jackets. They alternate female, male, female, male, female and cycle
  through human, computer and unselected with separate role numbering. Keyboard and mouse input
  support choosing 2–5 seats, including all-human and all-computer setups. The portraits
  and their robot matches use red, blue, green, black and yellow coats in screen order; physical
  train colors follow character identity even when choices are skipped. The roster has an outlined
  hover-filled back arrow, an instruction below the portraits, and a transient SET-UP BOARD hover fill.
  SET-UP BOARD opens a separate camera setup screen that hides the roster, starts the shared preview when
  available, and offers retry, cancel and final PLAY controls. Local ML repeatedly checks the live
  image and marks all four accepted outer board corners with small white plus signs. The piece model
  checks all four board rotations, blocks PLAY when it sees a train, and verifies one scoring marker
  for every selected color near printed 1. Missing colors and trains appear as actionable notices;
  the notice disappears when the camera, train, and marker checks are ready.
  Camera confirmation uses measured train-space centers for all 100 classic-US routes; each
  requested space receives its own pulsing yellow cue. Live overhead-camera acceptance remains open.
- [x] Remember window size, maximized state and full-screen preference across launches, preserving
  normal dimensions through maximize, minimize and full-screen transitions.
- [x] After PLAY, show the accepted live board crop in a uniformly scaled game table. Position
  2–5 player portraits and their public train/card/destination counts on its left and right sides,
  with face-down stacks and a latest-public-action line per seat. Alternate seats left and right;
  five players occupy three places on the left and two on the right. Center the draw piles and
  five-card market along the bottom after opening setup for all player counts. In solo play,
  T and D stacks reveal compact private card previews only when clicked; ordinary turns stay on
  the game table. Preserve the panel above the board as the human guidance area for the current
  phase, acting seat and next instruction throughout the game.
- [x] Group the solo train-card preview by color, including locomotives, with a top-left quantity
  badge for duplicates so large hands remain easy to count.
- [x] Replace the technical final-score list with themed portrait standings over the live board,
  horizontal player navigation, full scoring details and scrollable longest-route trails.
  Keep player cards equal in height, including the full-text shared results image.
- [x] Record full-turn time per player, including physical placement and scoring-marker movement,
  with total/average time shown only in the final standings. Exclude menus, technical tools,
  inactivity, sleep and reload checks; preserve timing across saves without inventing old history.
  Partially recorded turns do not contribute to the average.
- [x] Show a separate total game timer beside the turn number. Count continuously while a match is
  open, including menus, focus loss, technical screens, reconciliation and the final scoring-marker
  move. Stop on departure, shutdown or completion; restore the saved total without counting closed-app
  time. Seed older saves from recorded turn times, or zero when no timing exists; past pauses are unknown.
- [x] Validate turn timing, save compatibility, rewinding and physical-flow attribution with 194
  related regression tests. Offscreen WPF checks cover two and five result panels at 1280×800
  and 1920×1200, including player navigation and full route-text scrolling.
- [x] Name identifiable unexpected trains by route, color and count. Block solo card draws while
  a placement warning is unresolved; require fresh whole-board verification after removal.
- [x] Mark blocking train detections directly on the live map with yellow spheres during reload,
  card, placement and save checks, including detections without a named route. Keep check ownership
  separate and clear correction cues on the first fresh matching inventory. Verify with synthetic
  camera and scaled WPF checks; a live-camera walkthrough of these correction cues remains to run.
- [x] Show those train details in the Save Game dialog during checking and after a timeout,
  including the post-photo check. Show the exact failed camera frame with numbered detection
  boxes, colors, confidence and route/board location, including unmatched or duplicate boxes.
  Record positions in the local board-decision log. Clear temporary warnings on a fresh match,
  including after a timeout; name unpaid proposals and direct the player back to payment.
- [x] Require post-click whole-board verification for local human card actions, including the
  second train card and destination selection. Preserve the human's turn and offer payment for
  a payable board-first route instead of consuming a draw; reject stale/in-flight camera results.
- [x] Confirm a human's board-first route and whole-board inventory before showing payment.
  Keep the proposal and chosen cards stable through camera changes. Accept legal payment into
  the pending claim, then require fresh post-payment whole-board verification before committing
  it and completing the scoring-marker step. Allow explicit cancellation without spending cards.
- [x] Retain committed route colors during ordinary gameplay while checking fresh occupancy,
  distinct train detections, positions and extras. Keep new-route, save and reload color checks
  strict. Log card-action board failures with the route and nearby public candidate details.
- [ ] Re-register the live board crop automatically after a saved game is reloaded or the camera
  restarts; for now, use the technical Camera screen to register it again.
- [x] Measure all 309 printed train spaces on the 100 classic-US routes and show one pulsing
  yellow placement cue per requested train. Verify separate trains of the correct player color
  in two fresh upright camera observations; after commit show “Thank you” for three seconds,
  then request and verify the new printed scoring-marker position before continuing the AI turn.
- [ ] Test each route, especially adjacent parallel lanes and curved six-space routes, under
  live overhead-camera conditions. Record false acceptances, abstentions and detection latency;
  synthetic fixtures and a handful of photos do not establish physical-camera accuracy.
- [x] Refine reloaded board crops against their saved photo to correct small corner errors before
  assigning trains to parallel lanes. Three captured frames replayed with both failing Duluth–Omaha
  crops now verify all 20 saved trains; the adjacent empty lane remains rejected. See the
  [September 19 recognition check](docs/evidence/reload-alignment-2026-09-19/validation.md).
- [x] Anchor game-table crops to the fixed classic-US artwork coordinates, rather than inheriting
  a saved photo's crop bias. Three live Los Angeles–San Francisco frames recognize all three blue
  trains on lane B and reject lane A after alignment; keep broader physical-camera validation
  open. See the [canonical alignment check](docs/evidence/canonical-alignment-2026-09-19/validation.md).
- [x] Recover shifted corner estimates after refocusing within the existing correction bounds,
  and retain the earlier registration when fresh artwork confirms it fits better.
- [x] Retry a single weak corner on a padded board region to reduce interference from
  spare pieces beside the board. Require four confident, consistent corners and classic-US artwork
  agreement; retain frame freshness and full-camera geometry checks. The proposal floor covers
  the observed 0.31–0.40 bottom-left readings without lowering the final acceptance threshold.
- [x] Treat neutral train highlights as reduced color support, rather than a competing player
  color. The supplied Calgary–Helena screenshot reproduces one rejected black train despite
  all four detections being within their spaces. Preserve the total-support and competing-color
  thresholds; broader live-camera validation remains open.
- [x] Recover neutral-background color uncertainty on angled trains with guarded sampling
  inside their image-fitted bodies. Duluth–Winnipeg logs show confident detections with intermittent
  unknown colors; its screenshot has black readings at the support cutoff. Keep the same color
  support and competing-color thresholds, require agreement with the original leading color,
  and retain original route geometry. Live-camera validation remains open.
- [ ] Persist the physical scoring-marker move gate across app restart; resume currently requires
  board reconciliation, but it does not restore the in-memory post-claim marker instruction.
- [ ] Validate score-piece color and printed-1 acceptance with the real overhead camera for all
  five colors and rotated board orientations; synthetic tests do not establish live accuracy.
- [ ] Recognize the board's printed score-track orientation independently of marker placement;
  the current four-rotation proximity check can mistake a marker cluster at another corner for 1.
- [x] The affected corner and score-piece tests pass (72 focused cases). Synthetic WPF checks
  cover 88 render cases, selection input, repeated transitions, resizing, privacy hiding and
  shared game/camera ownership, with no binding warnings.
- [x] The full automated suite passed under the signed-in Windows user profile (719 cases).
  Game-save storage tests also passed in a restricted context without DPAPI.
- [ ] Perform a hands-on desktop check of focus, maximization, animation/reduced-motion settings
  and the live camera while switching layers on the target Windows machine.

## Still missing: feature checklist

These six features remain unfinished. The milestone tasks below define their implementation and
validation requirements; mark each feature complete only after those checks pass.

- [ ] **Camera tracking and recovery** — board recognition, move verification, and automatic recovery after camera movement (M3–M5).
- [ ] **Phone/tablet companion** — recommended browser Quick play and PRACTICAL laptop sharing,
  with private pass-and-hide on Android and iOS/iPadOS and laptop-hosted data
  and QR-assisted initial synchronization entirely within the LAN (M0/M2). No iPhone or iPad is
  available, so **iOS cannot be claimed as supported at release**; see
  [companion device evidence](docs/companion-device-evidence.md) for the honest wording and the
  borrowed-device checklist.
- [ ] **CPU/GPU inference selection** — actual Auto/CPU/GPU preprocessing, preference persistence,
  fallback and effective-backend status are implemented. Packaged-model execution and the full
  hardware/provider acceptance matrix remain M4/M5; preprocessing is not ML inference.
- [ ] **Photographed save and rebuild** — the state-only half is implemented (M2): named checkpoints,
  the `PreparingPackAway`/`PackedAway`/`Rebuilding` lifecycle, commit-then-readback validation,
  route-list guided reconstruction and exactly-once resume. Required operator-attested
  reference photos use plaintext format v2 with a SHA-256 checksum, immutable checkpoint binding
  and readback verification. Still missing:
  the board diagram, machine-verified photograph and full evidence lifecycle.
  The in-game Escape save now checks route positions and player colors from live frames, stores an
  observed color inventory and any authorized pending placement's per-slot mask beside a fresh
  board reference photo, and returns to the main menu
  only after both the digital checkpoint and matching photo validate. This does not yet make the
  photograph itself a machine-verified checkpoint. Missing or invalid required photos block reload
  with an error; automatic journal recovery without a user checkpoint remains a separate path.
- [ ] **Offline installer packaging** — self-contained Windows x64 distribution with required runtimes and assets included (M7).
- [ ] **Training mode, voice, story, and audio: last feature pass** — Training follows Story without effects/ambience; narration follows visual/voice/both settings. Complete photo save-and-rebuild and packaging foundation first (M6).

The user's requested order puts voice/story/audio last. Keep essential visual guidance available
earlier; perform final release checks and package refresh after the narrative features are finished.

Development now uses Visual Studio and GitHub [Windows CI](.github/workflows/windows-ci.yml).
Do not generate a personal app ZIP as a routine handoff. CI builds/tests and keeps diagnostic
evidence only; future installer/distribution work remains a separate requirement. Installed
Quick play must operate on the LAN without Internet access; PRACTICAL needs no network; see [the build/runtime contract](docs/build-and-ci.md).

## Audit fixes implemented

- [x] Make **Quick play** the recommended default: direct HTTP game QR, no app or certificate
  installation, with the same card/ticket actions and handoff controls.
- [x] Replace the second connection option with **PRACTICAL**: private laptop turns while other
  players look away, without phone-host or network setup. Remove the former PWA, certificate,
  service-worker and connectivity-experiment implementation.
- [ ] Validate Quick play on real Android/iOS devices: QR scanning, joining, handoff, reload,
  focus loss, WAN-disconnected play and image saving. Verify PRACTICAL through a complete
  multi-human laptop game with networking absent. Automated tests are not physical acceptance.

- [x] Compare complete checkpoint readback data, include supply policies in new logical hashes,
  require a fresh rebuild attestation after restart, and permit safe readback retry.
- [x] Keep packed/rebuilding games behind the privacy gate, scope operator acknowledgements to the
  current decision, and continue eligible AI turns after resolving a pause or rebuild.
- [x] Bound browser network checks, clear private data on page backgrounding/disconnect, and keep the
  browser dependent on the laptop for authoritative game state.
- [x] Remove the browser's inactivity timeout and private-view expiry, preserving cards and
  destination selections while connected; keep the hand visible through ordinary focus changes
  and touches outside its controls. Hide, backgrounding, disconnect and turn changes still cover it.
- [x] Send camera-detected routes to the revealed Quick play browser for private payment, with
  stable choices, cancellation and proposal-bound authorization. Apply the laptop's board check
  to phone card draws; keep camera-free manual route selection as a technical fallback.
- [x] Mirror current public placement, correction and score-marker instructions on the tablet
  while preserving the laptop's guidance and existing private-card controls.
- [x] Replace the unavailable browser reveal button during computer turns with the upright board
  preview and the laptop's gold placement dots. Signal new frames through SSE at a bounded rate,
  mirror correction subsets, and restore human reveal after placement/scoring completes.
- [x] Open a live map of all held destinations when a browser ticket is tapped, with the same
  dashed connections and city rings as single-player. Animate the ticket/map swap and action
  controls, preserve the map through the first draw, and clear private overlays at handoff.
- [x] Confine save paths and reject linked/unsupported paths before filesystem operations.
- [x] Check snapshot/journal/version integrity and prevent competing writers for supported saves.
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
- [x] **LAN-only runtime regression gates.** Validate bundled browser resources and the HTTP network boundary;
  exercise phone gameplay with Internet-unavailable browser state and block external browser origins.
  Physical WAN-disconnected phone acceptance remains outstanding.
- [x] **Embedded companion first slice.** HTTP Quick play, laptop approval/CSRF/private grants,
  shared human card/ticket actions and privacy/reconnect handling are integrated into Windows.
  Actual-device gates and remaining protocol requirements below are open.

- [x] **Single-human laptop play.** One human's card and ticket choices open directly on the laptop,
  with no **Back to table** action or plain-Escape dismissal during the unresolved opening choice.
  Later turns keep the public table visible; clicking T or D shows mini cards in place, while
  **Shift+Escape** exposes the technical private controls for taking a turn. There is no
  **Connect phone** step. The active or resumed roster determines this behavior. With multiple
  humans, choose Quick play for a shared phone or PRACTICAL for private laptop handoffs. Phone
  hosting starts only after an explicit choice of Private LAN connection. Board verification and
  AI secrecy remain in force.
- [x] **Multi-human controls on the table.** Present Quick play first and PRACTICAL second.
  Quick play shows an actual connection QR/code after explicit host startup; PRACTICAL stays on
  the laptop and has no network setup requirement.
- [ ] **Multi-human acceptance.** Verify Quick play QR joining, pairing, private cards and handoff
  on a real shared phone, and PRACTICAL laptop handoffs without networking.
- [x] **Final standings to the shared phone.** Multi-human games can send the board and standings
  image to an approved Quick play browser for preview and saving. Exports contain only public
  final results; session/version and controller checks apply.
- [ ] **Standings saving on real phones.** Verify Android/iOS PNG downloads, readability and
  subsequent sharing through Files/Photos, including return to the game.

- [x] **Camera and photo foundation.** Windows video-only capture, selectable formats, manual
  four-corner crop and conservative scene-reference checks; optional plaintext, checksummed,
  operator-attested checkpoint photos with integrity/readback and stale-capture protection.
  The Escape save verifies live train positions and colors against claimed routes on fresh frames;
  the photo itself is still an operator-attested reference, not a machine-verified checkpoint.
- [x] **Manual board-photo export.** A valid live crop can be exported to PNG without a scene
  reference, including while the scene has changed. Fresh-frame and crop/camera identity checks
  remain; checkpoint-photo capture retains its separate reference checks.
- [x] **Native-resolution preference and truthful 4K processing.** Default to exact native
  1920 × 1080 near 30 fps, allow 720p fallback with a poor-lighting warning, and reject modes
  below 1280 × 720. Show native 4K capture only when the selected webcam advertises a usable
  3840 × 2160 format. Display the actual delivered
  dimensions separately from processing output. Shared-read-only inspection confirms the current
  Pixel UVC connection advertises 1080p at most. Physical native-4K camera acceptance remains open.
- [ ] **720p webcam performance and gameplay acceptance.** Test a real webcam delivering
  1280 × 720 with the whole board visible, under good and poor lighting. Measure board-corner,
  train/color and scoring-marker detection accuracy, missed/false detections, response time and
  preview responsiveness; compare with 1080p under the same conditions. Exercise route claims,
  card draws, save/reload and camera reconnect, and verify the 720p warning stays visible.
  Record results and practical limits before claiming reliable 720p gameplay.
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
- [x] Recognize markers sharing a corner diagonally, as in the black/red pair on 50.
  Require a clearly read corner anchor, keep the fallback within a small inward region,
  preserve adjacent-cell readings, and retain fresh-frame confirmation. Live validation remains open.
- [x] Retry up to two weak train proposals with centered, same-frame model views; require
  independently strong matching detections and retain route/color/freshness checks. Log retry counts.
- [x] Correct Raleigh–Charleston's bent two-space geometry using the empty-board reference.
  Turn-114 diagnostics recognized both black pieces above 95% confidence; the old second-space
  mapping excluded one. Keep the existing confidence/color thresholds and sideways tolerance;
  cover both-piece confirmation, partial placement, neighboring pieces and off-route rejection.
  All 102 focused checks pass. Both supplied-screenshot crops confirm 2/2, including 18 small
  position-shift cases; omitting either detection remains incomplete, and the empty reference
  produces no train detections. These are offline checks; live play after rebuilding remains unverified.
- [ ] Capture an exact analyzed frame of the Chicago–Duluth middle-train miss and validate
  live recovery. Its screenshot detects all three after resampling; frames with no usable
  proposal are not recovered by the bounded weak-proposal retry.
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
  Technical capture requires an operator board check; the game-layer Save Game captures and
  validates its required photo before completion. A logical checkpoint alone is not a completed save.
- [x] **Obvious saved-match selection.** Show a checkmark, automatically select a sole save, keep
  selection across refreshes, and enable Resume only with a selected match. Display selection
  guidance and restore errors in the saved-matches panel; retain the board reconciliation gate.
- [x] **Saved-match names.** Show the entered name first, keep the latest committed name after
  resuming, and use readable status text.
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
- [x] Replace companion snapshot polling with event-driven SSE over the existing LAN HTTP connection.
  Push public game/camera/instruction changes, coalesce bursts, keep the connection alive without
  rereading unchanged game state, and reconnect covered with a fresh snapshot rather than replaying actions.
- [ ] Persist a protected approved-device registry, implement reconnect/lease recovery without pairing after every reload,
  and add hold-to-peek plus full accessibility/device acceptance. The plain-JS client remains a
  bundled implementation deviation from the planned TypeScript build.
- [x] **M0/M2: guided connection setup first slice.** Detect the Windows network profile, explain a
  Public-profile block, and show a scoped firewall command and the relevant Windows settings.
  Applying OS changes stays explicit. PRACTICAL does not need these network steps.

- [ ] **M0/M1: data and platform evidence.** Review every city connection, lane, color, train length,
  and all 30 tickets against the supported physical edition. Record reviewer/provenance. Supply
  measured route-cell geometry and board landmarks; retain the current unaudited status until done.
- [x] **Rare supply rules resolved.** Each paused position now offers one reviewed, versioned
  continuation the operator accepts explicitly; the acceptance is journalled with its policy version
  and holds for the match. The pass policy terminates: a full round of passes goes to final scoring.
  Documented in [docs/rules-policies.md](docs/rules-policies.md). Remaining: confirm each policy
  against an official clarification where one exists, rather than shipping them as house policy.
- [x] **Retire the connectivity experiment.** Remove its certificate/PWA tool and tests after
  moving production joining and request-boundary coverage to the embedded HTTP companion.
  Earlier dated reports are historical, not current setup requirements.
- [ ] **M0/M2: companion completion.** Extend HTTP hosting and the shared browser client with
  remaining update/recovery/registry and accessibility requirements. Keep pairing, grants, CSRF,
  idempotent commands and QR behaviour under regression tests. Prove initial synchronization,
  private-hand controls, image saving and reconnect on real devices with the WAN disconnected.
  Android is available throughout; iOS/iPadOS may be tested during a borrowed-device session.
  Use [the device checklist](docs/companion-device-evidence.md) and record limitations honestly.

- [ ] **M3: camera completion.** Validate the implemented WinRT acquisition, bounded frame ownership,
  camera choice, native-format fallback, preview, CPU/GPU preprocessing and manual crop against
  the real board. Evaluate the experimental empty-board detector. Add printable markers, board
  landmarks, automatic calibration, detailed quality gates and recording/replay.
- [ ] **M4: verification.** The board-first solo-human and Quick play paths confirm the new route and whole-board
  inventory before payment, preserve the payment choices, then verify fresh post-payment captures
  before completing the claim and handing off the turn. Complete the durable preauthorization substate, broader whole-board
  recognition, occlusion/unknown foreground rejection, jog/reconnect recovery, stale-epoch
  rejection, wake gesture, and explicit mode-change reconciliation. Persist the score-marker
  move obligation with the claim so an app restart cannot skip the physical marker check.
- [ ] **M4/M5: model inference.** Build on the implemented preprocessing preference/status flow.
  For any required learned recognizer, default to Auto: detect adapters at launch, validate GPU execution with
  the packaged model, and fall back to CPU on absence, incompatibility, timeout, or failure. Retain
  explicit CPU/GPU preferences and distinguish them from the effective backend. Display a chip/CPU
  icon or GPU text with lightning around it, with adapter/fallback details. Package native runtimes
  and test safe switching. Evaluate a baseline first; if needed train on developer data, validate
  held-out physical sets, and ship an offline model. Users never train or download a model.
- [x] **M2: state-only pack away and rebuild.** Named checkpoints, durable packed/rebuild lifecycles,
  frozen source state, commit-then-readback validation of the logical checkpoint, suspended
  partial operations preserved, route-list guided reconstruction with whole-target attestation,
  and exactly-once resume. Verified by `PackAwayTests` and `PackAwayDurabilityTests`. Completed
  user saves additionally require a validated matching board photo; logical verification alone
  does not authorize clearing the board.
- [x] **Desktop exit confirmation.** An unfinished match prompts before exit with No selected;
  wording distinguishes automatic digital recovery from a completed pack-away save with its
  validated board photo. A missing or invalid photo must not authorize clearing the board.
  Pending writes block closing; storage faults show uncertainty. Cancel retains usable tools and
  covers private hands. Confirm preserves deferred cleanup and final close without WPF reentry.
- [x] **Completed-save and menu gates.** Treat a user save as complete only when its digital
  checkpoint and matching board image validate. Recheck durable attachments after restart and
  before rollback; Quit to Menu retains the latest completed earlier save instead of promoting
  a failed photo capture. Reload reports missing or corrupt required images before gameplay.
  Escape cannot open the save/quit dialog while an action or computer work is running.
- [x] **Save and resume a computer's unfinished placement.** Save while waiting for trains, with
  zero, some, or all pending slots occupied. Keep the active seat, turn, phase, pending operation,
  and reserved cards in the existing journal; store physical progress separately from confirmed
  routes in the required photo sidecar. Check the exact pending mask before and after capture and
  during rebuild. After board verification, announce whose turn resumes and wait for the themed
  **OK** button before computer work or player actions. Saving still waits for active writes/work,
  scoring-marker moves, and cancelled-placement restoration. Persistence/photo regressions cover
  metadata validation and a computer turn resumed from SQLite without spending its payment twice.
- [ ] **M2/M4: persistence and pack away, remaining.** Add the geometry-based rebuild diagram, complete snapshots,
  full evidence pinning and machine-verified board photographs,
  photographed cancellation/restoration progress and current-checkpoint success receipts for the
  companion. Add correction branches and optional encrypted portable export/import.
- [ ] Complete non-audio theme/high-contrast/screen-reader/keyboard work and adjustable privacy timing
  before the final narrative feature pass.
- [ ] Implement and evaluate the specified Challenging AI sampled lookahead; current difficulty
  choices tune a heuristic. Keep opponent hands/deck state inaccessible and report strength honestly.
- [x] Add per-computer Standard/Aggressive badges to character selection, with a spinning toggle
  and saved style choices. Aggressive uses public human networks for opportunistic blocks and
  continuous-route interference.
- [x] Give computer play a shared destination/card plan, train and tempo reserves, compatible ticket
  selection, and completion priority over speculative sabotage. Add regressions and a reproducible
  frozen-policy comparison that exercises public human-network targeting across all seat positions.
  Final screening and held-out cohorts completed 640 games with no fallback or invalid actions;
  [recorded results](docs/ai-strategy-evidence.md) show stronger scores and ticket completion.
- [ ] Playtest Aggressive against humans across seat counts and seeds, measuring disruption,
  game completion and decision time before making comparative difficulty claims.
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
node --test tests/GoldenTicket.Domain.Tests/CompanionHostClient.test.cjs
dotnet run --project tools/GoldenTicket.Simulator --no-build --no-restore -- verify-data
dotnet run --project tools/GoldenTicket.Simulator --no-build --no-restore -- simulate --games 25 --seats 4 --seed 400
dotnet list GoldenTicket.sln package --vulnerable --include-transitive --no-restore
```

Game-save and photo persistence tests exercise plaintext payloads and integrity checks without
DPAPI. Companion transport checks use local HTTP and need no certificate or OS trust setup.
Former encrypted saved matches are unsupported and may be deleted.
