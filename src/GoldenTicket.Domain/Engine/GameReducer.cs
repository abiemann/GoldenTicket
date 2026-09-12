using System.Collections.Immutable;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Engine;

/// <summary>One journal row: a durable event with its ordering and the transaction it belonged to.</summary>
public sealed record JournaledEvent(long Sequence, long StateVersion, GameEvent Event);

/// <summary>
/// Applies durable events to <see cref="GameState"/>. This is the only code that mutates state, it
/// contains no randomness and no rule decisions, and every event carries its outcome explicitly.
/// That is what makes invariant 12 hold: replay reaches the same state and hashes without
/// re-running randomness or vision.
/// </summary>
public static class GameReducer
{
    /// <summary>Applies one transaction's events and advances the state version once.</summary>
    public static void ApplyTransition(GameState state, IEnumerable<GameEvent> events)
    {
        foreach (var domainEvent in events) Apply(state, domainEvent);
        state.StateVersion++;
    }

    /// <summary>Rebuilds a match from its complete journal (DESIGN 19.4).</summary>
    public static GameState Rebuild(
        BoardManifest manifest,
        CardCatalog catalog,
        IReadOnlyList<JournaledEvent> journal)
    {
        if (journal.Count == 0)
            throw new InvalidDataException("An empty journal cannot restore a match.");

        if (journal[0].Event is not SessionCreated created)
            throw new InvalidDataException("The first journal event must be SessionCreated.");

        var state = new GameState(
            created.SessionId, manifest, catalog, created.Seats, created.VerificationMode);

        foreach (var group in GroupByTransition(journal))
            ApplyTransition(state, group);

        return state;
    }

    private static IEnumerable<List<GameEvent>> GroupByTransition(IReadOnlyList<JournaledEvent> journal)
    {
        var current = new List<GameEvent>();
        var version = journal[0].StateVersion;

        foreach (var row in journal)
        {
            if (row.StateVersion != version && current.Count > 0)
            {
                yield return current;
                current = [];
            }

            version = row.StateVersion;
            current.Add(row.Event);
        }

        if (current.Count > 0) yield return current;
    }

    public static void Apply(GameState state, GameEvent domainEvent)
    {
        switch (domainEvent)
        {
            case SessionCreated e: ApplySessionCreated(state, e); break;
            case InitialHandsDealt e: ApplyInitialHands(state, e); break;
            case SetupOfferCreated e: ApplySetupOffer(state, e); break;
            case TicketSelectionCommitted e: ApplyTicketSelection(state, e); break;
            case SetupReturnsRecycled e: ApplySetupReturns(state, e); break;
            case SetupCompleted: state.Lifecycle = SessionLifecycle.Active; break;
            case TurnStarted e: ApplyTurnStarted(state, e); break;
            case FaceUpCardTaken e: ApplyFaceUpTaken(state, e); break;
            case BlindCardDrawn e: ApplyBlindDraw(state, e); break;
            case MarketRefilled e: ApplyMarketRefill(state, e); break;
            case MarketReset e: ApplyMarketReset(state, e); break;
            case DeckReshuffled e: ApplyReshuffle(state, e); break;
            case TicketOfferCreated e: ApplyTicketOffer(state, e); break;
            case ClaimPlanned e: ApplyClaimPlanned(state, e); break;
            case ManualVerificationRecorded: break; // Evidence only; the commit changes state.
            case ClaimCommitted e: ApplyClaimCommitted(state, e); break;
            case ClaimCancellationRequested e: ApplyCancellationRequested(state, e); break;
            case ClaimCancelled e: ApplyClaimCancelled(state, e); break;
            case TurnCompleted e: ApplyTurnCompleted(state, e); break;
            case FinalRoundStarted e: ApplyFinalRoundStarted(state, e); break;
            case FinalScoringCompleted e: ApplyFinalScoring(state, e); break;
            case RulesDecisionRaised e: ApplyRulesDecision(state, e); break;

            case PackAwayRequested e: ApplyPackAwayRequested(state, e); break;
            case PackAwayPreparationCancelled: ApplyPackAwayCancelled(state); break;
            case PackAwayCheckpointCommitted e: ApplyCheckpointCommitted(state, e); break;
            case PackAwayCheckpointVerified e: ApplyCheckpointStatus(state, e.CheckpointId, CheckpointStatus.Verified, null); break;
            case PackAwayCheckpointFaulted e: ApplyCheckpointStatus(state, e.CheckpointId, CheckpointStatus.Faulted, e.Reason); break;
            case BoardRebuildStarted e: ApplyRebuildStarted(state, e); break;
            case BoardRebuildAttested: state.RebuildAttested = true; break;
            case PackedGameResumed e: ApplyPackedGameResumed(state, e); break;

            default:
                throw new InvalidOperationException(
                    $"No reducer is defined for {domainEvent.GetType().Name}. " +
                    "Every event type must be handled or replay would diverge.");
        }

        state.JournalSequence++;
    }

