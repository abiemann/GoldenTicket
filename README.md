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
game phone companion, camera tools, and encrypted board reference photos. Use its morning
acceptance checklist and [local phone setup](docs/phone-setup.md). Real-device acceptance remains
in progress; automatic train recognition is not enabled.

## What this build does

This build implements the core game plus initial phone, camera and photo workflows from DESIGN
§23.1. Neither the physical data review nor the full milestone acceptance gates are complete:

- A classic North America rules data package with 36 cities, 100 routes, 30 destination tickets,
  and 110 train cards. Runtime loading requires the supported profile/version and a valid checksum;
  the physical data audit is still outstanding.
- Full turn structure: the two-card draw with its subphases, the face-up market with the
  three-locomotive reset and discard reshuffles, destination-ticket offers, and route claims.
- Route claims use the reserve-then-verify protocol: planning a claim reserves the payment but
  spends nothing, the operator places the physical trains, and one atomic commit spends the cards,
  records ownership, scores and ends the turn.
- Exact final scoring, including the longest continuous route as a true maximum edge-simple trail
  with the witness trail shown.
- Heuristic computer opponents at three difficulty levels, which see only their own seat's view.
- Durable local saves: an append-only event journal in SQLite with a tamper-evident hash chain,
  DPAPI-protected AES-GCM encryption for referee-only and private payloads, command deduplication,
  and restore by replay verified against a stored state fingerprint.
- Save and pack away: the game suspends mid-turn, writes a named checkpoint, reads it back and only
  then says the pieces may be cleared away. Reopening shows the saved position route by route with
  per-seat stock guidance, takes the operator's whole-board confirmation, and resumes the exact
  suspended action once. Packing away and rebuilding provably change nothing about the game.
- Save paths are confined to valid session directories; concurrent writers, inconsistent journal
  metadata, missing snapshots, and corrupted state stop the operation. An uncertain save outcome
  requires a reload. Unreadable saves remain listed with recovery guidance.
- A WPF interface in the box-derived palette: a public table screen, an opaque privacy curtain with
  a per-seat private view, the operator's placement instruction and confirmation, and a results
  screen.
- A laptop-hosted HTTPS phone PWA with private human cards/tickets, digital draws and route/payment
  choices. One shared controller is paired and explicitly approved on the laptop. Private views
  expire and hide on handoff, backgrounding, or connection loss; the laptop verifies physical moves.
- A camera screen with Windows video-only capture, resolution selection, preview, manual four-corner
  board crop, and conservative scene-reference change/recovery indication. It identifies camera
  changes and stale frames, but does not recognize trains or authorize route claims.
- Optional encrypted, immutable board reference photos attached to validated saved checkpoints.
  Photos are cropped from fresh camera frames and authenticated on readback. They assist manual
  rebuilding; checkpoints retain their explicit state-only provenance.
- Explicit manual-verification opt-in, per-placement whole-board attestation, and a board-check
  gate before restored games can resume AI or human actions. Hiding a private view invalidates late
  asynchronous results. Fault logs contain bounded error metadata rather than exception payloads.
- A headless simulator for reproducible matches and a data-audit command.

## What this build does **not** do

These are later milestones in `DESIGN.md`, and nothing here pretends they exist:

- **No automatic camera verification.** Physical placement is confirmed by the operator
  (`VerificationMode.Manual`). Scene similarity does not prove route ownership. Automatic landmarks,
  train recognition, gesture wakeup, and CPU/GPU inference remain unfinished.
- **Phone acceptance is incomplete.** The embedded companion is functional and tested with
  automated HTTPS/browser cases, but Android certificate/install/offline acceptance and all Apple
  device acceptance remain outstanding. This slice uses two-second snapshot polling and fresh
  laptop pairing after page reload; WSS/event-cursor recovery and durable controller registration
  remain design gaps. See [implementation details](docs/IMPLEMENTATION-2026-09-12.md).
- **No machine-verified photo checkpoint.** Optional operator-attested reference photos are saved
  separately with encryption, checkpoint association and readback checks. Checkpoints remain
  `LogicalStateOnly`; partial placement masks and automatic whole-board reconciliation are still M4.
