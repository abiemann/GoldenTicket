# Desktop layout evidence, September 12, 2026

The production WPF views were rendered and inspected at **1280×800** and **1000×620** using [GoldenTicket.UiSmoke](../../../tools/GoldenTicket.UiSmoke/README.md). This is an isolated STA rendering diagnostic, without a visible window, camera access, phone hosting, or operating-system input injection.

## Executed checks

- Six actual views at two sizes: Setup, Table, Rebuild, Camera (inactive), Connection (inactive), and Checkpoint Photo (a packed checkpoint without an image).
- **12 rendering cases passed**, producing 20 PNGs including bottom-of-scroll views where applicable.
- **Zero WPF binding errors or warnings** and no missing-resource exceptions.
- Zero labeled interactive controls outside the horizontal viewport in the measured cases.
- Primary-button text matches the foreground specified by its themed button, preventing dark text on burgundy.
- Synthetic WPF Right/Enter routed events move the camera's selection crosshair and record its first corner. No operating-system keystrokes were sent.

The screenshots were visually inspected. Findings corrected during the pass included low-contrast primary-button text, truncated lane information in the narrow Rebuild layout, outdated setup wording, and camera controls that did not use the common theme. The camera preview now has an explicit stopped state; corner selection accepts mouse or keyboard input. The final render also includes the photo recovery action, **Reload reference**, and the updated explanation of optional reference images on the Rebuild screen.

## Reproduce

From the repository root, on Windows with the project's .NET SDK:

```powershell
dotnet run --project tools/GoldenTicket.UiSmoke -c Release --no-restore -- artifacts/ui-smoke
```

The runner uses the real theme dictionaries and an in-memory computer-player match. Route choices may vary between runs. No physical board, real player's hidden cards or live camera frames appear in these screenshots. The screenshots show view content at the stated size; live window chrome and toolbar consume additional height and remain part of manual acceptance.

## Evidence

- [Measured controls and scroll extents](layout-report.json)
- [Binding trace](binding-errors.log), empty because no warning or error occurred
- [Source hashes at capture](source-hashes.json)
- [Setup at minimum width, bottom](setup-1000x620-bottom.png)
- [Table at minimum width](table-1000x620-top.png)
- [Rebuild at minimum width](rebuild-1000x620-top.png)
- [Camera at minimum width](camera-no-device-1000x620-top.png)
- [Camera guidance and controls](camera-no-device-1000x620-bottom.png)
- [Connection at minimum width](connection-off-1000x620-top.png)
- [Checkpoint photo at minimum width](checkpoint-photo-1000x620-top.png)

This pass does not establish actual Windows focus behavior, full keyboard navigation, screen-reader output, display scaling, live camera operation, certificate installation, phone networking, or real-device privacy lifecycle. Those remain acceptance checks. The capture and phone pages were not represented as connected when no device session was running.
