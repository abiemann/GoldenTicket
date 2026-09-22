# Multi-human device evidence plan

The current product has recommended HTTP **Quick play** on one shared phone/tablet and
**PRACTICAL** on the laptop. The user has played Quick play on a real Android tablet in Chrome
with the physical board, including a completed two-human and one-computer match on September 21.
This document records that experience alongside automated checks and the specific physical-device
scenarios still to confirm from DESIGN §22.7.

## Hardware and support position

| Platform | Availability | Recorded experience and remaining coverage |
|---|---|---|
| Android tablet / Chrome | Used in the user's September 21 play sessions | Real shared-browser gameplay and a completed physical-board match are confirmed. Exact tablet model, OS/browser versions and WAN state were not recorded; remaining cases are listed below. |
| Windows laptop and UVC camera | Used with the physical board | Camera-assisted physical gameplay is confirmed. A complete PRACTICAL multi-human session with networking unavailable has not been recorded. |
| iPhone/iPad | Not owned; a borrowed session may be possible | Untested. Record hardware and Safari versions when a device becomes available. |

For every run record device model, exact OS/browser versions, laptop build/commit, local network,
whether the WAN was disconnected, and pass/fail with observed errors. Keep private hands, pairing
codes and personal network details out of published screenshots and logs.

## Quick play checklist

Run this on Android Chrome and repeat on iPhone/iPad Safari when hardware is available.

| Check | Required result |
|---|---|
| Laptop and device on the same LAN; router WAN disconnected | Selected Windows adapter is Private and devices can reach each other; Ethernet plus Wi-Fi is valid. |
| Select Quick play and start hosting | A direct HTTP game address and QR appear; no certificate or install screen. |
| Scan with the device's real camera | QR opens the displayed game address. Scripted navigation and repository QR decoders are not scanner evidence. |
| Pair and approve | Separate code is accepted, matching identity is approved on the laptop, and unapproved devices cannot reveal cards. |
| Opening tickets | Each human sees only their own offer and can keep the required selection. |
| Normal card/ticket/route choices | Correct active-seat options work; laptop retains train-placement verification and authoritative game state. |
| First train-card draw | Hand stays visible, count and face-up market update, scroll position remains, and a second legal card can be drawn without another reveal. The second draw or a first face-up locomotive covers the hand for the next turn. |
| Camera-blocked train draw | Checking feedback appears beside the picker. A refused first or second draw leaves the hand, awarded cards and current turn intact; the camera reason and retry guidance appear inline. A successful retry awards exactly one card before any handoff. |
| Held destination map | Tap any held ticket: the row animates into the live board with all that player's destination cities connected by dashed lines. Claim controls move smoothly; Back restores the row and its scroll position. The map remains open through the first draw and clears on Hide, backgrounding, connection loss and handoff. Check portrait, landscape and reduced motion. |
| Place trains before paying | The revealed phone updates to the camera-detected route, offers only its legal payments, and commits after Pay with current board evidence. The hidden phone and public laptop expose no payment choices. |
| Computer placement and score markers | The browser and laptop show the same current placement, correction and score-marker instructions. During the computer's turn the browser replaces Reveal with the upright board image and matching gold train-space dots; correction subsets update, and the map clears when the next human can reveal. Check dots stay aligned in portrait and landscape. |
| Move/remove trains while choosing payment | Pay pauses or the proposal clears; an old proposal cannot spend cards. Phone card draws also wait for the recorded board to match. |
| Pass and hide | Cards clear before handoff; the next human obtains only their own private view. |
| Destination check/uncheck and idle time | Cards and selections remain visible and usable after several minutes without interaction while the page is foregrounded and connected. |
| Outside-control touches, focus changes and scrolling | Cards and selected destinations remain visible while the page stays in the foreground. |
| Background, lock, page departure and return | View resumes covered and fresh authorization is needed. |
| Reload | Fresh pairing works without losing or repeating the current turn. |
| Duplicate tabs or replacement controller | Previous control cannot continue spending cards or revealing a hand. |
| Laptop/LAN loss and recovery | Hand covers, commands stop, reconnection does not replay or invent an action. |
| Live SSE updates | Camera route changes, AI instructions and turn handoffs arrive without a two-second polling delay. No repeated `/api/session` requests appear. Backgrounding closes the stream; returning reconnects covered without restoring a private hand. |
| Completed-match image | Preview loads and Save image creates a readable PNG; sharing through Files/Photos works. |
| Browser opened from another app | Direct browser use remains possible; record any embedded-browser limitation. |
| Accessibility and layout | Touch targets, landscape, zoom, keyboard/assistive navigation where available are usable. |

