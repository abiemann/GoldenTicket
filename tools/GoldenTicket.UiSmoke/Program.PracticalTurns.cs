using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain;
using GoldenTicket.Vision;

internal static partial class Program
{
    private static async Task VerifyPracticalTurnPresentation(
        MainViewModel model, BitmapSource boardImage, List<string> checks)
    {
        model.SetGameLayerVisible(true);
        model.Camera.GameTablePreview = boardImage;
        model.Camera.GameTablePreviewStatus = "";
        await RenderSizes("practical-handoff-synthetic", () => new GameScreenView { DataContext = model }, view =>
        {
            var take = PracticalTurnButton(view);
            var board = Descendants<Border>(view).Single(border => border.Name == "GameBoardFrame");
            if (!IsElementShown(take) || !take.IsEnabled || take.Content as string != "Take my turn" ||
                !ReferenceEquals(take.Command, model.TakePracticalTurnCommand) || !IsElementShown(board) ||
                model.HasAcceptedPracticalTurn || model.CanUseGameTableControls || model.PrivateSeat is not null)
                throw new InvalidOperationException("Practical must keep the board visible and offer Take my turn before showing a human's cards.");
        });

        await TakePracticalTurnThroughButton(model);
        RequireHumanPrivateView(model, "First human test player", mustChooseTickets: true);
        await RenderPracticalOpening(model, "practical-first-opening-on-board-synthetic");
        await KeepPracticalOpeningThroughButton(model);
        if (model.HasAcceptedPracticalTurn || model.IsPrivateVisible || model.PrivateSeat is not null ||
            !model.ShowPracticalHandoff || model.ShowDestinationMarkersOnBoard ||
            !model.RevealPrompt.StartsWith("Second human test player", StringComparison.Ordinal))
            throw new InvalidOperationException("The next human's opening choice must cover the first player's cards and map connections. " +
                $"Accepted={model.HasAcceptedPracticalTurn}, privateVisible={model.IsPrivateVisible}, private={model.PrivateSeat?.SeatName}, " +
                $"handoff={model.ShowPracticalHandoff}, markers={model.ShowDestinationMarkersOnBoard}, prompt={model.RevealPrompt}.");

        await TakePracticalTurnThroughButton(model);
        RequireHumanPrivateView(model, "Second human test player", mustChooseTickets: true);
        await RenderPracticalOpening(model, "practical-second-opening-on-board-synthetic");
        model.Connection.UseQuickPlay = true;
        if (model.PrivateSeat is not null || model.HasAcceptedPracticalTurn || model.ShowPracticalHandoff ||
            model.ShowDestinationMarkersOnBoard)
            throw new InvalidOperationException("Switching to phone play must cover every laptop-only private card and destination.");
        model.Connection.UsePractical = true;
        if (!model.ShowPracticalHandoff)
            throw new InvalidOperationException("Returning to Practical must require the current human to take their turn again.");
        await TakePracticalTurnThroughButton(model);
        await KeepPracticalOpeningThroughButton(model);
        if (model.HasAcceptedPracticalTurn || !model.ShowPracticalHandoff || model.PrivateSeat is not null)
            throw new InvalidOperationException("Completing setup must require a fresh handoff before the first active turn.");
        checks.Add("Take my turn presents each human's opening destinations over the themed board; handoff and phone mode cover the prior private content.");

        await TakePracticalTurnThroughButton(model);
        if (!model.HasAcceptedPracticalTurn || !model.CanUseGameTableControls || !model.IsGameTableHumanTurn ||
            model.PrivateSeat is not null || model.Screen != Screen.Table || model.ShowSoloCardPanel ||
            model.ShowDestinationMarkersOnBoard)
            throw new InvalidOperationException("Taking an active Practical turn must enable the game table while keeping cards and destination connections closed.");
        var activeName = model.Table.ActiveSeatName;
        var table = new GameTableView { DataContext = model };
        await Arrange(table, 1280, 800);
        var trainStack = Descendants<Button>(table).Single(button =>
            AutomationProperties.GetName(button) == "Show your train cards" &&
            button.DataContext is GameTableSeat tile && tile.Seat.DisplayName == activeName);
        var otherTrainStacks = Descendants<Button>(table).Where(button =>
            AutomationProperties.GetName(button) == "Show your train cards" &&
            button.DataContext is GameTableSeat tile && tile.Seat.DisplayName != activeName).ToArray();
        if (!trainStack.IsHitTestVisible || !trainStack.IsEnabled ||
            otherTrainStacks.Length == 0 || otherTrainStacks.Any(stack => stack.IsHitTestVisible || stack.Focusable))
            throw new InvalidOperationException("Only the human taking the turn may inspect a hand on the shared game table.");
        await RenderSizes("practical-take-turn-cards-closed-synthetic", () => new GameTableView { DataContext = model }, view =>
        {
            var activeStacks = Descendants<Button>(view).Where(button =>
                button.DataContext is GameTableSeat tile && tile.Seat.DisplayName == activeName &&
                AutomationProperties.GetName(button) is "Show your train cards" or "Show your destinations").ToArray();
            if (!IsElementShown((Border)view.FindName("GameBoardFrame")) ||
                IsElementShown((Border)view.FindName("SoloCardPanel")) ||
                IsElementShown((ItemsControl)view.FindName("DestinationCityMarkers")) ||
                IsElementShown((ItemsControl)view.FindName("DestinationLines")) ||
                activeStacks.Length != 2 || activeStacks.Any(stack => !stack.IsEnabled || !stack.IsHitTestVisible))
                throw new InvalidOperationException("Take my turn must leave the board unobstructed and the active player's card stacks available to click.");
        });
        var initialCards = model.Table.Seats.Single(seat => seat.DisplayName == activeName).CardCount;
        InvokePracticalButton(trainStack);
        for (var attempt = 0; attempt < 40 && !model.ShowSoloTrainCards; attempt++) await Task.Delay(50);
        if (!model.ShowSoloTrainCards || model.SoloTrainCards.Sum(card => card.Count) != initialCards ||
            model.PrivateSeat is not null)
            throw new InvalidOperationException("The Practical player's train cards must expand in the same themed tray as single-player.");
        var destinationStack = Descendants<Button>(table).Single(button =>
            AutomationProperties.GetName(button) == "Show your destinations" &&
            button.DataContext is GameTableSeat tile && tile.Seat.DisplayName == activeName);
        InvokePracticalButton(destinationStack);
        for (var attempt = 0; attempt < 40 && !model.ShowSoloDestinations; attempt++) await Task.Delay(50);
        if (!model.ShowSoloDestinations || model.SoloDestinationCards.Count != 3 ||
            !model.ShowDestinationMarkersOnBoard || model.PrivateSeat is not null)
            throw new InvalidOperationException("The shared player's destination stack must expand tickets and map connections in place.");
        InvokePracticalButton(trainStack);
        for (var attempt = 0; attempt < 40 && !model.ShowSoloTrainCards; attempt++) await Task.Delay(50);
        await RenderSizes("practical-train-cards-on-board-synthetic", () => new GameTableView { DataContext = model }, view =>
        {
            if (!IsElementShown((Border)view.FindName("GameBoardFrame")) ||
                !IsElementShown((Border)view.FindName("SoloCardPanel")) ||
                !IsElementShown((Border)view.FindName("DrawPilesPanel")) ||
                !IsElementShown((Border)view.FindName("FaceUpMarketPanel")))
                throw new InvalidOperationException("Taking a Practical turn must keep the board, card tray and draw controls visible together.");
        });

        var faceUp = Descendants<Button>(table).First(button =>
            AutomationProperties.GetName(button).StartsWith("Draw face-up ", StringComparison.Ordinal) &&
            button.CommandParameter is MarketSlotRow { Kind: not null and not TrainCardKind.Locomotive } && button.IsEnabled);
        model.Camera.IsGameTablePreviewUpright = true;
        await VerifyOpenPracticalTrayDraw(model, table, faceUp, activeName, initialCards + 1);
        table.UpdateLayout();
        if (model.Table.Seats.Single(seat => seat.DisplayName == activeName).CardCount != initialCards + 1 ||
            model.Table.ActiveSeatName != activeName || !model.HasAcceptedPracticalTurn || model.ShowPracticalHandoff ||
            !model.ShowSoloTrainCards || model.PrivateSeat is not null || model.Screen != Screen.Table ||
            model.IsCheckingBoardBeforeNextTurn)
            throw new InvalidOperationException("The first face-up card must be awarded while the same player's themed game remains open for the second draw.");
        var blind = Descendants<Button>(table).Single(button =>
            AutomationProperties.GetName(button) == "Draw a train card from the pile");
        await InvokePracticalDrawImmediately(model, table, blind, activeName, initialCards + 2,
            waitForLandingBeforeClosingTray: true);
        table.UpdateLayout();
        if (model.Table.Seats.Single(seat => seat.DisplayName == activeName).CardCount != initialCards + 2 ||
            model.Table.ActiveSeatName == activeName || model.HasAcceptedPracticalTurn || model.ShowPracticalHandoff ||
            model.ShowSoloCardPanel || model.ShowDestinationMarkersOnBoard || model.PrivateSeat is not null ||
            !model.IsCheckingBoardBeforeNextTurn || model.TakePracticalTurnCommand.CanExecute(null))
            throw new InvalidOperationException("The second card must already belong to the outgoing player while the next player's turn waits for the board check.");
        await FinishPracticalTurnBoardCheck(model);
        if (!model.ShowPracticalHandoff || !model.TakePracticalTurnCommand.CanExecute(null))
            throw new InvalidOperationException("A fresh clear board must release the next player's handoff.");
        checks.Add("Take my turn leaves cards closed until clicked; cards award immediately and fly with rotation to their original owner, then a fresh-board check gates the next human's handoff.");
        checks.Add("An already-open train-card tray updates its bound hand on the first draw without hiding or replaying its entrance animation.");
        checks.Add("The final train card is awarded immediately while its owner's open tray remains visible during the flight; landing then closes the tray and begins the board check.");

        await TakePracticalTurnThroughButton(model);
        table.UpdateLayout();
        var destinationOwner = model.Table.ActiveSeatName;
        var initialTickets = model.Table.Seats.Single(seat => seat.DisplayName == destinationOwner).TicketCount;
        var destinationPile = Descendants<Button>(table).Single(button =>
            AutomationProperties.GetName(button) == "Draw destination tickets");
        await InvokePracticalDrawImmediately(model, table, destinationPile, destinationOwner,
            initialTickets + 3, destinationCards: 3);
        if (!model.ShowSoloTicketOffer || model.SoloTicketOffer.Count != 3 ||
            model.PrivateSeat is not null || model.IsCheckingBoardBeforeNextTurn ||
            !model.HasAcceptedPracticalTurn || model.Table.ActiveSeatName != destinationOwner)
            throw new InvalidOperationException("Destination cards must reach their owner's stack and show the on-board choice before any turn-boundary check.");
        await RenderSizes("practical-destination-choice-on-board-synthetic", () => new GameTableView { DataContext = model }, view =>
        {
            var offer = (Border)view.FindName("SoloTicketOfferPanel");
            if (!IsElementShown((Border)view.FindName("GameBoardFrame")) || !IsElementShown(offer) ||
                Descendants<CheckBox>(offer).Count() != 3 ||
                !IsElementShown((ItemsControl)view.FindName("DestinationLines")))
                throw new InvalidOperationException("The drawn destinations must remain on the themed board with their city connections and Keep choice.");
        });
        table.UpdateLayout();
        var keep = Descendants<Button>(table).Single(button => button.Content as string == "KEEP SELECTED");
        InvokePracticalButton(keep);
        for (var attempt = 0; attempt < 100 && !model.IsCheckingBoardBeforeNextTurn; attempt++) await Task.Delay(10);
        if (keep.Command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand { ExecutionTask: { } keepPending }) await keepPending;
        if (!model.IsCheckingBoardBeforeNextTurn || model.ShowSoloTicketOffer || model.ShowPracticalHandoff ||
            model.Table.Seats.Single(seat => seat.DisplayName == destinationOwner).TicketCount != initialTickets + 3)
            throw new InvalidOperationException("Keeping destinations must commit them before the following turn waits for board verification.");
        await FinishPracticalTurnBoardCheck(model);
        checks.Add("Drawing destinations immediately adds the pending stack and flies face-down cards to its owner; the board remains visible through Keep, and only then checks before the next turn.");
    }

