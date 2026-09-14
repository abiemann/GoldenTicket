namespace GoldenTicket.Vision;

/// <summary>Four clockwise registrations of image-ordered corners; no screen corner is assumed to be score 1.</summary>
public static class GameBoardOrientations
{
    public static IReadOnlyList<IReadOnlyList<NormalizedPoint>> Enumerate(IReadOnlyList<NormalizedPoint> imageCorners)
    {
        ArgumentNullException.ThrowIfNull(imageCorners);
        if (imageCorners.Count != 4) throw new ArgumentException("Four board corners are required.", nameof(imageCorners));
        return Enumerable.Range(0, 4).Select(scoreOneCorner => (IReadOnlyList<NormalizedPoint>)
            Enumerable.Range(0, 4).Select(index => imageCorners[(scoreOneCorner + 1 + index) % 4]).ToArray()).ToArray();
    }
}

public sealed record GameSetupBoardObservation(IReadOnlyList<PieceCandidate> Candidates,
    IReadOnlyList<ScoreMarkerReading> Markers);

public sealed record GameSetupBoardCheck(bool Ready, string Message);

/// <summary>Conservative setup gate over all four board rotations and the current ML piece detections.</summary>
public static class GameSetupBoardValidator
{
    public static GameSetupBoardCheck Check(IReadOnlyCollection<MarkerColor> selectedColors,
        IReadOnlyList<GameSetupBoardObservation> orientations)
    {
        ArgumentNullException.ThrowIfNull(selectedColors);
        ArgumentNullException.ThrowIfNull(orientations);
        var selected = selectedColors.Distinct().ToArray();
        if (selected.Length < 2 || selected.Length != selectedColors.Count)
            return new(false, "Choose at least two players with different train colors.");
        if (orientations.Count != 4)
            return new(false, "Checking all four board orientations…");

        var observedColors = orientations.SelectMany(view => view.Markers)
            .Where(marker => marker.Color is not null).Select(marker => marker.Color!.Value).Distinct().ToArray();
        var missing = selected.Where(color => !observedColors.Contains(color)).ToArray();
        if (missing.Length > 0)
        {
            var message = "Missing scoring marker" + (missing.Length == 1 ? ": " : "s: ") +
                string.Join(", ", missing.Select(color => color.ToString().ToLowerInvariant())) + ".";
            if (orientations.Any(view => view.Candidates.Any(candidate => candidate.Kind == PieceCandidateKind.Train)))
                message = "Remove all trains from the board before starting. " + message;
            return new(false, message);
        }

        var best = orientations.OrderByDescending(view => selected.Count(color =>
            view.Markers.Any(marker => marker.Color == color && NearOne(view, marker)))).First();
        if (best.Candidates.Any(candidate => candidate.Kind == PieceCandidateKind.Train))
            return new(false, "Remove all trains from the board before starting.");
        if (!selected.Any(color => best.Markers.Any(marker => marker.Color == color && NearOne(best, marker))))
            return new(false, "Move the scoring markers on or near the printed 1.");
        if (best.Markers.Any(marker => marker.Color is null))
            return new(false, "A scoring marker's color is unclear. Separate the markers and check the lighting.");

        foreach (var color in selected)
        {
            var matches = best.Markers.Where(marker => marker.Color == color).ToArray();
            if (matches.Length == 0)
                return new(false, $"Move the {color.ToString().ToLowerInvariant()} scoring marker on or near the printed 1.");
            if (matches.Length != 1)
                return new(false, $"More than one {color.ToString().ToLowerInvariant()} scoring marker was detected.");
            if (!NearOne(best, matches[0]))
                return new(false, $"Move the {color.ToString().ToLowerInvariant()} scoring marker on or near the printed 1.");
        }
        var extra = best.Markers.FirstOrDefault(marker => marker.Color is { } color && !selected.Contains(color));
        if (extra is not null)
            return new(false, $"Remove the unused {extra.Color!.Value.ToString().ToLowerInvariant()} scoring marker.");
        return new(true, "");
    }

    private static bool NearOne(GameSetupBoardObservation view, ScoreMarkerReading marker)
    {
        if (marker.CandidateIndex < 0 || marker.CandidateIndex >= view.Candidates.Count) return false;
        var candidate = view.Candidates[marker.CandidateIndex];
        if (candidate.Kind != PieceCandidateKind.PlayerMarker || candidate.Outline.Count < 3) return false;
        var x = candidate.Outline.Average(point => point.X);
        var y = candidate.Outline.Average(point => point.Y);
        // Several scoring markers can sit inward alongside 1; exclude the neighboring 2 and 100 cells.
        return x is >= 0 and <= .14 && Math.Abs(y - ScoreMarkerReader.ScoreOneCenter.Y) <= .034;
    }
}