- **No story mode, narration or sound.** That is M6.
- **No installer.** M7.
- **No board geometry.** DESIGN §6.3 forbids shipping placeholder coordinates, so the data package
  carries none. The interface identifies routes by their endpoint cities and lane, not by position.

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

The current desktop build is framework-dependent and needs the .NET 10 Windows Desktop runtime.
Build the self-contained offline ZIP with the [packaging workflow](docs/offline-package.md) when preparing a
validated source commit; clean-machine and installer acceptance remain outstanding.

The package builder runs the executable's windowless `--check-package` diagnostics against its
bundled runtime, WPF, SQLite, DPAPI, Windows PNG encoding, ASP.NET and local assets before archiving.
Both normal and cache-only offline package builds have passed; the local ZIP, source commit,
checksums and remaining manual gates are recorded in [package evidence](docs/evidence/offline-package-2026-09-12/README.md).

No paid IDE, account, or internet connection is needed to run the application. Building it the first
time downloads NuGet packages.

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
The persistence tests
need a normal Windows user profile with DPAPI access. A restricted or impersonated test context
can fail data protection even when the same tests pass as the signed-in Windows user.

### Headless matches

Reproducible all-computer games, checking the invariants and replay equality after each one:

```bash
dotnet run --project tools/GoldenTicket.Simulator -- simulate --games 20 --seats 4 --verbose
```

## Playing a match

1. Put the board and the plastic trains on the table. **Leave the physical cards and destination
   tickets in the box** — the application deals and holds every card, for every seat.
2. Name the seats, pick each one's physical train colour, and mark which are computer players.
   Explicitly select manual verification; this build has no camera verification.
3. Each human opens their private view in turn to keep their opening destination tickets. The
   laptop returns to the public table screen between seats.
4. On a human turn, that player opens their private view to draw cards, draw destination tickets, or
   choose a route and how to pay for it.

5. When any seat claims a route, the public screen names the seat, its colour and symbol, both
   endpoint cities, the exact lane, and how many trains to place. Place them in any order, then
   check the entire board, including previously claimed routes, tick the attestation checkbox, and
   confirm. Nothing is spent or scored until you do.
6. After someone finishes a turn with two trains or fewer, every seat takes one more turn, and then
   the results screen shows each seat's route points, destination tickets, longest continuous route
   and the trail that achieved it.

Use **Connect phone** to start the local host, install/trust its public certificate, open the PWA,
and approve the matching pairing identity. Return to **Game table** to enable phone play. The
laptop private view remains a fallback. Use **Camera** for preview, board crop and a stable scene
reference; after **Save and pack away**, choose **Add or view board photo** before clearing trains.

Press **Escape** at any time to cover a private view.
Private views also hide on deactivation and after 60 seconds without input. Lock/suspend handlers
request covering; real Windows lifecycle behavior remains an interactive acceptance test.
When reopening a save, check the list of committed routes and attest to the physical board before
continuing. Any pending placement or cancellation still needs its own normal completion checks.

## Repository layout

```text
src/GoldenTicket.Domain/        rules, cards, graph, events, projections, scoring
src/GoldenTicket.Application/   coordinator, command pipeline, computer-seat driver
src/GoldenTicket.AI/            heuristic opponents and route planning
src/GoldenTicket.Persistence/   SQLite journal, encryption, restore
src/GoldenTicket.Desktop/       WPF views and view models
tools/GoldenTicket.Simulator/   headless matches and the data audit
tools/GoldenTicket.ConnectivitySpike/ standalone local HTTPS/PWA feasibility tool; no game data
tests/                          rules fixtures, properties, privacy, persistence, view models
data/classic-us/                hashed board and ticket manifest; physical audit pending
shared/theme/                   canonical box-derived theme tokens
docs/                           rules policy decisions and milestone evidence
```

The product projects in DESIGN §18.3 that belong to later milestones (`GoldenTicket.Vision`,
`GoldenTicket.Windows`, `GoldenTicket.CompanionHost`, `companion/`, `training/`, `models/`) do not
exist yet. They are deliberately absent rather than present and empty.

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
actually been run is in `docs/evidence/`. Nothing about camera recognition, GPU backends, companion
devices, or AI strength has passed the required acceptance gates. The connectivity shell and
state-only rebuild have automated checks; neither establishes real-device PWA or camera support.