    private static Button PracticalTurnButton(DependencyObject view) => Descendants<Button>(view)
        .Single(button => AutomationProperties.GetName(button) == "Take my turn on this laptop");

    private static void InvokePracticalButton(Button button)
    {
        if (!button.IsEnabled || button.Command is null || !button.Command.CanExecute(button.CommandParameter))
            throw new InvalidOperationException("The expected game button must be enabled and bound to an executable command.");
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    }

    private static async Task TakePracticalTurnThroughButton(MainViewModel model)
    {
        var view = new GameScreenView { DataContext = model };
        try
        {
            await Arrange(view, 1280, 800);
            InvokePracticalButton(PracticalTurnButton(view));
            for (var attempt = 0; attempt < 40 && !model.HasAcceptedPracticalTurn; attempt++) await Task.Delay(50);
            // Acceptance is recorded before the awaited setup/offer refresh. Wait for the actual
            // command to finish, whether it presents required ticket choices or leaves trays closed.
            if (model.TakePracticalTurnCommand.ExecutionTask is { } pending) await pending;
            if (!model.HasAcceptedPracticalTurn || model.ShowPracticalHandoff || model.Screen != Screen.Table)
                throw new InvalidOperationException("The actual Take my turn button must accept the handoff while keeping the game table active.");
        }
        finally
        {
            // Discarded detached views must not keep presenting later card flights.
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            view.DataContext = null;
        }
    }

