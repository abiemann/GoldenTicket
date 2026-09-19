using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopCardActionBoardTests
{
    [Fact]
    public async Task Black_trains_after_the_first_card_block_the_second_card_until_removed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        await model.DrawSoloBlindCommand.ExecuteAsync(null); // Explicit manual/no-camera path.
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
        var faceUp = model.Table.Market.First(slot => model.DrawSoloFaceUpCommand.CanExecute(slot));
        var before = await fixture.SnapshotAsync();

        model.Camera.IsGameTablePreviewUpright = true;
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(1, at, blackTrains: true);
        fixture.Publish(2, at.AddSeconds(1.1), blackTrains: true);

        Assert.False(model.DrawSoloFaceUpCommand.CanExecute(faceUp));
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.Null(model.BoardFirstProposal);
        await model.DrawSoloFaceUpCommand.ExecuteAsync(faceUp);
        await fixture.AssertUnchangedAsync(before);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
        Assert.True(model.IsSoloHumanTurn);

        fixture.Publish(3, at.AddSeconds(2.2));
        Assert.False(model.DrawSoloFaceUpCommand.CanExecute(faceUp));
        fixture.Publish(4, at.AddSeconds(3.3));
        Assert.True(model.DrawSoloFaceUpCommand.CanExecute(faceUp));
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.Null(model.BoardFirstProposal);
        await fixture.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task Payable_trains_detected_after_click_show_a_claim_instead_of_spending_the_turn()
    {
        await using var fixture = await Fixture.CreateAsync(payableRoute: true);
        var model = fixture.Model;
        Assert.Contains((await fixture.Coordinator.GetLegalActionsAsync(
            fixture.Coordinator.Public.ActiveSeatId)).Claims,
            claim => claim.RouteId.Value == "little-rock--saint-louis");
        fixture.PublishCleanBaseline();
        var before = await fixture.SnapshotAsync();
        var draw = model.DrawSoloBlindCommand.ExecuteAsync(null);
        Assert.False(draw.IsCompleted);

        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at, blackTrains: true);
        fixture.Publish(4, at.AddSeconds(1.1), blackTrains: true);
        await draw.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);

        await fixture.AssertUnchangedAsync(before);
        Assert.Equal(TurnPhase.TurnStart, fixture.Coordinator.Public.TurnPhase);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
        await model.DrawSoloTicketsCommand.ExecuteAsync(null);
        await fixture.AssertUnchangedAsync(before);
        Assert.False(model.ShowSoloTicketOffer);

        // The route remains a valid claim at TurnStart; canceling the draw must reveal its
        // payment choice once the pending operation releases the board-first observer.
        fixture.Publish(5, at.AddSeconds(2.2), blackTrains: true);
        fixture.Publish(6, at.AddSeconds(3.3), blackTrains: true);
        for (var attempt = 0; model.BoardFirstProposal is null && attempt < 100; attempt++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        var proposal = Assert.IsType<BoardFirstClaimProposal>(model.BoardFirstProposal);
        Assert.Equal("little-rock--saint-louis", proposal.RouteId.Value);
        Assert.NotEmpty(proposal.Payments);
        Assert.Contains("Choose which train cards to spend", model.Game.GuidanceInstruction);
        Assert.Null(fixture.Coordinator.Public.PendingClaim);
        await fixture.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task Cached_and_inflight_clean_frames_cannot_authorize_a_new_card_draw()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        var before = await fixture.SnapshotAsync();
        var capturedBeforeClick = DateTimeOffset.UtcNow.AddMilliseconds(-20);
        var draw = model.DrawSoloBlindCommand.ExecuteAsync(null);
        Assert.False(draw.IsCompleted);
        await fixture.AssertUnchangedAsync(before);

        // New analysis sequences can still contain frames captured before this click.
        fixture.Publish(3, capturedBeforeClick.AddSeconds(-1.1));
        fixture.Publish(4, capturedBeforeClick);
        // A reused pre-click sequence cannot become new evidence by changing its timestamp.
        fixture.Publish(2, DateTimeOffset.UtcNow);
        // A recently published result can also carry an old capture timestamp internally.
        fixture.Publish(5, DateTimeOffset.UtcNow, stale: true);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(draw.IsCompleted);
        await fixture.AssertUnchangedAsync(before);

        var freshAt = DateTimeOffset.UtcNow;
        fixture.Publish(6, freshAt);
        Assert.False(draw.IsCompleted);
        await fixture.AssertUnchangedAsync(before);
        fixture.Publish(7, freshAt.AddSeconds(1.1));
        await draw.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);

        Assert.Equal(before.Version + 1, fixture.Coordinator.Public.StateVersion);
        Assert.Equal(before.Turn, fixture.Coordinator.Public.TurnNumber);
        Assert.Equal(before.Seat, fixture.Coordinator.Public.ActiveSeatId);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
        Assert.NotEqual(before.Hash, await fixture.Coordinator.ComputeStateHashAsync());
    }

    [Fact]
    public async Task Requested_camera_without_analysis_cannot_fall_back_to_manual_draws()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        model.SetGameLayerVisible(true);
        typeof(CameraViewModel).GetField("_gameTablePreviewRequested",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model.Camera, true);
        model.Camera.IsGameTablePreviewUpright = false;
        Assert.Null(model.Camera.GameTableAnalysis);
        var before = await fixture.SnapshotAsync();

        await model.DrawSoloBlindCommand.ExecuteAsync(null)
            .WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);

        await fixture.AssertUnchangedAsync(before);
        Assert.Equal(TurnPhase.TurnStart, fixture.Coordinator.Public.TurnPhase);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Losing_focus_cancels_a_pending_draw_even_if_clean_frames_arrive_later()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        var before = await fixture.SnapshotAsync();
        var draw = model.DrawSoloBlindCommand.ExecuteAsync(null);
        Assert.False(draw.IsCompleted);
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at);
        Assert.False(draw.IsCompleted);

        model.SetWindowActive(false);
        fixture.Publish(4, at.AddSeconds(1.1));
        await draw.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
        await fixture.AssertUnchangedAsync(before);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));

        // Returning to the table requires another click; later good frames must not revive it.
        model.SetWindowActive(true);
        fixture.Publish(5, at.AddSeconds(2.2));
        fixture.Publish(6, at.AddSeconds(3.3));
        await fixture.AssertUnchangedAsync(before);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.Equal(TurnPhase.TurnStart, fixture.Coordinator.Public.TurnPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Technical_private_card_actions_also_require_post_click_board_confirmation(bool tickets)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        await model.RevealPrivateSeatAsync();
        Assert.NotNull(model.PrivateSeat);
        var before = await fixture.SnapshotAsync();

        var action = tickets ? model.DrawTicketsAsync() : model.DrawBlindCardAsync();
        Assert.False(action.IsCompleted);
        await fixture.AssertUnchangedAsync(before);
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at);
        Assert.False(action.IsCompleted);
        fixture.Publish(4, at.AddSeconds(1.1));
        await action.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
        Assert.Equal(before.Version + 1, fixture.Coordinator.Public.StateVersion);
        Assert.Equal(tickets ? TurnPhase.AwaitingTicketKeep : TurnPhase.AwaitingSecondTrainCard,
            fixture.Coordinator.Public.TurnPhase);

        if (!tickets) return;
        await model.RevealPrivateSeatAsync();
        Assert.True(model.PrivateSeat!.MustChooseTickets);
        var offer = await fixture.SnapshotAsync();
        var keep = model.CommitTicketsAsync();
        Assert.False(keep.IsCompleted);
        at = DateTimeOffset.UtcNow;
        fixture.Publish(5, at, blackTrains: true);
        fixture.Publish(6, at.AddSeconds(1.1), blackTrains: true);
        await keep.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
        await fixture.AssertUnchangedAsync(offer);
        Assert.Equal(TurnPhase.AwaitingTicketKeep, fixture.Coordinator.Public.TurnPhase);
        Assert.Null(model.BoardFirstProposal);
    }

    private sealed record Snapshot(long Version, int Turn, SeatId Seat, string Hash);

    private sealed class Fixture(MainViewModel model) : IAsyncDisposable
    {
        public MainViewModel Model { get; } = model;
        public GameCoordinator Coordinator { get; } = Assert.IsType<GameCoordinator>(typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model));

        public static async Task<Fixture> CreateAsync(bool payableRoute = false)
        {
            var manifest = ManifestLoader.LoadClassicUs();
            var store = new InMemorySessionStore();
            var model = new MainViewModel(manifest, store);
            model.Setup.ManualVerificationAccepted = true;
            model.Setup.Seats[0].Color = PlayerColor.Black;
            model.Setup.Seats[1].Color = PlayerColor.Red;
            model.Setup.Seats[2].Color = PlayerColor.Blue;
            for (var index = 0; index < model.Setup.Seats.Count; index++)
                model.Setup.Seats[index].IsComputer = index != 0;
            if (payableRoute)
            {
                // Seed 42 deals the human two pink cards, a legal payment for this grey route.
                var game = await GameCoordinator.CreateAsync(new GameRules(manifest,
                        CardCatalog.FromManifest(manifest)), store, model.Setup.TryBuildSetup()!,
                    DeterministicRandom.SeedFrom(42));
                foreach (var seat in game.Seats)
                {
                    var view = await game.GetSeatViewAsync(seat.SeatId);
                    Assert.True((await game.SubmitAsync(new CommitTicketSelection(
                        game.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []))).IsAccepted);
                }
                await model.LoadSavedSessionsAsync();
                model.Setup.SelectedSavedSession = Assert.Single(model.Setup.SavedSessions);
                await model.ResumeMatchAsync();
                model.BoardReconciliationAcknowledged = true;
                await model.ConfirmBoardReconciledAsync();
                await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            }
            else
            {
                await model.StartMatchAsync();
                await model.CommitTicketsAsync();
            }
            var fixture = new Fixture(model);
            Assert.Equal(TurnPhase.TurnStart, fixture.Coordinator.Public.TurnPhase);
            Assert.Equal(PlayerColor.Black, fixture.Coordinator.Public.SeatOf(
                fixture.Coordinator.Public.ActiveSeatId).Color);
            return fixture;
        }

        public async Task<Snapshot> SnapshotAsync() => new(Coordinator.Public.StateVersion,
            Coordinator.Public.TurnNumber, Coordinator.Public.ActiveSeatId,
            await Coordinator.ComputeStateHashAsync());

        public async Task AssertUnchangedAsync(Snapshot before)
        {
            Assert.Equal(before.Version, Coordinator.Public.StateVersion);
            Assert.Equal(before.Turn, Coordinator.Public.TurnNumber);
            Assert.Equal(before.Seat, Coordinator.Public.ActiveSeatId);
            Assert.Equal(before.Hash, await Coordinator.ComputeStateHashAsync());
        }

        public void PublishCleanBaseline()
        {
            Model.Camera.IsGameTablePreviewUpright = true;
            var at = DateTimeOffset.UtcNow;
            Publish(1, at.AddSeconds(-1.1));
            Publish(2, at);
            Assert.True(Model.DrawSoloBlindCommand.CanExecute(null));
        }

        public void Publish(long sequence, DateTimeOffset capturedAt, bool blackTrains = false,
            bool stale = false)
        {
            const int width = 960, height = 600;
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)180);
            var candidates = new List<PieceCandidate>();
            if (blackTrains)
            {
                Assert.True(ClassicUsRouteGeometry.TryGetSlots("little-rock--saint-louis", out var slots));
                Assert.Equal(2, slots.Count);
                foreach (var spot in slots)
                {
                    var x = (int)Math.Round(spot.X * width);
                    var y = (int)Math.Round(spot.Y * height);
                    const int halfWidth = 11, halfHeight = 7;
                    for (var py = y - halfHeight; py <= y + halfHeight; py++)
                    for (var px = x - halfWidth; px <= x + halfWidth; px++)
                    {
                        var offset = (py * width + px) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 20;
                    }
                    candidates.Add(new(PieceCandidateKind.Train,
                        [new((double)(x - halfWidth) / width, (double)(y - halfHeight) / height),
                         new((double)(x + halfWidth) / width, (double)(y - halfHeight) / height),
                         new((double)(x + halfWidth) / width, (double)(y + halfHeight) / height),
                         new((double)(x - halfWidth) / width, (double)(y + halfHeight) / height)], .95));
                }
            }
            var frameClock = stale ? new ManualFrameTimeProvider() : null;
            var frame = CameraFrame.CopyFromBgra32(width, height, pixels, sequence,
                epoch: 1, capturedAt: capturedAt, clock: frameClock);
            frameClock?.Advance(TimeSpan.FromSeconds(3));
            var analysis = new GameTableAnalysis(frame, candidates, [], 1, 1, "synthetic-card-action-test");
            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                .GetSetMethod(nonPublic: true)!.Invoke(Model.Camera, [analysis]);
        }

        public async ValueTask DisposeAsync() => await Model.DisposeToolsAsync();
    }
}
