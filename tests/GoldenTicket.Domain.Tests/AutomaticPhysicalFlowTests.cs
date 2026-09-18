using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class AutomaticPhysicalFlowTests
{
    [Fact]
    public void Board_first_payment_requires_exact_legal_cards_including_wilds()
    {
        var whiteAndWild = new PaymentOption(TrainCardKind.White, 1, 1);
        var allWild = new PaymentOption(TrainCardKind.Locomotive, 0, 2);
        var proposal = new BoardFirstClaimProposal(SessionId.New(), new SeatId(1),
            "Player 1", new RouteId("example-route"), "Example route",
            1, 1, 1, 1,
            [new BoardFirstPaymentRow(whiteAndWild, whiteAndWild.Describe()),
             new BoardFirstPaymentRow(allWild, allWild.Describe())],
            [new HeldCard(new CardId(1), TrainCardKind.White),
             new HeldCard(new CardId(2), TrainCardKind.Yellow),
             new HeldCard(new CardId(3), TrainCardKind.Locomotive),
             new HeldCard(new CardId(4), TrainCardKind.Locomotive)]);

        Assert.False(proposal.CanConfirmPayment);
        Assert.DoesNotContain(proposal.Cards, card => card.Kind == TrainCardKind.Yellow);
        proposal.Cards[0].IsSelected = true;
        Assert.False(proposal.CanConfirmPayment);
        proposal.Cards[1].IsSelected = true;
        Assert.Equal(whiteAndWild, proposal.SelectedPayment?.Option);
        Assert.Equal([new CardId(1), new CardId(3)], proposal.SelectedCardIds);
        proposal.Cards[2].IsSelected = true;
        Assert.False(proposal.CanConfirmPayment);
        proposal.Cards[0].IsSelected = false;
        Assert.Equal(allWild, proposal.SelectedPayment?.Option);
    }

    [Fact]
    public async Task Solo_partial_unpayable_route_explains_the_invalid_move_without_claiming_it()
    {
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var coordinator = GetCoordinator(model);
            var active = coordinator.Public.ActiveSeatId;
            var legal = await coordinator.GetLegalActionsAsync(active,
                TestContext.Current.CancellationToken);
            var routeId = legal.Claims.Any(claim => claim.RouteId.Value == "calgary--seattle")
                ? "calgary--winnipeg" : "calgary--seattle";
            Assert.DoesNotContain(legal.Claims, claim => claim.RouteId.Value == routeId);

            model.Camera.IsGameTablePreviewUpright = true;
            var color = Enum.Parse<MarkerColor>(coordinator.Public.SeatOf(active).Color.ToString());
            var at = DateTimeOffset.UtcNow;
            PublishTrains(model.Camera, routeId, 1, at, color, 2);
            await WaitUntilAsync(() => typeof(MainViewModel)
                .GetField("_boardFirstLegalActions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(model) is not null);
            PublishTrains(model.Camera, routeId, 2, at.AddSeconds(1.1), color, 2);
            PublishTrains(model.Camera, routeId, 3, at.AddSeconds(2.2), color, 2);
            await WaitUntilAsync(() => model.Game.GuidanceTurn == "Invalid Move");

            Assert.Contains(manifest.Describe(new RouteId(routeId)), model.Game.GuidanceInstruction);
            Assert.Contains("2 of", model.Game.GuidanceInstruction);
            Assert.Contains("train cards", model.Game.GuidanceInstruction);
            Assert.Null(coordinator.Public.PendingClaim);
            Assert.Equal(TurnPhase.TurnStart, coordinator.Public.TurnPhase);
            Assert.Equal(Screen.Table, model.Screen);

            PublishTrains(model.Camera, routeId, 4, at.AddSeconds(3.3), color, 0);
            Assert.Equal("Invalid Move", model.Game.GuidanceTurn);
            PublishTrains(model.Camera, routeId, 5, at.AddSeconds(4.4), color, 0);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Invalid Move");
            Assert.Equal(model.Table.TurnText, model.Game.GuidanceTurn);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Solo_human_can_authorize_a_route_detected_from_trains_placed_first()
    {
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            Assert.Null(model.PrivateSeat);
            Assert.Equal(TurnPhase.TurnStart, GetCoordinator(model).Public.TurnPhase);
            Assert.Contains("Place trains on a route", model.Game.GuidanceInstruction);

            var coordinator = GetCoordinator(model);
            var active = coordinator.Public.ActiveSeatId;
            var legal = await coordinator.GetLegalActionsAsync(active,
                TestContext.Current.CancellationToken);
            var route = legal.Claims.First(claim =>
                RoutePlacementVerifier.Supports(claim.RouteId.Value, claim.Length));
            var at = DateTimeOffset.UtcNow;
            model.Camera.IsGameTablePreviewUpright = true;
            PublishBlueTrains(model.Camera, route.RouteId.Value, 1, at);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            PublishBlueTrains(model.Camera, route.RouteId.Value, 2, at.AddSeconds(1.1));
            PublishBlueTrains(model.Camera, route.RouteId.Value, 3, at.AddSeconds(2.2));
            await WaitUntilAsync(() => model.BoardFirstProposal is not null);

            var proposal = Assert.IsType<BoardFirstClaimProposal>(model.BoardFirstProposal);
            Assert.Equal(route.RouteId, proposal.RouteId);
            Assert.NotEmpty(proposal.Payments);
            Assert.Contains("Choose which train cards to spend", model.Game.GuidanceInstruction);
            Assert.Null(coordinator.Public.PendingClaim);

            Assert.False(proposal.CanConfirmPayment);
            await model.ConfirmBoardFirstClaimCommand.ExecuteAsync(null);
            Assert.Null(coordinator.Public.PendingClaim);
            var payment = proposal.Payments[0].Option;
            foreach (var card in proposal.Cards.Where(card => card.Kind == payment.Color)
                         .Take(payment.ColorCards)) card.IsSelected = true;
            foreach (var card in proposal.Cards.Where(card => card.Kind == TrainCardKind.Locomotive)
                         .Take(payment.Locomotives)) card.IsSelected = true;
            Assert.True(proposal.CanConfirmPayment);

            // A fresh board alignment changes the crop revision without changing the
            // physical route. Keep the chosen cards visible, but pause payment until
            // the route has been confirmed in the new crop.
            PublishBlueTrains(model.Camera, route.RouteId.Value, 4, at.AddSeconds(3.3),
                cropRevision: 2);
            Assert.Same(proposal, model.BoardFirstProposal);
            Assert.False(proposal.CameraEvidenceCurrent);
            Assert.False(proposal.CanConfirmPayment);
            await model.ConfirmBoardFirstClaimCommand.ExecuteAsync(null);
            Assert.Null(coordinator.Public.PendingClaim);

            PublishTrains(model.Camera, route.RouteId.Value, 5, at.AddSeconds(4.4),
                MarkerColor.Blue, count: 0, cropRevision: 2);
            Assert.Same(proposal, model.BoardFirstProposal);
            Assert.False(proposal.CanConfirmPayment);

            PublishBlueTrains(model.Camera, route.RouteId.Value, 6, at.AddSeconds(5.5),
                cropRevision: 2);
            PublishBlueTrains(model.Camera, route.RouteId.Value, 7, at.AddSeconds(6.6),
                cropRevision: 2);
            Assert.Same(proposal, model.BoardFirstProposal);
            Assert.True(proposal.CameraEvidenceCurrent);
            Assert.True(proposal.CanConfirmPayment);
            Assert.Equal(payment.Total, proposal.SelectedCount);
            await model.ConfirmBoardFirstClaimCommand.ExecuteAsync(null);
            Assert.Null(model.BoardFirstProposal);
            Assert.NotNull(coordinator.Public.PendingClaim);
            Assert.Contains("Keep your", model.Game.GuidanceInstruction);

            PublishBlueTrains(model.Camera, route.RouteId.Value, 8, at.AddSeconds(7.7),
                cropRevision: 2);
            PublishBlueTrains(model.Camera, route.RouteId.Value, 9, at.AddSeconds(8.8),
                cropRevision: 2);
            await WaitUntilAsync(() => model.Game.GuidanceInstruction == "Thank you");
            Assert.Null(coordinator.Public.PendingClaim);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Manual_claim_also_waits_for_the_physical_score_marker_before_the_next_turn()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = true;
        try
        {
            await model.StartMatchAsync();
            var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
            var scoreBefore = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score;

            model.Table.WholeBoardAcknowledged = true;
            var confirmation = model.ConfirmPlacementAsync();
            await WaitUntilAsync(() => model.Game.GuidanceInstruction == "Thank you");
            Assert.Null(model.Table.Placement);
            Assert.False(model.Table.WholeBoardAcknowledged);
            Assert.Equal(scoreBefore + TestManifest.Manifest.RulesConstants.ScoreForLength(placement.TrainCount),
                model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score);
            await confirmation;
            Assert.Contains("Move", model.Game.GuidanceInstruction);
            Assert.Equal("Scoring", model.Game.GuidanceTurn);

            var target = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score % 100 + 1;
            model.Camera.IsGameTablePreviewUpright = true;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            var firstAt = DateTimeOffset.UtcNow;
            PublishScore(model.Camera, 1, firstAt, color, target);
            Assert.Equal("Scoring", model.Game.GuidanceTurn);
            PublishScore(model.Camera, 2, firstAt.AddSeconds(1.1), color, target);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Scoring");
            Assert.NotEqual(placement.OperationId, model.Table.Placement?.OperationId);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Marker_step_waits_for_two_camera_readings_at_the_target_score()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = true;
        try
        {
            await model.StartMatchAsync();
            var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
            model.Table.WholeBoardAcknowledged = true;

            var confirmation = model.ConfirmPlacementAsync();
            await WaitUntilAsync(() => model.Game.GuidanceInstruction == "Thank you");
            Assert.False(model.ShowScoreMarkerDetectionPrompt);
            Assert.Equal("Scoring", model.Game.GuidanceTurn);

            await confirmation;
            Assert.True(model.ShowScoreMarkerDetectionPrompt);
            Assert.Equal(Screen.Table, model.Screen);
            var target = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score % 100 + 1;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            var firstAt = DateTimeOffset.UtcNow;
            model.Camera.IsGameTablePreviewUpright = true;
            PublishScore(model.Camera, 1, firstAt, color, target % 100 + 1);
            Assert.True(model.ShowScoreMarkerDetectionPrompt);
            Assert.Equal("Scoring", model.Game.GuidanceTurn);
            PublishScore(model.Camera, 2, firstAt.AddSeconds(1.1), color, target);
            Assert.Equal("Scoring", model.Game.GuidanceTurn);
            PublishScore(model.Camera, 3, firstAt.AddSeconds(2.2), color, target);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Scoring");

            Assert.False(model.ShowScoreMarkerDetectionPrompt);
            Assert.NotEqual("Scoring", model.Game.GuidanceTurn);
            Assert.NotEqual(placement.OperationId, model.Table.Placement?.OperationId);
            Assert.Equal(Screen.Table, model.Screen);
        }
        finally { await model.DisposeToolsAsync(); }
    }

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
            Assert.StartsWith("Up next: Turn ", model.Table.TurnText);
            Assert.Equal("Waiting for scoring marker", model.Table.PhaseText);
            Assert.Contains($"{placement.SeatName}'s {placement.Color} scoring marker",
                model.Table.Instruction);
            Assert.Contains("has not acted yet", model.Table.Instruction);
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

    [Fact]
    public async Task DetectingAComputerScoreMarkerKeepsASoloHumanOnTheGameTable()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        model.Setup.Seats[0].IsComputer = true;
        model.Setup.Seats[1].IsComputer = true;
        model.Setup.Seats[2].IsComputer = false;
        try
        {
            await model.StartMatchAsync();
            Assert.True(model.ShowSoloOpeningTicketsOnBoard);
            await model.CommitTicketsAsync();
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);

            var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
            var accept = typeof(MainViewModel).GetMethod("AcceptPhysicalPlacementAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var claim = Assert.IsAssignableFrom<Task>(accept.Invoke(model,
                [placement, EvidenceKind.CameraAutomatic, "synthetic-test-model", "Two stable route observations."]));
            await WaitUntilAsync(() => model.Game.GuidanceInstruction == "Thank you");
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);

            await claim;
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
            var target = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score % 100 + 1;
            model.Camera.IsGameTablePreviewUpright = true;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            var firstAt = DateTimeOffset.UtcNow;
            PublishScore(model.Camera, 1, firstAt, color, target);
            PublishScore(model.Camera, 2, firstAt.AddSeconds(1.1), color, target);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Scoring");

            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            Assert.False(model.IsPrivateVisible);
            Assert.False(model.ShowSoloCardPanel);
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

    private static GameCoordinator GetCoordinator(MainViewModel model) =>
        Assert.IsType<GameCoordinator>(typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(model));

    private static void PublishBlueTrains(CameraViewModel camera, string routeId,
        long sequence, DateTimeOffset capturedAt, long cropRevision = 1) =>
        PublishTrains(camera, routeId, sequence, capturedAt, MarkerColor.Blue,
            cropRevision: cropRevision);

    private static void PublishTrains(CameraViewModel camera, string routeId,
        long sequence, DateTimeOffset capturedAt, MarkerColor color, int count = int.MaxValue,
        long cropRevision = 1)
    {
        const int width = 960;
        const int height = 600;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 180;
        ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots);
        var candidates = new List<PieceCandidate>();
        var (red, green, blue) = color switch
        {
            MarkerColor.Red => (190, 35, 30),
            MarkerColor.Green => (20, 125, 35),
            MarkerColor.Yellow => (225, 180, 20),
            MarkerColor.Blue => (20, 75, 195),
            _ => (20, 20, 20)
        };
        foreach (var spot in slots.Take(count))
        {
            var x = (int)Math.Round(spot.X * width);
            var y = (int)Math.Round(spot.Y * height);
            const int halfWidth = 11;
            const int halfHeight = 7;
            for (var py = y - halfHeight; py <= y + halfHeight; py++)
            for (var px = x - halfWidth; px <= x + halfWidth; px++)
            {
                var offset = (py * width + px) * 4;
                pixels[offset] = (byte)blue;
                pixels[offset + 1] = (byte)green;
                pixels[offset + 2] = (byte)red;
            }
            candidates.Add(new(PieceCandidateKind.Train,
                [new((double)(x - halfWidth) / width, (double)(y - halfHeight) / height),
                 new((double)(x + halfWidth) / width, (double)(y - halfHeight) / height),
                 new((double)(x + halfWidth) / width, (double)(y + halfHeight) / height),
                 new((double)(x - halfWidth) / width, (double)(y + halfHeight) / height)], .9));
        }

        var frame = CameraFrame.CopyFromBgra32(width, height, pixels, sequence,
            epoch: 1, capturedAt: capturedAt);
        var analysis = new GameTableAnalysis(frame, candidates, [], cropRevision, 1,
            "synthetic-test-model");
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