    private static async Task InvokePracticalDrawImmediately(
        MainViewModel model, GameTableView table, Button button, string playerName, int expectedCards,
        int destinationCards = 0, bool waitForLandingBeforeClosingTray = false)
    {
        var analysisBeforeClick = model.Camera.GameTableAnalysis;
        var owner = model.Game.TableSeats.Single(tile => tile.Seat.DisplayName == playerName);
        var source = button.CommandParameter as MarketSlotRow;
        var layer = (Canvas)table.FindName("DrawCardFlightLayer");
        var flights = new List<CardFlightEventArgs>();
        Border[] flyingCards = [];
        Storyboard? storyboard = null;
        Task<(double X, double Y, double Angle)>? midpoint = null;
        void CaptureFlight(object? sender, CardFlightEventArgs flight)
        {
            flights.Add(flight);
            flyingCards = layer.Children.OfType<Border>().Where(card => ReferenceEquals(card.Tag, flight)).ToArray();
            if (flyingCards.Length == 0) return;
            var flyingCard = flyingCards[0];
            var animations = (Dictionary<Border, Storyboard>)typeof(GameTableView)
                .GetField("_cardFlights", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
            animations.TryGetValue(flyingCard, out storyboard);
            midpoint = ObservePracticalFlightMidpoint(table, flyingCard, storyboard, destinationCards > 0
                ? "practical-destination-card-flight-midpoint.png" : source is null
                    ? "practical-blind-card-flight-midpoint.png" : "practical-face-up-card-flight-midpoint.png",
                waitForLandingBeforeClosingTray ? VerifyFinalDrawMidpoint : null);
        }
        model.CardDrawn += CaptureFlight;
        try
        {
            InvokePracticalButton(button);
            for (var attempt = 0; attempt < 100 &&
                CurrentCardCount() < expectedCards; attempt++)
                await Task.Delay(10);
            if (CurrentCardCount() != expectedCards ||
                !ReferenceEquals(analysisBeforeClick, model.Camera.GameTableAnalysis))
                throw new InvalidOperationException("Clicking a card pile or face-up card must award the draw to the clicking player before another camera frame arrives.");
            if (flights.Count != 1 || flights[0].SeatId != owner.Seat.SeatId || flights[0].Count != Math.Max(1, destinationCards) ||
                flights[0].Source != (destinationCards > 0 ? CardFlightSource.DestinationPile :
                    source is null ? CardFlightSource.TrainPile : CardFlightSource.FaceUpTrain) ||
                flights[0].MarketSlot != source?.Slot || flights[0].VisibleKind != source?.Kind)
                throw new InvalidOperationException("Every awarded card must animate to its original owner, with blind cards remaining face down.");
            if (SystemParameters.ClientAreaAnimation)
            {
                if (flyingCards.Length != Math.Max(1, destinationCards) || storyboard is null || midpoint is null)
                    throw new InvalidOperationException("An awarded card must start a flight on the game board when Windows animations are enabled.");
                var flyingCard = flyingCards[0];
                if (destinationCards > 0 && flyingCards.Any(card => VisibleText(card).Trim() != "D"))
                    throw new InvalidOperationException("Flying destination cards must not reveal their private city names or values.");
                var motion = await midpoint;
                if (Math.Abs(motion.X) + Math.Abs(motion.Y) < 1 || Math.Abs(motion.Angle) < 1)
                    throw new InvalidOperationException($"The flying card must translate and rotate during its flight: x={motion.X}, y={motion.Y}, rotation={motion.Angle}.");
                var path = storyboard.Children.OfType<DoubleAnimationUsingPath>().First().PathGeometry;
                var end = ((QuadraticBezierSegment)path.Figures[0].Segments.Last()).Point2;
                var landing = new Point(Canvas.GetLeft(flyingCard) + flyingCard.Width / 2 + end.X,
                    Canvas.GetTop(flyingCard) + flyingCard.Height / 2 + end.Y);
                if ((landing - new Point(owner.Left + 125, owner.Top + 80)).Length > .1)
                    throw new InvalidOperationException("The final card's flight must finish at the player who drew it, even after the active seat changes.");
                if (PresentationSource.FromVisual(table) is null)
                {
                    var animations = (Dictionary<Border, Storyboard>)typeof(GameTableView)
                        .GetField("_cardFlights", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
                    foreach (var card in flyingCards)
                        if (animations.TryGetValue(card, out var animation))
                            animation.SeekAlignedToLastTick(table, TimeSpan.FromMilliseconds(700), TimeSeekOrigin.BeginTime);
                }
                for (var attempt = 0; attempt < 100 && flyingCards.Any(card => layer.Children.Contains(card)); attempt++) await Task.Delay(10);
                if (flyingCards.Any(card => layer.Children.Contains(card)))
                    throw new InvalidOperationException("Completed card flights must remove their visual from the board.");
            }
            else if (layer.Children.Count != 0)
                throw new InvalidOperationException("Reduced-motion mode must award cards without leaving animated visuals on the board.");
            // The final draw deliberately waits for its production storyboard to finish. Drive
            // detached clocks through landing before waiting for the command's final refresh.
            if (button.Command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand { ExecutionTask: { } pending })
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
            if (waitForLandingBeforeClosingTray && model.ShowSoloCardPanel)
                throw new InvalidOperationException("The outgoing player's train-card tray must close after the final card lands.");
        }
        finally { model.CardDrawn -= CaptureFlight; }

        int CurrentCardCount()
        {
            var seat = model.Table.Seats.Single(seat => seat.DisplayName == playerName);
            return destinationCards > 0 ? seat.TicketCount : seat.CardCount;
        }

        void VerifyFinalDrawMidpoint()
        {
            if (!model.ShowSoloTrainCards || !IsElementShown((Border)table.FindName("SoloCardPanel")) ||
                !IsElementShown((ItemsControl)table.FindName("SoloTrainCards")) ||
                model.IsCheckingBoardBeforeNextTurn || model.ShowPracticalHandoff ||
                button.Command is not CommunityToolkit.Mvvm.Input.IAsyncRelayCommand { ExecutionTask.IsCompleted: false })
                throw new InvalidOperationException("The final card must finish flying before its owner's open train-card tray closes or the board check/handoff begins.");
        }
    }

    private static async Task VerifyOpenPracticalTrayDraw(
        MainViewModel model, GameTableView table, Button button, string playerName, int expectedCards)
    {
        // Let the deliberate tray opening finish before watching for an unwanted second entrance.
        await Arrange(table, 1280, 800);
        await Task.Delay(320);
        var panel = (Border)table.FindName("SoloCardPanel");
        var cards = (ItemsControl)table.FindName("SoloTrainCards");
        var translation = (TranslateTransform)panel.RenderTransform;
        if (!IsElementShown(panel) || !IsElementShown(cards) ||
            Math.Abs(translation.Y) > .1 || panel.Opacity < .999)
            throw new InvalidOperationException("The existing train-card tray must be fully open before the first draw.");

        var visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border));
        var offset = DependencyPropertyDescriptor.FromProperty(TranslateTransform.YProperty, typeof(TranslateTransform));
        var opacity = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Border));
        var visibilityChanges = new List<Visibility>();
        var entranceRestarted = false;
        void VisibilityChanged(object? sender, EventArgs args) => visibilityChanges.Add(panel.Visibility);
        void EntranceChanged(object? sender, EventArgs args) =>
            entranceRestarted |= Math.Abs(translation.Y) > .1 || panel.Opacity < .999;
        visibility.AddValueChanged(panel, VisibilityChanged);
        offset.AddValueChanged(translation, EntranceChanged);
        opacity.AddValueChanged(panel, EntranceChanged);
        try
        {
            await InvokePracticalDrawImmediately(model, table, button, playerName, expectedCards);
            await Arrange(table, 1280, 800);
            if (visibilityChanges.Count != 0 || entranceRestarted || !IsElementShown(panel) ||
                !IsElementShown(cards) || cards.Items.Cast<SoloTrainCardRow>().Sum(card => card.Count) != expectedCards)
                throw new InvalidOperationException("Drawing with the train-card tray open must update its visible hand without closing or animating open again. " +
                    $"Visibility changes=[{string.Join(", ", visibilityChanges)}], entrance restarted={entranceRestarted}, " +
                    $"displayed cards={cards.Items.Cast<SoloTrainCardRow>().Sum(card => card.Count)}, expected={expectedCards}.");
        }
        finally
        {
            visibility.RemoveValueChanged(panel, VisibilityChanged);
            offset.RemoveValueChanged(translation, EntranceChanged);
            opacity.RemoveValueChanged(panel, EntranceChanged);
        }
    }

    private static async Task<(double X, double Y, double Angle)> ObservePracticalFlightMidpoint(
        GameTableView table, Border card, Storyboard? storyboard, string screenshot, Action? verify = null)
    {
        await Task.Delay(150);
        var transforms = (TransformGroup)card.RenderTransform;
        var movement = transforms.Children.OfType<TranslateTransform>().Single();
        // Detached trees still tick, but sampling a fixed point makes the screenshot deterministic
        // without a presentation source. Use the production timeline, transforms and paths.
        if (PresentationSource.FromVisual(table) is null)
            storyboard?.SeekAlignedToLastTick(table, TimeSpan.FromMilliseconds(150), TimeSeekOrigin.BeginTime);
        table.UpdateLayout();
        verify?.Invoke();
        var motion = (movement.X, movement.Y, transforms.Children.OfType<RotateTransform>().Single().Angle);
        Save(table, screenshot, 1280, 800);
        return motion;
    }

    private static async Task FinishPracticalTurnBoardCheck(MainViewModel model)
    {
        if (!model.IsCheckingBoardBeforeNextTurn)
            throw new InvalidOperationException("The completed card turn must wait for fresh board evidence.");
        var sequence = model.Camera.GameTableAnalysis?.Board.Sequence ?? 0;
        var at = DateTimeOffset.UtcNow;
        if (model.Camera.GameTableAnalysis?.Board.CapturedAt is { } last && last >= at)
            at = last.AddMilliseconds(10);
        PublishEmptyPracticalBoard(model.Camera, sequence + 1, at);
        PublishEmptyPracticalBoard(model.Camera, sequence + 2, at.AddSeconds(1.1));
        var finishing = typeof(MainViewModel).GetField("_finishingCardBoardCheck", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var attempt = 0; attempt < 100 &&
            (model.IsCheckingBoardBeforeNextTurn || (bool)finishing.GetValue(model)!); attempt++) await Task.Delay(10);
        if (model.IsCheckingBoardBeforeNextTurn || (bool)finishing.GetValue(model)!)
            throw new InvalidOperationException("Fresh stable empty-board frames must release the completed-turn board check.");
    }

    private static void PublishEmptyPracticalBoard(CameraViewModel camera, long sequence, DateTimeOffset at)
    {
        var frame = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
            sequence: sequence, epoch: 1, capturedAt: at);
        var analysis = new GameTableAnalysis(frame, [], [], 1, 1, "synthetic-ui-test-model");
        typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
            .GetSetMethod(nonPublic: true)!.Invoke(camera, [analysis]);
    }

    private static Task RenderPracticalOpening(MainViewModel model, string name) => RenderSizes(name, () =>
    {
        var layers = new Grid();
        layers.Children.Add(new GameScreenView { DataContext = model });
        layers.Children.Add(new PrivateSeatView { DataContext = model });
        return new UserControl { Content = layers, DataContext = model };
    }, view =>
    {
        var board = Descendants<Border>(view).Single(border => border.Name == "GameBoardFrame");
        var opening = Descendants<Grid>(view).Single(grid => grid.Name == "SoloOpeningOverlay");
        var lines = Descendants<ItemsControl>(view).Single(control => control.Name == "DestinationLines");
        var guidanceSeat = Descendants<TextBlock>(view).Single(text => text.Name == "GuidanceSeatText");
        if (!model.ShowSoloOpeningTicketsOnBoard || !IsElementShown(board) || !IsElementShown(opening) ||
            !IsElementShown(lines) || lines.Items.Count != 3 ||
            model.Game.GuidanceSeat != model.PrivateSeat?.SeatName ||
            guidanceSeat.Text != model.PrivateSeat?.SeatName ||
            Descendants<Button>(view).Count(button => IsElementShown(button) && button.DataContext is TicketChoiceRow) != 3 ||
            VisibleText(view).Contains(" - private view", StringComparison.Ordinal) ||
            VisibleButtons(view).Contains("Keep the ticked destinations"))
            throw new InvalidOperationException("Practical opening cards and their city connections must appear on the themed board, with no engineering view.");
    });

    private static async Task KeepPracticalOpeningThroughButton(MainViewModel model)
    {
        var previous = model.PrivateSeat;
        var view = new PrivateSeatView { DataContext = model };
        await Arrange(view, 1280, 800);
        var keep = (Button)view.FindName("SoloKeepAllButton");
        keep.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        // The submitting view is deliberately discarded before the awaited save and turn refresh.
        // Wait for both the click handler to finish and the next handoff to become actionable.
        for (var attempt = 0; attempt < 100 &&
            (ReferenceEquals(previous, model.PrivateSeat) || !keep.IsEnabled || !model.ShowPracticalHandoff); attempt++)
            await Task.Delay(25);
        if (ReferenceEquals(previous, model.PrivateSeat) || !keep.IsEnabled || !model.ShowPracticalHandoff)
            throw new InvalidOperationException("Keep all three must finish saving and refresh the next player's themed handoff. " +
                $"PreviousPrivate={ReferenceEquals(previous, model.PrivateSeat)}, keepEnabled={keep.IsEnabled}, " +
                $"handoff={model.ShowPracticalHandoff}, accepted={model.HasAcceptedPracticalTurn}, status={model.Status}.");
    }
}
