# Companion device evidence plan

DESIGN §22.7 requires real-device evidence before claiming support for a platform, and §24.2 lists
local PWA installation and trust as open until that evidence exists. This file records what must be
captured, and the honest support position given the hardware actually available.

## Hardware position

| Platform | Availability | Consequence |
|---|---|---|
| Android phone/tablet | Owned | Can be tested repeatedly during development. Android support can be claimed if the checklist passes. |
| Windows laptop + UVC camera | Owned | Covers the host side and, later, M3–M5. |
| iPhone / iPad | **Not owned.** A friend may lend one for a single session. | iOS/iPadOS support **cannot be claimed at release** on current hardware. If a borrowed session happens and passes, the claim becomes "tested once, on the recorded OS and hardware" — not "supported". |

### What the release may say

- **Android:** setup is in progress on a Pixel 8 Pro; companion acceptance remains unverified.
  Say supported only after the real-device
  checklist passes, with the exact OS and Chrome versions recorded.
- **iOS/iPadOS:** *untested* — not "unsupported". The companion is built to web standards and will
  probably work, but DESIGN §22.7 does not let an untested platform be advertised. Say so plainly in
  the known-limitations section rather than omitting iOS.

A single borrowed-device session is **not** equivalent to the repeated testing Android gets. If it
passes, record it as one data point with its date, OS version and device model, and keep the support
claim proportionate.

## Sequencing risk, accepted knowingly

§22.7: *"M0 must prove certificate trust, local-origin resolution, and offline installation behavior
on actual iOS and Android before substantial companion UI work."*

With no iPhone to hand, that gate cannot be met for iOS before the companion is built. The accepted
position is:

1. Build the M0 connectivity spike first, and prove it on Android.
2. Build the companion against Android evidence.
3. Run the iOS checklist below in one borrowed session, as early as one can be arranged.
4. If iOS fails, fix it if the fix is small, or ship Android-only and record the iOS failure.

The spike must therefore be a **standalone, scriptable checklist** with no game UI, so a borrowed
device can be taken through it in about fifteen minutes.

## What every session must record

Without these, the run is not evidence:

- Device model, OS version, browser version (exact, not "latest").
- Router/network description, and confirmation that the **WAN was disconnected** for the offline steps.
- The laptop's build id and the certificate fingerprint shown at pairing.
- A pass/fail per checklist line, with the observed failure text where it failed.

## Android checklist

Run this repeatedly during development; it is the platform with routine access.

| # | Step | Pass condition |
|---|---|---|
| A1 | Join laptop and device to the same trusted LAN, WAN unplugged; laptop Ethernet plus phone Wi-Fi is valid | Both on the same subnet; the selected Windows connection is classified **Private** |
| A0 | **Scan the QR on the laptop console with the phone's own camera** | The camera offers the bootstrap address and opens it. This is the acceptance test for the locally generated symbol; nothing else proves it |
| A2 | Install the laptop's generated CA on the device | Certificate appears under user credentials |
| A3 | Open the laptop origin in Chrome | Padlock shown, **no** interstitial, no bypass used |
| A4 | Check secure-context status on the page | `window.isSecureContext === true` |
| A5 | Resolve the `.local` hostname | Resolves without the IP fallback, **or** the IP fallback is exercised and recorded as required |
| A6 | Register the service worker | Registration succeeds; shell cached |
| A7 | Reload with the laptop stopped | Cached reconnect screen appears; no game state is invented |
| A8 | Install / Add to Home Screen | Launches standalone, or the shortcut fallback is recorded honestly |
| A9 | Pair inside the launched context | Pairing code accepted; laptop confirms the same identity |
| A10 | Background, lock, return | View resumes **covered**; a fresh private-view grant is required |
| A11 | Open the address from inside a messaging app (paste it into a chat to yourself, tap it) | The handoff gate appears instead of the checks, and **Open in Chrome** leaves the in-app browser |

Known Android risk: `.local` name resolution is historically weaker than on iOS. If A5 needs the IP
fallback, that is a finding to record, not a workaround to hide — DESIGN §18.5 requires the
IP-origin caveat to be explained to the user.

A0 is listed first because it is the cheapest thing to get wrong. The QR encoder is written here
rather than taken from a package (DESIGN §18.3 requires bundled generation, not a custom encoder).
It is checked against published capacity, format, version and alignment tables and read back with
an in-repository test decoder and Reed-Solomon syndrome check. That decoder shares some encoder
metadata; it is not independent scanner evidence, and none of those checks is a phone camera.

## iOS / iPadOS checklist (borrowed device, one session)

Same lines as Android, plus the platform-specific ones. Prepare the laptop **before** the friend
arrives: server running, CA export ready, pairing screen open, this file printed or on screen.

