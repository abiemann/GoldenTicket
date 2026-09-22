using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    public async Task Temporary_unlocated_detection_clears_on_the_next_fresh_matching_board_without_spending_a_turn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        var before = await fixture.SnapshotAsync();
        var handCount = fixture.Coordinator.Public.SeatOf(before.Seat).TrainCardCount;
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at, offRouteTrain: true);
        var warning = model.Game.GuidanceInstruction;
        Assert.Contains("yellow spheres", warning);
        var target = Assert.Single(model.Game.PlacementTargets);
        Assert.True(model.Game.ShowPlacementTarget);
        Assert.Equal(.476 * 960, target.X, precision: 5);
        Assert.Equal(.9135 * 600, target.Y, precision: 5);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));

        // Neither reusing a capture nor losing fresh camera evidence proves correction.
        fixture.Publish(3, at.AddMilliseconds(100));
        Assert.Equal(warning, model.Game.GuidanceInstruction);
        Assert.Equal(target, Assert.Single(model.Game.PlacementTargets));
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        fixture.Publish(4, at.AddMilliseconds(200), stale: true);
        Assert.Equal(warning, model.Game.GuidanceInstruction);
        Assert.Equal(target, Assert.Single(model.Game.PlacementTargets));
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));

        fixture.Publish(5, at.AddMilliseconds(300));
        Assert.Equal(model.Table.Instruction, model.Game.GuidanceInstruction);
        Assert.Empty(model.Game.PlacementTargets);
        Assert.False(model.Game.ShowPlacementTarget);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.True(model.DrawSoloTicketsCommand.CanExecute(null));
        await fixture.AssertUnchangedAsync(before);

        // Clearing a warning does not itself authorize or retry a card draw.
        var draw = model.DrawSoloBlindCommand.ExecuteAsync(null);
        Assert.False(draw.IsCompleted);
        var freshAt = DateTimeOffset.UtcNow;
        fixture.Publish(6, freshAt);
        Assert.False(draw.IsCompleted);
        await fixture.AssertUnchangedAsync(before);
        fixture.Publish(7, freshAt.AddSeconds(1.1));
        await draw.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
        Assert.Equal(before.Version + 1, fixture.Coordinator.Public.StateVersion);
        Assert.Equal(before.Turn, fixture.Coordinator.Public.TurnNumber);
        Assert.Equal(before.Seat, fixture.Coordinator.Public.ActiveSeatId);
        Assert.Equal(handCount + 1, fixture.Coordinator.Public.SeatOf(before.Seat).TrainCardCount);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
    }

    [Fact]
    public async Task Claimed_Calgary_trains_with_unclear_colors_clear_the_warning_and_allow_a_fresh_draw()
    {
        await using var fixture = await Fixture.CreateAsync(claimedCalgary: true);
        var model = fixture.Model;
        var before = await fixture.SnapshotAsync();
        var handCount = fixture.Coordinator.Public.SeatOf(before.Seat).TrainCardCount;
        model.Camera.IsGameTablePreviewUpright = true;
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(1, at, claimedCalgaryCount: 0);
        Assert.Contains("cannot verify every train on Calgary - Helena", model.Game.GuidanceInstruction);
        Assert.Contains("The claim is still recorded", model.Game.GuidanceInstruction);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));

        fixture.Publish(2, at.AddSeconds(1.1), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        fixture.Publish(3, at.AddSeconds(2.2), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.Null(model.BoardFirstProposal);
        await fixture.AssertUnchangedAsync(before);

        var draw = model.DrawSoloBlindCommand.ExecuteAsync(null);
        Assert.False(draw.IsCompleted);
        var freshAt = DateTimeOffset.UtcNow;
        fixture.Publish(4, freshAt, claimedCalgaryCount: 4, claimedCalgaryColor: null);
        Assert.False(draw.IsCompleted);
        fixture.Publish(5, freshAt.AddSeconds(1.1), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        await draw.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);

        Assert.Equal(before.Version + 1, fixture.Coordinator.Public.StateVersion);
        Assert.Equal(before.Turn, fixture.Coordinator.Public.TurnNumber);
        Assert.Equal(before.Seat, fixture.Coordinator.Public.ActiveSeatId);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
        Assert.Equal(handCount + 1, fixture.Coordinator.Public.SeatOf(before.Seat).TrainCardCount);
        Assert.Equal(7, fixture.Coordinator.Public.SeatOf(before.Seat).RouteScore);
        Assert.Equal(before.Seat, fixture.Coordinator.Public.RouteOwners[new RouteId("calgary--helena")]);
        Assert.Null(fixture.Coordinator.Public.PendingClaim);
        Assert.Null(model.BoardFirstProposal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_color_reuse_still_blocks_missing_or_extra_trains_without_spending_another_card(bool extra)
    {
        await using var fixture = await Fixture.CreateAsync(claimedCalgary: true);
        var model = fixture.Model;
        await model.DrawSoloBlindCommand.ExecuteAsync(null); // No camera has been used yet.
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
        var before = await fixture.SnapshotAsync();
        var faceUp = model.Table.Market.First(slot => model.DrawSoloFaceUpCommand.CanExecute(slot));
        model.Camera.IsGameTablePreviewUpright = true;
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(1, at, blackTrains: extra, claimedCalgaryCount: extra ? 4 : 3,
            claimedCalgaryColor: null);

        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.False(model.DrawSoloFaceUpCommand.CanExecute(faceUp));
        Assert.Contains(extra ? "You already chose to draw cards" : "The claim is still recorded",
            model.Game.GuidanceInstruction);
        Assert.Null(model.BoardFirstProposal);
        await model.DrawSoloFaceUpCommand.ExecuteAsync(faceUp);
        await fixture.AssertUnchangedAsync(before);

        fixture.Publish(2, at.AddSeconds(1.1), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        fixture.Publish(3, at.AddSeconds(2.2), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.True(model.DrawSoloFaceUpCommand.CanExecute(faceUp));
        await fixture.AssertUnchangedAsync(before);
        Assert.Equal(before.Seat, fixture.Coordinator.Public.RouteOwners[new RouteId("calgary--helena")]);
    }

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
        Assert.True(model.DrawSoloFaceUpCommand.CanExecute(faceUp));
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
            fixture.Coordinator.Public.ActiveSeatId, cancellationToken: TestContext.Current.CancellationToken)).Claims,
            claim => claim.RouteId.Value == "little-rock--saint-louis");
        fixture.PublishCleanBaseline();
        var before = await fixture.SnapshotAsync();
        var draw = model.DrawSoloBlindCommand.ExecuteAsync(null);
        Assert.False(draw.IsCompleted);

        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at, blackTrains: true);
        await draw.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);

        await fixture.AssertUnchangedAsync(before);
        Assert.Equal(TurnPhase.TurnStart, fixture.Coordinator.Public.TurnPhase);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
        await model.DrawSoloTicketsCommand.ExecuteAsync(null);
        await fixture.AssertUnchangedAsync(before);
        Assert.False(model.ShowSoloTicketOffer);

        // Wait for cancellation before publishing the route proof: frames sent while the
        // draw is still pending are intentionally ignored by the board-first observer.
        // Two frames discover the route; the third completes whole-board verification
        // before payment is offered. Waiting alone must not substitute for camera evidence.
        fixture.Publish(4, at.AddSeconds(1.1), blackTrains: true);
        Assert.Null(model.BoardFirstProposal);
        fixture.Publish(5, at.AddSeconds(2.2), blackTrains: true);
        Assert.Null(model.BoardFirstProposal);
        Assert.Null(fixture.Coordinator.Public.PendingClaim);
        await fixture.AssertUnchangedAsync(before);
        fixture.Publish(6, at.AddSeconds(3.3), blackTrains: true);
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
        Assert.NotEqual(before.Hash, await fixture.Coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken));
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

        public static async Task<Fixture> CreateAsync(bool payableRoute = false, bool claimedCalgary = false)
        {
            var token = TestContext.Current.CancellationToken;
            var manifest = ManifestLoader.LoadClassicUs();
            var store = new InMemorySessionStore();
            var model = new MainViewModel(manifest, store);
            model.Setup.ManualVerificationAccepted = true;
            model.Setup.Seats[0].Color = PlayerColor.Black;
            model.Setup.Seats[1].Color = PlayerColor.Red;
            model.Setup.Seats[2].Color = PlayerColor.Blue;
            for (var index = 0; index < model.Setup.Seats.Count; index++)
                model.Setup.Seats[index].IsComputer = index != 0;
            if (payableRoute || claimedCalgary)
            {
                // Seed 42 starts with a legal two-pink-card payment for Little Rock-Saint Louis.
                var game = await GameCoordinator.CreateAsync(new GameRules(manifest,
                        CardCatalog.FromManifest(manifest)), store, model.Setup.TryBuildSetup()!,
                    DeterministicRandom.SeedFrom(42), token);
                foreach (var seat in game.Seats)
                {
                    var view = await game.GetSeatViewAsync(seat.SeatId, token);
                    Assert.True((await game.SubmitAsync(new CommitTicketSelection(
                        game.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []), token)).IsAccepted);
                }
                if (claimedCalgary) await ClaimCalgaryAsync(game);
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

        private static async Task ClaimCalgaryAsync(GameCoordinator game)
        {
            var token = TestContext.Current.CancellationToken;
            var human = game.Public.ActiveSeatId;
            var routeId = new RouteId("calgary--helena");
            LegalClaim? choice = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                choice = (await game.GetLegalActionsAsync(human, token)).Claims.FirstOrDefault(claim => claim.RouteId == routeId);
                if (choice is not null) break;
                do { await DrawTurnAsync(game); } while (game.Public.ActiveSeatId != human);
            }
            Assert.NotNull(choice);
            var hand = await game.GetSeatViewAsync(human, token);
            Assert.True((await game.SubmitAsync(new PlanClaim(game.NewEnvelope(human), routeId,
                LegalActionCalculator.ResolveCards(hand, choice.Payments[0])), token)).IsAccepted);
            Assert.True((await game.SubmitAsync(new SubmitClaimEvidence(game.NewEnvelope(human),
                game.Public.PendingClaim!.OperationId, EvidenceKind.CameraAutomatic, "synthetic-test",
                "All four black Calgary-Helena trains were verified."), token)).IsAccepted);
            while (game.Public.ActiveSeatId != human) await DrawTurnAsync(game);
            Assert.Equal(7, game.Public.SeatOf(human).RouteScore);
        }

        private static async Task DrawTurnAsync(GameCoordinator game)
        {
            var token = TestContext.Current.CancellationToken;
            var seat = game.Public.ActiveSeatId;
            Assert.True((await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(seat), null), token)).IsAccepted);
            Assert.True((await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(seat), null), token)).IsAccepted);
        }

        public async Task<Snapshot> SnapshotAsync() => new(Coordinator.Public.StateVersion,
            Coordinator.Public.TurnNumber, Coordinator.Public.ActiveSeatId,
            await Coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken));

        public async Task AssertUnchangedAsync(Snapshot before)
        {
            Assert.Equal(before.Version, Coordinator.Public.StateVersion);
            Assert.Equal(before.Turn, Coordinator.Public.TurnNumber);
            Assert.Equal(before.Seat, Coordinator.Public.ActiveSeatId);
            Assert.Equal(before.Hash, await Coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken));
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
            bool stale = false, int? claimedCalgaryCount = null, MarkerColor? claimedCalgaryColor = MarkerColor.Black,
            bool offRouteTrain = false)
        {
            const int width = 960, height = 600;
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)180);
            if (Model.Camera.GameTablePreview is null)
            {
                var preview = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                    null, pixels, width * 4);
                preview.Freeze();
                Model.Camera.GameTablePreview = preview;
            }
            var candidates = new List<PieceCandidate>();
            if (blackTrains) Paint("little-rock--saint-louis", 2, MarkerColor.Black);
            if (claimedCalgaryCount is { } count) Paint("calgary--helena", count, claimedCalgaryColor);
            if (offRouteTrain)
                candidates.Add(new(PieceCandidateKind.Train,
                    [new(.465, .905), new(.487, .905), new(.487, .922), new(.465, .922)], .95));

            void Paint(string routeId, int trainCount, MarkerColor? color)
            {
                Assert.True(ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots));
                foreach (var spot in slots.Take(trainCount))
                {
                    var x = (int)Math.Round(spot.X * width);
                    var y = (int)Math.Round(spot.Y * height);
                    const int halfWidth = 11, halfHeight = 7;
                    for (var py = y - halfHeight; py <= y + halfHeight; py++)
                    for (var px = x - halfWidth; px <= x + halfWidth; px++)
                    {
                        var offset = (py * width + px) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = color == MarkerColor.Black ? (byte)20 : (byte)128;
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
            if (claimedCalgaryColor is null && claimedCalgaryCount is > 0)
                Assert.All(candidates.TakeLast(claimedCalgaryCount.Value),
                    candidate => Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, candidate)));
            var analysis = new GameTableAnalysis(frame, candidates, [], 1, 1, "synthetic-card-action-test");
            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                .GetSetMethod(nonPublic: true)!.Invoke(Model.Camera, [analysis]);
        }

        public async ValueTask DisposeAsync() => await Model.DisposeToolsAsync();
    }
}
