# GoldenTicket

A Windows companion for a physical game of **Ticket to Ride** (classic English North America
edition). The board and the plastic trains stay on the table; train cards, destination tickets, the
decks and the market are digital for every seat, and the application acts as referee and as the
computer opponents.

`DESIGN.md` is the full design. This README says what is **built** and what is **not**.

The September 12, 2026 [implementation audit](docs/AUDIT-2026-09-12.md) found that the full design is
**not implemented**. It reviews the new pack-away, supply-policy and connectivity work, documents
the corrected bugs and security issues, and separates automated evidence from remaining device tests.
[TODO.md](TODO.md) tracks the work needed to complete the product.

The subsequent [September 12 implementation update](docs/IMPLEMENTATION-2026-09-12.md) adds the
game phone companion, camera tools, and board reference photos. Use its morning
acceptance checklist and [local phone setup](docs/phone-setup.md). Real-device acceptance remains
in progress; automatic claim confirmation now has measured slots for all 100 classic-US routes.

The latest [camera processing update](docs/camera-processing.md) adds native 4K preference with
source-resolution reporting, actual CPU/GPU image enhancement, and the original comparison baseline.
Those outlines are visual candidates; the camera-processing report predates the route-slot
verification described below. Locked restore/build, **546 automated tests** and **50 synthetic WPF render cases** passed;
the render pass reported no binding warnings/errors. A supplied board-photo pair produced 15 train
and 5 marker candidates, including the enhanced paths; the unchanged empty-board comparison
produced none. These bounded checks do not complete physical-camera acceptance. See the
[validation record](docs/evidence/camera-processing-2026-09-12/validation.md).

For the all-route placement update, 26 focused verifier tests passed, including every route and
both directions of each parallel pair. The WPF smoke pass rendered 97 cases without binding
warnings. On the supplied played-board photo, all 43 model-detected trains fell within measured
train spaces and their sampled colors classified; the supplied empty board yielded no train
detections. These still-image and synthetic checks do not measure live-camera accuracy.

The September 13 [ML preview experiment](docs/piece-recognition-ml.md) replaces live Piece outlines
with a locally trained two-class detector. It finds trains and score markers without an empty-board
reference, using ONNX Runtime on DirectML or CPU. Reviewed runtime models and their trained weights
are committed under `assets/models/`; training photos, labels and intermediate checkpoints stay ignored.
Train outlines follow the piece angle when a local image fit is reliable; score-marker outlines
stay square. Uncertain train fits keep the original model box.
**Piece outlines → Score track** reads each detected marker's color and printed track value.
Markers beside the same row or column may share a score. Missing or uncertain readings are
shown explicitly and clear with stale outlines. Keep the upright USA board tightly cropped;
these are track positions, not inferred full-lap totals or changes to the game's scores.
**Save detection example…** records the analyzed image, predictions and a note for later review.
The [validation record](docs/evidence/ml-preview-2026-09-13/validation.md) separates measured photo
results from the live-camera and independent-session tests still needed.

The latest [model retraining](docs/evidence/ml-retrain-large-lighting-2026-09-13/validation.md) incorporates
38 reviewed photos, including crowded markers, printed-route false positives, the missed
yellow train beside Denver, parallel black trains and major lighting changes.
The local preview model matches all 1,663 labels without extras and preserves separate
parallel-train boxes. Thirteen labeled photo checks and a separate score-reading fixture pass on
CPU and DirectML. The latest saved frame was already correct before training, so it does not
verify a fix for intermittent live extras. These are training-set checks;
new capture sessions remain necessary to measure generalization. Use **Reload ML model**
in an already open preview after the local model files are updated.

The [automatic corner experiment](docs/board-corners-ml.md) adds a separate learned model that
selects the four outer board corners on camera startup. **Detect board corners** retries it;
the numbered handles include a narrow outward crop margin (about 0.25% per side),
and remain editable with the existing zoom and pan controls.

The September 14 game-screen update opens on the snowy-twilight artwork and adds the new-game,
previous-game and player-portrait flow described below. The existing technical screens remain
available through Shift+Escape. The affected corner-flow tests pass after the latest recheck change;
71 synthetic WPF render cases pass without binding warnings. The full suite still needs a rerun under
a loaded Windows user profile. Hands-on focus, maximization,
reduced-motion and live-camera switching remain to be checked on the target desktop.

## What this build does

