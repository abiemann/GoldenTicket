# Playing with multiple humans

On **How will you play?**, choose how the humans will view their cards:

| Mode | How to play | Needed |
|---|---|---|
| **Quick play** (recommended) | Scan the laptop QR, join in a normal browser, and pass the phone/tablet between players. | One shared phone/tablet and the same trusted local network as the laptop. |
| **PRACTICAL** | Use the laptop for each human's private cards. Everyone else looks away while that player chooses. | Just the laptop; no phone or network setup. |

The Windows laptop is the referee and save owner in both modes. Quick play needs no app,
certificate, account or Internet connection. It uses unencrypted HTTP on the trusted LAN;
laptop approval and private-view controls do not encrypt network traffic. Use PRACTICAL when a
shared phone or suitable network is unavailable.

## Quick play: join and play

1. Connect the laptop and phone/tablet to the same trusted local network. Ethernet on the laptop
   and Wi-Fi on the phone is fine. Avoid isolated guest networks.
2. Choose **Quick play** on the multi-human game screen. Select the Windows connection used by
   the phone and start hosting explicitly. It must have the **Private** network profile.
3. Scan the displayed QR with the phone's camera, or type the displayed local address. It opens
   the game directly in the browser, normally `http://<laptop-address>:8080/companion/`.
4. Enter the pairing code shown on the laptop. Approve the matching connection on
   the laptop, then return it to the game table.
5. Pass the device to the named player. Reveal only that player's cards, make the choice, then
   hide the hand before passing it on. The laptop checks physical train placement.

Keep the laptop and browser open. Reloading the page currently requires fresh pairing. If the
phone loses the laptop connection, its hand is covered and actions stop; it cannot play a second
game from remembered data. Stopping hosting revokes the controller. A changed laptop IP requires
opening the new address and pairing again.

The laptop pushes game changes, camera-detected routes and instructions through a live SSE
connection over the same local HTTP address. The browser no longer polls for game snapshots.
Small heartbeat messages keep track of the connection; if it drops, cards cover and the browser
reconnects automatically with the current game state. Reveal again to continue. No actions are
replayed, and no HTTPS certificate or installation is needed.

The pairing code stays valid while hosting is running, including after a phone joins. Reuse it
after a browser reload or when connecting a replacement device. It changes only when hosting
restarts or you choose **New pairing code**; each new connection still needs laptop approval.

Cards stay visible without an inactivity timeout while the page remains open and connected.
Destination selections are preserved, and tapping elsewhere on the page or scrolling does not
hide the hand. Leaving the page, putting the browser in the background, losing the connection
or changing turns still covers it; use **Hide** before passing the device.

After the first train-card draw, your hand stays open and the hand count and face-up cards
update in place. Choose your second card without revealing again or losing your scroll position.
Drawing destination tickets also opens the offer directly during your turn. When the turn ends,
the cards are covered for the next player.

Your destination tickets appear in one horizontal row. Swipe or scroll through the cards to see
each starting city, destination city and point value without lengthening the page.

To claim a route with the camera, place your trains on the board. Once the laptop verifies the
route, the revealed browser view automatically shows its name and legal payment choices. Choose
the cards to spend and press **Pay**. This is the route you placed, not a recommendation. If the
camera needs another look, payment waits; correcting or removing the trains updates the browser.
Card draws stay blocked while unclaimed or misplaced trains need attention. Payment choices
remain private on the phone, and detecting a route never uncovers a hidden hand automatically.
Camera-free technical play retains the manual route selector.

When a computer needs trains placed or a score marker moved, the browser shows the same current
instruction as the laptop. Both displays update as the board is checked; cards stay covered until
the next human can play.

The QR carries only the local address, not the pairing code or private cards. No phone camera
permission is needed inside the game: use the phone's existing QR scanner. ADB and USB are not
required. There is no installation step, offline app cache or home-screen requirement.

## PRACTICAL: share the laptop

Select **PRACTICAL** and leave network setup behind. The active human presses **SHOW MY CARDS**
on the table to use the laptop's private card controls while the other players look away. Cover
the cards before the next person takes over. Choosing PRACTICAL stops any active phone host and
revokes its controller. This is a social privacy convention, not protection against someone watching the screen.
Computer hands remain hidden and the same rules and physical-board checks apply.

## If the phone cannot reach the laptop

A private-range IP address such as `192.168.x.x` does not prove that Windows classifies the
connection as Private. For a trusted home network, open **Settings → Network & internet**,
choose the active Ethernet connection or Wi-Fi network properties, and select **Private network**.
Do not change unrelated adapters or mark an unfamiliar public hotspot Private.

The app shows a scoped Windows firewall command for the selected connection, local subnet and
TCP port **8080**. Applying Windows settings remains an explicit administrator action; managed
policies may prevent it. Do not disable the firewall or open router ports. No public DNS, router
port forwarding or incoming Internet access is needed.

An administrator can inspect the selected connection with:

```powershell
Get-NetConnectionProfile | Format-Table InterfaceIndex, InterfaceAlias, NetworkCategory
```

Microsoft documents the profile controls in
[Essential network settings and tasks in Windows](https://support.microsoft.com/en-us/windows/experience/connectivity-networking/essential-network-settings-and-tasks-in-windows).

## Share the final standings

In a game with at least two humans, **Share to phone** prepares an image of the board and every
player's full results and selects Quick play. Connect and approve a phone if needed, then request the
image. The phone previews it and offers **Save image**; share the downloaded PNG through the
device's Files or Photos app. Native browser share sheets are not required.

Finish saving before selecting **Back to Menu**, which ends the phone session. The image contains
public final results, not hidden hands. The laptop holds only the latest shared image for the
current game; no cloud service or browser offline cache stores it automatically.

## Evidence and remaining checks

Automated transport/browser checks cover joining, private views, handoff, response validation,
image saving and LAN-only requests. They do not establish physical-device support. Test Android
and iPhone/iPad QR scanning, pair approval, touch controls, reload, focus loss, downloads and
reconnect with the router WAN disconnected while the local network stays available. Record
device/OS/browser versions and outcomes in the [device evidence plan](companion-device-evidence.md).
Repeat a full multi-human game in PRACTICAL without networking, including opening tickets,
turn handoffs, pauses, save/reload and final scoring.

The September 12 reports describe an earlier certificate-based experiment. That approach and
its installation requirements have been removed; those dated records are historical evidence,
not instructions for current play.
