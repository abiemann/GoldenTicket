# Read-only camera format inventory

On Windows, run `dotnet run --project tools/GoldenTicket.CameraDiagnostics -c Release -- Pixel` to list the connected Pixel/Android webcam's current and advertised source formats. Pass another camera name as the final argument to inspect that device.

The tool initializes matching cameras with `SharedReadOnly` access. It never sets a format, starts a frame reader, captures an image, or stops another application's camera. If shared inspection is refused, it reports the error and exits. It does not retry with exclusive access. No camera identifiers, photographs, or private game state are emitted.

The advertised UVC modes, rather than the phone sensor's recording specifications, determine which native resolutions Windows can request. See [Android's webcam configuration documentation](https://source.android.com/docs/core/camera/webcam?hl=en) and [Google's Pixel webcam instructions](https://support.google.com/pixelcamera/answer/14274129?hl=en).

## Local verification on September 12, 2026

Shared inspection of the connected `Android Webcam` succeeded while GoldenTicket retained its camera session. Windows reported one `VideoRecord` source, currently configured at 1920 × 1080, 15 fps, NV12. Its advertised resolutions were 1920 × 1080, 1280 × 720, 640 × 480, and 640 × 360. The two larger sizes supported NV12 and MJPG at 15, 24, 30, and 60 fps; the smaller sizes additionally supported YUY2.

This particular connection did not advertise a 3840 × 2160 mode. The app therefore hides **4K · best available** for this webcam and uses its native 1080p ceiling. This is an observation of that device's current UVC configuration, not a limit on every Pixel camera or its built-in video recording. The diagnostic did not start a reader, so these are source-format measurements rather than a fresh-frame pixel measurement.

Gameplay recommends native 1920 × 1080 or better. A webcam that supplies at least 1280 × 720 is allowed with a warning about less reliable gameplay and train detection in poor lighting. Lower-resolution modes are not compatible, including when using the current shared Windows format. The diagnostic can still inventory those modes; listing a mode does not mean gameplay accepts it.
