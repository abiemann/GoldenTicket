using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Testing;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopCardActionBoardTests
{
    [Fact]
    public async Task First_card_is_awarded_immediately_without_a_new_camera_capture()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.PublishCleanBaseline();
        var model = fixture.Model;
        var before = await fixture.SnapshotAsync();
        var hand = await fixture.Coordinator.GetSeatViewAsync(before.Seat, TestContext.Current.CancellationToken);
        var faceUp = model.Table.Market.First(slot => slot.Kind != TrainCardKind.Locomotive);

        await ImmediateAsync(() => model.DrawSoloFaceUpCommand.ExecuteAsync(faceUp));

        var after = await fixture.Coordinator.GetSeatViewAsync(before.Seat, TestContext.Current.CancellationToken);
        Assert.Equal(faceUp.Kind, Assert.Single(after.Hand.Except(hand.Hand)).Kind);
        Assert.Equal(before.Turn, fixture.Coordinator.Public.TurnNumber);
        Assert.Equal(before.Seat, fixture.Coordinator.Public.ActiveSeatId);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
        Assert.False(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(2, model.Camera.GameTableAnalysis!.Board.Sequence);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.Equal(0, fixture.Ai.Decisions);
    }

    [Fact]
    public async Task Second_card_is_saved_before_board_check_and_computers_wait_for_fresh_good_frames()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.PublishCleanBaseline();
        var model = fixture.Model;
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        var beforeSecond = await fixture.Coordinator.GetSeatViewAsync(seat, TestContext.Current.CancellationToken);
        var faceUp = model.Table.Market.First(slot => model.DrawSoloFaceUpCommand.CanExecute(slot));
        await ImmediateAsync(() => model.DrawSoloFaceUpCommand.ExecuteAsync(faceUp));

        var awarded = await fixture.Coordinator.GetSeatViewAsync(seat, TestContext.Current.CancellationToken);
        Assert.Equal(faceUp.Kind, Assert.Single(awarded.Hand.Except(beforeSecond.Hand)).Kind);
        Assert.Equal(6, awarded.Hand.Length);
        Assert.NotEqual(seat, fixture.Coordinator.Public.ActiveSeatId);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.False(model.CanRevealPrivateSeat);
        Assert.Equal(0, fixture.Ai.Decisions);

        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(0, fixture.Ai.Decisions);
        fixture.Publish(4, at.AddSeconds(1.1));
        await fixture.WaitForNextHumanTurnAsync();
        Assert.Equal(4, fixture.Ai.Decisions);
        Assert.Equal(6, (await fixture.Coordinator.GetSeatViewAsync(seat,
            TestContext.Current.CancellationToken)).Hand.Length);
    }

    [Fact]
    public async Task Unexpected_train_during_card_selection_does_not_steal_the_second_card_and_is_marked_at_handoff()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.PublishCleanBaseline();
        var model = fixture.Model;
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at, offRouteTrain: true);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        Assert.Equal(6, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);

        fixture.Publish(4, at.AddSeconds(1.1), offRouteTrain: true);
        Assert.Contains("yellow spheres", model.Game.GuidanceInstruction);
        var target = Assert.Single(model.Game.PlacementTargets);
        Assert.True(model.Game.ShowPlacementTarget);
        Assert.Equal(.476 * 960, target.X, precision: 5);
        Assert.Equal(.9135 * 600, target.Y, precision: 5);
        var checkedState = await fixture.SnapshotAsync();
        fixture.Publish(4, at.AddSeconds(2.2));
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(target, Assert.Single(model.Game.PlacementTargets));
        fixture.Publish(5, at.AddSeconds(3.3), stale: true);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        await fixture.AssertUnchangedAsync(checkedState);
        Assert.Equal(0, fixture.Ai.Decisions);

        fixture.Publish(6, at.AddSeconds(4.4));
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        fixture.Publish(7, at.AddSeconds(5.5));
        await fixture.WaitForNextHumanTurnAsync();
        Assert.Empty(model.Game.PlacementTargets);
        Assert.False(model.Game.ShowPlacementTarget);
        Assert.Equal(6, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_extra_claimed_route_trains_block_only_the_handoff_after_cards_are_awarded(bool extra)
    {
        await using var fixture = await Fixture.CreateAsync(claimedCalgary: true);
        var model = fixture.Model;
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        var beforeCount = fixture.Coordinator.Public.SeatOf(seat).TrainCardCount;
        model.Camera.IsGameTablePreviewUpright = true;
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(1, at, claimedCalgaryCount: 4, claimedCalgaryColor: null);
        fixture.Publish(2, at.AddSeconds(1.1), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        fixture.Publish(3, at.AddSeconds(2.2), blackTrains: extra,
            claimedCalgaryCount: extra ? 4 : 3, claimedCalgaryColor: null);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        Assert.Equal(beforeCount + 2, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);

        fixture.Publish(4, at.AddSeconds(3.3), blackTrains: extra,
            claimedCalgaryCount: extra ? 4 : 3, claimedCalgaryColor: null);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.NotEmpty(model.Game.PlacementTargets);
        Assert.Equal(0, fixture.Ai.Decisions);
        Assert.Equal(seat, fixture.Coordinator.Public.RouteOwners[new RouteId("calgary--helena")]);
        Assert.Equal(7, fixture.Coordinator.Public.SeatOf(seat).RouteScore);

        fixture.Publish(5, at.AddSeconds(4.4), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        fixture.Publish(6, at.AddSeconds(5.5), claimedCalgaryCount: 4, claimedCalgaryColor: null);
        await fixture.WaitForNextHumanTurnAsync();
        Assert.Equal(beforeCount + 2, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);
    }

    [Fact]
    public async Task Payable_trains_detected_after_a_card_cannot_replace_the_already_started_draw_turn()
    {
        await using var fixture = await Fixture.CreateAsync(payableRoute: true);
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        var at = DateTimeOffset.UtcNow;
        for (var sequence = 3; sequence <= 6; sequence++)
            fixture.Publish(sequence, at.AddSeconds((sequence - 3) * 1.1), blackTrains: true);
        Assert.Null(model.BoardFirstProposal);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, fixture.Coordinator.Public.TurnPhase);
        Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        Assert.Equal(6, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        fixture.Publish(7, at.AddSeconds(4.4), blackTrains: true);
        Assert.NotEmpty(model.Game.PlacementTargets);
        Assert.Null(model.BoardFirstProposal);
        Assert.Null(fixture.Coordinator.Public.PendingClaim);
        Assert.Equal(0, fixture.Ai.Decisions);
    }

    [Fact]
    public async Task Cached_and_inflight_frames_cannot_release_the_next_player_after_cards_are_saved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        var capturedBeforeCompletion = DateTimeOffset.UtcNow.AddMilliseconds(-20);
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        var awarded = await fixture.SnapshotAsync();

        fixture.Publish(3, capturedBeforeCompletion.AddSeconds(-1.1));
        fixture.Publish(4, capturedBeforeCompletion);
        fixture.Publish(2, DateTimeOffset.UtcNow);
        fixture.Publish(5, DateTimeOffset.UtcNow, stale: true);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(0, fixture.Ai.Decisions);
        await fixture.AssertUnchangedAsync(awarded);

        var at = DateTimeOffset.UtcNow;
        fixture.Publish(6, at);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        fixture.Publish(7, at.AddSeconds(1.1));
        await fixture.WaitForNextHumanTurnAsync();
    }

    [Fact]
    public async Task Requested_camera_without_analysis_allows_card_awards_but_holds_the_next_turn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        model.SetGameLayerVisible(true);
        typeof(CameraViewModel).GetField("_gameTablePreviewRequested",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model.Camera, true);
        Assert.Null(model.Camera.GameTableAnalysis);
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        Assert.False(model.IsCheckingBoardBeforeNextTurn);
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        Assert.Equal(6, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(0, fixture.Ai.Decisions);
        Assert.False(model.CanRevealPrivateSeat);

        model.Camera.IsGameTablePreviewUpright = true;
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(1, at);
        fixture.Publish(2, at.AddSeconds(1.1));
        await fixture.WaitForNextHumanTurnAsync();
    }

    [Fact]
    public async Task Focus_loss_preserves_awarded_cards_and_requires_new_board_evidence_on_return()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        await ImmediateAsync(() => model.DrawSoloBlindCommand.ExecuteAsync(null));
        var awarded = await fixture.SnapshotAsync();
        model.SetWindowActive(false);
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at);
        fixture.Publish(4, at.AddSeconds(1.1));
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(0, fixture.Ai.Decisions);
        await fixture.AssertUnchangedAsync(awarded);

        model.SetWindowActive(true);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.False(model.CanRevealPrivateSeat);
        fixture.Publish(5, at.AddSeconds(2.2));
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        fixture.Publish(6, at.AddSeconds(3.3));
        await fixture.WaitForNextHumanTurnAsync();
        Assert.Equal(6, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Technical_private_card_actions_save_immediately_and_verify_only_when_the_turn_completes(bool tickets)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        fixture.PublishCleanBaseline();
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        var ticketCount = fixture.Coordinator.Public.SeatOf(seat).TicketCount;
        await model.RevealPrivateSeatAsync();
        await ImmediateAsync(tickets ? model.DrawTicketsAsync : model.DrawBlindCardAsync);
        Assert.Equal(tickets ? TurnPhase.AwaitingTicketKeep : TurnPhase.AwaitingSecondTrainCard,
            fixture.Coordinator.Public.TurnPhase);
        Assert.False(model.IsCheckingBoardBeforeNextTurn);
        await model.RevealPrivateSeatAsync();
        var offered = model.PrivateSeat!.Offer.Count;
        await ImmediateAsync(tickets ? model.CommitTicketsAsync : model.DrawBlindCardAsync);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(tickets ? 4 : 6, fixture.Coordinator.Public.SeatOf(seat).TrainCardCount);
        Assert.Equal(tickets ? ticketCount + offered : ticketCount,
            fixture.Coordinator.Public.SeatOf(seat).TicketCount);
        Assert.Equal(0, fixture.Ai.Decisions);
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at);
        fixture.Publish(4, at.AddSeconds(1.1));
        await fixture.WaitForNextHumanTurnAsync();
    }

    [Fact]
    public async Task Face_up_locomotive_is_awarded_immediately_then_requires_the_handoff_board_check()
    {
        await using var fixture = await Fixture.CreateAsync(visibleLocomotive: true);
        fixture.PublishCleanBaseline();
        var model = fixture.Model;
        var seat = fixture.Coordinator.Public.ActiveSeatId;
        var before = await fixture.Coordinator.GetSeatViewAsync(seat, TestContext.Current.CancellationToken);
        var locomotive = model.Table.Market.First(slot => slot.Kind == TrainCardKind.Locomotive);
        await ImmediateAsync(() => model.DrawSoloFaceUpCommand.ExecuteAsync(locomotive));
        var after = await fixture.Coordinator.GetSeatViewAsync(seat, TestContext.Current.CancellationToken);
        Assert.Equal(TrainCardKind.Locomotive, Assert.Single(after.Hand.Except(before.Hand)).Kind);
        Assert.Equal(5, after.Hand.Length);
        Assert.True(model.IsCheckingBoardBeforeNextTurn);
        Assert.Equal(0, fixture.Ai.Decisions);
        Assert.NotEqual(seat, fixture.Coordinator.Public.ActiveSeatId);
        var at = DateTimeOffset.UtcNow;
        fixture.Publish(3, at);
        fixture.Publish(4, at.AddSeconds(1.1));
        await fixture.WaitForNextHumanTurnAsync();
    }

    private static Task ImmediateAsync(Func<Task> action) =>
        action().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

    private sealed record Snapshot(long Version, int Turn, SeatId Seat, string Hash);

    private sealed class CountingDrawPolicy : IAiPolicy
    {
        public int Decisions { get; private set; }

        public ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest,
            DecisionBudget budget, DeterministicRandom random, CancellationToken cancellationToken)
        {
            Decisions++;
            return ValueTask.FromResult<AiDecision>(new AiDrawTrainCard(null));
        }
    }

    private sealed class Fixture(MainViewModel model) : IAsyncDisposable
    {
        public MainViewModel Model { get; } = model;
        public GameCoordinator Coordinator { get; } = Assert.IsType<GameCoordinator>(typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model));
        public CountingDrawPolicy Ai { get; } = new();

        public static async Task<Fixture> CreateAsync(bool payableRoute = false, bool claimedCalgary = false,
            bool visibleLocomotive = false)
        {
            var token = TestContext.Current.CancellationToken;
            var manifest = ManifestLoader.LoadClassicUs();
            var store = new InMemorySessionStore();
            var model = new MainViewModel(manifest, store,
                camera: new CameraViewModel(capture: new FakeCameraCapture()));
            model.Setup.ManualVerificationAccepted = true;
            model.Setup.Seats[0].Color = PlayerColor.Black;
            model.Setup.Seats[1].Color = PlayerColor.Red;
            model.Setup.Seats[2].Color = PlayerColor.Blue;
            for (var index = 0; index < model.Setup.Seats.Count; index++)
                model.Setup.Seats[index].IsComputer = index != 0;
            if (payableRoute || claimedCalgary || visibleLocomotive)
            {
                // Seed 42 starts with a legal two-pink-card payment for Little Rock-Saint Louis.
                var rules = new GameRules(manifest, CardCatalog.FromManifest(manifest));
                var setup = model.Setup.TryBuildSetup()!;
                var seed = visibleLocomotive
                    ? Enumerable.Range(1, 100).First(candidate => rules.ProjectPublic(rules.CreateSession(
                        setup, DeterministicRandom.SeedFrom((ulong)candidate)).State).FaceUp
                        .Contains(TrainCardKind.Locomotive))
                    : 42;
                var game = await GameCoordinator.CreateAsync(rules, store, setup,
                    DeterministicRandom.SeedFrom((ulong)seed), token);
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
            typeof(MainViewModel).GetField("_driver", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, new ComputerSeatDriver(fixture.Coordinator, fixture.Ai, aiSeed: 1));
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

        public async Task WaitForNextHumanTurnAsync()
        {
            for (var attempt = 0; attempt < 200 &&
                 (Model.IsCheckingBoardBeforeNextTurn || Ai.Decisions < 4 ||
                  !Model.DrawSoloBlindCommand.CanExecute(null)); attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.False(Model.IsCheckingBoardBeforeNextTurn);
            Assert.Equal(4, Ai.Decisions);
            Assert.True(Model.IsSoloHumanTurn);
            Assert.True(Model.DrawSoloBlindCommand.CanExecute(null));
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