This build implements the core game plus initial phone, camera and photo workflows from DESIGN
§23.1. Neither the physical data review nor the full milestone acceptance gates are complete:

- A classic North America rules data package with 36 cities, 100 routes, 30 destination tickets,
  and 110 train cards. Runtime loading requires the supported profile/version and a valid checksum;
  the physical data audit is still outstanding.
- Full turn structure: the two-card draw with its subphases, the face-up market with the
  three-locomotive reset and discard reshuffles, destination-ticket offers, and route claims.
- Route claims use the reserve-then-verify protocol: planning a claim reserves the payment but
  spends nothing. The classic-US board has measured centers for all 309 printed train spaces across
  100 routes. One pulsing yellow cue appears on each requested space. Two fresh camera observations
  must identify a separate train of the player's color in every requested space before one atomic
  commit spends the cards, records ownership, scores and ends the turn. Adjacent parallel lanes are
  checked separately. This geometry and automated tests still need live-camera accuracy validation.
  Each normal app launch starts a fresh local board-decision log at
  `%LOCALAPPDATA%\GoldenTicket\diagnostics\board-interactions.jsonl`. It records camera and model
  availability, detected candidate positions and confidence near the requested route, color and
  slot checks, and claim/score-marker outcomes. It contains no camera images or private cards.
  When it reaches 64 MB, the older segment moves to `board-interactions.previous.jsonl`; both
  segments are cleared on the next app launch.
- Exact final scoring, including the longest continuous route as a true maximum edge-simple trail
  with the witness trail shown.
- Heuristic computer opponents at three difficulty levels, which see only their own seat's view.
- Durable local saves: an append-only event journal in SQLite with a tamper-evident hash chain,
  plaintext local payloads, command deduplication, and restore by replay verified against a stored
  state fingerprint. Game saves are not encrypted; saves written in the former encrypted format
  are no longer supported and may be deleted before starting a new game.
- Save and pack away: the game suspends mid-turn, writes a named checkpoint, reads it back and only
  then says the pieces may be cleared away. Reopening shows the saved position route by route with
  per-seat stock guidance, takes the operator's whole-board confirmation, and resumes the exact
  suspended action once. Packing away and rebuilding provably change nothing about the game.
- During play, **Escape** opens **Save Game**, **Quit to Menu**, and **Return to Game**. Save Game
  compares every visible train position and color with the claimed routes across fresh camera
  frames, writes and reads back the digital checkpoint, captures an unprocessed board photo with
  the observed color totals, checks fresh frames again, and returns to the main menu. If any check
  fails, the game stays open so the board and camera can be corrected. Quit to Menu discards the
  current unsaved progress; a prior verified save remains available when one exists.
- Save paths are confined to valid session directories; concurrent writers, inconsistent journal
  metadata, missing snapshots, and corrupted state stop the operation. An uncertain save outcome
  requires a reload. Unreadable saves remain listed with recovery guidance.
- A WPF interface in the box-derived palette: a public table screen, an opaque privacy curtain with
  a per-seat private view, the operator's placement instruction and confirmation, and a results
  screen.
- A player-facing launch layer using the supplied snowy-twilight artwork. Choose 2–5 players from
  five portraits whose faces and scenery begin grayscale while their jackets keep their train
  colors. The portraits cycle through human, computer and unselected. You can also reopen the
  most recently updated save. Shift+Escape reveals engineering-only screens for diagnostics and
  recovery; **Return to game** slides the game layer back. Both presentations use the same match
  and camera objects.
- With one human, the three opening destinations appear over the game board. Keep all three or
  click one to drop it. A confirmed drop removes that card and its board highlight together, then
  slides the remaining **Your Cards** panel down before saving the two kept destinations. The
  public table stays visible after either opening choice; click the human's T or D stack to see
  compact card previews. The unresolved opening choice has no **Back to table** action, and plain
  Escape does not dismiss it; the exit menu covers it until **Return to Game**. Later laptop private
  controls are not yet available on the main game layer, and **Connect phone** is hidden.
  With multiple humans, the game table presents phone setup so one shared phone can be passed
  between players. Hosting still requires an explicit start on a selected Private LAN connection;
  the table shows the real connection QR only after the local host supplies an address.
- A laptop-hosted HTTPS phone PWA foundation with pairing and private card controls. One shared
  controller is paired and explicitly approved on the laptop. The complete pass-around card flow
  still needs implementation and real-device validation; the laptop verifies physical moves.
