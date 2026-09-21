# Build in Visual Studio; run on the LAN

GoldenTicket is developed in Visual Studio and continuously checked by GitHub Actions. After the
Windows application and any required runtime prerequisites are installed, gameplay uses only the
laptop and local network. GitHub is not a game server and is not consulted at application launch.

## Visual Studio

1. Install the .NET SDK recorded in `global.json` and a compatible Visual Studio installation with
   **.NET desktop development**. Restore packages while developer Internet access is available.
2. Open `GoldenTicket.sln` and select **GoldenTicket.Desktop** as the startup project.
3. Build/run normally. Test Explorer can run the .NET regression suite.

The SDK/build prerequisites are development tools. For a framework-dependent installation on
another laptop, install the matching .NET 10 Windows Desktop and ASP.NET Core runtimes first.
An eventual installer must bundle or provision prerequisites during installation; installed gameplay
must not download them. No Node/npm/Playwright dependency is shipped in the Windows app or phone browser.

## GitHub Actions

[Windows CI](../.github/workflows/windows-ci.yml) runs on pushes to `main`, pull requests and manual
**Run workflow** requests. A bounded Windows job:

1. Checks out source without persisting repository credentials.
2. Installs the SDK from `global.json` and Node 24 for development tests.
3. Restores NuGet and the development-only Playwright dependency from committed lock files.
4. Builds the complete solution in Release, including the WPF Windows application.
5. Runs .NET regression tests, including actual local HTTP and controller-boundary checks.
6. Runs companion JavaScript privacy/LAN tests and 20 complete simulated matches with replay checks.
7. Renders production WPF views and exercises Quick play in the runner's installed Chrome/Edge, using
   synthetic game fixtures exported by the .NET tests. Non-laptop browser origins are blocked.
8. Retains test reports and synthetic UI screenshots for seven days, including partial evidence
   when a later check fails. It does not upload game saves, certificates, dumps or player photos.

A separate `ubuntu-24.04` job restores and runs `GoldenTicket.Core.Tests` with the same pinned SDK.
That portable suite covers Domain/Application/AI/SQLite and executable architecture boundaries;
it has no WPF, camera or phone-host dependency. The existing `GoldenTicket.Domain.Tests` project
retains its historical name for Windows integration coverage. Each suite writes a separate TRX
report. See [architecture and test ownership](architecture.md).

To run only the portable suite locally:

```sh
dotnet restore tests/GoldenTicket.Core.Tests/GoldenTicket.Core.Tests.csproj --locked-mode --configfile NuGet.config
dotnet test tests/GoldenTicket.Core.Tests/GoldenTicket.Core.Tests.csproj --no-restore
```

The job uses `windows-2025`, read-only repository permissions, immutable official-action commit
pins and no project secrets. New runs cancel superseded runs for the same PR/ref, and the job has
a 20-minute limit. It does not change repository visibility, publish releases, produce an app ZIP,
or configure end-user Windows firewall settings. CI uses the repository's GitHub Actions
allowance; no running server or subscription is introduced for players.

The runner builds/tests Windows-targeted code but does not establish Windows 11 hardware support.
Actual camera, phone joining/handoff, OS lifecycle, display scaling and physical-board acceptance
remain manual. Browser smoke exercises Quick play on an insecure HTTP test hostname mapped to
loopback, in an isolated Chromium process. This covers normal LAN browser restrictions without
pretending that a loopback secure-context exception represents a phone. It does not establish real
Android/iOS behaviour or a successful phone-camera QR scan.

The official [setup-dotnet action](https://github.com/actions/setup-dotnet) supports the SDK and
lock-file cache setup used here. GitHub's [Windows runner inventory](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md)
describes the hosted environment. Runner selection alone is not evidence of success; review the
individual workflow run's results.

### Keeping UI and camera checks current

After changing the game-table layout, run the WPF smoke tool as well as the .NET tests:

```powershell
dotnet run --project tools/GoldenTicket.UiSmoke -c Release --no-build -- artifacts/ci/desktop-ui
```

Its assertions cover balanced player columns for two through five players, public card stacks
that remain visible without exposing computer hands, and train-card previews grouped by color.
Grouped cards only need a horizontal scrollbar when they overflow the tray. Update these
assertions alongside intentional presentation changes; focused unit tests do not render the UI.

Camera-flow tests must wait for a canceled action to finish before publishing the next route
observations. Frames received while that action is still pending do not verify a new claim;
waiting longer without fresh frames cannot complete verification before payment.

## Installed-runtime contract

| Traffic or data | Location |
|---|---|
| Referee, AI, cards/decks, saves and photos | Windows laptop and local storage. |
| Phone shell, scripts, fonts/styles/icons and API calls | Bundled files on the same local HTTP origin for Quick play. No CDN or external font service. |
| Joining and pairing | Private IP, local QR and laptop approval. No public pairing service. |
| PRACTICAL | Private laptop choices with other players looking away; no networking UI or listener is required. |
| Runtime cloud services | None: no login, analytics, remote model/download, updater or Internet API. |
| CI/development package restoration | Internet-connected build environment; not installed gameplay. |

The Windows host checks the selected connection's **Private** profile and local interface/subnet,
not whether Windows reports Internet access. The phone keeps working when Internet availability
is reported as offline as long as the laptop's local endpoint responds. Losing the laptop/LAN
covers private cards; the phone cannot continue a separate game from cached state.

Windows/Android/iOS and the browser may perform their own background networking. GoldenTicket
does not need that traffic, control system-wide networking, or promise to disable OS/browser
updates. Its bundled web content restricts its own resource and API requests to the local origin.

For physical acceptance, disconnect the router's WAN while keeping Ethernet/Wi-Fi LAN working;
disable mobile-data fallback for the test. Complete private card/ticket actions, route planning,
laptop physical verification, save/reload, and LAN reconnect. Record exact Quick play results in
[the device evidence plan](companion-device-evidence.md). Separately run PRACTICAL with multiple
humans and networking absent. Certificate or installed-app checks are no longer product gates.

## Reproduce the extra browser test locally

After a normal Release solution build, with Node 24 installed:

```powershell
npm ci --prefix tools/ci --ignore-scripts --no-audit --no-fund
$env:NODE_PATH = Join-Path $PWD 'tools/ci/node_modules'
$env:GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY = Join-Path $PWD 'artifacts/ci/fixtures'
$env:GOLDENTICKET_COMPANION_FIXTURES = Join-Path $env:GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY 'fixtures.json'
$env:GOLDENTICKET_BROWSER_EVIDENCE = Join-Path $PWD 'artifacts/ci/companion-browser'
dotnet test tests/GoldenTicket.Domain.Tests/GoldenTicket.Domain.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~CompanionHostBrowserFixtures
node tests/GoldenTicket.Domain.Tests/CompanionHostBrowserUi.cjs
```

The test uses installed Chrome/Edge and an isolated temporary browser profile. Its HTTP fixture
uses a non-loopback hostname mapped to the local test server so browser secure-context features
remain unavailable, as on a real LAN phone. All other origins are blocked. The scripts and
`tools/ci/node_modules` are developer/test dependencies, not application runtime requirements.
Synthetic fixtures validate page behaviour; physical device and PRACTICAL acceptance remain open.
