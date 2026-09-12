# GoldenTicket implementation completion

Updated September 11, 2026 after the [implementation audit](docs/AUDIT-2026-09-11.md).
The complete requirements remain in [DESIGN.md](DESIGN.md). This is a partial manual desktop
implementation, not a completed camera-assisted product.

## Still missing: feature checklist

These six features remain unfinished. The milestone tasks below define their implementation and
validation requirements; mark each feature complete only after those checks pass.

- [ ] **Camera tracking and recovery** — board recognition, move verification, and automatic recovery after camera movement (M3–M5).
- [ ] **Phone/tablet PWA** — private pass-and-hide on iOS/iPadOS and Android over the local network (M0/M2).
- [ ] **CPU/GPU inference selection** — user-selectable acceleration, a complete CPU path, and GPU failure recovery (M4/M5).
- [ ] **Voice, story, and audio** — visual/voice/both modes, offline narration, train sounds, and congratulations (M6).
- [ ] **Photographed save and rebuild** — save the board photo and exact game state, pack away, then reconstruct and resume (M2/M4).
- [ ] **Offline installer packaging** — self-contained Windows x64 distribution with required runtimes and assets included (M7).

## Audit fixes implemented

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

- [ ] **M0/M1: data and platform evidence.** Review every city connection, lane, color, train length,
  and all 30 tickets against the supported physical edition. Record reviewer/provenance. Supply
  measured route-cell geometry and board landmarks; retain the current unaudited status until done.
- [ ] Resolve the documented rare supply rules with an explicit reviewed/versioned policy and
  continuation UI. A safe saved pause prevents corruption but does not make these cases playable.
- [ ] **M0/M2: companion.** Implement the embedded same-origin HTTPS/WSS host, protected per-laptop
  certificate setup, local naming/pairing, controller/private-view grants, authorization and CSRF
  validation, and versioned idempotent commands. Build the iOS/iPadOS/Android PWA with pass-and-hide,
  shell-only caching, reconnect, update handling, and accessibility. Prove offline setup and local
  certificate trust on real devices before claiming support.
- [ ] **M3: camera.** Add WinRT high-resolution acquisition, bounded frame ownership, camera choice,
  preview and quality checks, printable markers, board landmarks, calibration, and recording/replay.
- [ ] **M4: verification.** Implement whole-board recognition, authorized pending evidence,
  board-first human placement, occlusion/unknown foreground rejection, jog/reconnect recovery,
  stale-epoch rejection, wake gesture, and explicit mode-change reconciliation.
- [ ] **M4/M5: inference.** Provide CPU-only and GPU selection, packaged native runtimes and CPU
  fallback. Evaluate a baseline first; if needed train on developer data, validate held-out physical
  sets, and ship an offline versioned model. Users must not have to train or download a model.
- [ ] **M2/M4: persistence and pack away.** Add complete encrypted snapshots, backup-before-migration,
  named checkpoints and pinned images, state/photo readback, durable packed/rebuild lifecycles,
  partial-operation targets, guided reconstruction, current-checkpoint success receipts, and
  exactly-once resume. Add correction branches and optional encrypted portable export/import.
- [ ] **M6: experience.** Add voice/visual/both settings, local speech/recorded fallback, original
  story and sound assets, public-event filtering, volume and interruption controls, and adjustable
  privacy timing. Test theme/high-contrast/screen-reader and keyboard behavior in the running UI.
- [ ] Implement and evaluate the specified Challenging AI sampled lookahead; current difficulty
  choices tune a heuristic. Keep opponent hands/deck state inaccessible and report strength honestly.
- [ ] **M7: distribution.** Provide the self-contained x64 offline build and installer/ZIP, native
  dependency smoke checks, license/asset notices and provenance, and documented upgrades/uninstall
  that retain saves. Re-run package advisories when preparing a release.
- [ ] Run the full DESIGN §22 physical, crash/power-loss, privacy lifecycle, camera, CPU/GPU,
  story, iOS/Android, network-loss, and clean-machine offline acceptance matrix. Automated view-model
  tests and simulated games are not substitutes for those checks.

## Verification commands

```powershell
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln --no-restore
dotnet test GoldenTicket.sln --no-restore
dotnet run --project tools/GoldenTicket.Simulator --no-build --no-restore -- verify-data
dotnet run --project tools/GoldenTicket.Simulator --no-build --no-restore -- simulate --games 25 --seats 4 --seed 400
dotnet list GoldenTicket.sln package --vulnerable --include-transitive --no-restore
```

Persistence tests require Windows DPAPI under a loaded user profile. Do not replace encryption with
plaintext or skip those tests to make a restricted execution context report success.
