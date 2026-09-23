using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;

internal static partial class Program
{
    private static async Task VerifyFivePlayerTrayLayout()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        try
        {
            model.Game.ShowPlaying();
            model.Screen = Screen.Table;
            model.Table.TurnText = "Turn 3";
            model.Table.ActiveSeatName = "Player 1";
            var pixels = new byte[960 * 600 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 85;
                pixels[index + 1] = 120;
                pixels[index + 2] = 155;
                pixels[index + 3] = 255;
            }
            model.Camera.GameTablePreview = BitmapSource.Create(960, 600, 96, 96,
                PixelFormats.Bgra32, null, pixels, 960 * 4);
            model.Camera.GameTablePreviewStatus = "";
            var colors = new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green,
                PlayerColor.Black, PlayerColor.Yellow };
            for (var index = 0; index < 5; index++)
                model.Table.Seats.Add(new SeatRow(new SeatId(index + 1), $"Player {index + 1}",
                    colors[index], "★", "human", 0, 45, 5, 3, 0,
                    index == 0, "Waiting for first action."));

            var closedTops = model.Game.TableSeats.ToDictionary(tile => tile.Seat.SeatId.Value,
                tile => tile.Top);
            if (closedTops.Count != 5 || closedTops[1] != 205 || closedTops[2] != 205 ||
                closedTops[3] != 395 || closedTops[4] != 585 || closedTops[5] != 585)
                throw new InvalidOperationException("The five-player tray fixture must begin with the ordinary balanced seat positions.");

