# Windows installer and v1.0.0 release

GoldenTicket's per-user Windows 11 x64 Setup file wraps the same self-contained payload as the
[portable ZIP](offline-package.md). It installs the game, browser client, board data, recognition
models, .NET/WPF/ASP.NET Core runtimes, documentation and package notices. Setup requires no
administrator account and does not download prerequisites. It adds Start Menu launch/uninstall
shortcuts. Saved games and board photos live separately in `%LOCALAPPDATA%\GoldenTicket`, which
the uninstaller does not remove.

The first installer is **unsigned**. Windows may show an unknown-publisher warning; it must not
disable SmartScreen, camera permissions, or firewall checks. There is no automatic updater. The
current clean-machine, physical-camera and LAN/device acceptance remains to be recorded in
[release readiness](../DESIGN.md#24-release-readiness-and-remaining-evidence).

## Build from the release source commit

Before tagging or building, update and commit README, TODO, DESIGN and affected documentation
against the final implementation. Keep the source tree clean. Run the full Windows CI checks on
that commit; the CI workflow itself does not publish assets.

1. On Windows x64 with the pinned SDK, build the verified ZIP with
   `pwsh -NoProfile -File tools/Build-OfflinePackage.ps1`. Keep the exact
   `artifacts/release/<run>/package-result.json` path printed by that run.
2. Install the [official Inno Setup 7.1.0 x64 compiler](https://jrsoftware.org/isdl.php) on the
   build machine. Verify the downloaded installer has a valid Authenticode signature from
   **Pyrsys B.V.**; the verified release download has SHA-256
   `0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f`.
   Inno Setup is a build tool and is not shipped to players.
3. Run the installer builder with the result from step 1:

   ```powershell
   pwsh -NoProfile -File tools/Build-WindowsInstaller.ps1 `
     -Version 1.0.0 `
     -PackageResultPath 'artifacts/release/<run>/package-result.json' `
     -InnoCompilerPath 'C:\Program Files\Inno Setup 7\ISCC.exe'
   ```

The builder checks that the ZIP, every payload file, the provenance and the desktop version
match the current committed source. It requires Inno Setup 7.1.0 and refuses to replace an
existing installer. It writes `GoldenTicket-Setup-1.0.0-win-x64.exe`, its `.sha256`, a compiler
log and `installer-result.json` beside the ZIP. The `AppId` in
[`tools/GoldenTicket.Installer.iss`](../tools/GoldenTicket.Installer.iss) is stable across future
versions so later installers can upgrade the same per-user installation.

Attach the installer, portable ZIP, and both checksum files to the GitHub release for the source
tag. Match the release tag, source commit, `package-result.json` and `installer-result.json` before
uploading. Release notes should state that the installer is unsigned and list the physical/device
checks still pending; a green build does not prove clean-machine acceptance.

## Acceptance on another Windows 11 x64 computer

Use a fresh user or machine without a separately installed .NET runtime. Check the downloaded
installer's hash, install without elevation or a runtime download, launch from the Start Menu,
and play without Internet. Check a camera preview, a complete match, save/reload, and a phone on
the trusted Private LAN. Then upgrade with a future version using the same `AppId`; uninstall and
confirm the Start Menu entry and installed files disappear while the saved match and board photo
remain under `%LOCALAPPDATA%\GoldenTicket`. Record Windows build, hardware, camera, phone/browser,
source commit and observed results. The current release does not claim those steps passed until
their actual results are recorded.
