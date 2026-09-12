using System.Collections.Immutable;

namespace GoldenTicket.Domain.Model;

/// <summary>One seat's fully itemised final score. Every component is shown, never just a total.</summary>
public sealed record SeatScore(
    SeatId SeatId,
    int RoutePoints,
    ImmutableArray<TicketId> CompletedTickets,
    ImmutableArray<TicketId> IncompleteTickets,
    int TicketPointsGained,
    int TicketPointsLost,
    int LongestTrailLength,
    ImmutableArray<RouteId> LongestTrailWitness,
    bool HoldsLongestRouteBonus,
    int LongestRouteBonusPoints)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int Total => RoutePoints + TicketPointsGained - TicketPointsLost + LongestRouteBonusPoints;

    [System.Text.Json.Serialization.JsonIgnore]
    public int CompletedTicketCount => CompletedTickets.Length;
}

/// <summary>
/// The exact final result (DESIGN 7.2 invariant 11). <see cref="SharedVictory"/> records the
/// documented product clarification from DESIGN 6.4 when the official tie-breaks still leave
/// more than one winner.
/// </summary>
public sealed record FinalResult(
    ImmutableArray<SeatScore> Scores,
    ImmutableArray<SeatId> Winners,
    int LongestTrailLength,
    ImmutableArray<SeatId> LongestRouteBonusHolders,
    bool SharedVictory,
    string TieBreakExplanation)
{
    public SeatScore ScoreOf(SeatId seat) => Scores.First(s => s.SeatId == seat);
}