- A camera screen with Windows video-only capture, resolution selection, preview, ML-assisted four-corner
  board crop with manual selection and draggable corners, and conservative scene-reference
  change/recovery indication. Focus the preview and press **1–4**, then arrow keys, to adjust a
  corner; **Shift** makes larger steps. Invalid crops retain their handles for correction. It identifies camera
  changes and stale frames. Only fresh upright game-table detections of a calibrated route can
  authorize a claim; the technical camera preview itself cannot.
- Camera preview **zoom up to 800%** with + / − or Ctrl + mouse wheel. Drag the zoomed image
  to move around, or use **Fit** to see the whole image. ML tries to select the outer corners
  when capture starts; **Detect board corners** retries. For manual placement, choose
  **Select four board corners** beside zoom; the prompt shows which corner to click next.
  A click places the next crop corner during selection;
  dragging a numbered handle adjusts that corner. **Pan**, Space + drag and middle-button drag
  are also available. Keys **1–4** bring a crop corner into view; arrow nudges
  become finer when zoomed. Zooming leaves the crop, camera references and exported image unchanged.
- **1080p preferred · best available** is the default capture profile. It selects exact
  1920 × 1080 at the native frame rate closest to 30 fps when advertised, then falls back through
  smaller usable native modes. **4K preferred · best available** remains selectable in the Camera
  utility and permits advertised modes up to 3840 × 2160. The UI distinguishes delivered camera
  resolution from the enhanced processing size. The connected Pixel's USB webcam currently
  advertises a maximum of 1920 × 1080; a 4K preview from it is explicitly labeled upscaled.
- **Auto · prefer GPU**, **CPU only**, and **GPU · CPU fallback** processing, with a remembered
  preference and the actual backend/adapter shown. A hardware Direct3D 11 compute path validates
  its output before activation and falls back to CPU on failure. It performs bounded image
  enhancement and resizing. ML inference reports its own backend under Piece outlines; game logic
  remains on CPU.
- Experimental **Piece outlines** use the installed local ML model on the current board crop.
  Check the four selected corners; no empty-board reference is needed. White rectangles show trains and
  white squares show score markers. Camera/crop/model changes and stale results clear outlines.
  **Reload ML model** reloads the local model; CPU only selects CPU for that reload, otherwise it
  prefers a hardware GPU. Missing/invalid models leave the preview and manual play available.
- **Save detection example…** exports one local ZIP containing the exact analyzed board PNG,
  model hash, predictions and your optional review note. Predictions are marked unreviewed;
  saving an example does not automatically add it to training. Colors and route ownership are
  not inferred by this two-class model.
- Optional immutable board reference photos attached to validated saved checkpoints. New photo
  sidecars use plaintext format v2 with a SHA-256 checksum. Photos are cropped from fresh camera
  frames and checked on readback. They assist manual
  rebuilding; checkpoints retain their explicit state-only provenance.
- Explicit manual-verification opt-in, whole-board attestation for uncalibrated routes, and a board-check
  gate before restored games can resume AI or human actions. Hiding a private view invalidates late
  asynchronous results. Fault logs contain bounded error metadata rather than exception payloads.
- A headless simulator for reproducible matches and a data-audit command.

## What this build does **not** do

These are later milestones in `DESIGN.md`, and nothing here pretends they exist:

- **Automatic verification needs live-camera validation.** The model's train candidates and image
  color checks can authorize any measured classic-US route after two stable, fresh observations.
  The table says “Thank you” for three seconds, then asks for the scoring marker to move and waits
  for two fresh readings of its new printed position before play continues. Full-board
  reconciliation, gesture handling, and measured false-acceptance/abstention rates remain unfinished.
- **Phone acceptance is incomplete.** The embedded companion is functional and tested with
  automated HTTPS/browser cases, but Android certificate/install/offline acceptance and all Apple
  device acceptance remain outstanding. This slice uses two-second snapshot polling and fresh
  laptop pairing after page reload; WSS/event-cursor recovery and durable controller registration
  remain design gaps. See [implementation details](docs/IMPLEMENTATION-2026-09-12.md).
- **No machine-verified photo checkpoint.** The Escape save checks live train positions and colors
  against claimed routes before and after its board photo. The image remains an operator-attested
  reference with a checksum, checkpoint association and readback checks. Checkpoints remain
  `LogicalStateOnly`; partial placement masks and automatic whole-board reconciliation are still M4.