            static void SetPanelKind(MainViewModel viewModel, SoloCardPanelSelection kind) =>
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.SoloCardPanelKind))!
                    .GetSetMethod(nonPublic: true)!.Invoke(viewModel, [kind]);

            void Activate(int player)
            {
                for (var index = 0; index < model.Table.Seats.Count; index++)
                {
                    var row = model.Table.Seats[index];
                    if (row.IsActive != (index + 1 == player))
                        model.Table.Seats[index] = row with { IsActive = index + 1 == player };
                }
                model.Table.ActiveSeatName = $"Player {player}";
            }

            static Rect SceneBounds(FrameworkElement element, Canvas scene) =>
                element.TransformToAncestor(scene).TransformBounds(new Rect(element.RenderSize));

            void VerifyLayout(UserControl view, int owner, SoloCardPanelSelection kind)
            {
                var scene = (Canvas)view.FindName("TableScene");
                var tray = (Border)view.FindName("SoloCardPanel");
                var bounds = Descendants<Border>(view)
                    .Where(border => border.Width == 250 && border.Height == 160 &&
                        border.DataContext is GameTableSeat)
                    .ToDictionary(border => ((GameTableSeat)border.DataContext).Seat.SeatId.Value,
                        border => SceneBounds(border, scene));
                if (bounds.Count != 5 || !IsElementShown(tray) ||
                    (kind == SoloCardPanelSelection.TrainCards &&
                     !IsElementShown((ItemsControl)view.FindName("SoloTrainCards"))) ||
                    (kind == SoloCardPanelSelection.Destinations &&
                     !IsElementShown((ItemsControl)view.FindName("SoloDestinationCards"))))
                    throw new InvalidOperationException($"The five-player {kind} tray must render for Player {owner}.");

                var panel = SceneBounds(tray, scene);
                var station = bounds[owner];
                if (panel.Left < 8 || panel.Top < 8 || panel.Right > scene.Width - 8 ||
                    panel.Bottom > scene.Height - 8 ||
                    bounds.Values.Any(tile => panel.IntersectsWith(tile)))
                    throw new InvalidOperationException($"Player {owner}'s {kind} tray must fit inside the scene without covering a station: {panel}.");
                if (bounds[1].Bottom >= bounds[3].Top ||
                    bounds[3].Bottom >= bounds[5].Top ||
                    bounds[2].Bottom >= bounds[4].Top ||
                    bounds.Values.Any(tile => tile.Top < 8 || tile.Bottom > scene.Height - 8))
                    throw new InvalidOperationException("All five player stations must retain their order and remain inside the scene when a tray opens.");
                if (owner == 4 && kind == SoloCardPanelSelection.Destinations)
                {
                    if (Math.Abs(station.Top - panel.Bottom - 6) > 3)
                        throw new InvalidOperationException($"Player 4's destination tray must fit directly above the fixed bottom-right station: tray {panel}; station {station}.");
                }
                else if (Math.Abs(panel.Top - station.Bottom - 6) > 2)
                    throw new InvalidOperationException($"Player {owner}'s {kind} tray must open directly below that player's station: tray {panel}; station {station}.");
                if (owner == 1 &&
                    (bounds[3].Top <= closedTops[3] + 1 || bounds[5].Top <= closedTops[5] + 1))
                    throw new InvalidOperationException("Player 3 and Player 5 must move down when Player 1 opens a tray.");
                if (owner == 3 && bounds[5].Top <= closedTops[5] + 1)
                    throw new InvalidOperationException("Player 5 must move down when Player 3 opens a tray.");
                if ((owner is 2 or 4) &&
                    (Math.Abs(bounds[2].Top - closedTops[2]) > .5 ||
                     Math.Abs(bounds[4].Top - closedTops[4]) > .5))
                    throw new InvalidOperationException("Players 2 and 4 must keep their ordinary right-column positions while either right-hand tray is open.");
            }

            void VerifyClosed(UserControl view)
            {
                var scene = (Canvas)view.FindName("TableScene");
                var tray = (Border)view.FindName("SoloCardPanel");
                var seats = Descendants<Border>(view)
                    .Where(border => border.Width == 250 && border.Height == 160 &&
                        border.DataContext is GameTableSeat).ToArray();
                if (IsElementShown(tray) || seats.Length != 5 || seats.Any(border =>
                    Math.Abs(SceneBounds(border, scene).Top -
                        closedTops[((GameTableSeat)border.DataContext).Seat.SeatId.Value]) > .5))
                    throw new InvalidOperationException("Closing a private tray must restore all five player stations to their ordinary positions.");
            }

            // A private setter is used here only to present a synthetic hand. The normal command
            // path requires a live match coordinator and is exercised by the Practical turn smoke.
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.SoloTrainCards))!
                .GetSetMethod(nonPublic: true)!.Invoke(model,
                    [new SoloTrainCardRow[]
                    {
                        new(TrainCardKind.Blue, "Blue", 3),
                        new(TrainCardKind.Black, "Black", 1),
                        new(TrainCardKind.Pink, "Pink", 1),
                    }]);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.SoloDestinationCards))!
                .GetSetMethod(nonPublic: true)!.Invoke(model,
                    [new SoloDestinationCardRow[]
                    {
                        new("Atlanta", "Raleigh", 7, false),
                        new("Seattle", "New York", 22, false),
                    }]);

            foreach (var kind in new[] { SoloCardPanelSelection.TrainCards,
                         SoloCardPanelSelection.Destinations })
            {
                foreach (var owner in new[] { 1, 3, 2, 4, 5 })
                {
                    Activate(owner);
                    SetPanelKind(model, kind);
                    var title = $"game-table-five-player-{kind.ToString().ToLowerInvariant()}-player-{owner}";
                    await RenderSizes(title, () => new GameTableView { DataContext = model },
                        view => VerifyLayout(view, owner, kind),
                        [(1280, 800), (1000, 620)], settleDelayMs: 350);
                    SetPanelKind(model, SoloCardPanelSelection.None);
                    await RenderSizes($"{title}-closed", () => new GameTableView { DataContext = model },
                        VerifyClosed, [(1280, 800)], settleDelayMs: 350);
                }
            }
            Console.WriteLine("Five-player trays: train cards and destinations fit, left followers shift, right seats stay fixed, and closing restores seats.");
        }
        finally { await model.DisposeToolsAsync(); }
    }
}