| # | Step | Pass condition |
|---|---|---|
| I1 | Install the CA profile | Profile installs via Settings |
| I2 | **Enable full trust**: Settings → General → About → Certificate Trust Settings | The CA toggle is present and can be enabled. DESIGN §18.5 calls this out specifically: installing the profile is not enough |
| I3 | Open the laptop origin in Safari | Padlock, no interstitial, no bypass |
| I4 | Secure context | `window.isSecureContext === true` |
| I5 | `.local` resolution in Safari | Resolves (Bonjour is usually strong here) |
| I6 | Service worker registration + shell cache | Succeeds with WAN disconnected |
| I7 | Add to Home Screen via the Safari share sheet | Launches standalone |
| I8 | **Pair inside the home-screen app**, not the Safari tab | Pairing succeeds there. DESIGN §18.5 step 5 warns not to assume the tab and the home-screen app share cookies or storage — this line is the whole reason to check |
| I9 | Background / task switcher / return | Resumes covered; fresh grant required |
| I10 | Reopen after the laptop restarts | Reconnect guidance; no stale private data |
| I11 | Scan the laptop QR with the iOS camera | The camera offers the bootstrap address and opens it in Safari |
| I12 | Open the address from inside a messaging app | The handoff gate appears with Safari share-sheet instructions, and the copy-address button works |

If the session runs short, I2, I6 and I8 are the three that cannot be inferred from Android and
must not be skipped.

## Running the spike

Follow [Connect a phone or tablet on the local network](phone-setup.md) for the Windows Private
profile, scoped firewall, certificate trust, and installation steps. Its dated setup record tracks
the first Pixel connection attempt and distinguishes authorization from verified changes.

```powershell
dotnet run --project tools/GoldenTicket.ConnectivitySpike -- --address <laptop private IP>
```

It prints the origin, the CA fingerprint to compare aloud, a pairing code, and a QR code for the
address the device should land on next. Keys while it runs: `n` new pairing code, `b` close the
certificate bootstrap, `a` re-announce the local name, `c` redraw the connection QR, `s` save a
report, `q` quit. A report is written to `docs/evidence/m0-connectivity/` on exit either way.

The QR carries **only** the landing address, never the pairing code (DESIGN §18.5), so a photograph
of the laptop screen gives nothing away on its own. While the certificate bootstrap is open the
symbol points at `http://<ip>:8080/`; pressing `b` closes the bootstrap and redraws it pointing at
the trusted HTTPS origin.

Useful switches: `--port` and `--bootstrap-port` if something else holds 8443 or 8080, `--no-mdns`
to force the IP fallback and see what a device does without the `.local` name, and `--qr <text>` to
draw one symbol and exit — worth running before a borrowed-device session to confirm the console's
font and window size produce something a camera can actually read. If the printed symbol will not
scan, the console also writes a larger one to
`%LOCALAPPDATA%\GoldenTicket\companion-host\connect\connect.svg`, which any browser will open.

If the phone cannot reach the laptop, the console prints the exact `netsh` rule to allow the ports
on the **private** profile only. The spike never changes the firewall itself.

### Before the friend arrives

1. Laptop and phone on the same LAN, WAN unplugged; the selected Windows connection is Private.
2. Spike running, bootstrap page open on the laptop so you can read the fingerprint.
3. This checklist to hand.
4. Know which of the two origins you are testing: the `.local` name, or the IP fallback.

## Current status

| Layer | State |
|---|---|
| Laptop side | **Works.** Verified on this machine: the generated chain validates by name and by IP with no bypass, the bootstrap serves only the public CA, an unknown `Host` is refused with 421, a cross-origin or header-less POST is refused with 403, API responses are `no-store`, the shell is served under a same-origin CSP, and the pairing round-trip issues an HttpOnly/Secure cookie that a later request recognises. mDNS advertisement started. |
| Connection QR | **Generated and read back in repository tests.** Capacity, format, version and alignment checks and a Reed-Solomon syndrome check pass. Earlier implementation notes report a separate decode of four landing addresses, but no standalone decoder artifact is retained here; this audit reproduced the in-repository tests only. **No recorded phone-camera scan** — see checklist line A0. |
| Handoff and install order | **Behaviour verified in a desktop browser** against simulated user agents for eight in-app browsers and for real Chrome and Safari, at a 375-pixel viewport. Not verified on a phone. |
| Android | Pixel 8 Pro, Android 17 / SDK 37, Chrome `151.0.7922.108`, tested on 2026-09-12. After the verified Private-profile and scoped firewall changes, the phone loaded the HTTP bootstrap over Wi-Fi LAN. Chrome's insecure HTTP download was discarded; only the public CA was then transferred over authorized USB, with matching SHA-256 hashes on phone and laptop. The phone is waiting for the user's explicit Android CA-trust confirmation. HTTPS trust, installed PWA, pairing, and offline acceptance remain pending. See the [setup event](phone-setup.md#setup-event-2026-09-12-pixel-8-pro). |
| iOS/iPadOS | Not run, and no device available. |

The laptop-side checks say the host is ready to be pointed at a phone. They say nothing about
whether a phone will trust it, resolve its name, or install it offline - which is the entire point
of the spike, and needs the real devices.
