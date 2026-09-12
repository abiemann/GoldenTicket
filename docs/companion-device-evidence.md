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

- **Android:** supported, with the exact OS and Chrome versions recorded.
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
| A1 | Join laptop and device to the same private Wi-Fi, WAN unplugged | Both on the same subnet |
| A2 | Install the laptop's generated CA on the device | Certificate appears under user credentials |
| A3 | Open the laptop origin in Chrome | Padlock shown, **no** interstitial, no bypass used |
| A4 | Check secure-context status on the page | `window.isSecureContext === true` |
| A5 | Resolve the `.local` hostname | Resolves without the IP fallback, **or** the IP fallback is exercised and recorded as required |
| A6 | Register the service worker | Registration succeeds; shell cached |
| A7 | Reload with the laptop stopped | Cached reconnect screen appears; no game state is invented |
| A8 | Install / Add to Home Screen | Launches standalone, or the shortcut fallback is recorded honestly |
| A9 | Pair inside the launched context | Pairing code accepted; laptop confirms the same identity |
| A10 | Background, lock, return | View resumes **covered**; a fresh private-view grant is required |

Known Android risk: `.local` name resolution is historically weaker than on iOS. If A5 needs the IP
fallback, that is a finding to record, not a workaround to hide — DESIGN §18.5 requires the
IP-origin caveat to be explained to the user.

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

If the session runs short, I2, I6 and I8 are the three that cannot be inferred from Android and
must not be skipped.

## Current status

Nothing here has been run. The embedded host and the PWA do not exist yet, so there is nothing to
test. This file exists so the evidence requirements are fixed **before** the code, and so a borrowed
device is not wasted on an unprepared session.