Record QR-to-play time and any unexpected permissions or setup screens. HTTP traffic is
unencrypted; run on a trusted LAN. No installed-app state, certificate trust, service worker or
native share-sheet success is part of the current acceptance contract.

## PRACTICAL checklist

Use at least two humans on the laptop with networking unavailable. Other players look away when
the active player reveals cards. Verify opening ticket choices, every action type, covering and
handoff, AI secrecy, pause/focus changes, save/reload mid-turn, final scoring and returning to the
menu. Confirm there is no phone-connection gate and no background phone host required for play.

## Run preparation

Follow [phone setup](phone-setup.md) for the Windows Private profile and scoped firewall access.
Prepare a multi-human match and this checklist before a borrowed-device session. Use the
production app, not the retired connectivity experiment. Keep recorded outcomes clearly separate
from planned tests.

## Current status

### Physical play

The user's September 21 screenshots and reports show the Android tablet running the current
browser game with private hands and destinations while the laptop handles the physical board.
The completed two-human and one-computer match is also corroborated by the saved-game analysis in
[computer strategy evidence](ai-strategy-evidence.md). These are real gameplay results, beyond the
earlier certificate-download experiment.

That playtesting also exposed problems, including interrupted private views and confusing feedback
when a camera check refused a card draw. Subsequent fixes have the automated coverage below. The
completed match does not by itself confirm every later fix or every checklist scenario.

### Dated automated checks

The September 21 SSE implementation passed 135 targeted .NET tests, 46 JavaScript client tests,
and 64 Chromium scenarios at 320, 448 and 768 CSS pixels. Checks cover event-only synchronization,
approval, camera/AI updates, first-draw continuity, reconnects and backgrounding. The browser
harness uses synthetic fixtures over ordinary HTTP; these checks do not replace device acceptance.

The September 21 computer-map update passed a warnings-as-errors desktop build, 151 targeted
.NET tests, 55 JavaScript tests and 76 Chromium workflow scenarios at those same widths. Five
additional landscape scenarios passed at 1024 by 768 CSS pixels. Rendered views used synthetic
game state with an existing upright board photo; placement and correction dots were visually
checked against it. Automated coverage also checks map continuity through computer scoring,
camera loss, bounded frame updates and clearing at human handoff. Evidence is in
`artifacts/companion-computer-map-20260921/`; this remains browser simulation, not physical-tablet
or live-camera acceptance.

The destination-map update passed 161 targeted .NET checks and 62 JavaScript tests. Twenty-one
focused Chromium scenarios covered 320, 448, 768 and 1024-pixel layouts, plus intermediate phone
widths. They verify smooth height changes, the Back touch target, all held destination connections,
matching image/overlay frames, preserved scroll through the first draw, and immediate privacy
cleanup. Another 15 computer-map and 27 card/SSE workflow regression scenarios passed, for 63
browser checks in total. Phone and tablet renders were visually checked. Evidence is in
`artifacts/companion-destination-map-20260921/`; a physical-tablet check of this animation and its
privacy/scroll behavior has not been recorded.

The blocked-draw follow-up passed a warnings-as-errors build, 164 targeted .NET checks,
70 JavaScript client tests and 20 Chromium workflows at 320 and 768 CSS pixels. Checks cover
rejected first/second draws retaining the same hand and turn, exact-card retries before handoff,
stale/duplicate/concurrent requests, and inline feedback without losing scroll position. The
separate standings adjustment passed 12 actual WPF render cases for 2–5 players, including small
windows and full-text exports, with equal card heights and no binding warnings. Evidence is in
`artifacts/card-draw-audit-20260921/`, `artifacts/companion-blocked-draw-20260921/` and
`artifacts/final-equal-height-20260921/`. These are bounded automated checks, not a new
physical-tablet acceptance run.

### Physical checks not yet recorded

The available reports do not confirm the following cases. An unrecorded case is not a claim that
the user has never tried it.

- iPhone/iPad Safari joining and gameplay, including its download and lifecycle behavior.
- A fresh real-camera QR scan, pairing and a full Quick play session with the router's WAN
  physically disconnected.
- A complete PRACTICAL multi-human game with networking unavailable.
- Device lock, sleep, app switching and history restoration; deliberate laptop/LAN loss,
  reconnect, DHCP changes, and controller replacement during play.
- Saving the final PNG and sharing the saved file through Android Files/Photos.
- The full portrait/landscape, enlarged-text, accessibility and reduced-motion checklist on
  physical devices.
- A physical-tablet retest of the latest blocked-draw fixes and destination-map transitions,
  including unchanged hand/turn after a refused draw and exactly one awarded card after retry.

Record device model, OS/browser versions, app revision and WAN state for these follow-up runs.
Historical certificate/PWA reports remain dated evidence of the discarded approach and impose no
current installation requirement.
