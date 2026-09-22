using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            model.PrivateSeat is not null || model.Screen != Screen.Table)
            throw new InvalidOperationException("Taking an active Practical turn must enable the game table without opening the technical private view.");
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
        var initialCards = model.Table.Seats.Single(seat => seat.DisplayName == activeName).CardCount;
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
        await InvokePracticalDrawWithFreshBoard(model, faceUp);
        for (var attempt = 0; attempt < 40 &&
            model.Table.Seats.Single(seat => seat.DisplayName == activeName).CardCount == initialCards; attempt++) await Task.Delay(50);
        table.UpdateLayout();
        if (model.Table.Seats.Single(seat => seat.DisplayName == activeName).CardCount != initialCards + 1 ||
            model.Table.ActiveSeatName != activeName || !model.HasAcceptedPracticalTurn || model.ShowPracticalHandoff ||
            !model.ShowSoloTrainCards || model.PrivateSeat is not null || model.Screen != Screen.Table)
            throw new InvalidOperationException("The first face-up card must be awarded while the same player's themed game remains open for the second draw.");
        var blind = Descendants<Button>(table).Single(button =>
            AutomationProperties.GetName(button) == "Draw a train card from the pile");
        await InvokePracticalDrawWithFreshBoard(model, blind);
        for (var attempt = 0; attempt < 40 && model.Table.ActiveSeatName == activeName; attempt++) await Task.Delay(50);
        table.UpdateLayout();
        if (model.Table.Seats.Single(seat => seat.DisplayName == activeName).CardCount != initialCards + 2 ||
            model.Table.ActiveSeatName == activeName || model.HasAcceptedPracticalTurn || !model.ShowPracticalHandoff ||
            model.ShowSoloCardPanel || model.ShowDestinationMarkersOnBoard || model.PrivateSeat is not null)
            throw new InvalidOperationException("The second card must be awarded before the next player's handoff covers the previous hand.");
        checks.Add("Practical active turns use the single-player card tray and draw controls; the first draw stays visible and the second awards its card before handoff.");
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
        await Arrange(view, 1280, 800);
        InvokePracticalButton(PracticalTurnButton(view));
        for (var attempt = 0; attempt < 40 && (!model.HasAcceptedPracticalTurn ||
            model.PrivateSeat is null && !model.ShowSoloTrainCards); attempt++) await Task.Delay(50);
        if (!model.HasAcceptedPracticalTurn || model.ShowPracticalHandoff || model.Screen != Screen.Table)
            throw new InvalidOperationException("The actual Take my turn button must accept the handoff while keeping the game table active.");
    }

    private static async Task InvokePracticalDrawWithFreshBoard(MainViewModel model, Button button)
    {
        model.Camera.IsGameTablePreviewUpright = true;
        var check = typeof(MainViewModel).GetField("_cardBoardCheck", BindingFlags.Instance | BindingFlags.NonPublic)!;
        InvokePracticalButton(button);
        for (var attempt = 0; attempt < 100 && check.GetValue(model) is null; attempt++) await Task.Delay(10);
        if (check.GetValue(model) is null)
            throw new InvalidOperationException("The Practical card action must wait for a fresh board verification.");
        var sequence = model.Camera.GameTableAnalysis?.Board.Sequence ?? 0;
        var at = DateTimeOffset.UtcNow;
        if (model.Camera.GameTableAnalysis?.Board.CapturedAt is { } last && last >= at)
            at = last.AddMilliseconds(10);
        PublishEmptyPracticalBoard(model.Camera, sequence + 1, at);
        PublishEmptyPracticalBoard(model.Camera, sequence + 2, at.AddSeconds(1.1));
        if (button.Command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand { ExecutionTask: { } pending })
            await pending;
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
