using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardInventoryVerifierTests
{
    private const string BlueRoute = "atlanta--raleigh--a";
    private const string GreenRoute = "kansas-city--saint-louis--a";
    private static readonly Train[] Complete =
    [
        new(1586, 755, MarkerColor.Blue), new(1643, 712, MarkerColor.Blue),
        new(1154, 640, MarkerColor.Green), new(1220, 638, MarkerColor.Green)
    ];

    [Fact]
    public void Confirms_all_claimed_routes_and_reports_color_inventory_after_two_stable_frames()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var second = Scene(2, 1, now.AddSeconds(1.1), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        var confirmed = verifier.Observe(second.Frame, second.Candidates, 1, 1);
        Assert.True(confirmed.Confirmed);
        Assert.Equal(2, confirmed.ConfirmedByColor[MarkerColor.Blue]);
        Assert.Equal(2, confirmed.ConfirmedByColor[MarkerColor.Green]);
        Assert.Equal(2, confirmed.ConfirmedByColor.Count);
    }

    [Fact]
    public void Missing_wrong_color_or_ambiguous_route_never_confirms()
    {
        var now = DateTimeOffset.UtcNow;
        var missing = Scene(1, 1, now, Complete[..^1]);
        Assert.Equal(BoardInventoryState.MissingTrains,
            Inventory().Observe(missing.Frame, missing.Candidates, 1, 1).State);

        var wrong = Scene(1, 1, now,
            [.. Complete[..^1], new Train(1220, 638, MarkerColor.Red)]);
        Assert.Equal(BoardInventoryState.WrongColor,
            Inventory().Observe(wrong.Frame, wrong.Candidates, 1, 1).State);

        var good = Scene(1, 1, now, Complete);
        Assert.Equal(BoardInventoryState.Ambiguous,
            Inventory().Observe(good.Frame,
                [good.Candidates[0], good.Candidates[0], good.Candidates[2], good.Candidates[3]],
                1, 1).State);
    }

    [Fact]
    public void Extra_train_elsewhere_on_the_board_blocks_save_even_when_claims_match()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var extra = Scene(2, 1, now.AddSeconds(1.1),
            [.. Complete, new Train(900, 400, MarkerColor.Yellow)]);
        var restored = Scene(3, 1, now.AddSeconds(2.2), Complete);
        var final = Scene(4, 1, now.AddSeconds(3.3), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.UnexpectedTrain,
            verifier.Observe(extra.Frame, extra.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(restored.Frame, restored.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(final.Frame, final.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void A_piece_changed_after_the_first_matching_frame_cannot_use_stale_route_confirmation()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var changed = Scene(2, 1, now.AddSeconds(1.1),
            [.. Complete[..^1], new Train(1220, 638, MarkerColor.Red)]);
        var restored = Scene(3, 1, now.AddSeconds(2.2), Complete);
        var final = Scene(4, 1, now.AddSeconds(3.3), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.WrongColor,
            verifier.Observe(changed.Frame, changed.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(restored.Frame, restored.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(final.Frame, final.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Both_frames_must_be_at_least_one_second_apart()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var tooSoon = Scene(2, 1, now.AddSeconds(.5), Complete);
        var later = Scene(3, 1, now.AddSeconds(1.1), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(tooSoon.Frame, tooSoon.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(later.Frame, later.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Empty_board_can_confirm_zero_inventory_but_any_train_blocks_it()
    {
        var verifier = new BoardInventoryVerifier([]);
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, []);
        var second = Scene(2, 1, now.AddSeconds(1.1), []);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        var confirmed = verifier.Observe(second.Frame, second.Candidates, 1, 1);
        Assert.True(confirmed.Confirmed);
        Assert.Empty(confirmed.ConfirmedByColor);

        var train = Scene(3, 1, now.AddSeconds(2.2), [new Train(1586, 755, MarkerColor.Blue)]);
        Assert.Equal(BoardInventoryState.UnexpectedTrain,
            verifier.Observe(train.Frame, train.Candidates, 1, 1).State);
    }

    [Fact]
    public void Repeated_frame_and_camera_or_calibration_change_restart_stability()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.WaitingForFreshFrame,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);

        var changedCrop = Scene(2, 1, now.AddSeconds(1.1), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(changedCrop.Frame, changedCrop.Candidates, 2, 1).State);
        var changedModel = Scene(3, 1, now.AddSeconds(2.2), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(changedModel.Frame, changedModel.Candidates, 2, 2).State);
        var changedCamera = Scene(4, 2, now.AddSeconds(3.3), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(changedCamera.Frame, changedCamera.Candidates, 2, 2).State);
        var stable = Scene(5, 2, now.AddSeconds(4.4), Complete);
        Assert.True(verifier.Observe(stable.Frame, stable.Candidates, 2, 2).Confirmed);
    }

    [Fact]
    public void Invalid_or_duplicate_expected_route_is_unsupported()
    {
        var frame = Scene(1, 1, DateTimeOffset.UtcNow, []);
        Assert.Equal(BoardInventoryState.Unsupported,
            new BoardInventoryVerifier([new("unmeasured", MarkerColor.Blue, 2)])
                .Observe(frame.Frame, frame.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Unsupported,
            new BoardInventoryVerifier([
                new(BlueRoute, MarkerColor.Blue, 2), new(BlueRoute, MarkerColor.Red, 2)])
                .Observe(frame.Frame, frame.Candidates, 1, 1).State);
    }

    private static BoardInventoryVerifier Inventory() => new(
        [new(BlueRoute, MarkerColor.Blue, 2), new(GreenRoute, MarkerColor.Green, 2)]);

    private readonly record struct Train(double X, double Y, MarkerColor Color);

    private static (CameraFrame Frame, PieceCandidate[] Candidates) Scene(long sequence, long epoch,
        DateTimeOffset capturedAt, Train[] trains)
    {
        const int width = 960;
        const int height = 600;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 180;
        var candidates = new List<PieceCandidate>();
        foreach (var train in trains)
        {
            var x = (int)Math.Round(train.X / 1996 * width);
            var y = (int)Math.Round(train.Y / 1248 * height);
            var (red, green, blue) = train.Color switch
            {
                MarkerColor.Red => (190, 35, 30),
                MarkerColor.Green => (20, 125, 35),
                MarkerColor.Yellow => (225, 180, 20),
                MarkerColor.Blue => (20, 75, 195),
                _ => (20, 20, 20),
            };
            for (var py = y - 7; py <= y + 7; py++)
            for (var px = x - 11; px <= x + 11; px++)
            {
                var offset = (py * width + px) * 4;
                pixels[offset] = (byte)blue;
                pixels[offset + 1] = (byte)green;
                pixels[offset + 2] = (byte)red;
            }
            candidates.Add(new(PieceCandidateKind.Train,
                [new((double)(x - 11) / width, (double)(y - 7) / height),
                    new((double)(x + 11) / width, (double)(y - 7) / height),
                    new((double)(x + 11) / width, (double)(y + 7) / height),
                    new((double)(x - 11) / width, (double)(y + 7) / height)], .9));
        }
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, epoch, capturedAt),
            candidates.ToArray());
    }
}
