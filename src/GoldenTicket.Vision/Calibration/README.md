# Classic-US artwork calibration

`classic-us-board-320x200.gray` is the fixed artwork reference for the crop coordinate
system used by `ClassicUsRouteGeometry`. It contains 64,000 row-major luminance bytes,
one byte per pixel, without a header. The Vision assembly embeds it; consumers do not
need the original photograph or a separately installed image file.

| Property | Value |
| --- | --- |
| Board profile | `ttr-us-classic-en-v1` |
| Geometry version | `classic-us-slots-2026-09-22-v6` |
| Reference size | 320 × 200 |
| Resource SHA-256 | `0b8ab46ff79d5ea6ddc9aef338c340e394a4bca498bf6a797a4740c909baccec` |
| Original photo | `GoldenTicket-board-20260914-181653.png` |
| Original size | 3456 × 2160 |
| Original SHA-256 | `9b7746396ff559f932c1578c3d3c662ad1f79cffb04e6b4261e790e2ab9e238d` |

The original user-supplied photograph remains local and is not shipped. Only this
derived, reduced grayscale calibration is embedded. Training photographs, labels,
checkpoints and diagnostic captures remain ignored. This is fixed calibration data,
not a trained model or a saved-game image.

## Deterministic extraction

Verify the original SHA-256 and dimensions before extracting. Decode its first frame
with WPF `BitmapDecoder`, `BitmapCreateOptions.PreservePixelFormat`, and
`BitmapCacheOption.OnLoad`, then use `FormatConvertedBitmap` to obtain `Bgra32`.
Apply no crop, padding, rotation, sharpening, or WPF scaling transform.

For output pixel `(x, y)`, sample the original at
`sx = x / 319.0 * 3455` and `sy = y / 199.0 * 2159`. At each of the four surrounding
input pixels, compute `B * 0.114 + G * 0.587 + R * 0.299`, then bilinearly interpolate
those luminances using the fractional parts of `sx` and `sy`. Clamp the right/bottom
neighbor at the image edge. Round the result with `Math.Round(value,
MidpointRounding.ToEven)` and write one byte. This uses the endpoint-aligned coordinate
and bilinear luminance convention in `BoardPhotoAlignmentReference`.

The runtime expands the bytes to opaque BGRA once and constructs the shared alignment
reference from that frame. Matching uses the existing interior artwork samples and
bounded correction logic; expected route occupancy and detected pieces are not inputs.
Changing this calibration requires rechecking printed-slot coordinates and adjacent
lanes against the new reference, and updating its provenance and validation evidence.

## Route-center correction, September 19

The v4 geometry moves the first two Duluth–Sault St. Marie centers up by eight
reference pixels. Replaying the supplied gameplay screenshot found three separate
black trains at high confidence, but the first sat only 0.01 pixels inside the old
16-pixel sideways limit. The correction leaves room for small detection shifts,
retains the original centered placements, and increases separation from the nearby
Duluth–Toronto spaces. The artwork reference, model and global tolerances are unchanged.

## Nashville–Saint Louis indicator correction, September 22

The v6 geometry moves the Nashville-side train-space center from `(1385, 701)` to
`(1390, 716)` in the 1996 × 1248 reference axes, measured against the original board
photograph. The Saint Louis-side center stays at `(1321, 689)`. This centers the
right-hand placement sphere on its printed space in both the laptop and companion
views and keeps camera verification aligned with that cue. The artwork reference,
model and global tolerances are unchanged.
