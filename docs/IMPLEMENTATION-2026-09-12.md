# September 12 implementation update and acceptance walkthrough

This is an implementation record for the follow-up to the September 12 audit. It does not mark
the complete DESIGN finished. Physical train placement still requires the operator's whole-board
confirmation. The source and automated UI fixtures contain no real player's saved private cards.

## What is implemented

### Windows shell and private phone play

The desktop now has **Game table**, **Camera**, **Connect phone**, and **Saved board photo**
navigation. Navigating away closes the laptop private view. Returning to the game preserves the
current setup, table, rebuild, or final-results screen.

`GoldenTicket.CompanionHost` serves the game companion from the laptop over HTTPS on the selected
Windows Private network. Pairing requires a short-lived code followed by explicit approval of the
same device identity on the laptop. There is one shared controller; replacing it revokes the old
controller. Only the expected human seat may reveal cards or submit digital actions.

The phone can choose opening destination tickets, see its own train cards and tickets, draw visible
or blind train cards, request/keep destination tickets with return ordering, and authorize a route
with an explicit payment. The existing coordinator validates and durably journals every action.
Physical placement/cancellation, rules decisions, save/rebuild, and other administrative operations
remain on Windows. A phone cannot issue those commands through the action endpoint.

Private grants bind the controller, human seat, game, state version and handoff generation. They
expire after 30 seconds and lose authority after a six-second heartbeat gap. The client removes
private DOM on Hide, blur, backgrounding, navigation and disconnection. Late responses cannot
reveal an old hand after Hide. The laptop's idle timeout applies only to its own visible private
view, so a covered idle laptop does not repeatedly revoke phone play.

API responses are no-store; the service worker caches only its explicit public shell assets. Game
snapshots, hands, ticket choices and action queues are not persisted in browser storage. TLS keys
remain DPAPI-protected on Windows. Requests have Host/Origin/subnet/Private-profile checks, CSRF
protection, bounded bodies/connections and rate limits. HTTP certificate sharing is temporary and
has a separate close control.

### Camera tools

`GoldenTicket.Vision` uses Windows video-only acquisition, reports real camera formats, and offers
balanced up-to-1080p, high-detail up-to-4K and shared-current-format choices. It keeps one latest
owned BGRA frame with a sequence, monotonic timestamp and camera epoch. Capture has bounded
startup/stop waits and clears stale/disconnected evidence.

The Camera screen provides preview, four manually selected board corners, a projective board crop,
and a scene-reference comparison. Several fresh stable frames are required before the comparison
reports similarity. Camera changes, stale frames, insufficient detail and scene changes hold photo
capture until corrected. This is a conservative scene check, **not train or route recognition**.
The UI accurately reports CPU image processing; there is no inference model or active GPU backend.

Actual Windows enumeration during this session found **USB2.0 HD UVC WebCam**, but the connected
Pixel was not exposing a webcam stream in its current USB mode. No unrelated room camera was
opened. Pixel webcam mode and actual board capture remain physical acceptance steps.

### Saved board reference photos

After the digital checkpoint has been saved and read back successfully, the operator may attach a
fresh, cropped camera reference. A checkbox confirms that the whole image contains the committed
board target, with private cards/hands and uncommitted trains removed.

Each photo is an immutable encrypted sidecar bound to the exact checkpoint content, logical and
physical hashes, source versions, camera identity/epoch and manual crop revision. AES-GCM protects
its contents; a per-photo random key is protected with the Windows account's DPAPI. The store
validates PNG structure/decompression/dimensions, bounds bytes/pixels, rejects linked paths, writes
atomically, and authenticates complete readback before reporting success. Failed/missing/corrupt
photos leave the digital checkpoint and saved route list available.

If storage completed but its confirmation was interrupted, **Reload reference** reads the durable
attachment without taking another photo. An uncertain attempt requires this readback before any
recapture, preserving the immutable checkpoint association.

These are explicitly **operator-attested reference photos**. The checkpoint remains
`LogicalStateOnly`; no machine-verified photograph, pending-placement mask, or automatic board
rebuild validation is claimed. A different reference requires a new checkpoint.

### Offline distribution foundation