    // ---- Setup ---------------------------------------------------------------------------

    private static void ApplySessionCreated(GameState state, SessionCreated e)
    {
        state.TrainDeckInternal.Clear();
        state.TrainDeckInternal.AddRange(e.TrainDeckOrder);
        state.TicketDeckInternal.Clear();
        state.TicketDeckInternal.AddRange(e.TicketDeckOrder);
        state.RandomState = RandomState.FromWire(e.RandomStateAfter);
        state.ActiveSeatIndex = state.SeatIndexOf(e.StartingSeatId);
        state.Lifecycle = SessionLifecycle.Setup;
        state.TurnPhase = TurnPhase.SetupTicketSelection;
    }

    private static void ApplyInitialHands(GameState state, InitialHandsDealt e)
    {
        foreach (var (seat, cards) in e.Hands)
        {
            foreach (var card in cards)
            {
                TakeSpecificCardFromDeck(state, card);
                state.TrainHandsInternal[seat].Add(card);
            }
        }

        for (var slot = 0; slot < e.FaceUp.Length; slot++)
        {
            TakeSpecificCardFromDeck(state, e.FaceUp[slot]);
            state.FaceUpInternal[slot] = e.FaceUp[slot];
        }
    }

    private static void ApplySetupOffer(GameState state, SetupOfferCreated e)
    {
        foreach (var ticket in e.Offered)
        {
            if (!state.TicketDeckInternal.Remove(ticket))
                throw new InvalidDataException($"Ticket {ticket} was offered but is not in the deck.");
        }

        state.SetupOffersInternal[e.SeatId] = e.Offered;
    }

    private static void ApplyTicketSelection(GameState state, TicketSelectionCommitted e)
    {
        state.TicketHandsInternal[e.SeatId].AddRange(e.Kept);

        if (e.IsSetupSelection)
        {
            state.SetupOffersInternal.Remove(e.SeatId);
            // Held aside; DESIGN 6.4 recycles setup returns only after every seat has chosen.
            state.PendingTicketReturnsInternal[e.SeatId] = e.Returned;
        }
        else
        {
            state.CurrentTicketOffer = null;
            // In-game rejections go underneath in the order the acting seat chose.
            state.TicketDeckInternal.AddRange(e.Returned);
        }
    }

    private static void ApplySetupReturns(GameState state, SetupReturnsRecycled e)
    {
        var heldAside = state.PendingTicketReturnsInternal.Values.SelectMany(t => t).ToHashSet();
        foreach (var ticket in e.AppendedInOrder)
        {
            if (!heldAside.Remove(ticket))
                throw new InvalidDataException($"Ticket {ticket} was recycled but was not held aside.");

            state.TicketDeckInternal.Add(ticket);
        }

        if (heldAside.Count > 0)
            throw new InvalidDataException("Setup recycling must append every held-aside ticket exactly once.");

        state.PendingTicketReturnsInternal.Clear();
    }

    // ---- Turn flow -----------------------------------------------------------------------

    private static void ApplyTurnStarted(GameState state, TurnStarted e)
    {
        state.ActiveSeatIndex = state.SeatIndexOf(e.SeatId);
        state.TurnNumber = e.TurnNumber;
        state.TurnPhase = TurnPhase.TurnStart;
        state.CurrentTurnAction = TurnAction.None;
        state.TrainCardsTakenThisTurn = 0;
    }

