# Offline Windows package

This is retained distribution tooling and historical package evidence. The current development
workflow is [Visual Studio and GitHub CI](build-and-ci.md); routine personal ZIP creation is not
requested, and CI does not invoke this packaging script.

GoldenTicket's packaging script prepares a self-contained Windows 11 x64 ZIP. It includes the
application, local companion browser assets, board data, .NET/WPF/ASP.NET runtimes, documentation,
dependency notices, source provenance, and SHA256 checksums. Players extract the complete
`GoldenTicket` folder and run `GoldenTicket.exe`; installing .NET or paying for a service is not
part of that workflow.

The first normal and cache-only offline builds passed the builder and executable checks; see
[the package record](evidence/offline-package-2026-09-12/README.md) for source identity, local ZIP
and checksums. Clean-machine and physical-device acceptance remain unverified. This is a portable distribution foundation,
not an installer, signed release, automatic updater, or completed application acceptance.

## Build from a documented source commit

The project rule is **documentation first, source commit second, package build third**. Before
building or rebuilding a distribution, check README, TODO, DESIGN, this guide, phone setup, and
other affected documentation against the final implementation. Record what works, what was
actually tested, and what remains incomplete. Validate affected documentation links and tests,
then commit those changes together with the implementation. Create any release tag only after
that source commit exists. Do not use a package build to bypass this sequence.

On the developer machine, use Windows x64, Git, PowerShell 7.2 or newer, and the exact free .NET
SDK version in `global.json`. These are build tools; they are not required on the player's laptop.
The script refuses a modified or untracked source tree. Ignored build artifacts are permitted.
It also checks the Git commit again after restore, publish, and archive generation.

From the repository root:

```powershell
pwsh -NoProfile -File tools/Build-OfflinePackage.ps1 -CheckOnly
pwsh -NoProfile -File tools/Build-OfflinePackage.ps1
```

`-CheckOnly` performs source/documentation/SDK preflight without restoring dependencies,
publishing, or creating a release directory. It cannot establish that the prose is accurate;
that review remains part of the source change.

The package build uses locked NuGet restore with `GoldenTicketOfflinePackage=true`, which selects
each participating project's committed `packages.win-x64.lock.json`. The seven application
projects have separate package locks because a self-contained publish applies `win-x64` to the
entire project-reference graph. Normal builds and solution tests keep using `packages.lock.json`;
packaging does not rewrite those development locks or weaken locked restore.

The build may download the exact free dependency/runtime packs on the developer machine. It does
not require a subscription or a hosted service. To build
without network access after populating the exact package cache:

```powershell
pwsh -NoProfile -File tools/Build-OfflinePackage.ps1 -OfflineBuild
```

That mode uses only the local package cache and an empty local feed. Missing packages fail;
there is no fallback to downloading another version. NuGet vulnerability queries are disabled
for that offline restore, so retain a separately performed connected vulnerability audit in the
release evidence. Neither mode changes pinned dependencies or SDK versions deliberately.

When a source change intentionally changes dependencies, update and review both lock sets before
the source commit. The package graph is regenerated explicitly with the pinned SDK:

```powershell
dotnet restore src/GoldenTicket.Desktop/GoldenTicket.Desktop.csproj --force-evaluate --configfile NuGet.config --runtime win-x64 -p:SelfContained=true -p:GoldenTicketOfflinePackage=true
dotnet restore src/GoldenTicket.Desktop/GoldenTicket.Desktop.csproj --locked-mode --configfile NuGet.config --runtime win-x64 -p:SelfContained=true -p:GoldenTicketOfflinePackage=true
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
```

The first command is a deliberate dependency-maintenance operation, not part of packaging. Inspect
the seven package-lock diffs and confirm normal locks only change when the source dependency update
requires it. The second command verifies the package graph; the third verifies and restores the
normal development graph. These restore operations do not publish release files. Commit updated
locks and documentation with the implementation before running the packaging script.

Each run gets a unique directory under `artifacts/release/`, identified by UTC timestamp,
source commit prefix, and a random suffix. Earlier output is never removed or overwritten.
An interrupted run remains there for diagnosis. A complete run has:

- `GoldenTicket/`: the extracted application and all required runtime/content files.
- `GoldenTicket-win-x64-….zip` and its adjacent `.sha256` file.
- `restore.log`, `publish.log`, and `package-result.json` outside the player payload.
- `runtime-diagnostic.json`, recording seven checks run by the published executable, outside the
  player payload. It identifies the actual loaded runtime directory and reports component failures.
- `GoldenTicket/package-provenance.json`, identifying the source commit, SDK, included runtime
  versions, publish settings, and outstanding manual acceptance.
- `GoldenTicket/SHA256-MANIFEST.json`, covering every payload file except the manifest itself.
  The detached archive SHA256 covers the complete ZIP, including that manifest.
