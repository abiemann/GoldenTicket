namespace GoldenTicket.Desktop.ViewModels;

/// <summary>Seat and private-card positions in the 1440 × 900 game-table scene.</summary>
internal static class GameTableLayout
{
    internal const double StationHeight = 160;
    internal const double TrayGap = 6;
    internal const double SceneBottom = 892;

    internal static double TrayHeight(SoloCardPanelSelection selection) =>
        selection == SoloCardPanelSelection.Destinations ? 188 : 132;

    internal static (double Left, double Top)[] SeatPositions(
        int count, int activeSeatIndex, SoloCardPanelSelection selection)
    {
        if (count > 5) throw new ArgumentOutOfRangeException(nameof(count));
        if (count < 5)
            return count switch
            {
                0 => [],
                1 => [(10, 330)],
                2 => [(10, 330), (1180, 330)],
                3 => [(10, 330), (1180, 330), (10, 530)],
                4 => [(10, 265), (1180, 265), (10, 530), (1180, 530)],
                _ => throw new ArgumentOutOfRangeException(nameof(count)),
            };

        (double Left, double Top)[] positions =
            [(10, 205), (1180, 205), (10, 395), (1180, 585), (10, 585)];
        // Right-side stations retain their ordinary positions when a tray opens.
        if (selection == SoloCardPanelSelection.None || activeSeatIndex is not (0 or 2 or 4))
            return positions;

        var trayHeight = TrayHeight(selection);
        // Keep the tray immediately below its owner. Move later left seats down;
        // when the longer destination tray reaches the scene bottom, pack the
        // earlier seats upward just enough to keep all three tiles visible.
        var tops = new[] { 205d, 395d, 585d };
        var owner = activeSeatIndex / 2;
        for (var index = owner + 1; index < tops.Length; index++)
        {
            var gap = StationHeight + TrayGap +
                (index - 1 == owner ? trayHeight + TrayGap : 0);
            tops[index] = Math.Max(tops[index], tops[index - 1] + gap);
        }

        var lastTrayHeight = owner == 2 ? trayHeight + TrayGap : 0;
        tops[2] = Math.Min(tops[2], SceneBottom - StationHeight - lastTrayHeight);
        for (var index = tops.Length - 1; index > 0; index--)
        {
            var gap = StationHeight + TrayGap +
                (index - 1 == owner ? trayHeight + TrayGap : 0);
            tops[index - 1] = Math.Min(tops[index - 1], tops[index] - gap);
        }

        // A train hand is shorter than a destination hand, so use its spare
        // space to keep the later stations comfortably apart.
        if (selection == SoloCardPanelSelection.TrainCards && owner < 2)
        {
            var extraGap = Math.Min(24,
                (SceneBottom - tops[2] - StationHeight) / (2 - owner));
            for (var index = owner + 1; index < tops.Length; index++)
                tops[index] += extraGap * (index - owner);
        }

        positions[0] = (10, tops[0]);
        positions[2] = (10, tops[1]);
        positions[4] = (10, tops[2]);
        return positions;
    }
}
