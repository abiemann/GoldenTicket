# Build in Visual Studio; run on the LAN

GoldenTicket is developed in Visual Studio and continuously checked by GitHub Actions. After the
Windows application and any required runtime prerequisites are installed, gameplay uses only the
laptop and local network. GitHub is not a game server and is not consulted at application launch.

## Visual Studio

1. Install the .NET SDK recorded in `global.json` and a compatible Visual Studio installation with
   **.NET desktop development**. Restore packages while developer Internet access is available.
2. Open `GoldenTicket.sln` and select **GoldenTicket.Desktop** as the startup project.
3. Build/run normally. Test Explorer can run the .NET regression suite. DPAPI tests need an ordinary
   Windows user profile; a restricted impersonated profile can fail data protection.

The SDK/build prerequisites are development tools. For a framework-dependent installation on
another laptop, install the matching .NET 10 Windows Desktop and ASP.NET Core runtimes first.
An eventual installer must bundle or provision prerequisites during installation; installed gameplay
must not download them. No Node/npm/Playwright dependency is shipped in the Windows app or PWA.

## GitHub Actions

[Windows CI](../.github/workflows/windows-ci.yml) runs on pushes to `main`, pull requests and manual
**Run workflow** requests. One bounded Windows job:

1. Checks out source without persisting repository credentials.
2. Installs the SDK from `global.json` and Node 24 for development tests.
3. Restores NuGet and the development-only Playwright dependency from committed lock files.
4. Builds the complete solution in Release, including the WPF Windows application.
5. Runs .NET regression tests, including actual local HTTPS and Windows DPAPI checks.
6. Runs companion JavaScript privacy/LAN tests and 20 complete simulated matches with replay checks.
7. Renders production WPF views and exercises the PWA in the runner's installed Chrome/Edge, using
   synthetic game fixtures exported by the .NET tests. Non-laptop browser origins are blocked.
8. Retains test reports and synthetic UI screenshots for seven days, including partial evidence
   when a later check fails. It does not upload game saves, certificates, dumps or player photos.

The job uses `windows-2025`, read-only repository permissions, immutable official-action commit
pins and no project secrets. New runs cancel superseded runs for the same PR/ref, and the job has
a 20-minute limit. It does not change repository visibility, publish releases, produce an app ZIP,
or configure end-user Windows firewalls/certificate trust. CI uses the repository's GitHub Actions
allowance; no running server or subscription is introduced for players.

The runner builds/tests Windows-targeted code but does not establish Windows 11 hardware support.
Actual camera, phone trust/install, OS lifecycle, display scaling and physical-board acceptance
remain manual. Browser smoke uses loopback as its local secure origin; it is not evidence that a
particular phone has installed or trusted the laptop's certificate.

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
| Phone shell, scripts, fonts/styles/icons and API calls | Bundled files served from the same local HTTPS origin. No CDN or external font service. |
| Discovery and pairing | Local mDNS/private IP, local QR, laptop approval and local certificates. No public pairing service. |
| Certificate creation/validation inside GoldenTicket | Per-laptop authority; no online issuer, OCSP/CRL download or public trust lookup. |
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
laptop physical verification, save/reload, and LAN reconnect. Repeat installed-PWA and browser
fallback contexts separately and record exact device results in [phone setup](phone-setup.md) and
[the acceptance walkthrough](IMPLEMENTATION-2026-09-12.md). Certificate transfer/approval and
initial installation remain distinct from steady-state LAN gameplay.

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

The test uses installed Chrome/Edge and an isolated temporary browser profile. The scripts and
`tools/ci/node_modules` are developer/test dependencies only. They are not part of application
installation or required for LAN play.