    private static void ApplyTurnCompleted(GameState state, TurnCompleted e)
    {
        state.CurrentTurnAction = TurnAction.None;
        state.TrainCardsTakenThisTurn = 0;

        if (state.FinalRound is { } finalRound &&
            finalRound.RemainingTurnsBySeat.TryGetValue(e.SeatId, out var remaining) &&
            remaining > 0)
        {
            state.FinalRound = finalRound with
            {
                RemainingTurnsBySeat = finalRound.RemainingTurnsBySeat.SetItem(e.SeatId, remaining - 1),
            };
        }
    }

    // ---- Train cards ---------------------------------------------------------------------

    private static void ApplyFaceUpTaken(GameState state, FaceUpCardTaken e)
    {
        if (state.FaceUpInternal[e.Slot] != e.Card)
            throw new InvalidDataException($"Slot {e.Slot} does not hold card {e.Card}.");

        state.FaceUpInternal[e.Slot] = null;
        state.TrainHandsInternal[e.SeatId].Add(e.Card);
        state.TrainCardsTakenThisTurn++;
        state.CurrentTurnAction = TurnAction.DrawTrainCards;
        state.TurnPhase = e.EndsTurn ? TurnPhase.TurnStart : TurnPhase.AwaitingSecondTrainCard;
    }

    private static void ApplyBlindDraw(GameState state, BlindCardDrawn e)
    {
        TakeSpecificCardFromDeck(state, e.Card);
        state.TrainHandsInternal[e.SeatId].Add(e.Card);
        state.TrainCardsTakenThisTurn++;
        state.CurrentTurnAction = TurnAction.DrawTrainCards;
        state.TurnPhase = TurnPhase.AwaitingSecondTrainCard;
    }

    private static void ApplyMarketRefill(GameState state, MarketRefilled e)
    {
        TakeSpecificCardFromDeck(state, e.Card);
        state.FaceUpInternal[e.Slot] = e.Card;
    }

    private static void ApplyMarketReset(GameState state, MarketReset e)
    {
        foreach (var card in e.Discarded)
        {
            var slot = state.FaceUpInternal.IndexOf(card);
            if (slot < 0) throw new InvalidDataException($"Card {card} is not face up and cannot be reset.");
            state.FaceUpInternal[slot] = null;
            state.TrainDiscardInternal.Add(card);
        }
    }

    private static void ApplyReshuffle(GameState state, DeckReshuffled e)
    {
        foreach (var card in e.NewOrder)
        {
            if (!state.TrainDiscardInternal.Remove(card))
                throw new InvalidDataException($"Card {card} was reshuffled but was not in the discards.");
        }

        state.TrainDeckInternal.AddRange(e.NewOrder);
        state.RandomState = RandomState.FromWire(e.RandomStateAfter);
    }

    // ---- Destination tickets ---------------------------------------------------------------

    private static void ApplyTicketOffer(GameState state, TicketOfferCreated e)
    {
        foreach (var ticket in e.Offered)
        {
            if (!state.TicketDeckInternal.Remove(ticket))
                throw new InvalidDataException($"Ticket {ticket} was offered but is not in the deck.");
        }

        state.CurrentTicketOffer = new TicketOffer(e.SeatId, e.Offered, e.MinimumKeep, IsSetupOffer: false);
        state.CurrentTurnAction = TurnAction.DrawTickets;
        state.TurnPhase = TurnPhase.AwaitingTicketKeep;
    }

    // ---- Route claims ----------------------------------------------------------------------

    private static void ApplyClaimPlanned(GameState state, ClaimPlanned e)
    {
        state.PendingClaim = new PendingClaim(
            e.OperationId, e.SeatId, e.RouteId, e.ReservedCards, e.BaseBoardRevision, e.CreatedAtStateVersion);
        state.CurrentTurnAction = TurnAction.ClaimRoute;
        state.TurnPhase = TurnPhase.AwaitingPhysicalPlacement;
    }

    private static void ApplyClaimCommitted(GameState state, ClaimCommitted e)
    {
        var hand = state.TrainHandsInternal[e.SeatId];
        foreach (var card in e.SpentCards)
        {
            if (!hand.Remove(card))
                throw new InvalidDataException($"Card {card} was spent but is not in seat {e.SeatId}'s hand.");

            state.TrainDiscardInternal.Add(card);
        }

        var route = state.Manifest.Route(e.RouteId);
        state.RouteOwnersInternal[e.RouteId] = e.SeatId;
        state.TrainStockInternal[e.SeatId] -= route.Length;
        state.RouteScoreInternal[e.SeatId] += e.Points;
        state.PendingClaim = null;
        state.BoardRevision++;
        state.TurnPhase = TurnPhase.TurnStart;
    }

