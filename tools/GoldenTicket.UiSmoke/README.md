# Desktop layout smoke runner

This diagnostic renders the production WPF Setup, Table, Private Seat, Rebuild, Camera, Connection and Checkpoint Photo views on an isolated STA dispatcher. It uses the actual merged theme dictionaries, in-memory matches, and `RenderTargetBitmap`; it never creates a visible window, starts the phone server, opens a camera, or changes Windows settings.

Run from the repository root on Windows:

```powershell
dotnet run --project tools/GoldenTicket.UiSmoke -c Release -- artifacts/ui-smoke
```

The optional argument is an output directory. The runner creates PNG screenshots at 1280×800 and 1000×620, including bottom-of-scroll views where relevant, plus `layout-report.json` and `binding-errors.log`. It fails for WPF binding warnings/errors, missing resources, interactive controls overflowing the horizontal viewport, or primary button labels overriding their intended foreground. It also sends synthetic WPF keyboard routed events directly to the camera preview's key handler, without injecting operating-system input. These checks exercise initial corner placement, selecting and adjusting existing corners before and after initial selection, returning to placement, image-edge clamping, and keeping photo capture disabled without a fresh camera frame. The production pointer-coordinate mapper is checked against the displayed image bounds and letterboxing. Results are written to `camera-corner-interactions.json`.

Separate synthetic solo and shared-human fixtures exercise setup guidance, the phone command's availability, automatic solo opening destinations and turn cards, **Your cards** / **Back to table** labels, deliberate covering, and explicit handoffs between two humans. The private-view checks require all hand and destination controls to bind to the currently revealed human's own collections. Each view is rendered at both sizes, and results are written to `human-presentation-interactions.json`. These checks complement the domain/privacy regression suite; they do not claim that inspecting a visual tree proves every hidden-information boundary.

Photo-presentation fixtures cover a zero-route rebuild without a photo, a missing-photo capture page, the live crop before capture, and saved-reference images on both the rebuild and photo pages. The checks require clear missing-photo and empty-board guidance, identify the live crop as not saved, preserve the operator confirmation, and verify that the displayed saved image is the checkpoint's `PhotoImage` rather than a different live crop. Results are written to `checkpoint-photo-presentation.json`. These states and visibly labeled images are seeded solely for layout validation; the runner does not persist an attachment or claim camera/storage acceptance. Separate automated tests cover those behaviors.

Inspect the rendered screenshots for visual issues that bounds assertions cannot judge. Game fixtures are local synthetic play using the in-memory store; route choices may vary. Camera screenshots include an inactive state and an explicitly labeled synthetic pattern with four editable corner handles. Connection screenshots show the inactive state. These checks establish layout, coordinate mapping, binding and routed-key handler evidence, not live-window mouse capture, keyboard focus, DPI, screen-reader, camera, firewall, certificate-trust or real-phone acceptance.
