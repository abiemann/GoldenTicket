using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Camera_claim_thanks_for_three_seconds_then_waits_for_the_moved_score_marker()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = true;
        try
        {
            await model.StartMatchAsync();
            var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
            var previousScore = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score;

            var accept = typeof(MainViewModel).GetMethod("AcceptPhysicalPlacementAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var claim = Assert.IsAssignableFrom<Task>(accept.Invoke(model,
                [placement, EvidenceKind.CameraAutomatic, "synthetic-test-model", "Two stable route observations."]));

            await WaitUntilAsync(() => model.Game.GuidanceInstruction == "Thank you");
            Assert.Equal("Scoring", model.Game.GuidanceTurn);
            Assert.Equal(placement.SeatName, model.Game.GuidanceSeat);
            Assert.Null(model.Table.Placement);
            Assert.Equal(previousScore + TestManifest.Manifest.RulesConstants.ScoreForLength(placement.TrainCount),
                model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score);

            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Equal("Thank you", model.Game.GuidanceInstruction);
            await claim;
            Assert.Contains($"Move {placement.SeatName}'s {placement.Color} scoring marker",
                model.Game.GuidanceInstruction);
            var target = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score % 100 + 1;
            Assert.Contains($"to {target}", model.Game.GuidanceInstruction);

            model.Camera.IsGameTablePreviewUpright = true;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            var firstAt = DateTimeOffset.UtcNow;
            PublishScore(model.Camera, 1, firstAt, color, target);
            Assert.Equal("Scoring", model.Game.GuidanceTurn);
            PublishScore(model.Camera, 2, firstAt.AddSeconds(1.1), color, target);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Scoring");
            Assert.NotEqual("Thank you", model.Game.GuidanceInstruction);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static void PublishScore(CameraViewModel camera, long sequence,
        DateTimeOffset capturedAt, MarkerColor color, int score)
    {
        var frame = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
            sequence: sequence, epoch: 1, capturedAt: capturedAt);
        var analysis = new GameTableAnalysis(frame, [],
            [new ScoreMarkerReading(0, color, score, ScoreMarkerReadingStatus.Read,
                "Printed score track position read.")],
            1, 1, "synthetic-test-model");
        typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
            .GetSetMethod(nonPublic: true)!.Invoke(camera, [analysis]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The expected game-table guidance was not reached.");
    }
}
