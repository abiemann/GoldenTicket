using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

internal static partial class Program
{
    private static async Task VerifyFinalStandingsSharing()
    {
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, new InMemorySessionStore());
        var reports = new List<object>();
        try
        {
            var board = SyntheticCropFixture("SYNTHETIC FINAL BOARD\nNO CAMERA OR PRIVATE CARDS");
            var prepare = typeof(FinalStandingsView).GetMethod("PrepareForImage",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The standings scene needs its detached export layout.");
            var render = typeof(MainViewModel).Assembly
                .GetType("GoldenTicket.Desktop.Services.FinalStandingsImageRenderer")?
                .GetMethod("RenderAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The standings PNG renderer is missing.");

            foreach (var players in new[] { 2, 3, 4, 5 })
            {
                BindingLog.Context = $"final-standings-export-{players}-players";
                var scores = StandingsSmokeScores(model, players);
                var summary = players == 5 ? "Player 1 and Player 2 share the victory." : "Player 1 wins. The journey is complete.";
                var scene = new FinalStandingsView
                {
                    BoardImage = board,
                    DataContext = new StandingsSmokePresentation(summary, scores)
                };
                var width = Math.Max(1440, players * 356 + 72);
                await Arrange(scene, width, 1000);
                var height = (int)Math.Ceiling((double)prepare.Invoke(scene, [ (double)width ])!);
                await Arrange(scene, width, height);
                RequireStandings(Descendants<TextBlock>(scene).Any(text => text.Text == "THE JOURNEY HAS COME TO AN END"),
                    "The shared image must use the completed-journey heading.");
                var cards = Descendants<Border>(scene)
                    .Where(border => border.Name == "PlayerResultPanel").ToArray();
                RequireStandings(cards.Length == players, "The export must contain every player exactly once.");
                RequireStandings(cards.Select(card => card.DataContext).Distinct().Count() == players,
                    "Player result panels must not repeat a seat.");
                var boardImage = (Image)scene.FindName("FinalBoardImage");
                RequireStandings(boardImage.Source is not null && boardImage.ActualWidth > 0 && boardImage.ActualHeight > 0,
                    "The detached export must retain the board image.");
                foreach (var card in cards)
                {
                    var bounds = card.TransformToAncestor(scene).TransformBounds(new Rect(card.RenderSize));
                    RequireStandings(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width + .5 && bounds.Bottom <= height + .5,
                        "Every player panel must fit inside the exported image.");
                    var row = (FinalScoreRow)card.DataContext;
                    var trail = Descendants<ScrollViewer>(card).Single(view => view.Name == "WitnessTrailScroll");
                    RequireStandings(trail.ScrollableHeight <= .5 && trail.ScrollableWidth <= .5,
                        "The PNG must include the full longest-route trail without scrolling.");
                    RequireStandings(Descendants<TextBlock>(trail).Single().Text == row.WitnessTrail,
                        "The export must preserve the complete route text.");
                    RequireStandings(Descendants<Image>(card).Single().Source is not null,
                        "Every player needs their portrait in the image.");
                    var texts = Descendants<TextBlock>(card).Select(text => text.Text).ToArray();
                    foreach (var expected in new[] { row.SeatName, row.Total.ToString(), row.TicketSummary,
                                 row.TotalTurnTimeText, row.AverageTurnTimeText, row.TimingNote,
                                 "Claimed routes", "Completed destinations", "Missed destinations", "Longest-route bonus" })
                        RequireStandings(texts.Contains(expected), $"The export lost a public result value: {expected}");
                }
                RequireStandings(!Descendants<Button>(scene).Any(IsElementShown) &&
                    !Descendants<ScrollBar>(scene).Any(IsElementShown),
                    "Shared PNGs must not contain phone buttons, navigation or scrollbars.");

                var png = await (Task<byte[]>)render.Invoke(null, [ summary, scores, board ])!;
                RequireStandings(png.Length is > 1000 and <= 16 * 1024 * 1024,
                    "The PNG must contain a rendered image and respect the transfer limit.");
                using var stream = new MemoryStream(png, writable: false);
                var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoded.Frames.Single();
                RequireStandings(frame.PixelWidth == (int)Math.Ceiling(width * 1.5) &&
                    frame.PixelHeight == (int)Math.Ceiling(height * 1.5),
                    "The PNG must retain the full export layout at its intended resolution.");
                await File.WriteAllBytesAsync(Path.Combine(Output, $"final-standings-{players}-players.png"), png);
                reports.Add(new { Scenario = "public standings PNG", Players = players, Cards = cards.Length,
                    frame.PixelWidth, frame.PixelHeight, Bytes = png.Length, FullTrailsVisible = true,
                    ControlsVisible = false });
                scene.DataContext = null;
            }

            foreach (var humanCount in new[] { 1, 2 })
                reports.Add(await VerifyStandingsPhoneAvailability(manifest, humanCount));
            await File.WriteAllTextAsync(Path.Combine(Output, "final-standings-sharing.json"),
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Final standings: all 2–5 players and full trails exported to PNG; solo sharing hidden; missing phone approval opens setup without publishing an image.");
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static FinalScoreRow[] StandingsSmokeScores(MainViewModel model, int players) =>
        Enumerable.Range(0, players).Select(index =>
        {
            var choice = model.Game.SeatChoices[index];
            var tiedWinner = players == 5 && index == 1;
            var routePoints = tiedWinner ? 72 : 72 - index * 4;
            var ticketsLost = index == 4 ? 95 : tiedWinner ? 0 : index * 7;
            var longestBonus = index == 0 || tiedWinner ? 10 : 0;
            var route = string.Join(" → ", Enumerable.Repeat(
                "Los Angeles → Phoenix → Santa Fe → Oklahoma City → Kansas City → Saint Louis → Chicago → Pittsburgh", 6));
            return new FinalScoreRow($"Player {index + 1}", choice.TrainColor, "●", routePoints,
                34, ticketsLost, longestBonus, routePoints + 34 - ticketsLost + longestBonus,
                $"{5 - index} completed, {index} missed", 43 - index * 3, route,
                index == 0 || tiedWinner)
            {
                Portrait = choice.PortraitFor(index != 0), RankLabel = tiedWinner ? "#1" : $"#{index + 1}",
                TotalTurnTimeText = index == 4 ? "—" : "1:23:42",
                AverageTurnTimeText = index == 4 ? "—" : "2:19", CompletedTurns = index == 4 ? 0 : 36,
                TimingNote = index == 4 ? "Timing was not recorded for this game."
                    : index == 0 ? "Includes placement; excludes pauses. Timing available from turn 84; partial turns excluded from the average."
                    : "Includes placement; excludes pauses."
            };
        }).ToArray();

    private static async Task<object> VerifyStandingsPhoneAvailability(BoardManifest manifest, int humanCount)
    {
        BindingLog.Context = $"final-standings-phone-{humanCount}-humans";
        var store = new InMemorySessionStore();
        var model = new MainViewModel(manifest, store);
        try
        {
            // Only the public projection is finished for this UI fixture. No game actions,
            // camera, persisted user settings, listener or native window are involved.
            var game = await GameCoordinator.CreateAsync(new GameRules(manifest, CardCatalog.FromManifest(manifest)), store,
                new SessionSetup(SessionId.New(),
                    [new(new(1), "Player 1", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
                     new(new(2), "Player 2", PlayerColor.Blue, humanCount == 2 ? SeatKind.Human : SeatKind.Computer, AiDifficulty.Standard)],
                    new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), CancellationToken.None);
            typeof(MainViewModel).GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, game);
            typeof(GameCoordinator).GetField("_publicView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(game, game.Public with { Lifecycle = SessionLifecycle.Finished });
            model.Game.Stage = GameScreenStage.Playing;
            model.Screen = Screen.FinalScore;
            foreach (var row in StandingsSmokeScores(model, 2)) model.FinalScores.Add(row);
            var scene = new FinalStandingsView
            {
                BoardImage = SyntheticCropFixture("SYNTHETIC FINAL BOARD\nNO CAMERA OR PRIVATE CARDS"),
                DataContext = model
            };
            await Arrange(scene, 1280, 800);
            var view = Descendants<FinalScoreView>(scene).Single();
            var send = (Button)view.FindName("ShareToPhoneButton");
            var back = (Button)view.FindName("BackToMenuButton");
            var actions = (FrameworkElement)view.FindName("FinalActionsPanel");
            RequireStandings(send.Content as string == "Share to phone" && back.Content as string == "Back to Menu",
                "The final actions must use the game-facing share and menu labels.");
            RequireStandings(model.CanShareFinalStandings == (humanCount == 2) &&
                IsElementShown(send) == (humanCount == 2) &&
                model.SendFinalStandingsToPhoneCommand.CanExecute(null) == (humanCount == 2),
                "Share to phone must be available only for finished multi-human games.");
            RequireStandings(IsElementShown(back) && model.CanLeaveFinalStandings &&
                model.BackToMenuCommand.CanExecute(null) && actions.HorizontalAlignment == HorizontalAlignment.Right,
                "Every finished game needs Back to Menu in the right-aligned action group.");
            var backBounds = back.TransformToAncestor(view).TransformBounds(new Rect(back.RenderSize));
            RequireStandings(backBounds.Right >= view.ActualWidth - 28 && backBounds.Bottom >= view.ActualHeight - 24,
                "Final actions must sit at the bottom-right of the standings.");
            if (humanCount == 2)
            {
                var shareBounds = send.TransformToAncestor(view).TransformBounds(new Rect(send.RenderSize));
                RequireStandings(shareBounds.Right <= backBounds.Left && Math.Abs(shareBounds.Top - backBounds.Top) <= 1,
                    "Share to phone and Back to Menu must form a non-overlapping horizontal pair.");
            }
            Save(scene, $"final-standings-footer-{humanCount}-humans-1280x800.png", 1280, 800);
            if (humanCount == 2)
            {
                // Simulate the presentation flag only. No actual host runs; this makes the
                // setup's RefreshInterfaces return before reading Windows network profiles.
                model.Connection.IsRunning = true;
                model.Connection.HasApprovedController = false;
                await model.SendFinalStandingsToPhoneCommand.ExecuteAsync(null);
                RequireStandings(model.ShowMultiHumanPhoneSetup &&
                    model.FinalStandingsShareStatus.Contains("Connect and approve", StringComparison.Ordinal) &&
                    typeof(MainViewModel).GetField("_finalStandingsImage", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(model) is null,
                    "A missing approved phone must open setup without claiming an image was sent.");
                model.Connection.IsRunning = false;
            }

            var publicField = typeof(GameCoordinator).GetField("_publicView", BindingFlags.Instance | BindingFlags.NonPublic)!;
            publicField.SetValue(game, game.Public with { Lifecycle = SessionLifecycle.Active });
            RequireStandings(!model.CanShareFinalStandings && !model.SendFinalStandingsToPhoneCommand.CanExecute(null) &&
                !model.CanLeaveFinalStandings && !model.BackToMenuCommand.CanExecute(null),
                "Active matches must not share provisional standings or leave through the completed-game shortcut.");
            await ExecuteStandingsBackToMenu(model);
            RequireStandings(model.Screen == Screen.FinalScore && model.Game.Stage == GameScreenStage.Playing,
                "Invoking the guarded menu action for an active game must leave it open.");
            publicField.SetValue(game, game.Public with { Lifecycle = SessionLifecycle.Finished });
            var before = await store.RestoreAsync(game.SessionId, manifest, CardCatalog.FromManifest(manifest), CancellationToken.None);
            await ExecuteStandingsBackToMenu(model);
            var after = await store.RestoreAsync(game.SessionId, manifest, CardCatalog.FromManifest(manifest), CancellationToken.None);
            RequireStandings(model.Screen == Screen.Setup && model.Game.Stage == GameScreenStage.Welcome &&
                !model.ShowMultiHumanPhoneSetup && !model.CanShareFinalStandings,
                "Back to Menu must return the finished game to Welcome without leaving phone setup over it.");
            RequireStandings(after.State.StateVersion == before.State.StateVersion &&
                StateHash.Compute(after.State) == StateHash.Compute(before.State) && after.Journal.Count == before.Journal.Count &&
                (await store.ListSessionsAsync(CancellationToken.None)).Any(session => session.SessionId == game.SessionId),
                "Returning from final standings must retain the save and its journal without deletion or rewind.");
            scene.DataContext = null;
            return new { Scenario = "phone sharing eligibility", HumanPlayers = humanCount,
                ShareVisible = humanCount == 2, MissingApprovalOpensSetup = humanCount == 2,
                BackToMenuAtBottomRight = true, SaveRetained = true, ActiveGameBackBlocked = true, NoImagePublished = true };
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static void RequireStandings(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExecuteStandingsBackToMenu(MainViewModel model)
    {
        if (model.BackToMenuCommand is IAsyncRelayCommand asynchronous)
            await asynchronous.ExecuteAsync(null);
        else model.BackToMenuCommand.Execute(null);
    }

    private sealed record StandingsSmokePresentation(string FinalSummary, IReadOnlyList<FinalScoreRow> FinalScores)
    {
        public bool CanShareFinalStandings => false;
        public System.Windows.Input.ICommand? SendFinalStandingsToPhoneCommand => null;
        public System.Windows.Input.ICommand? BackToMenuCommand => null;
        public bool CanLeaveFinalStandings => false;
        public string FinalStandingsShareStatus => "";
    }
}