The [package builder](offline-package.md) prepares a self-contained Windows x64 ZIP from committed
documentation and source. The first package attempt exposed a runtime-specific NuGet lock mismatch;
separate reviewed Windows-runtime lock files now preserve the normal development locks. Both lock
graphs restore successfully in locked mode.

The executable has an explicit windowless `--check-package` diagnostic. It exercises WPF, native
SQLite, account DPAPI, Windows PNG encoding, ASP.NET construction and shipped assets without
opening a camera, network listener or player save. It also requires CoreCLR to load from the
package directory. Before publication, its development-build negative check correctly detected
the framework-dependent runtime while the other seven component checks passed. The builder runs
all eight checks against the published executable before archiving. Actual package results are
recorded [in package evidence](evidence/offline-package-2026-09-12/README.md): both normal and
cache-only builds passed, as did all eight executable checks and every archived payload hash.
A clean-machine/manual walkthrough is still required.

## Deliberate implementation limits

- The phone uses bundled plain JavaScript with no npm runtime/build dependency. The planned
  TypeScript client migration is unfinished.
- Public synchronization polls an authoritative snapshot every two seconds. WSS/event cursors,
  durable device registration and seamless controller recovery remain unfinished.
- Reloading the phone page creates a fresh tab identity and requires new laptop-approved pairing.
  Reopening never silently restores a private hand. A 30-second reveal and explicit Hide are
  implemented; hold-to-peek is still absent.
- Name discovery and IP fallback are exposed separately. An IP change creates a different origin
  and can require installing/pairing again. Real `.local` resolution needs device testing.
- Windows setup offers instructions and a scoped firewall command. It does not silently change
  network profiles, firewall policy or phone trust. The previous test rule allowed the diagnostic
  executable; the integrated `GoldenTicket.exe` needs its own correctly scoped rule if blocked.
- Camera landmarks, train recognition, gesture wakeup, learned models, automatic CPU/GPU inference,
  measured physical recovery and a geometry-based rebuild diagram remain unfinished.
- Complete encrypted state snapshots/migration backups, portable exports and sampled-lookahead AI
  remain DESIGN gaps. Voice, Training/Story and audio remain last, as requested.
- The packaging workflow creates a self-contained Windows x64 ZIP. Installer, clean-machine,
  license/provenance signoff and full release acceptance remain separate checks.

## Automated validation

Final validation: **432/432 .NET tests**, **29/29 JavaScript behavioral tests**, **21/21 browser
scenarios**, and **12/12 WPF rendering cases** passed, with no skipped tests. Locked restore and
Release build passed. Full compilation still reports the existing xUnit cancellation-token
advisories; these are not test failures. Retained [test and dependency results](evidence/implementation-2026-09-12/README.md)
identify the commands and limits.

The integration includes regression tests for the rules, persistence, desktop navigation/privacy,
camera primitives, actual camera PNG encoding, encrypted photo attachment and companion security.
The companion transport tests use actual Kestrel HTTPS and a custom trusted test-root chain;
they do not bypass certificate errors or install a machine-wide trust entry.

Final review corrected two companion races: a queued command now retains the exact authorization
cancellation lease validated before dispatch, and an older reveal response cannot display a hand
after a newer observed laptop handoff/revocation. Regression tests cover both. The PWA asset/cache
version was advanced so an installed older client must update before playing.

Browser UI tests use a headless Chrome process, real shipped PWA assets and fixtures exported from
the actual game bridge. The loopback test origin is a secure context. Those tests prove browser
behavior and layout against fixtures, not phone certificate trust, QR scanning or WAN-disconnected
device installation. WPF render checks construct the production views with application resources
in an isolated STA process; they are not native mouse/touch/hardware acceptance.

Native desktop automation could not run because the computer-use app approval timed out. The
following manual checklist is therefore still required even when rendered UI checks pass.

Retained visual evidence is available for the [Windows views](evidence/desktop-ui-2026-09-12/README.md)
and [phone-sized browser views](evidence/companion-browser-2026-09-12/README.md). The headless
simulator completed 20 four-seat matches (seeds 1–20), preserving all invariants and exact journal
replay equality. The connected NuGet vulnerability query found no reported vulnerable direct or
transitive package in the solution on September 12; this is a dependency-feed result, not a claim
that application security is complete.

