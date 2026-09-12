# GoldenTicket implementation completion

Updated September 12, 2026 after the [implementation audit](docs/AUDIT-2026-09-12.md).
The complete requirements remain in [DESIGN.md](DESIGN.md). This is a partial manual desktop
implementation with a game companion and camera/reference-photo tools, not a completed automatic
camera-assisted product. See [September 12 progress and morning checks](docs/IMPLEMENTATION-2026-09-12.md).

## Still missing: feature checklist

These six features remain unfinished. The milestone tasks below define their implementation and
validation requirements; mark each feature complete only after those checks pass.

- [ ] **Camera tracking and recovery** — board recognition, move verification, and automatic recovery after camera movement (M3–M5).
- [ ] **Phone/tablet PWA** — private pass-and-hide on Android and iOS/iPadOS, with laptop-hosted data
  and QR-assisted initial synchronization entirely within the LAN (M0/M2). No iPhone or iPad is
  available, so **iOS cannot be claimed as supported at release**; see
  [companion device evidence](docs/companion-device-evidence.md) for the honest wording and the
  borrowed-device checklist.
- [ ] **CPU/GPU inference selection** — Auto at launch uses a validated GPU or falls back to CPU; retain manual overrides and show a CPU chip or GPU/lightning status indicator (M4/M5).
- [ ] **Photographed save and rebuild** — the state-only half is implemented (M2): named checkpoints,
  the `PreparingPackAway`/`PackedAway`/`Rebuilding` lifecycle, commit-then-readback validation,
  route-list guided reconstruction and exactly-once resume. Optional encrypted, operator-attested
  reference photos now have immutable checkpoint binding and authenticated readback. Still missing:
  the board diagram, machine-verified photograph, pending-placement mask and full evidence lifecycle.
- [ ] **Offline installer packaging** — self-contained Windows x64 distribution with required runtimes and assets included (M7).
- [ ] **Training mode, voice, story, and audio: last feature pass** — Training follows Story without effects/ambience; narration follows visual/voice/both settings. Complete photo save-and-rebuild and packaging foundation first (M6).

The user's requested order puts voice/story/audio last. Keep essential visual guidance available
earlier; perform final release checks and package refresh after the narrative features are finished.

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

- [x] **Embedded companion first slice.** Local HTTPS host, controller approval/CSRF/private grants,
  shared human card/ticket actions, public-only shell cache and privacy/reconnect handling are
  integrated into the Windows shell. Actual device gates and the remaining protocol requirements
  below are still open.
- [x] **Camera and photo foundation.** Windows video-only capture, selectable formats, manual
  four-corner crop and conservative scene-reference checks; optional encrypted operator-attested
  checkpoint photos with integrity/readback and stale-capture protection. No automated train
  verification or machine-verified photo checkpoint is claimed.
- [x] **Offline package build workflow.** A clean-source, locked-dependency PowerShell builder creates
  a self-contained Windows x64 ZIP with runtime/assets checks, notices, provenance and checksums.
  Actual package output is recorded separately in [packaging evidence](docs/offline-package.md);
  clean-machine acceptance and an installer remain open.
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
  camera choice, preview and manual crop against the real board. Add printable markers, board
  landmarks, automatic calibration, detailed quality gates and recording/replay.
- [ ] **M4: verification.** Implement whole-board recognition, authorized pending evidence,
  board-first human placement, occlusion/unknown foreground rejection, jog/reconnect recovery,
  stale-epoch rejection, wake gesture, and explicit mode-change reconciliation.
- [ ] **M4/M5: inference.** Default to Auto: detect adapters at launch, validate GPU execution with
  the packaged model, and fall back to CPU on absence, incompatibility, timeout, or failure. Retain
  explicit CPU/GPU preferences and distinguish them from the effective backend. Display a chip/CPU
  icon or GPU text with lightning around it, with adapter/fallback details. Package native runtimes
  and test safe switching. Evaluate a baseline first; if needed train on developer data, validate
  held-out physical sets, and ship an offline model. Users never train or download a model.
- [x] **M2: state-only pack away and rebuild.** Named checkpoints, durable packed/rebuild lifecycles,
  frozen source state, commit-then-readback validation before any safe-to-pack result, suspended
  partial operations preserved, route-list guided reconstruction with whole-target attestation,
  and exactly-once resume. Verified by `PackAwayTests` and `PackAwayDurabilityTests`.
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