- `GoldenTicket/LICENSE`, containing the project's PolyForm Noncommercial 1.0.0 license.
- `GoldenTicket/licenses/`, retaining exact package specifications and available license/notices
  from the local restored packages, including the shipped runtime packs.

The script checks that the published runtime configuration is self-contained, and that CoreCLR,
WPF, ASP.NET/Kestrel, SQLite, Windows camera interop, the companion shell, and board data are
present. It rejects missing assets, symbolic-link/junction storage, bundled game saves or private
keys, and unexpected untracked companion assets. It neither installs a CA nor changes firewall
rules, network profiles, trust stores, Git tags, GitHub releases, or repository visibility.

Before archiving, the builder runs `GoldenTicket.exe --check-package <new-report-path>` with a
45-second process limit. This explicit command-line mode opens no window, camera, network listener
or saved game. It checks that CoreCLR is loaded from the package, validates the shipped board data,
renders the WPF theme, executes native SQLite in memory, encodes/decodes a synthetic PNG through
Windows and WPF, constructs the ASP.NET host without
listening, and checks bundled browser assets. Existing report files are never overwritten. A failed component
or framework-dependent runtime blocks packaging. These developer-machine checks supplement the
clean-machine/device acceptance table below.

Some NuGet packages contain a license expression or URL without the full license text.
`licenses/dependencies.json` preserves this distinction, and `PACKAGE-THIRD-PARTY-NOTICES.md` lists
those packages for notice-text review before wider distribution. Collecting their metadata is
not a completed license audit. The root project LICENSE and any other project notices are copied
unchanged.

## Run the extracted application

1. Transfer the ZIP and its checksum file. Compare `Get-FileHash -Algorithm SHA256` with the
   adjacent `.sha256` before extraction. A checksum detects a changed copy; it is not a digital
   signature or a substitute for obtaining the archive from a trusted source.
2. Extract the entire folder into a directory the Windows user can read, such as
   `%LOCALAPPDATA%\Programs\GoldenTicket`. Keep the runtime DLLs, `companion-web`, and `data`
   beside the executable. Run `GoldenTicket.exe` from the extracted directory.
3. The portable application uses `%LOCALAPPDATA%\GoldenTicket` for this user's state. Extracting
   a newer package beside the old one does not migrate saves to another account or delete them.
   Current game and board-photo payloads are plaintext with integrity checks. Preserve each saved game's
   matching board-photo attachment: completed saves require it, and reload validates it before play.
4. Windows may show its normal unsigned-application or camera-permission prompts. The package
   does not disable those protections. A signed installer is still outstanding.
5. For the companion, follow [phone setup](phone-setup.md). Laptop-only play does not require
   administrator rights. Authorizing the scoped Windows firewall rule or changing a managed
   network profile can require additional Windows permissions; the package does not bypass them.
   Quick play connects over unencrypted HTTP on the chosen trusted Private LAN. PRACTICAL
   uses the laptop and needs neither a phone nor networking.

The browser game is served by the laptop. Disconnecting Internet access is supported; switching
off the laptop host or losing the LAN disconnects Quick play. The phone has no independent game
state or offline app cache. The current implementation and physical-verification limits are recorded in
[README](../README.md), [TODO](../TODO.md), and [DESIGN](../DESIGN.md).

## Acceptance before distributing a particular build

Record the source commit, ZIP checksum, Windows version, devices, and outcomes. These are
acceptance tasks, not claims made by successful publication:

| Check | Required result |
|---|---|
| Fresh Windows 11 x64 user or machine with no .NET runtime installed | Extracted executable opens without a .NET download prompt. |
| Internet disconnected, laptop-only operation | Create a match, use private hands, complete turns and a manual physical claim, save, exit, reopen and resume. |
| Application directory without write permission | Ordinary game state remains in the user's local data directory; launch and save work. |
| Complete simulated/manual match and final scoring | No missing data, dependency, native DLL, or runtime errors. |
| Physical camera permission, stop/reconnect and scene change | Preview/crop controls work and stale frames cannot produce a saved reference. |
| Photo attachment, application restart and board rebuild | Stored reference survives with the correct checkpoint; digital state and explicit whole-board confirmation remain authoritative. |
| Android/iOS trusted LAN joining | HTTP Quick play pairing, private-seat controls, downloads and reconnect work with Internet disconnected; loss of connection covers cards and does not commit queued actions. |
| PRACTICAL with networking unavailable | Multiple humans complete private laptop turns and deliberate handoffs; no phone setup is required. |
| Separate new package folder and rollback to previous folder | Existing saves remain intact; incompatible save formats stop with an actionable message. |
| Dependency notices and artifact hashes | Inventory matches shipped versions, notice-text gaps are resolved before wider distribution, and extracted payload hashes match. |

An installer, uninstaller, signing, automatic update policy, and clean-machine/device evidence
remain separate milestones. This script makes the portable package concrete and reviewable
without asserting that the full DESIGN is finished.
