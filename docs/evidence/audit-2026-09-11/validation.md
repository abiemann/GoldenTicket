# Audit validation evidence

Date: September 11, 2026. Workspace: `D:\Projects\GoldenTicket`.
Host: Windows x64, OS build 26200; .NET SDK 10.0.401, .NET/Windows Desktop runtime 10.0.12.
See the [audit verdict](../../AUDIT-2026-09-11.md) for scope and limitations.

## Final automated checks

| Check | Command/evidence | Result |
|---|---|---|
| Locked dependency restore | `dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config --nologo` | Passed; seven project lock files, no dependency changes required. |
| Release-configuration build | `dotnet build GoldenTicket.sln --configuration Release --no-restore --no-incremental --nologo` | Passed, 0 errors, 97 xUnit1051 test-analyzer warnings. See [build.log](build.log). |
| Full regression suite | `dotnet test GoldenTicket.sln --configuration Release --no-build --no-restore --nologo` | 173 passed, 0 failed, 0 skipped. See [final-tests.trx](final-tests.trx). |
| Dependency advisories | `dotnet list GoldenTicket.sln package --vulnerable --include-transitive --format json --no-restore` | Seven projects checked against nuget.org; no vulnerable-package entries reported. See [nuget-audit.json](nuget-audit.json). |
| Classic data structure/hash | `dotnet run --project tools/GoldenTicket.Simulator --configuration Release --no-build --no-restore -- verify-data` | Profile/schema/policy/hash agree; physical audit remains UNAUDITED. |
| WPF XAML structure | Parse all source `.xaml` files as XML; compile with WPF build | Eight files parse; WPF compilation passes. No interactive display validation claimed. |

The 97 build warnings are exclusively xUnit1051 suggestions to propagate the test runner's
cancellation token. They do not indicate C# compiler errors or skipped tests. They remain visible
in the build log and have not been suppressed to present a warning-free result.

The first restricted-profile baseline was 85 pass/6 fail because DPAPI could not use the user
profile. Running the unchanged baseline with normal Windows DPAPI access passed 91/91. The final
suite was likewise run with DPAPI access; no crypto bypass or test skip was introduced. Intermediate
`audit-tests.trx` reflects an earlier integration run and is not the final result.

## Simulator checks

Command template used after the application/domain fixes:

```powershell
dotnet run --project tools/GoldenTicket.Simulator --no-build --no-restore -- simulate --games 25 --seats N --seed S
```

| Seats | Seeds | Completed | Invariants | Journal replay |
|---|---|---|---|---|
| 2 | 200–224 | 25/25 | Passed | State hashes equal |
| 3 | 300–324 | 25/25 | Passed | State hashes equal |
| 4 | 400–424 | 25/25 | Passed | State hashes equal |
| 5 | 500–524 | 25/25 | Passed | State hashes equal |

All runs used Standard difficulty and the same production rules engine. These are deterministic
simulation checks with manual evidence supplied by the simulator, not camera tests or independent
proof of physical data/rule correctness. Timings from concurrent simulator runs are not presented
as performance benchmarks.

## Data identity

```text
profile            ttr-us-classic-en-v1
schema             1
rules policy       1
edition            Ticket to Ride - classic English North America [DO7201, 7201]
cities             36
routes             100 (22 parallel groups, 44 grey lanes)
total track length 309 train spaces
tickets            30 (4–22 points)
train cards        110 (9 kinds)
dataHash           sha256:913e8931538b124a05d146adb536fcc9258869bdf159a5a4efb287392da3df3f
physical audit     UNAUDITED
```

Source/project/data file fingerprints are recorded in [source-hashes.json](source-hashes.json) so
the audited inputs can be identified despite this directory having no Git repository.

## Boundaries

Tests create temporary synthetic sessions. No existing user save was migrated, edited, deleted,
or used as a test fixture. No installer or distributable release was created, and no commit/push
was performed. There was no real camera/board, iOS/Android, GPU, voice/story, or clean-machine
offline installation test. The full product remains incomplete as detailed in the audit and TODO.
