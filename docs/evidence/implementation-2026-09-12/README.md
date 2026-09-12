# Implementation validation, September 12, 2026

This evidence follows the implementation record in [the acceptance guide](../../IMPLEMENTATION-2026-09-12.md).
It applies to the source committed with this evidence, including the final authorization/reveal
race fixes and immutable-photo recovery controls. No real player's save or private camera photo
is included.

| Check | Executed result |
|---|---|
| Locked solution restore | Passed with `NuGet.config` and SDK 10.0.401. |
| Release solution build | Passed. Full compilation has xUnit cancellation-token advisories; the final incremental build had zero errors/warnings. |
| Full .NET regression suite | 432 passed, 0 failed, 0 skipped. [TRX](final-tests.trx) |
| Both shipped JavaScript behavior suites | 29 passed, 0 failed, 0 skipped. [Runner output](node-tests.log) |
| Headless Chrome with actual PWA assets | 21 scenarios passed at three sizes. [Browser evidence](../companion-browser-2026-09-12/README.md) |
| Production WPF view rendering | 12 cases passed at two sizes; zero binding/resource/critical horizontal overflow errors. [Desktop evidence](../desktop-ui-2026-09-12/README.md) |
| Four-seat simulation, seeds 1–20 | 20/20 matches completed; invariants and journal replay equality held. |
| Connected NuGet vulnerability query | No reported vulnerable direct/transitive packages in the solution. [Feed result](dependency-audit.json) |

```powershell
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln -c Release --no-restore
dotnet test GoldenTicket.sln -c Release --no-build --no-restore --logger "trx;LogFileName=final-tests.trx"
node --test tests/GoldenTicket.ConnectivitySpike.Tests/shell.test.cjs tests/GoldenTicket.Domain.Tests/CompanionHostClient.test.cjs
dotnet run --project tools/GoldenTicket.Simulator -c Release --no-build --no-restore -- simulate --games 20 --seats 4
dotnet package list --project GoldenTicket.sln --vulnerable --include-transitive --no-restore --format json
```

The .NET suite used the signed-in Windows profile needed for real DPAPI and HTTPS test-root
validation. Chrome used an isolated temporary profile. Neither test installed operating-system
certificate trust or changed firewall/network settings. Browser lifecycle signals and WPF key
events in these diagnostics are synthetic; no physical phone/camera acceptance is implied.

Native computer-use launch approval timed out. Physical phone trust/install, focus/touch/DPI,
real camera capture/recovery, board contents and clean-machine package acceptance remain on
the manual checklist. Package output is recorded separately after the documented source commit.
