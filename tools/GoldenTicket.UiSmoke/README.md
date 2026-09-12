# Desktop layout smoke runner

This diagnostic renders the production WPF Setup, Table, Rebuild, Camera, Connection and Checkpoint Photo views on an isolated STA dispatcher. It uses the actual merged theme dictionaries, an in-memory match, and `RenderTargetBitmap`; it never creates a visible window, starts the phone server, opens a camera, or changes Windows settings.

Run from the repository root on Windows:

```powershell
dotnet run --project tools/GoldenTicket.UiSmoke -c Release -- artifacts/ui-smoke
```

The optional argument is an output directory. The runner creates PNG screenshots at 1280×800 and 1000×620, including bottom-of-scroll views where relevant, plus `layout-report.json` and `binding-errors.log`. It fails for WPF binding warnings/errors, missing resources, interactive controls overflowing the horizontal viewport, or primary button labels overriding their intended foreground. It also sends synthetic WPF Right/Enter routed events directly to the camera preview's key handler and checks that the first corner moves and is recorded, without injecting operating-system input.

Inspect the rendered screenshots for visual issues that bounds assertions cannot judge. Game fixtures are local synthetic play using the in-memory store; route choices may vary. Camera and connection screenshots show their inactive states. These checks establish layout and binding evidence, not live-window keyboard focus, DPI, screen-reader, camera, firewall, certificate-trust or real-phone acceptance.
