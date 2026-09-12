using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain;

/// <summary>
/// A canonical fingerprint of the complete referee state. DESIGN 7.2 invariant 12 requires replay to
/// reach the same state and hashes; DESIGN 19.3/19.4 use the same value to verify a restored match
/// against its journal before play resumes.
/// </summary>
public static class StateHash
{
    public static string Compute(GameState state) => "sha256-v2:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(state))));

    /// <summary>Validates either a current complete-state hash or an existing legacy save hash.</summary>
    public static bool Matches(GameState state, string expected) =>
        expected.StartsWith("sha256-v2:", StringComparison.Ordinal)
            ? string.Equals(Compute(state), expected, StringComparison.Ordinal)
            : expected.StartsWith("sha256:", StringComparison.Ordinal) &&
              string.Equals(ComputeLegacy(state), expected, StringComparison.Ordinal);

    /// <summary>
    /// Structured encoding covers every persisted state field, including final scoring evidence.
    /// Strings are JSON escaped and dictionary entries are sorted, avoiding delimiter collisions
    /// and insertion-order differences. This contains referee secrets and must not enter public logs.
    /// </summary>
    public static string Canonicalize(GameState state) => CanonicalizeCore(state) + PackAwaySuffix(state);

    /// <summary>
    /// The gameplay fingerprint: the same content, with lifecycle, the transaction counters and the
    /// pack-away bookkeeping normalised away. DESIGN 19.8 compares a checkpoint's logical-state hash
    /// with the restored state "excluding lifecycle/version bookkeeping", which is exactly this, and
    /// invariant 15 uses it to prove that packing away and rebuilding changed nothing about the game.
    /// </summary>
    public static string ComputeLogical(GameState state)
    {
        var normalised = state.Fork();
        normalised.StateVersion = 0;
        normalised.JournalSequence = 0;
        normalised.Lifecycle = SessionLifecycle.Active;
        normalised.Checkpoint = null;
        normalised.PackAwayRequest = null;
        normalised.RebuildAttested = false;
        normalised.CheckpointFault = null;

        return "logical-v1:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalizeCore(normalised))));
    }

    /// <summary>
    /// Appended only when a save is in flight or committed, so a match that has never been packed
    /// away hashes exactly as it did before this state existed and older saves stay readable.
    /// </summary>
    private static string PackAwaySuffix(GameState state) =>
        state.Checkpoint is null && state.PackAwayRequest is null &&
        !state.RebuildAttested && state.CheckpointFault is null
            ? string.Empty
            : "\n" + JsonSerializer.Serialize(new
            {
                state.Checkpoint,
                state.PackAwayRequest,
                state.RebuildAttested,
                state.CheckpointFault,
            });

