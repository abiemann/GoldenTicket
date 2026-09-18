namespace GoldenTicket.Vision;

/// <summary>
/// Finds a single physical route filled by the active human's trains before the route has
/// been selected digitally. This is only a proposal: the rules engine must still authorize
/// the route and its card payment, then verify fresh board evidence before committing it.
/// </summary>
public sealed class BoardFirstRouteDetector
{
    private static readonly TimeSpan MinimumRemovalInterval = TimeSpan.FromSeconds(1);
    private readonly record struct LegalRoute(string RouteId, int TrainCount);
    private readonly record struct Identity(string TurnKey, MarkerColor Color,
        long CropRevision, long ModelRevision, long CameraEpoch, string LegalRouteKey);

    private readonly Dictionary<string, RoutePlacementVerifier> _verifiers =
        new(StringComparer.Ordinal);
    private Identity? _identity;
    private long _proposalLastSequence;
    private DateTimeOffset? _proposalAbsentSince;

    /// <summary>The one route proposed for this turn, until <see cref="Reset"/> is called.</summary>
    public string? ProposedRouteId { get; private set; }

    /// <summary>
    /// Returns a route ID once, after all its spaces match in stable, distinct camera frames.
    /// Multiple complete legal routes in one frame are ambiguous and restart observation.
    /// The caller supplies only routes that this seat may legally claim and pay for.
    /// </summary>
    public string? Observe(CameraFrame board, IReadOnlyList<PieceCandidate> candidates,
        IReadOnlyList<(string RouteId, int TrainCount)> legalRoutes, MarkerColor color,
        string turnKey, long cropRevision, long modelRevision)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(legalRoutes);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnKey);

        // Sorting makes the same legal set stable even when its caller enumerates it differently.
        // Conflicting lengths for one route cannot be resolved safely, so ignore that route.
        var routes = legalRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.RouteId))
            .GroupBy(route => route.RouteId, StringComparer.Ordinal)
            .Where(group => group.Select(route => route.TrainCount).Distinct().Count() == 1)
            .Select(group => new LegalRoute(group.Key, group.First().TrainCount))
            .Where(route => RoutePlacementVerifier.Supports(route.RouteId, route.TrainCount))
            .OrderBy(route => route.RouteId, StringComparer.Ordinal)
            .ToArray();
        var routeKey = string.Join('|', routes.Select(route => $"{route.RouteId}:{route.TrainCount}"));
        var identity = new Identity(turnKey, color, cropRevision, modelRevision, board.Epoch, routeKey);
        if (_identity != identity)
        {
            Reset();
            _identity = identity;
        }

        if (ProposedRouteId is { } proposedRouteId)
        {
            // Recheck each later frame independently so an old proposal cannot hide pieces
            // that were removed or changed after the route first appeared.
            if (board.Sequence <= _proposalLastSequence) return null;
            var proposed = routes.Single(route => route.RouteId == proposedRouteId);
            var check = new RoutePlacementVerifier().Observe(board, candidates, proposedRouteId, color,
                proposed.TrainCount, turnKey, cropRevision, modelRevision);
            if (check.State == RoutePlacementState.WaitingForFreshFrame) return null;
            _proposalLastSequence = board.Sequence;
            if (check.MatchedCount == proposed.TrainCount &&
                check.State is RoutePlacementState.Stabilizing or RoutePlacementState.Confirmed)
            {
                _proposalAbsentSince = null;
                return null;
            }

            // An ambiguous detection does not prove the pieces have been removed. Require
            // sustained missing/wrong-color evidence before withdrawing the payment choice.
            if (check.State is not (RoutePlacementState.Incomplete or RoutePlacementState.WrongColor))
            {
                _proposalAbsentSince = null;
                return null;
            }
            if (_proposalAbsentSince is not { } absentSince || board.CapturedAt < absentSince)
            {
                _proposalAbsentSince = board.CapturedAt;
                return null;
            }
            if (board.CapturedAt - absentSince < MinimumRemovalInterval)
                return null;

            Reset();
            _identity = identity;
            return null;
        }

        var complete = new List<(LegalRoute Route, RoutePlacementState State)>();
        foreach (var route in routes)
        {
            if (!_verifiers.TryGetValue(route.RouteId, out var verifier))
                _verifiers.Add(route.RouteId, verifier = new RoutePlacementVerifier());
            var observation = verifier.Observe(board, candidates, route.RouteId, color,
                route.TrainCount, turnKey, cropRevision, modelRevision);
            if (observation.MatchedCount == route.TrainCount &&
                observation.State is RoutePlacementState.Stabilizing or RoutePlacementState.Confirmed)
                complete.Add((route, observation.State));
        }

        if (complete.Count > 1)
        {
            // A route that was already stabilizing must not confirm immediately after another
            // simultaneously occupied route disappears from camera view.
            _verifiers.Clear();
            return null;
        }

        if (complete.Count != 1 || complete[0].State != RoutePlacementState.Confirmed)
            return null;

        ProposedRouteId = complete[0].Route.RouteId;
        _proposalLastSequence = board.Sequence;
        _proposalAbsentSince = null;
        _verifiers.Clear();
        return ProposedRouteId;
    }

    public void Reset()
    {
        _identity = null;
        _verifiers.Clear();
        ProposedRouteId = null;
        _proposalLastSequence = 0;
        _proposalAbsentSince = null;
    }
}
