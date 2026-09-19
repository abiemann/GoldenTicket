namespace GoldenTicket.Vision;

public sealed record BoardFirstDiagnosticRoute(string RouteId, int TrainCount, bool CanClaim);

public sealed record BoardFirstMoveFeedback(string RouteId, int DetectedTrains, int RequiredTrains,
    bool CanClaim, int UnverifiedSlotMask = 0);

/// <summary>
/// Presentation-only diagnosis of a solo human's uncommitted pieces. Unlike a claim, feedback
/// may describe a partial route, but only after the same unique route is visible in fresh frames.
/// It never supplies evidence to the rules engine.
/// </summary>
public sealed class BoardFirstMoveFeedbackDetector
{
    private static readonly TimeSpan MinimumStableInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private readonly record struct Identity(string TurnKey, MarkerColor Color, long CropRevision,
        long ModelRevision, long CameraEpoch);

    private Identity? _identity;
    private long _lastSequence;
    private BoardFirstMoveFeedback? _candidate;
    private DateTimeOffset? _firstSeenAt;

    public BoardFirstMoveFeedback? Observe(CameraFrame board, IReadOnlyList<PieceCandidate> candidates,
        IReadOnlyList<BoardFirstDiagnosticRoute> routes, MarkerColor color, string turnKey,
        long cropRevision, long modelRevision)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnKey);

        var identity = new Identity(turnKey, color, cropRevision, modelRevision, board.Epoch);
        if (_identity != identity)
        {
            Reset();
            _identity = identity;
        }
        if (board.Age > MaximumFrameAge ||
            Math.Abs((double)board.Width / board.Height - 1.6) > .02 ||
            board.Sequence <= _lastSequence)
        {
            _candidate = null;
            _firstSeenAt = null;
            return null;
        }
        _lastSequence = board.Sequence;

        var possible = new List<BoardFirstMoveFeedback>();
        var completeLegalRoute = false;
        foreach (var route in routes)
        {
            if (!RoutePlacementVerifier.Supports(route.RouteId, route.TrainCount)) continue;
            // A fresh verifier reports occupancy in this frame, without retaining an old claim.
            var observation = new RoutePlacementVerifier().Observe(board, candidates,
                route.RouteId, color, route.TrainCount, turnKey, cropRevision, modelRevision);
            if (observation.State is RoutePlacementState.WrongColor or RoutePlacementState.Ambiguous or
                RoutePlacementState.WaitingForFreshFrame or RoutePlacementState.Unsupported) continue;

            if (observation.MatchedCount == route.TrainCount && route.CanClaim)
            {
                completeLegalRoute = true;
                break;
            }
            // Two distinct pieces make a partial multi-space route meaningful. A complete
            // one-space route also merits feedback if the player cannot legally claim it.
            if (observation.MatchedCount < (route.TrainCount == 1 ? 1 : 2)) continue;
            possible.Add(new BoardFirstMoveFeedback(route.RouteId,
                observation.MatchedCount, route.TrainCount, route.CanClaim,
                observation.UnverifiedSlotMask));
        }

        // Ambiguous or valid arrangements must not be called an invalid move.
        if (completeLegalRoute || possible.Count != 1)
        {
            _candidate = null;
            _firstSeenAt = null;
            return null;
        }
        var current = possible[0];
        if (_candidate != current || _firstSeenAt is null || board.CapturedAt < _firstSeenAt)
        {
            _candidate = current;
            _firstSeenAt = board.CapturedAt;
            return null;
        }
        return board.CapturedAt - _firstSeenAt >= MinimumStableInterval ? current : null;
    }

    public void Reset()
    {
        _identity = null;
        _lastSequence = 0;
        _candidate = null;
        _firstSeenAt = null;
    }
}