- **No story mode, narration or sound.** That is M6.
- **No installer.** M7.
- **No complete semantic board geometry.** The separate classic-US slot map measures all 309 train
  spaces for placement cues and route-specific checking, but the broader board registration,
  whole-board comparison and reconstruction geometry in DESIGN §6.3 remain unfinished. The rules
  data package itself carries no placeholder coordinates.

## Board data is not audited yet

`data/classic-us/classic-us-v1.json` was transcribed from the classic English rules profile. It has
**not** been checked against a physical board, and the application says so on its setup screen.
DESIGN §6.3 requires a reviewer to verify every connection, printed colour, lane and ticket value
before release. Until then, treat a surprising route or ticket as a data bug.

```bash
dotnet run --project tools/GoldenTicket.Simulator -- verify-data
```

## Requirements

- Windows 11 (audit build/test host: OS build 26200; the declared compatibility floor of 22000 has
  not been tested by this audit)
- .NET 10 SDK (pinned to 10.0.401 in `global.json`)

The preferred development workflow is **Visual Studio**: open `GoldenTicket.sln`, select
`GoldenTicket.Desktop` as the startup project, and build/run. The .NET SDK supplies the development
runtimes. A framework-dependent installation on another laptop needs both the .NET 10 Windows
Desktop and ASP.NET Core runtimes installed; a future installer must supply these prerequisites
or bundle them. Once installation is complete, the application does not download runtime components.

[Windows CI](.github/workflows/windows-ci.yml) builds and tests the solution on pushes to `main`,
pull requests and manual runs. It retains test reports and synthetic screenshots, with no app ZIP
or release publication. See [Visual Studio, CI and LAN operation](docs/build-and-ci.md).

The earlier [portable packaging tool](docs/offline-package.md) and its [validation record](docs/evidence/offline-package-2026-09-12/README.md)
remain available for future distribution work; generating a ZIP is not part of the normal workflow.

No paid IDE, account, or internet connection is needed to run the application. Building it the first
time downloads NuGet packages.

## Installed operation stays on the LAN

The Windows laptop is the game server. The phone connects directly to its selected trusted Private
LAN address over local HTTPS. Cards, saves, photos, PWA scripts/styles/icons, pairing and game actions
stay local. GoldenTicket has no cloud login, telemetry, Internet connectivity gate, remote font/CDN,
cloud AI, updater or Internet API dependency. The phone needs the laptop and LAN to remain available;
the router's Internet/WAN connection may be disconnected. Laptop-only play also works without a LAN.

CI and developer restores use the Internet to obtain build tools/dependencies. They are separate
from installed gameplay. Automated browser tests block non-laptop origins while exercising the game;
real phone certificate, home-screen installation and WAN-disconnected device acceptance remain open.

## Build, test, run

```bash
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln --no-restore
```

```bash
dotnet test GoldenTicket.sln
```

The connectivity scripts also have behavioral regression tests using Node's built-in runner
(validated with Node 24.19.0; no npm packages). Node is a development test tool, not an app runtime:

```bash
node --test tests/GoldenTicket.ConnectivitySpike.Tests/shell.test.cjs
node --test tests/GoldenTicket.Domain.Tests/CompanionHostClient.test.cjs
```

```bash
dotnet run --project src/GoldenTicket.Desktop
```

Open `GoldenTicket.sln` in a Visual Studio version that supports the pinned .NET 10 SDK and set
**GoldenTicket.Desktop** as the startup project.

Each project has a `packages.lock.json` covering transitive dependencies. Use locked restore for
verification; intentionally update the locks when changing dependencies. The seven application
projects also have `packages.win-x64.lock.json` for the self-contained package's runtime graph.
Only an explicit `-p:GoldenTicketOfflinePackage=true` selects those locks; the
[packaging workflow](docs/offline-package.md) sets it for both restore and publish and documents
how to regenerate and verify both lock sets without changing normal development locks.
Game-save and photo persistence tests do not need DPAPI. Companion TLS private keys still use
DPAPI, so tests of that separate certificate path can fail in a restricted or impersonated context.

### Headless matches

Reproducible all-computer games, checking the invariants and replay equality after each one:

```bash
dotnet run --project tools/GoldenTicket.Simulator -- simulate --games 20 --seats 4 --verbose
```

