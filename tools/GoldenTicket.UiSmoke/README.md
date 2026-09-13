# Desktop layout smoke runner

This diagnostic renders the production WPF Setup, Table, Private Seat, Rebuild, Camera, Connection and Checkpoint Photo views on an isolated STA dispatcher. It uses the actual merged theme dictionaries, in-memory matches, and `RenderTargetBitmap`; it never creates a visible window, starts the phone server, opens a camera, or changes Windows settings.

Run from the repository root on Windows:

```powershell
dotnet run --project tools/GoldenTicket.UiSmoke -c Release -- artifacts/ui-smoke
```

The optional argument is an output directory. The runner creates PNG screenshots at 1280×800 and 1000×620, including bottom-of-scroll views where relevant, plus `layout-report.json` and `binding-errors.log`. It fails for WPF binding warnings/errors, missing resources, interactive controls overflowing the horizontal viewport, or primary button labels overriding their intended foreground. It also sends synthetic WPF keyboard routed events directly to the camera preview's key handler, without injecting operating-system input. These checks exercise initial corner placement, selecting and adjusting existing corners before and after initial selection, returning to placement, image-edge clamping, and keeping photo capture disabled without a fresh camera frame. The production pointer-coordinate mapper is checked against the displayed image bounds and letterboxing. Results are written to `camera-corner-interactions.json`.

Separate synthetic solo and shared-human fixtures exercise setup guidance, the phone command's availability, automatic solo opening destinations and turn cards, **Your cards** / **Back to table** labels, deliberate covering, and explicit handoffs between two humans. The private-view checks require all hand and destination controls to bind to the currently revealed human's own collections. Each view is rendered at both sizes, and results are written to `human-presentation-interactions.json`. These checks complement the domain/privacy regression suite; they do not claim that inspecting a visual tree proves every hidden-information boundary.

A synthetic valid-crop/changed-scene fixture verifies the actual **Export board photo** button
remains enabled while checkpoint photo capture stays held. It appears in `layout-report.json`
and its screenshots at both sizes. The fixture opens neither a camera nor a Save dialog;
`CameraPhotoExportTests` separately exercise PNG encoding from owned synthetic frames.

The corner interaction checks also require no placement plus before keyboard input, a visible cue
after keyboard positioning, and its removal on a synthetic mouse move while retaining numbered
corners. Routed mouse events in this check do not inject operating-system input.

Photo-presentation fixtures cover a zero-route rebuild without a photo, a missing-photo capture page, the live crop before capture, and saved-reference images on both the rebuild and photo pages. The checks require clear missing-photo and empty-board guidance, identify the live crop as not saved, preserve the operator confirmation, and verify that the displayed saved image is the checkpoint's `PhotoImage` rather than a different live crop. Results are written to `checkpoint-photo-presentation.json`. These states and visibly labeled images are seeded solely for layout validation; the runner does not persist an attachment or claim camera/storage acceptance. Separate automated tests cover those behaviors.

Shutdown fixtures construct the production `MainWindow` with an in-memory model and call `Close()` twice without showing the window or raising `Loaded`. They exercise both immediately completed cleanup and camera disposal delayed by its lifecycle semaphore. Each case requires input to be disabled during cleanup, exactly one eventual `Closed` event, and no dispatcher exception. Results are written to `window-shutdown-interactions.json`. These checks use real WPF closing events without accessing a camera, hosting a server, or loading a saved game; physical camera-driver shutdown remains a hardware acceptance check.

An exit-confirmation fixture starts a synthetic human match and replaces only the modal answer
callback. Cancel must keep the window usable, leave private cards covered, and allow a deliberate
reveal. Confirm must close once after cleanup; repeated close requests during cleanup must not
prompt again. Results are written to `window-exit-confirmation.json`. The native message box is
not displayed by this fixture; its appearance and default No button remain an interactive check.

Saved-match selection fixtures render the sole auto-checked save and a multiple-save list with no
selection. WPF automation peers toggle the real checkboxes and invoke the bound Resume button.
Checks require exactly one selected match, refresh/reorder preservation by session ID, disabled
Resume without selection, and a resumed synthetic match that still requires physical reconciliation
with private cards covered. Results are written to `saved-match-selection-interactions.json`;
these fixtures use in-memory saves and no native pointer input or player storage.
An additional fixture saves a synthetic match as `test2` through the desktop command, then renders
a new picker at both sizes. It checks the actual row label begins with `test2`, shows the readable
**Packed away** status, and retains its selection checkmark.

Camera processing fixtures verify the default 4K capture request and Auto processor choice, source
versus upscaled labels, and white train and square player-marker outlines at both viewport sizes.
The outline canvas must not intercept pointer input, its toggle must hide/show geometry, and clearing
the reference must remove candidates. These rendered outlines use synthetic geometry; actual detector
tests and the explicit two-photo `GoldenTicket.PieceDetectionSmoke` tool test recognition separately.

Inspect the rendered screenshots for visual issues that bounds assertions cannot judge. Game fixtures are local synthetic play using the in-memory store; route choices may vary. Camera screenshots include an inactive state and an explicitly labeled synthetic pattern with four editable corner handles. Connection screenshots show the inactive state. These checks establish layout, coordinate mapping, binding and routed-key handler evidence, not live-window mouse capture, keyboard focus, DPI, screen-reader, camera, firewall, certificate-trust or real-phone acceptance.