    private static void ApplyCancellationRequested(GameState state, ClaimCancellationRequested e)
    {
        if (e.RestoreRequired) state.TurnPhase = TurnPhase.RestoreBeforeState;
    }

    private static void ApplyClaimCancelled(GameState state, ClaimCancelled e)
    {
        state.PendingClaim = null;
        state.CurrentTurnAction = TurnAction.None;
        state.TurnPhase = TurnPhase.TurnStart;
    }

    // ---- Endgame ---------------------------------------------------------------------------

    private static void ApplyFinalRoundStarted(GameState state, FinalRoundStarted e) =>
        state.FinalRound = new FinalRound(e.TriggeringSeatId, e.TriggeringTurnNumber, e.RemainingTurnsBySeat);

    private static void ApplyFinalScoring(GameState state, FinalScoringCompleted e)
    {
        state.FinalResult = e.Result;
        state.Lifecycle = SessionLifecycle.Finished;
        state.TurnPhase = TurnPhase.Finished;
    }

    private static void ApplyRulesDecision(GameState state, RulesDecisionRaised e)
    {
        state.RulesDecision = new RulesDecision(e.Code, e.Explanation);
        state.TurnPhase = TurnPhase.RulesDecisionRequired;
    }

    // ---- Save, pack away and rebuild (DESIGN 19.8) -------------------------------------------

    /// <summary>
    /// Step 1. The turn phase is deliberately left alone: the checkpoint records it, and the
    /// lifecycle gate is what stops play, so there is no second copy of the suspended phase to
    /// drift out of step.
    /// </summary>
    private static void ApplyPackAwayRequested(GameState state, PackAwayRequested e)
    {
        state.Lifecycle = SessionLifecycle.PreparingPackAway;
        state.PackAwayRequest = new PackAwayRequest(
            e.RequestId, e.CheckpointId, e.Name, e.SuspendedTurnPhase, e.PendingOperationId);
    }

    private static void ApplyPackAwayCancelled(GameState state)
    {
        state.Lifecycle = SessionLifecycle.Active;
        state.PackAwayRequest = null;
    }

    private static void ApplyCheckpointCommitted(GameState state, PackAwayCheckpointCommitted e)
    {
        state.Checkpoint = e.Checkpoint;
        state.CheckpointFault = null;

        // DESIGN 19.8 step 7: persisting PackedAway before the success message is what makes
        // clearing the pieces safe even if the process dies immediately afterwards.
        state.Lifecycle = SessionLifecycle.PackedAway;
    }

    private static void ApplyCheckpointStatus(
        GameState state, CheckpointId checkpointId, CheckpointStatus status, string? fault)
    {
        if (state.Checkpoint is not { } checkpoint || checkpoint.CheckpointId != checkpointId)
        {
            throw new InvalidDataException(
                $"Checkpoint {checkpointId} is not the one this session is packed against.");
        }

        state.Checkpoint = checkpoint with { Status = status };
        state.CheckpointFault = fault;
    }

    private static void ApplyRebuildStarted(GameState state, BoardRebuildStarted e)
    {
        if (state.Checkpoint is not { } checkpoint || checkpoint.CheckpointId != e.CheckpointId)
            throw new InvalidDataException($"Checkpoint {e.CheckpointId} is not available to rebuild.");

        state.Lifecycle = SessionLifecycle.Rebuilding;
        state.RebuildAttested = false;
    }

    private static void ApplyPackedGameResumed(GameState state, PackedGameResumed e)
    {
        state.Lifecycle = SessionLifecycle.Active;
        state.TurnPhase = e.RestoredTurnPhase;
        state.Checkpoint = null;
        state.PackAwayRequest = null;
        state.RebuildAttested = false;
        state.CheckpointFault = null;
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static void TakeSpecificCardFromDeck(GameState state, CardId card)
    {
        if (!state.TrainDeckInternal.Remove(card))
            throw new InvalidDataException($"Card {card} was drawn but is not in the draw pile.");
    }
}
