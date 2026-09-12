# Audit validation — September 12, 2026

Reviewed base: `f7fd310ec98dc092194ab32496f088c701456ef8` on `main`; the remote matched.
These results describe the audit working tree, not a published release. Source fingerprints are in
[`source-hashes.json`](source-hashes.json). The [audit report](../../AUDIT-2026-09-12.md) describes
findings, fixes and the incomplete DESIGN requirements.

## Environment

- Windows 11, reported OS build `10.0.26200.0`.
- .NET SDK `10.0.401`; .NET, ASP.NET Core and Windows Desktop runtime `10.0.12`.
- Node `24.19.0` for browser-script tests, with no npm dependencies.
- Restore and DPAPI tests ran under the normal Windows profile with sandbox escalation. A restricted
  attempt could not read the user NuGet configuration; encryption was not bypassed and no tests
  were skipped to compensate.

## Results

| Check | Result | Evidence |
|---|---|---|
| Locked restore, eight projects | Passed | [restore.log](restore.log) |
| Release-configuration solution build | 0 errors; 104 existing `xUnit1051` cancellation-token warnings in tests | [build.log](build.log) |
| Complete .NET suite | **332 passed, 0 failed, 0 skipped**, up from baseline 279 | [final-tests.trx](final-tests.trx), [tests.log](tests.log) |
| Executed browser/service-worker scripts in controlled Node harness | **14 passed, 0 failed** | [browser-script-tests.log](browser-script-tests.log) |
| Standard AI, 25 two-seat games, seeds 1400–1424 | All completed; invariants and replay equality passed | [simulation-2-seats.log](simulation-2-seats.log) |
| Standard AI, 25 three-seat games, seeds 1500–1524 | All completed; invariants and replay equality passed | [simulation-3-seats.log](simulation-3-seats.log) |
| Standard AI, 25 four-seat games, seeds 1600–1624 | All completed; invariants and replay equality passed | [simulation-4-seats.log](simulation-4-seats.log) |
| Standard AI, 25 five-seat games, seeds 1700–1724 | All completed; invariants and replay equality passed | [simulation-5-seats.log](simulation-5-seats.log) |
| Manifest structure and checksum | Passed; physical audit still `UNAUDITED` | [manifest.log](manifest.log) |
| NuGet advisory query, direct/transitive | No vulnerable package entries reported across eight projects | [nuget-audit.json](nuget-audit.json) |
| XAML syntax | All 9 files parsed as XML | Headless syntax check, not rendered UI acceptance |
| Patch whitespace and documentation links | Passed | `git diff --check`; local Markdown targets checked |

The 53 added .NET cases cover checkpoint integrity/concurrency/retry/restart, saved operations,
legacy hashes, supply pauses, private-view and rebuild transitions, local network request policy,
certificate consistency, bounded reporting and session expiry. Browser tests execute the shipped
scripts; they include pairing before initial checks finish and late session-read races.

## Reproduce

From the repository root:

```powershell
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln -c Release --no-restore
dotnet test GoldenTicket.sln -c Release --no-build --no-restore
node --test tests/GoldenTicket.ConnectivitySpike.Tests/shell.test.cjs
dotnet run --project tools/GoldenTicket.Simulator -c Release --no-build --no-restore -- verify-data
foreach ($seats in 2..5) {
    $seed = 1200 + ($seats * 100)
    dotnet run --project tools/GoldenTicket.Simulator -c Release --no-build --no-restore -- simulate --games 25 --seats $seats --seed $seed
}
dotnet list GoldenTicket.sln package --vulnerable --include-transitive --no-restore --format json
git diff --check
```

## Not established by these checks

No real iOS/Android HTTPS trust, home-screen installation, QR camera scan or LAN mDNS acceptance
was performed. No CA was installed and no firewall or network category was changed. The current
Wi-Fi is classified Public by Windows NLM; the spike rejects startup on that profile.

No camera detection, jog recovery, mounted Pixel stability, inference provider, audio, clean-machine
installer, actual power cut, or interactive Windows lock/suspend/task-switcher privacy acceptance
was performed. Checkpoint fault injection and view-model tests cover code behavior only. The
earlier Pixel USB snapshot experiment is not evidence of an implemented recognition pipeline.