## Playing a match

The app opens on the game screen. Choose **Start a new game** with ↑/↓ and Enter, or hover and
click. When saved matches exist, **Reload the previous game** opens the most recently updated one.
The **Settings** button beneath these choices opens display mode, camera quality, processor,
and preview options. **OK** applies a changed processor choice and returns to the game menu.
Display mode defaults to a resizable window; **Full screen** hides the title bar,
and switching back restores the window's size and position. Camera controls use the same configuration
as the utility screens.
The main screen currently reloads the most recent save. Selecting a different save is available
only on the engineering-only **Saved matches** screen. A single saved match is checked
automatically there. Check the physical board before play resumes. Named saves show their name,
date, turn, status and players; **Packed away** is a status, not the save's name.

1. Put the board and the plastic trains on the table. **Leave the physical cards and destination
   tickets in the box** — the application deals and holds every card, for every seat.
2. On **Choose players**, all five portraits start unselected, with grayscale faces and scenery
   but colored jackets. They alternate female, male, female, male, female. Use arrow keys and
   Enter, or point and click, to cycle each face through color human, matched robot and unselected.
   The first selected human is **Player 1** and the first selected computer is **Computer 1**;
   each type is numbered separately. The characters' coats and physical train pieces match in
   red, blue, green, black and yellow screen order, even if some characters are skipped. Choose 2–5
   characters, then select **SET-UP BOARD** to open **Before we begin**. Position the board and
   camera so the entire board is visible in the shared live preview. The local ML model checks
   the current image and places small white plus signs on all four outer corners. Place each chosen
   color's physical scoring marker on or near the printed **1**. The local piece model checks the
   board in each of its four possible orientations for trains and scoring markers. **PLAY!** becomes
   available only while all four corners are fresh, no trains are detected, and every chosen color's
   marker is detected near the **1** area. A missing color is named in the on-board notice; the notice
   disappears when setup is ready. **CANCEL** returns to
   character selection. The camera starts automatically when available; use **Retry camera** if
   it does not start. This screen requires both the deployed corner and piece models; without either,
   **PLAY!** stays disabled. Human-only and computer-only games are allowed; a person places computer trains. The
   technical setup screen offers seat names, colours and AI difficulty.
3. **PLAY!** opens the game table with a live, cropped board from the accepted four camera corners.
   The table keeps an upright board reference and checks the live crop during play. If the board
   moves or turns, it reacquires the corners and compares all four orientations before resuming the
   preview, so Miami remains at the lower right. While the view cannot be verified, the board and
   automatic piece readings pause rather than showing an uncertain orientation. A resumed saved
   game uses its saved upright board photo as the reference.
   A persistent panel above the board identifies the current phase, the acting player, and the
   instruction humans should follow next.
   In a one-human game's opening setup, the board moves up beneath the guidance panel, while a
   compact **Your Cards** row below it shows all three destinations at the same time. Thick rings
   mark their endpoint cities on the live board, and a line connects each card's city pair.
   Confirming a drop removes its card, line, and rings together, while an endpoint shared with
   another kept destination stays marked. The remaining **Your Cards** panel then slides down and
   off-screen before the choice is saved. The draw piles and face-up train cards stay hidden until
   the opening choice is complete. Select **KEEP ALL THREE** or click one card and confirm its drop;
   at least two must be kept. There is no **Back to table** action for this choice, and plain Escape
   leaves it open. After either choice, the public board stays visible. In solo play, click the T or D
   stack in the human's tile on that human's turn to slide down small train cards or destinations
   without leaving the table. The stacks cannot be opened during the computer's turn.
   Opening the destination stack also circles the endpoint cities of the held destinations on the
   live board; closing it removes those circles. This board overlay is for one-human games only.
   On the solo human's turn, click the T draw pile or a face-up train card to take a train card.
   A second draw can come from the T pile or an eligible face-up card; a face-up locomotive cannot
   be the second card. Click the D pile to draw destinations, then choose at least one from the
   compact row below the board. Their city rings and connecting lines stay visible while choosing.
   The draw controls are disabled during the computer's turn.
   The computer chooses its own destinations by value and estimated route cost and may keep all three.
   The chosen players sit around it with their matching portraits, train colors, remaining trains,
   and face-down card and destination stacks showing public counts. The first two face each other;
   with five players, two tiles flank each side of the board and the fifth sits centered below it
   during normal play (the fifth moves to the free corner during solo opening selection).
   Each tile aligns the player name, train-card label, and destination label on one row, with their
   score and card counts directly below. Remaining trains appear as text above the card stacks,
   and the latest public action is centered along the tile's bottom edge. During opening selection, each
   unresolved three-ticket offer is included in its player's public count without revealing any
   destination identity. With two to four players, the draw piles and five face-up train cards
   slide into centered positions along the bottom after opening setup. With five players, they
   remain at the outer bottom edges to leave room for the fifth player tile.
   The complete scene scales together when the window is resized or maximized.
   Shift+Escape opens engineering-only screens for diagnostics and recovery. Their **Game table**
   screen still contains development controls. A camera restart or format change requires checking
   and restoring the board crop through the technical Camera screen.
