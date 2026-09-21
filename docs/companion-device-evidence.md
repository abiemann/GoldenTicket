# Multi-human device evidence plan

The current product has recommended HTTP **Quick play** on one shared phone/tablet and
**PRACTICAL** on the laptop. Real-device acceptance remains required by DESIGN §22.7. Automated
browser rendering and network tests do not establish phone support.

## Hardware and support position

| Platform | Availability | Claim allowed before acceptance |
|---|---|---|
| Android phone/tablet | Owned; earlier development used a Pixel 8 Pro | Quick play remains unverified until the checklist below passes on recorded hardware/browser versions. |
| Windows laptop and UVC camera | Owned | Automated UI checks exist; full PRACTICAL and physical-board sessions remain manual gates. |
| iPhone/iPad | Not owned; a borrowed session may be possible | Untested. A successful borrowed session is one recorded data point, not evidence of broad ongoing support. |

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
| Place trains before paying | The revealed phone updates to the camera-detected route, offers only its legal payments, and commits after Pay with current board evidence. The hidden phone and public laptop expose no payment choices. |
| Move/remove trains while choosing payment | Pay pauses or the proposal clears; an old proposal cannot spend cards. Phone card draws also wait for the recorded board to match. |
| Pass and hide | Cards clear before handoff; the next human obtains only their own private view. |
| Destination check/uncheck and idle time | Cards and selections remain visible and usable after several minutes without interaction while the page is foregrounded and connected. |
| Outside-control touches, focus changes and scrolling | Cards and selected destinations remain visible while the page stays in the foreground. |
| Background, lock, page departure and return | View resumes covered and fresh authorization is needed. |
| Reload | Fresh pairing works without losing or repeating the current turn. |
| Duplicate tabs or replacement controller | Previous control cannot continue spending cards or revealing a hand. |
| Laptop/LAN loss and recovery | Hand covers, commands stop, reconnection does not replay or invent an action. |
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

Quick play and PRACTICAL implementation have automated coverage; full current-mode device
acceptance is pending. Earlier September 12 Android evidence established reachability of the old
HTTP certificate-download page only. It does not establish the current browser game's QR,
private-hand, download or reconnect behaviour. Historical certificate/PWA reports remain dated
evidence of the discarded approach and impose no current installation requirement.
