# GoldenTicket

A Windows companion for a physical game of **Ticket to Ride** (classic English North America
edition). Keep the board, plastic trains and scoring markers on the table. GoldenTicket handles
the train cards, destination tickets, decks, rules, scoring and computer opponents.

A camera watches the board as you play. Place your trains, pay with your digital cards, and follow
the instructions on the laptop or shared tablet. Play against computers, with other people, or both.

GoldenTicket is source-available under the [PolyForm Noncommercial License 1.0.0](LICENSE).
See [License](#license) for its scope and third-party material.

## What you can do

- **Play with 2–5 human and computer players.** Choose portraits with matching train colors, then select
  Standard or Aggressive 😈 play for each computer.
- **Use the physical board throughout the game.** The camera recognizes trains and scoring markers,
  checks route placement, and shows gold dots where computer trains need to go.
- **Keep cards on the laptop or a shared phone/tablet.** Quick play opens in a normal browser;
  PRACTICAL lets people take turns on the laptop while everyone else looks away.
- **See your destinations on the live board.** On the tablet, tap any held destination ticket to
  show dashed connections for all your destinations. Laptop play also highlights destinations.
- **Save and pack away.** Save Game checks the board and stores the digital game with a matching
  board photo. Reload helps you rebuild the board and checks it before play resumes.
- **Finish with a full score breakdown.** Results show route points, completed and missed
  destinations, the longest-route bonus, the actual longest trail, and player turn times.
  Games with multiple humans can share a results image to the phone.

The rules package contains **36 cities, 100 routes, 30 destination tickets and 110 train cards**.
The camera's slot map covers all **309 printed train spaces**, including separate parallel lanes.
The application is being developed and playtested with a real physical board and a tablet browser.

## What you need

- A Windows 11 laptop or desktop.
- The classic English North America board, trains and scoring markers. Leave the physical cards
  and destination tickets in the box; the app manages them for every player.
- A camera with a clear view of the entire board. **720p is the minimum.**
  Native 4K capture is available when the camera supports it. Keep all four corners visible and
  provide enough light to distinguish the pieces.
- For Quick play, one shared phone/tablet on the same local network as the laptop. Solo play and
  PRACTICAL need neither a phone nor a network.

To build from source, use the **.NET 10 SDK**, pinned to **10.0.401** in [global.json](global.json).
Visual Studio with the **.NET desktop development** workload is the preferred development setup.
A framework-dependent build copied to another laptop needs the .NET 10 Windows Desktop and
ASP.NET Core runtimes. Both recognition models are bundled with the project and copied during build.

## Start a game

1. Choose **Start a new game**. On **Choose players**, click a portrait to cycle through human,
   computer and unselected. Select 2–5 players. Click a computer's smiling badge to switch between
   Standard and Aggressive 😈 play; the choice is remembered in saves.
2. Select **SET-UP BOARD**. Point the camera at the whole board, leave the routes empty, and put
   each selected color's scoring marker near the printed **1**. The app finds the four corners
   and checks the selected markers. Choose **PLAY!** when the board is ready.
3. With multiple humans, choose **Quick play** or **PRACTICAL**. With one human, private choices
   appear directly on the laptop.
4. Choose your starting destinations. Keep at least two of the three offered tickets.
5. Follow the current instruction above the board. During computer turns, a person places the
   computer's physical trains and moves its scoring marker.

**Never stack scoring markers on top of one another.** Every train and scoring marker must be
visible from directly above. When players share a score, place their markers side by side near
that score so the camera can see each color.

**Settings** controls display mode, camera quality and processor preference. The app remembers
its window size, maximized state or full-screen choice. If no camera is connected, board setup
keeps looking; use its camera selector when more than one is available.

### Quick play: scan QR → join → play

**No installation needed.** Use the laptop's **LAN Game** controls to open the connection options.

1. Connect the laptop and phone/tablet to the same local network. The laptop can use Ethernet
   while the phone uses Wi-Fi. Select that connection in the app; Windows must classify it as
   a **Private** network.
2. Choose **Quick play**, then **Start hosting**.
3. Scan the QR or open the displayed address, normally `http://<laptop-address>:8080/companion/`.
4. Enter the laptop's pairing code in the browser and approve the matching phone on the laptop.
5. Pass the device to the named player for private choices. Use **Hide** before passing it on.

The pairing code remains valid while hosting runs. Reloading the browser requires pairing again;
restarting hosting or choosing **New pairing code** changes the code.

Game changes and instructions arrive through **Server-Sent Events (SSE)** over the local connection.
The browser hand stays open through the first train-card draw, and there is no idle timeout.
Tapping elsewhere or scrolling does not hide it. Changing turns, leaving the page or losing the
connection covers the hand. On reconnect, the browser receives the current game state.

Held destination tickets form a horizontal row. Tap one to smoothly replace the row with the live
board and connections for all your destinations; **Back** returns to the tickets. During computer
turns, the browser shows the live board, placement dots and the same instructions as the laptop.

See [phone and tablet setup](docs/phone-setup.md) for pairing, firewall help and reconnect behavior.

### PRACTICAL: share the laptop

Choose **PRACTICAL**, the second option, when you do not have another device. Network setup is
hidden and any active phone host stops. When your turn warning appears, ask everyone else to
look away, then select **Take my turn**. This enables your controls while keeping the card tray closed;
select your **T** or **D** stack when you want to see your cards or destinations. Opening destinations
and route payments stay on the game board, using the same controls as solo play. Cards are covered
when the turn ends, and the next player gets their own turn warning. The game rules, computer opponents and camera checks work
the same way.

### Draw cards and claim routes

On your turn, draw train cards, draw destination tickets, or claim a route:

- **Train cards:** select the **T** draw pile or a face-up card. Take two cards, one at a time.
  A face-up locomotive uses the whole turn and cannot be your second card. Market replacements,
  three-locomotive resets and discard reshuffles happen automatically.
- **Destination tickets:** select the **D** pile and keep at least one of the new tickets.
- **Routes:** place your trains on the physical route first. Once the camera confirms the route
  and checks the rest of the board, your payment choices appear. On the tablet, choose the cards
  and press **Pay**. On the laptop, select the exact cards and press **OK**.
  Camera updates won't interrupt this choice. After accepting payment, the app checks the board
  again before completing the claim and allowing the next turn. For a wrong route, remove those
  trains and select **Cancel**.

Card choices are saved immediately. On the laptop, each drawn card flies and rotates into your
player tile, and the corresponding stack updates. Destination offers count in the stack while
you choose which tickets to keep. The camera checks the board once your card-drawing turn is
complete, before the next player can act. Any discrepancies are described and marked with yellow
spheres; correcting the board lets play continue without taking back the cards you drew.

In solo play and after selecting **Take my turn** in PRACTICAL, the **T** and **D** stacks in your
player tile show your hand and destinations without leaving the table. In Quick play, make private
choices on the shared phone or tablet.

The camera checks separate train positions, the player's color and the correct parallel lane
before a claim completes. Card payment, route ownership, points and turn progression are committed
together. If the board changes or a reading becomes uncertain, the game waits for a fresh check.
A rejected card draw awards no card and leaves the turn with the same player; an accepted draw is
saved before the next player can act. The tablet displays rejected choices beside the card picker
and turn-end board discrepancies on its live map.

For a computer claim, follow the named route, lane, color and train count on either screen. Gold
dots mark the required spaces. After placement is confirmed, move the player's scoring marker as
instructed. The marker step is saved with the claim and restored after an interruption; the camera
checks its new position before continuing. Correct misplaced trains or
clear hands from the board when prompted.

### Computer opponents

Computers choose compatible destinations, plan shared connections and collect cards for their
next useful route. **Aggressive 😈** opponents also compete for routes near human networks while
reserving the cards, trains and time needed for their own destinations. Computers see public
information and their own cards; they do not see other players' private hands or tickets.

The [strategy report](docs/ai-strategy-evidence.md) records measured comparisons, and the
[simulator guide](tools/GoldenTicket.Simulator/README.md) explains how to reproduce the benchmarks.

### Final standings

When a player finishes a turn with two trains or fewer, each player gets one final turn. The
results panels show the complete scoring breakdown and each player's longest continuous route.
Longest-route scoring uses an exact maximum trail: a route cannot be counted twice, but cities
may be revisited.

All result panels have the same height; scroll horizontally to see every player and within a
long route description to read the full trail. Total and average player turn times include
physical placement and scoring-marker movement, excluding pauses. The game-table timer separately
tracks total elapsed time while the match is open.

With two or more humans, **Share to phone** prepares a PNG of the board and all results. The
approved phone can preview it and choose **Save image**. Finish downloading before selecting
**Back to Menu**, which ends the phone session.

## Save, pack away and resume

Before clearing the board, press **Escape → Save Game** and wait for it to finish. The app:

1. Checks the visible trains against claimed routes and any authorized unfinished placement.
2. Saves the digital checkpoint and verifies it by reading it back.
3. Captures the matching board photo, validates the attachment and checks fresh camera frames again.
4. Returns to the menu when the save is complete.

You can save while waiting for a computer's train placement, including a partially placed route.
The save remembers the exact placed subset, the active turn and the reserved payment. Finish any
scoring-marker move or cancelled-placement restoration first. If a save check fails, the game
stays open so you can correct the board or camera.
The save dialog shows the failed camera frame with numbered areas and detection details,
even when a route cannot be identified. A fresh matching view clears a temporary warning;
after a timeout, select **Save Game** again. Finish payment for a detected route, or remove
its trains and cancel, before saving.

Choose **Reload the previous game** to open camera setup and resume the most recent match. The
saved photo and route list help reconstruct the board. The app checks each scoring marker and
then the saved train positions and colors. Yellow spheres on the live board mark detected extra
or misplaced trains, even when their route is uncertain; missing trains mark the expected spaces.
The spheres clear when a fresh camera reading shows the problem is corrected.
An unrelated unreadable marker detection no longer blocks a clearly identified player marker.
If the camera still cannot resolve the board, choose **Check board myself** to inspect the saved
photo, routes, unfinished placement and scoring-marker positions, then explicitly confirm them
and resume. This records your confirmation rather than claiming camera verification.
When the board matches, acknowledge **OK** to resume
from the saved turn. Missing or invalid save attachments produce recovery guidance; an earlier
save is restored only when you explicitly choose it.

Completed actions are also recorded automatically in a local SQLite journal. Use **Save Game**
before packing away so you have a completed checkpoint with its board photo. **Quit to Menu**
discards progress since the latest completed save, or discards the current game if no completed
save exists. Local game saves and photos are not encrypted.

## Camera controls and diagnostics

The game uses local ONNX models for board corners, trains and scoring markers. Piece recognition
does not require an empty-board reference. The live board stays upright and uses fresh camera
observations for placement and scoring checks.

Settings offers **Auto · prefer GPU**, **CPU only**, and **GPU · CPU fallback**. Image processing
and model inference report their actual backends; GPU processing falls back to CPU when needed.
Camera quality defaults to **Auto**, which tries the webcam's best supported native mode in
order: 4K, 1080p or better, then 720p. The label shows the highest usable advertised format,
such as **Auto (2160p)**, **Auto (1440p)**, **Auto (1080p)** or **Auto (720p)**. The **1080p** and
**720p** choices appear only when the webcam advertises a usable native 1920 × 1080 or
1280 × 720 format, respectively, and only when that choice is lower than Auto's highest mode;
**Shared · current Windows format** remains available. The app distinguishes capture resolution
from the processed image size.

When Auto selects a higher mode, use **720p** under **Camera quality**, then stop and start
preview in **Camera**. This requests a native 1280 × 720 stream, preferring
30 fps, and reports an error if the webcam cannot supply that size. The Camera screen reports
the delivered capture dimensions. On a 720p-only webcam, Auto already selects that mode. Camera
quality can be changed by selecting another option and restarting preview; reopening the app
restores Auto.

The app remembers your selected webcam across restarts. If it disconnects, the board shows
which webcam it is waiting for and reconnects when that camera returns. You can choose another
webcam in Settings or Camera. If multiple cameras have the same name and the original device
cannot be identified, select the one you want to use.

Press **Shift+Escape** for technical screens, including camera setup, saved matches and recovery.
In **Saved matches**, select a match and choose **Delete selected match** to remove it and its
saved board photos after confirmation. The currently loaded match cannot be deleted.

The Camera preview shows the original frame at the webcam's delivered resolution, without
filtering or upscaling. In the camera preview you can:

- Retry **Detect board corners**, select four corners manually, or drag the numbered handles.
  Press **1–4** to select a corner and use arrow keys to nudge it; **Shift** makes larger steps.
- Zoom up to **800%**, pan, or choose **Fit** without changing the exported board crop.

The experimental **Piece outlines** card, score-track preview, and **Reload ML model** button
have been removed from the Camera screen. The local detector still supports game-board analysis;
its predictions and score-marker readings do not directly change game scores or claim routes.
Restart the app after replacing a locally installed model pair. Developer checks and review
artifacts are described in the [model guide](docs/piece-recognition-ml.md).

Board-decision diagnostics are written locally to
`%LOCALAPPDATA%\GoldenTicket\diagnostics\board-interactions.jsonl`. They include placement checks
and card-action outcomes, without camera images or private cards. Logs reset on a new app launch.

The [model guide](docs/piece-recognition-ml.md), [corner guide](docs/board-corners-ml.md) and
[camera processing reference](docs/camera-processing.md) contain implementation and diagnostic details.

## Local operation

The Windows laptop owns the game, saves and camera checks. Quick play connects directly to it
over **HTTP on the Private LAN**. The traffic is unencrypted; no certificate setup is required.
The router's Internet connection can be disconnected while the local network stays available.
Solo and PRACTICAL play also work without a LAN.

There is no cloud login, telemetry, cloud AI, remote asset download or Internet API needed for
play. Browser assets and recognition models ship with the app. Building from source initially
requires Internet access to restore development dependencies.

## Build, test and run

Open `GoldenTicket.sln` in Visual Studio and set **GoldenTicket.Desktop** as the startup project,
or use these commands on Windows:

```bash
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln --no-restore
dotnet test GoldenTicket.sln --no-build --no-restore
dotnet run --project src/GoldenTicket.Desktop --no-build
```

The portable rules, application, AI and SQLite tests can also run without Windows:

```bash
dotnet restore tests/GoldenTicket.Core.Tests/GoldenTicket.Core.Tests.csproj --locked-mode --configfile NuGet.config
dotnet test tests/GoldenTicket.Core.Tests/GoldenTicket.Core.Tests.csproj --no-restore
```

Browser-client behavioral tests use Node's built-in runner, with no npm packages required for
this command. Node is a development tool, not an application runtime:

```bash
node --test tests/GoldenTicket.Domain.Tests/CompanionHostClient.test.cjs
```

Run reproducible computer matches or validate the rules manifest:

```bash
dotnet run --project tools/GoldenTicket.Simulator -- simulate --games 20 --seats 4 --verbose
dotnet run --project tools/GoldenTicket.Simulator -- verify-data
```

Each project has a `packages.lock.json`; use locked restore when verifying a build. The
[Windows CI workflow](.github/workflows/windows-ci.yml) runs on pushes to `main`, pull requests
and manual dispatch. Failed runs attempt to retain test reports and screenshots when GitHub
artifact storage is available. See the
[build and CI guide](docs/build-and-ci.md) for the full browser and WPF checks and
[portable packaging guide](docs/offline-package.md) for distribution tooling.

## Repository and documentation

```text
src/GoldenTicket.Domain/               rules, cards, events, projections and scoring
src/GoldenTicket.Application/          coordination, commands and computer turns
src/GoldenTicket.AI/                   computer strategy and route planning
src/GoldenTicket.Persistence/          SQLite journal, checkpoints and board photos
src/GoldenTicket.Desktop/              WPF game screens and diagnostics
src/GoldenTicket.Vision/               camera capture, board crop and piece recognition
src/GoldenTicket.CompanionHost/        local HTTP host, SSE and bundled browser client
tools/GoldenTicket.Simulator/          headless matches, benchmarks and manifest checks
tools/GoldenTicket.CameraDiagnostics/  native camera format inventory
tools/GoldenTicket.MlPieceSmoke/        CPU/GPU model evaluation
tools/piece-training/                  annotation, training and export tools
tests/                                rules, storage, integration and UI checks
data/classic-us/                       board and ticket manifest
shared/theme/                         canonical theme tokens
docs/                                 technical guides and validation records
```

Reviewed [recognition models](assets/models/README.md) and [game artwork](assets/artwork/README.md)
are committed. Builds copy the models beside the executable under `models/`; training photos
and intermediate checkpoints stay in ignored `artifacts/`.

- [Architecture](docs/architecture.md): implemented layers and dependency boundaries.
- [Contributor guide](CONTRIBUTING.md): development conventions and checks.
- [Rules policies](docs/rules-policies.md): decisions for rare rules and supply cases.
- [Design](DESIGN.md): detailed product design, including future features.
- [Roadmap](TODO.md): remaining work, including narration and installer work.
- [Validation records](docs/evidence/): detailed results and historical implementation reports.

## License

GoldenTicket's original source code, documentation, and any licensable project-owned rights in
bundled assets are offered under the [PolyForm Noncommercial License 1.0.0](LICENSE)
(`PolyForm-Noncommercial-1.0.0`). Use, changes and distribution are permitted for the purposes
covered by that license. Commercial use requires separate permission from the rights holder.

The license does not override third-party terms. Dependencies and upstream material in the
[recognition models](assets/models/README.md) retain their own licenses and notices. The
[artwork audit](docs/artwork-audit-2026-09-23.md) documents the AI-generated images; this license
grants only rights the project owner holds in them. It does not grant rights to publisher-owned
Ticket to Ride board imagery, ticket content, names, or trademarks.