4. With one human, opening destination choices appear directly on the laptop. With multiple
   humans, the visible game table guides setup of one shared phone for private cards. Start hosting
   on a selected Private LAN connection before scanning its QR. The phone's card display and
   handoff still need real-device PWA validation.
5. The laptop stays on **THE GAME TABLE** after turns and scoring-marker detection. In solo play,
   the T and D stacks show read-only mini cards on the table when clicked. A human can place trains
   on a legal, camera-measured route before choosing it digitally: after stable detection, the table
   asks which of the legal train-card payments to spend. The game then checks the placed trains and
   every previously claimed route in fresh frames before committing the claim. If trains have been
   moved off an older route, return them to that route; the new claim waits until the board matches.
   A stable, recognizable partial or unpayable solo placement shows **Invalid Move** with the route,
   detected train count, and any missing card-payment requirement. Uncertain camera readings do not
   produce an invalid-move warning or change the game state.
   Player-facing train-card and destination draw controls are not yet on the main game layer;
   their current implementation is in the engineering-only
   screen. With multiple humans, the shared phone is the intended private controller; actions do
   not automatically reveal a private screen on the laptop.

6. When any seat claims a route, the public screen names the seat, its colour and symbol, both
   endpoint cities, the exact lane, and how many trains to place. Place them in any order.
   The board shows one pulsing yellow cue in each requested train space. The camera checks those
   spaces and all earlier claimed trains automatically, shows “Thank you” for three seconds,
   then asks you to move that player's scoring marker. It waits until the marker appears at the new printed score before
   continuing automatically on **THE GAME TABLE**. If the camera cannot read the marker, the
   game waits for a clear view of it. Nothing is spent or scored until placement is verified.
7. After someone finishes a turn with two trains or fewer, every seat takes one more turn, and then
   the results screen shows each seat's route points, destination tickets, longest continuous route
   and the trail that achieved it.

For multiple humans, follow the phone setup shown on **THE GAME TABLE**. Select a Private LAN
connection and explicitly start the local host; only then can the table display a connection QR.
Install/trust the laptop's public certificate, open the PWA, and approve the matching pairing
identity. The phone is intended to pass between human players for private cards; its card and
handoff flow remains unfinished and unverified on real devices. A single human needs no
phone connection or local HTTPS setup. Use **Camera** for preview, board crop and a stable scene
reference. **Save and pack away** saves the digital game; it does not automatically take a picture.
**Export board photo** writes a PNG of the current crop and works without a scene reference, even
when the scene has changed. It needs a fresh camera frame and valid corners; it does not save a match.
Exports preserve the board's 8:5 shape at 3456 × 2160 and use the selected image processor.
Checkpoint-reference photos use the same output shape but retain unsharpened camera evidence.
For a live outline experiment, first remove all trains and score markers and choose **Capture
empty board**, or load a matching previously exported empty-board crop. Return pieces and clear
hands to inspect candidates. Uncheck **Enhanced 4K preview** to compare the raw camera image;
piece analysis continues on the enhanced path. See [camera setup and limits](docs/camera-processing.md).
Before clearing trains, choose **Add or view board photo**. Use **Camera setup** if prompted,
select the four crop corners and establish a stable scene reference. Check the live crop, tick the
board confirmation, then select **Capture reference photo**. Wait for the saved image to appear.
The live crop is labeled **not saved**. An attached photo also appears directly above the saved
route list on **Rebuild the board**; a checkpoint with no photo or no routes says so explicitly.