    private static string CanonicalizeCore(GameState state) => JsonSerializer.Serialize(new
    {
        FormatVersion = 2,
        state.SessionId,
        Profile = new
        {
            state.Manifest.ProfileId, state.Manifest.SchemaVersion,
            state.Manifest.RulesPolicyVersion, state.Manifest.DataHash,
        },
        state.StateVersion, state.JournalSequence, state.BoardRevision,
        state.Lifecycle, state.VerificationMode, state.TurnPhase, state.CurrentTurnAction,
        state.TrainCardsTakenThisTurn, state.ActiveSeatIndex, state.TurnNumber,
        state.Seats,
        Owners = state.RouteOwners.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal).ToArray(),
        Stock = state.TrainStock.OrderBy(pair => pair.Key.Value).ToArray(),
        RouteScores = state.RouteScore.OrderBy(pair => pair.Key.Value).ToArray(),
        state.TrainDeck, state.TrainDiscard, state.FaceUp, state.TicketDeck,
        TrainHands = state.TrainHandsInternal.OrderBy(pair => pair.Key.Value).ToArray(),
        TicketHands = state.TicketHandsInternal.OrderBy(pair => pair.Key.Value).ToArray(),
        SetupOffers = state.SetupOffers.OrderBy(pair => pair.Key.Value).ToArray(),
        PendingTicketReturns = state.PendingTicketReturns.OrderBy(pair => pair.Key.Value).ToArray(),
        state.CurrentTicketOffer, state.PendingClaim,
        FinalRound = state.FinalRound is { } round ? new
        {
            round.TriggeringSeatId, round.TriggeringTurnNumber,
            RemainingTurns = round.RemainingTurnsBySeat.OrderBy(pair => pair.Key.Value).ToArray(),
        } : null,
        RandomState = state.RandomState.ToWire(),
        state.RulesDecision,
        state.FinalResult,
    });

    /// <summary>Unchanged encoding retained only to read saves written before hash version 2.</summary>
    public static string ComputeLegacy(GameState state) => "sha256:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalizeLegacy(state))));

    private static string CanonicalizeLegacy(GameState state)
    {
        var invariant = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();

        builder.Append("session=").Append(state.SessionId.Value).Append('\n');
        builder.Append("profile=").Append(state.Manifest.ProfileId)
            .Append('|').Append(state.Manifest.DataHash).Append('\n');
        builder.Append("versions=").Append(state.StateVersion.ToString(invariant))
            .Append('|').Append(state.JournalSequence.ToString(invariant))
            .Append('|').Append(state.BoardRevision.ToString(invariant)).Append('\n');
        builder.Append("phase=").Append(state.Lifecycle).Append('|').Append(state.TurnPhase)
            .Append('|').Append(state.CurrentTurnAction)
            .Append('|').Append(state.TrainCardsTakenThisTurn.ToString(invariant))
            .Append('|').Append(state.VerificationMode).Append('\n');
        builder.Append("turn=").Append(state.TurnNumber.ToString(invariant))
            .Append('|').Append(state.ActiveSeatId.Value.ToString(invariant)).Append('\n');
        builder.Append("random=").Append(state.RandomState.ToWire()).Append('\n');

        foreach (var seat in state.Seats)
        {
            builder.Append("seat=").Append(seat.SeatId.Value.ToString(invariant))
                .Append('|').Append(seat.DisplayName)
                .Append('|').Append(seat.Color)
                .Append('|').Append(seat.Kind)
                .Append('|').Append(seat.Difficulty)
                .Append('|').Append(state.TrainStock[seat.SeatId].ToString(invariant))
                .Append('|').Append(state.RouteScore[seat.SeatId].ToString(invariant))
                .Append('\n');

            builder.Append("hand=").Append(seat.SeatId.Value.ToString(invariant)).Append(':')
                .AppendJoin(',', state.HandOf(seat.SeatId).Select(card => card.Value)).Append('\n');

            builder.Append("tickets=").Append(seat.SeatId.Value.ToString(invariant)).Append(':')
                .AppendJoin(',', state.TicketsOf(seat.SeatId).Select(ticket => ticket.Value)).Append('\n');

            if (state.SetupOffers.TryGetValue(seat.SeatId, out var offer))
            {
                builder.Append("setupOffer=").Append(seat.SeatId.Value.ToString(invariant)).Append(':')
                    .AppendJoin(',', offer.Select(ticket => ticket.Value)).Append('\n');
            }

            if (state.PendingTicketReturns.TryGetValue(seat.SeatId, out var returns))
            {
                builder.Append("setupReturn=").Append(seat.SeatId.Value.ToString(invariant)).Append(':')
                    .AppendJoin(',', returns.Select(ticket => ticket.Value)).Append('\n');
            }
        }

        builder.Append("trainDeck=").AppendJoin(',', state.TrainDeck.Select(card => card.Value)).Append('\n');
        builder.Append("trainDiscard=").AppendJoin(',', state.TrainDiscard.Select(card => card.Value)).Append('\n');
        builder.Append("faceUp=")
            .AppendJoin(',', state.FaceUp.Select(card => card is { } value ? value.Value.ToString(invariant) : "-"))
            .Append('\n');
        builder.Append("ticketDeck=").AppendJoin(',', state.TicketDeck.Select(ticket => ticket.Value)).Append('\n');

        foreach (var (routeId, seatId) in state.RouteOwners.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
        {
            builder.Append("owner=").Append(routeId.Value)
                .Append('=').Append(seatId.Value.ToString(invariant)).Append('\n');
        }

        if (state.CurrentTicketOffer is { } ticketOffer)
        {
            builder.Append("ticketOffer=").Append(ticketOffer.SeatId.Value.ToString(invariant))
                .Append('|').Append(ticketOffer.MinimumKeep.ToString(invariant))
                .Append('|').AppendJoin(',', ticketOffer.Offered.Select(ticket => ticket.Value)).Append('\n');
        }

        if (state.PendingClaim is { } claim)
        {
            builder.Append("pendingClaim=").Append(claim.OperationId.Value)
                .Append('|').Append(claim.SeatId.Value.ToString(invariant))
                .Append('|').Append(claim.RouteId.Value)
                .Append('|').Append(claim.BaseBoardRevision.ToString(invariant))
                .Append('|').Append(claim.CreatedAtStateVersion.ToString(invariant))
                .Append('|').AppendJoin(',', claim.ReservedCards.Select(card => card.Value)).Append('\n');
        }

        if (state.FinalRound is { } finalRound)
        {
            builder.Append("finalRound=").Append(finalRound.TriggeringSeatId.Value.ToString(invariant))
                .Append('|').Append(finalRound.TriggeringTurnNumber.ToString(invariant))
                .Append('|')
                .AppendJoin(',', finalRound.RemainingTurnsBySeat
                    .OrderBy(pair => pair.Key.Value)
                    .Select(pair => $"{pair.Key.Value}:{pair.Value}"))
                .Append('\n');
        }

        if (state.RulesDecision is { } decision)
            builder.Append("rulesDecision=").Append(decision.Code).Append('\n');

        if (state.FinalResult is { } result)
        {
            foreach (var score in result.Scores.OrderBy(s => s.SeatId.Value))
            {
                builder.Append("finalScore=").Append(score.SeatId.Value.ToString(invariant))
                    .Append('|').Append(score.RoutePoints.ToString(invariant))
                    .Append('|').Append(score.TicketPointsGained.ToString(invariant))
                    .Append('|').Append(score.TicketPointsLost.ToString(invariant))
                    .Append('|').Append(score.LongestTrailLength.ToString(invariant))
                    .Append('|').Append(score.LongestRouteBonusPoints.ToString(invariant))
                    .Append('|').Append(score.Total.ToString(invariant))
                    .Append('\n');
            }

            builder.Append("winners=")
                .AppendJoin(',', result.Winners.OrderBy(seat => seat.Value).Select(seat => seat.Value))
                .Append('\n');
        }

        return builder.ToString();
    }
}