## Morning acceptance checklist

Use a new test match first. Do not replace a valued save or clear a board until its current digital
checkpoint is verified and any desired reference photo has completed its separate readback.

1. **Open Windows app.** Build/run `src/GoldenTicket.Desktop`, or extract the validated offline ZIP
   into a writable folder and run `GoldenTicket.exe`. Check the four navigation buttons, keyboard
   focus and readable button labels at your normal display scaling.
2. **Start a test match.** Set two human seats and optionally AI seats. Agree to manual whole-board
   verification. Select opening tickets on the laptop to check the fallback privacy curtain.
3. **Prepare phone trust.** The public certificate was copied to the Pixel's
   `Downloads/GoldenTicket-laptop-CA.crt` and its hash was verified. Its Android CA confirmation
   was left for the user; no successful installation is assumed. Complete that explicit trust
   decision and authentication on the phone. See [phone setup](phone-setup.md).
4. **Start companion hosting.** On **Connect phone**, select the actual Private LAN and start.
   If blocked, inspect the displayed firewall command for this executable and run it as
   Administrator. Keep the rule limited to this app, selected interface/IP, Private profile and
   local subnet. Close certificate sharing once transfer is complete.
5. **Verify HTTPS and QR.** Scan the QR physically. Open the `.local` address without a certificate
   warning; if it fails to resolve, record that and use the displayed IP fallback. Never tap through
   an HTTPS warning. Record exact phone OS/browser and the address that worked.
6. **Install then pair.** Use the phone's home-screen installation flow if offered. Otherwise use
   the explicit browser fallback and record it as such. Enter a fresh laptop code, compare the
   identity on both displays, approve on Windows, and return the laptop to **Game table**.
7. **Exercise human actions.** Reveal only the active human's view, make opening ticket choices,
   draw visible/blind cards including the second draw, keep new tickets, choose a route/payment,
   and check that the laptop waits for physical placement. Confirm the whole board on the laptop
   and verify the next seat receives the device covered.
8. **Check privacy and recovery.** Hide while a reveal is loading; background/lock the phone; wait
   past the private timeout; interrupt LAN connectivity. Every return must be covered. Reload and
   pair again using a new code. Verify no duplicate card/action after an uncertain response.
9. **Check offline behavior.** Keep LAN working but disconnect WAN and disable other phone Internet
   paths for this test. Check initial setup/install fallback, normal play and reconnect. Stop the
   laptop and reload: only the cached reconnect shell should appear, with no private hand or
   invented game progress. Record these separately from Internet-connected browser tests.
10. **Enable Pixel webcam mode.** Choose **Webcam** in the Pixel USB options. Open **Camera**,
    refresh, select the actual Pixel stream and start. Fit the whole board, choose its four corners
    in the displayed order, and set a scene reference with hands clear. Check image detail and
    stability. A second device may be needed to use a passed-around PWA while this phone is mounted.
11. **Jog/reconnect.** Move the camera briefly and restore it. Check stale/changed-scene holds and
    similarity recovery. Unplug/reconnect and select new corners/reference. These checks do not
    verify trains: continue whole-board manual confirmation for every move.
12. **Save/photo/rebuild.** Save and pack a test match, then choose **Add or view board photo**.
    Set a fresh scene reference for this final board if earlier moves changed it. Confirm
    board-only committed content and capture. Verify the saved image appears after
    authenticated readback. Restart, load the checkpoint, view the photo and rebuild from the route
    list. Confirm the whole board, resume, and check the exact suspended turn/cards/score.
13. **Package acceptance.** Test the ZIP on a separate Windows 11 account/machine without .NET
    installed and with Internet unavailable. Check camera/native dependencies, local hosting,
    upgrades and preserved saves. Existing developer-machine runtime tests do not prove this gate.

Record failures and exact observed behavior in [the device checklist](companion-device-evidence.md).
iOS/iPadOS remains untested. No mobile-platform support or fully automatic board tracking should be
advertised from these automated tests alone.