Closing an unfinished game asks **Are you sure you want to exit?**, with **No** selected by
default. Completed game actions are saved automatically; choose **No** and use **Save and pack
away** before clearing the physical board. A verified packed checkpoint (including an unfinished
rebuild) needs no exit warning, even without a photo. An action or photo save still running must
finish before you retry closing. If the latest save is uncertain, the app warns you.

Press **Escape** on the game table to open the save/quit dialog. An unresolved solo opening
destination choice remains underneath the dialog and returns unchanged if you dismiss it; choose
whether to keep all three or drop one to continue. Engineering-only screens remain available
through **Shift+Escape** for diagnostics and recovery. Other private views hide on deactivation and after 60 seconds without input.
Lock/suspend handlers request covering; real Windows lifecycle behavior remains an interactive
acceptance test.
When reopening a save, check the list of committed routes and attest to the physical board before
continuing. Any pending placement or cancellation still needs its own normal completion checks.

## Repository layout

```text
src/GoldenTicket.Domain/        rules, cards, graph, events, projections, scoring
src/GoldenTicket.Application/   coordinator, command pipeline, computer-seat driver
src/GoldenTicket.AI/            heuristic opponents and route planning
src/GoldenTicket.Persistence/   SQLite journal, checkpoint photos, restore
src/GoldenTicket.Desktop/       WPF views and view models
src/GoldenTicket.Vision/        capture, crop, CPU/GPU preprocessing, experimental piece candidates
src/GoldenTicket.CompanionHost/ embedded local HTTPS game PWA and controller protocol
tools/GoldenTicket.Simulator/   headless matches and the data audit
tools/GoldenTicket.ConnectivitySpike/ standalone local HTTPS/PWA feasibility tool; no game data
tools/GoldenTicket.CameraDiagnostics/ shared-read-only native camera format inventory
tools/GoldenTicket.MlPieceSmoke/ offline CPU/GPU model evaluation and outlined board images
tools/piece-training/           local annotation, training, export and comparison tools
tests/                          rules fixtures, properties, privacy, persistence, view models
data/classic-us/                hashed board and ticket manifest; physical audit pending
shared/theme/                   canonical box-derived theme tokens
docs/                           rules policy decisions and milestone evidence
```

The proposed `GoldenTicket.Windows` and separate `companion/` TypeScript build remain later
work. The current companion's plain JavaScript assets are bundled with `GoldenTicket.CompanionHost`.
Reviewed piece and corner models, with embedded trained weights and matching manifests, live
under [`assets/models/`](assets/models/README.md). Builds copy both pairs beside the executable
under `models/`. Training data and intermediate checkpoints stay in ignored `artifacts/`.
The [finished game artwork](assets/artwork/README.md) is committed; generation masters, prompts
and unused avatar copies are ignored.

## Design correspondence

Where the code implements a specific design requirement it says which one, in a comment naming the
section. The load-bearing ones:

| Design | Where |
|---|---|
| §5.2 information projections | `Projections/Projector.cs`, `EventVisibility` |
| §5.3 fair AI access | `IAiPolicy` takes a `SeatView`, never `GameState` |
| §5.4 randomness and replay | `DeterministicRandom`, events carry their own outcomes |
| §6.1 rules profile | `data/classic-us/classic-us-v1.json` |
| §6.4 rare supply cases | `docs/rules-policies.md` |
| §6.5 longest trail | `Scoring/LongestTrail.cs` |
| §7.2 invariants | `InvariantChecker.cs` |
| §8.1 command envelope and deduplication | `GameCommands.cs`, `SqliteSessionStore` |
| §8.3 claim commit protocol | `GameRules.HandleSubmitClaimEvidence` |
| §19.2–19.4 persistence and restore | `GoldenTicket.Persistence` |
| §4.7 pass-and-hide | `MainViewModel.PrivateSeat`, `PrivateSeatView.xaml` |
| §4.8 box palette | `shared/theme/tokens.json`, `Theme/Palette.xaml` |

## Not a claim of correctness

DESIGN §23.2 asks for measured results to stay distinguishable from design assumptions. What has
actually been run is in `docs/evidence/` and the current [camera processing report](docs/camera-processing.md).
The preprocessing hardware probe is narrower than the full camera/model/provider acceptance gates.
Camera recognition, companion devices and AI strength have not passed their required acceptance
gates. Automated checks do not establish complete real-device PWA or camera support.
