# Piece candidate diagnostic

Run from the repository with two equally cropped, equally sized board photos, the empty board first:

```powershell
dotnet run --project tools/GoldenTicket.PieceDetectionSmoke -- <empty-board.png> <board-with-pieces.png> artifacts/piece-candidates
```

The tool writes `piece-candidates.json` and a photo with white outlines, and reports bounded detector execution time. It uses the same reference differencing and shape code as the desktop preview. It never opens the camera, saved games or the network. Photos are supplied explicitly and are not bundled with the app or tests.

Add `--enhanced` to test an original imported reference against the current image after automatic CPU/GPU 4K preparation. Add `--enhanced-reference` to prepare both images, matching a captured reference workflow. Both paths normalize board images to 1920×1200 before running the detector. Also run the empty image against itself with `--enhanced` to check whether processing alone creates false candidates. The report records the actual processing backend and adapter, and separates preparation from detection time.

Train rectangles and score-marker squares are experimental candidates. Adjacent trains may merge or be split incorrectly; dark pieces, shadows, hands, glare and changed board geometry can cause misses or false positives. A reference containing pieces cannot detect those unchanged pieces. These outputs do not verify legal moves, train colors, route ownership, score positions or final board reconstruction. Processing image pairs is a developer diagnostic, not evidence that a trained recognition model exists.
