using System.Collections.Immutable;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Domain.Scoring;

namespace GoldenTicket.Domain.Engine;

/// <summary>The table configuration chosen during onboarding (DESIGN 3.3 step 9).</summary>
public sealed record SessionSetup(
    SessionId SessionId,
    ImmutableArray<Seat> Seats,
    SeatId StartingSeatId,
    VerificationMode VerificationMode);

/// <summary>
/// The deterministic rules engine for <c>ttr-us-classic-en-v1</c>. DESIGN 6.2: it is independent of
/// images, speech, UI, model confidences and wall-clock delays. Its only outputs are legal actions,
/// projections, and transitions made of durable events.
/// </summary>
public sealed class GameRules(BoardManifest manifest, CardCatalog catalog, TimeProvider? timeProvider = null)
{
    /// <summary>DESIGN 6.4: bounds repeated market refresh instead of looping forever.</summary>
    public const int MaximumMarketResets = 10;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public BoardManifest Manifest { get; } = manifest;

    public CardCatalog Catalog { get; } = catalog;

    private RulesConstants Constants => Manifest.RulesConstants;

    public LegalActions GetLegalActions(SeatView view) => LegalActionCalculator.For(view, Manifest);

    public PublicView ProjectPublic(GameState state) => Projector.ProjectPublic(state);

    public SeatView ProjectSeat(GameState state, SeatId seat) => Projector.ProjectSeat(state, seat);

    // ---- Session creation ------------------------------------------------------------------

    /// <summary>
    /// Shuffles both decks, deals the opening hands, lays out the market and offers setup tickets.
    /// The returned events are the first transaction; the state has already had them applied.
    /// </summary>
    public (GameState State, Transition Transition) CreateSession(SessionSetup setup, RandomState seed)
    {
        ValidateSetup(setup);

        var random = new DeterministicRandom(seed);

        var trainDeck = Catalog.All.ToList();
        random.Shuffle(trainDeck);

        var ticketDeck = Manifest.Tickets.Select(ticket => ticket.TicketId).ToList();
        random.Shuffle(ticketDeck);

        var state = new GameState(
            setup.SessionId, Manifest, Catalog, setup.Seats, setup.VerificationMode);
        var context = new TransitionContext(state, random);

        context.Emit(new SessionCreated(
            setup.SessionId,
            Manifest.ProfileId,
            Manifest.DataHash,
            Manifest.RulesPolicyVersion,
            DeterministicRandom.ShuffleVersion,
            setup.Seats,
            setup.StartingSeatId,
            setup.VerificationMode,
            [.. trainDeck],
            [.. ticketDeck],
            random.State.ToWire(),
            _time.GetUtcNow()));

        var dealt = 0;
        var hands = ImmutableDictionary.CreateBuilder<SeatId, ImmutableArray<CardId>>();
        foreach (var seat in setup.Seats)
        {
            hands[seat.SeatId] = [.. trainDeck.Skip(dealt).Take(Constants.StartingTrainCards)];
            dealt += Constants.StartingTrainCards;
        }

        var faceUp = trainDeck.Skip(dealt).Take(Constants.FaceUpMarketSize).ToImmutableArray();
        context.Emit(new InitialHandsDealt(hands.ToImmutable(), faceUp));

        // The three-locomotive rule applies to the opening layout as well.
        MaintainMarket(context);

        foreach (var seat in setup.Seats)
        {
            var offered = state.TicketDeck.Take(Constants.SetupTicketOffer).ToImmutableArray();
            context.Emit(new SetupOfferCreated(seat.SeatId, offered));
        }

        state.StateVersion++;
        return (state, new Transition([.. context.Events]));
    }

    private void ValidateSetup(SessionSetup setup)
    {
        var seats = setup.Seats;

        if (seats.Length < Constants.MinPlayers || seats.Length > Constants.MaxPlayers)
        {
            throw new ArgumentException(
                $"The pinned profile supports {Constants.MinPlayers}-{Constants.MaxPlayers} seats, not {seats.Length}.",
                nameof(setup));
        }

        if (seats.Select(seat => seat.SeatId).Distinct().Count() != seats.Length)
            throw new ArgumentException("Seat ids must be distinct.", nameof(setup));

        if (seats.Select(seat => seat.Color).Distinct().Count() != seats.Length)
            throw new ArgumentException("Physical train colours must be distinct.", nameof(setup));

        if (seats.All(seat => seat.SeatId != setup.StartingSeatId))
            throw new ArgumentException("The starting seat must be at the table.", nameof(setup));

        var requiredCards = seats.Length * Constants.StartingTrainCards + Constants.FaceUpMarketSize;
        if (requiredCards > Manifest.TotalTrainCards)
            throw new ArgumentException("The supply is too small for this table.", nameof(setup));

        if (seats.Length * Constants.SetupTicketOffer > Manifest.Tickets.Length)
            throw new ArgumentException("The ticket deck is too small for this table.", nameof(setup));
    }

    // ---- Command handling ------------------------------------------------------------------

    /// <summary>
    /// Validates a command against current state and returns either the transaction to commit or an
    /// explained refusal. Nothing is mutated: the engine evaluates against a detached working copy.
    /// </summary>
    public CommandResult ValidateAndApply(GameState state, GameCommand command)
    {
        var envelope = command.Envelope;

        if (envelope.SessionId != state.SessionId)
            return CommandResult.Reject("SessionMismatch", "This command belongs to a different match.");

        // DESIGN 7.2 invariant 8: stale UI selections and stale results are rejected by version.
        if (envelope.ExpectedStateVersion != state.StateVersion)
        {
            return CommandResult.Reject("StaleStateVersion",
                $"The table has moved on. This command expected version {envelope.ExpectedStateVersion} " +
                $"but the match is at {state.StateVersion}.");
        }

        if (state.Lifecycle == SessionLifecycle.Finished)
            return CommandResult.Reject("MatchFinished", "The match is over.");

        // DESIGN 9.2: while a save is being captured, the game is packed, or the board is being
        // rebuilt, only the save/rebuild/recovery controls are accepted. Gameplay commands, AI
        // submissions and ordinary move inference are all refused.
        var isLifecycleCommand = IsLifecycleCommand(command);

        // Accepting a continuation is the one command the rules pause exists to receive.
        var isRulesResolution = command is ResolveRulesDecision;

        if (state.IsGameplaySuspended && !isLifecycleCommand)
        {
            return CommandResult.Reject("SessionSuspended",
                state.Lifecycle switch
                {
                    SessionLifecycle.PreparingPackAway => "The game is being saved. Play resumes when the save finishes or is cancelled.",
                    SessionLifecycle.PackedAway => "This game is packed away. Rebuild the board to continue it.",
                    _ => "The board is being rebuilt. Finish the rebuild before playing on.",
                });
        }

        // DESIGN 21.1: a rare unresolved supply state must still be savable, so the lifecycle
        // controls are allowed through the pause that blocks ordinary play.
        if (state.TurnPhase == TurnPhase.RulesDecisionRequired && !isLifecycleCommand && !isRulesResolution)
        {
            return CommandResult.Reject("RulesDecisionRequired",
                state.RulesDecision?.Explanation ?? "The match is paused on an unresolved supply state.");
        }

        var context = new TransitionContext(state.Fork(), new DeterministicRandom(state.RandomState));

        return command switch
        {
            SaveAndPackAway c => HandleSaveAndPackAway(context, c),
            CancelPackAwayPreparation c => HandleCancelPackAway(context, c),
            CommitPackAwayCheckpoint c => HandleCommitCheckpoint(context, c),
            RecordCheckpointReadback c => HandleCheckpointReadback(context, c),
            BeginBoardRebuild c => HandleBeginRebuild(context, c),
            AttestBoardRebuild c => HandleAttestRebuild(context, c),
            ResumePackedGame c => HandleResumePackedGame(context, c),
            ResolveRulesDecision c => HandleResolveRulesDecision(context, c),
            CommitTicketSelection c => HandleTicketSelection(context, c),
            SelectTrainCard c => HandleSelectTrainCard(context, c),
            RequestTicketOffer c => HandleRequestTicketOffer(context, c),
            PlanClaim c => HandlePlanClaim(context, c),
            SubmitClaimEvidence c => HandleSubmitClaimEvidence(context, c),
            CancelPendingClaim c => HandleCancelPendingClaim(context, c),
            ConfirmBeforeStateRestored c => HandleConfirmRestored(context, c),
            _ => CommandResult.Reject("UnknownCommand", $"{command.GetType().Name} is not a supported command."),
        };
    }

    private static bool HasAccepted(GameState state, string code) =>
        RulesContinuations.For(code) is { } policy &&
        state.AcceptedRulesPolicies.TryGetValue(code, out var accepted) &&
        string.Equals(accepted, policy.PolicyId, StringComparison.Ordinal);

    // ---- Rare supply states (DESIGN 6.4) ------------------------------------------------------

    /// <summary>
    /// Accepts the reviewed continuation for the paused position and carries play forward. The
    /// acceptance holds for the rest of the match, so the same position does not stop play again.
    /// </summary>
    private CommandResult HandleResolveRulesDecision(TransitionContext context, ResolveRulesDecision command)
    {
        var state = context.State;

        if (state.RulesDecision is not { } decision)
            return CommandResult.Reject("NoRulesDecision", "The match is not paused on a rules decision.");

        if (!string.Equals(decision.Code, command.Code, StringComparison.Ordinal))
            return CommandResult.Reject("RulesDecisionMismatch", "That decision is not the one the match is paused on.");

        if (RulesContinuations.For(command.Code) is not { } policy)
        {
            return CommandResult.Reject("NoReviewedPolicy",
                "There is no reviewed way forward for this position yet. The match stays saved and paused.");
        }

        // Echoing the policy id back means an operator cannot accept a policy they were not shown.
        if (!string.Equals(policy.PolicyId, command.PolicyId, StringComparison.Ordinal))
            return CommandResult.Reject("PolicyMismatch", "That is not the policy offered for this position.");

        if (string.IsNullOrWhiteSpace(command.Operator))
            return CommandResult.Reject("OperatorMissing", "Accepting a rules policy must record who accepted it.");

        var restoredPhase = decision.InterruptedTurnPhase is { } interrupted &&
            interrupted != TurnPhase.RulesDecisionRequired
            ? interrupted
            : state.Lifecycle == SessionLifecycle.Setup
                ? TurnPhase.SetupTicketSelection
                : state.CurrentTurnAction == TurnAction.DrawTrainCards && state.TrainCardsTakenThisTurn == 1
                    ? TurnPhase.AwaitingSecondTrainCard
                    : TurnPhase.TurnStart;

        context.Emit(new RulesDecisionResolved(
            policy.Code, policy.PolicyId, RulesContinuations.PolicyVersion,
            command.Operator.Trim(), _time.GetUtcNow(), restoredPhase));

        ContinueAfterRulesDecision(context, policy.Code);
        return CommandResult.Accept(context.Events);
    }

    /// <summary>
    /// Resumes whatever the pause interrupted. The turn counters survive the pause, so the engine
    /// can tell a seat that still owes its second card from one whose draw had already finished.
    /// </summary>
    private void ContinueAfterRulesDecision(TransitionContext context, string code)
    {
        var state = context.State;
        if (state.Lifecycle != SessionLifecycle.Active) return;

        switch (code)
        {
            case RulesContinuations.NoLegalAction:
                EndTurn(context, state.ActiveSeatId, TurnAction.None);
                return;

            case RulesContinuations.NoSelectableSecondDraw:
                EndTurn(context, state.ActiveSeatId, TurnAction.DrawTrainCards);
                return;

            default:
                // Permission to keep a partial market is distinct from permission to disable
                // locomotive resets. Apply the remaining checks before resuming a card selection.
                if (code == RulesContinuations.PartialMarketSupply)
                {
                    MaintainMarket(context);
                    if (state.RulesDecision is not null) return;
                }

                // A market pause can interrupt a draw before its turn has finished.
                if (state.CurrentTurnAction != TurnAction.DrawTrainCards) return;

                if (state.TurnPhase == TurnPhase.TurnStart ||
                    state.TrainCardsTakenThisTurn >= Constants.TrainCardsPerDrawTurn)
                {
                    EndTurn(context, state.ActiveSeatId, TurnAction.DrawTrainCards);
                    return;
                }

                if (SecondPickPossible(state)) return;   // the seat can still take its second card

                if (HasAccepted(state, RulesContinuations.NoSelectableSecondDraw))
                {
                    EndTurn(context, state.ActiveSeatId, TurnAction.DrawTrainCards);
                    return;
                }

                context.Emit(new RulesDecisionRaised(
                    RulesContinuations.NoSelectableSecondDraw,
                    "The first train card is saved, but no legal second card is available. " +
                    "The match is paused until the depleted-supply policy is resolved."));
                return;
        }
    }

    /// <summary>The save, rebuild and resume controls, which the lifecycle gate lets through.</summary>
    private static bool IsLifecycleCommand(GameCommand command) => command is
        SaveAndPackAway or CancelPackAwayPreparation or CommitPackAwayCheckpoint or
        RecordCheckpointReadback or BeginBoardRebuild or AttestBoardRebuild or ResumePackedGame;

    // ---- Save, pack away and rebuild (DESIGN 19.8) --------------------------------------------

    /// <summary>The longest a save name may be, so a name cannot bloat the journal.</summary>
    public const int MaximumCheckpointNameLength = 120;

    /// <summary>
    /// Step 1. Suspends play and records what was interrupted. Reservations and the pending
    /// operation are deliberately kept: DESIGN 19.8 allows suspending a partial draw, an open ticket
    /// offer or an authorised placement without forcing the turn to finish.
    /// </summary>
    private CommandResult HandleSaveAndPackAway(TransitionContext context, SaveAndPackAway command)
    {
        var state = context.State;

        if (state.Lifecycle != SessionLifecycle.Active)
        {
            return CommandResult.Reject("CannotPackAwayNow",
                state.Lifecycle == SessionLifecycle.Setup
                    ? "Finish choosing opening destination tickets before saving."
                    : "A save is already in progress for this match.");
        }

        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return CommandResult.Reject("SaveNameMissing", "A saved game needs a name.");

        if (name.Length > MaximumCheckpointNameLength)
            return CommandResult.Reject("SaveNameTooLong",
                $"A saved game name is at most {MaximumCheckpointNameLength} characters.");

        context.Emit(new PackAwayRequested(
            command.Envelope.CommandId,
            CheckpointId.New(),
            name,
            state.TurnPhase,
            state.PendingClaim?.OperationId));

        return CommandResult.Accept(context.Events);
    }

    private CommandResult HandleCancelPackAway(TransitionContext context, CancelPackAwayPreparation command)
    {
        var state = context.State;

        if (state.Lifecycle != SessionLifecycle.PreparingPackAway || state.PackAwayRequest is not { } request)
            return CommandResult.Reject("NoSaveInProgress", "No save is being prepared.");

        var reason = string.IsNullOrWhiteSpace(command.Reason) ? "cancelled by the operator" : command.Reason.Trim();
        context.Emit(new PackAwayPreparationCancelled(request.RequestId, reason));
        return CommandResult.Accept(context.Events);
    }

    /// <summary>
    /// Step 6, first transaction. Freezes the source state into an immutable checkpoint. This build
    /// records a state-only target: the committed ownership map, with no photograph and no
    /// uncommitted physical progress, exactly as DESIGN 19.8 specifies for that path.
    /// </summary>
    private CommandResult HandleCommitCheckpoint(TransitionContext context, CommitPackAwayCheckpoint command)
    {
        var state = context.State;

        if (state.Lifecycle != SessionLifecycle.PreparingPackAway || state.PackAwayRequest is not { } request)
            return CommandResult.Reject("NoSaveInProgress", "No save is being prepared.");

        if (request.CheckpointId != command.CheckpointId)
            return CommandResult.Reject("CheckpointMismatch", "That checkpoint belongs to a different save request.");

        var target = PackAwayCheckpoint.TargetFrom(state);

        var checkpoint = new PackAwayCheckpoint(
            request.CheckpointId,
            state.SessionId,
            request.Name,
            _time.GetUtcNow(),
            PackAwayCheckpoint.CurrentFormatVersion,
            SourceStateVersion: command.Envelope.ExpectedStateVersion,
            SourceJournalSequence: state.JournalSequence,
            BoardRevision: state.BoardRevision,
            ProfileId: Manifest.ProfileId,
            ManifestHash: Manifest.DataHash,
            LogicalStateHash: StateHash.ComputeLogical(state),
            SuspendedTurnPhase: request.SuspendedTurnPhase,
            PendingOperationId: request.PendingOperationId,
            PhysicalTarget: target,
            PhysicalTargetHash: PackAwayCheckpoint.HashTarget(target),
            TargetProvenance: TargetProvenance.LogicalStateOnly,
            PhotoHash: null,
            Status: CheckpointStatus.CommittedAwaitingReadback);

        context.Emit(new PackAwayCheckpointCommitted(checkpoint));
        return CommandResult.Accept(context.Events);
    }

    /// <summary>
    /// Step 6, second transaction. DESIGN 19.8: a safe-to-pack result is only issued once the
    /// committed checkpoint has been read back and validated; a failure leaves the match packed and
    /// faulted rather than resuming play or reporting success.
    /// </summary>
    private CommandResult HandleCheckpointReadback(TransitionContext context, RecordCheckpointReadback command)
    {
        var state = context.State;

        if (state.Lifecycle != SessionLifecycle.PackedAway)
            return CommandResult.Reject("NotPackedAway", "Readback only completes a packed save.");

        if (state.Checkpoint is not { } checkpoint || checkpoint.CheckpointId != command.CheckpointId)
            return CommandResult.Reject("UnknownCheckpoint", "That checkpoint is not the one being validated.");

        if (checkpoint.Status == CheckpointStatus.Verified)
            return CommandResult.Reject("AlreadyVerified", "That checkpoint has already been validated.");

        if (command.Succeeded)
            context.Emit(new PackAwayCheckpointVerified(checkpoint.CheckpointId));
        else
            context.Emit(new PackAwayCheckpointFaulted(
                checkpoint.CheckpointId,
                string.IsNullOrWhiteSpace(command.FailureReason) ? "readback failed" : command.FailureReason.Trim()));

        return CommandResult.Accept(context.Events);
    }

    private CommandResult HandleBeginRebuild(TransitionContext context, BeginBoardRebuild command)
    {
        var state = context.State;

        if (state.Lifecycle is not (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding) ||
            state.Checkpoint is not { } checkpoint)
            return CommandResult.Reject("NotPackedAway", "This match is not packed away.");

        if (checkpoint.CheckpointId != command.CheckpointId)
            return CommandResult.Reject("CheckpointMismatch", "That checkpoint does not belong to this match.");

        if (checkpoint.Status != CheckpointStatus.Verified)
        {
            return CommandResult.Reject("CheckpointNotVerified",
                "That save has not been validated yet, so it cannot be rebuilt from.");
        }

        context.Emit(new BoardRebuildStarted(checkpoint.CheckpointId));
        return CommandResult.Accept(context.Events);
    }

    /// <summary>
    /// The operator's whole-target attestation. DESIGN 19.8 accepts this in place of camera
    /// agreement under the existing manual-verification policy; the echoed target hash stops an
    /// attestation being applied to a different saved arrangement.
    /// </summary>
    private CommandResult HandleAttestRebuild(TransitionContext context, AttestBoardRebuild command)
    {
        var state = context.State;

        if (state.Lifecycle != SessionLifecycle.Rebuilding || state.Checkpoint is not { } checkpoint)
            return CommandResult.Reject("NotRebuilding", "No board rebuild is in progress.");

        if (checkpoint.CheckpointId != command.CheckpointId)
            return CommandResult.Reject("CheckpointMismatch", "That attestation names a different checkpoint.");

        if (state.VerificationMode != VerificationMode.Manual)
        {
            return CommandResult.Reject("ManualVerificationNotSelected",
                "Attesting to a rebuilt board requires an explicitly selected manual verification mode.");
        }

        if (!string.Equals(checkpoint.PhysicalTargetHash, command.PhysicalTargetHash, StringComparison.Ordinal))
            return CommandResult.Reject("TargetHashMismatch", "That attestation does not match the saved board.");

        if (string.IsNullOrWhiteSpace(command.Operator))
            return CommandResult.Reject("AttestationDetailsMissing", "A rebuild attestation must record the operator.");

        context.Emit(new BoardRebuildAttested(
            checkpoint.CheckpointId, command.Operator.Trim(), checkpoint.PhysicalTargetHash, _time.GetUtcNow()));

        return CommandResult.Accept(context.Events);
    }

    /// <summary>
    /// DESIGN 19.8: Resume rechecks the logical target and restores the saved operation exactly once.
    /// In manual mode physical agreement is an operator attestation, not camera evidence.
    /// </summary>
    private CommandResult HandleResumePackedGame(TransitionContext context, ResumePackedGame command)
    {
        var state = context.State;

        if (state.Lifecycle != SessionLifecycle.Rebuilding || state.Checkpoint is not { } checkpoint)
            return CommandResult.Reject("NotRebuilding", "No board rebuild is in progress.");

        if (checkpoint.CheckpointId != command.CheckpointId)
            return CommandResult.Reject("CheckpointMismatch", "That resume names a different checkpoint.");

        if (!state.RebuildAttested)
        {
            return CommandResult.Reject("BoardNotConfirmed",
                "Confirm that the rebuilt board matches the saved position before resuming.");
        }

        // The target is immutable and the board cannot have changed underneath us without an event,
        // but the comparison is repeated here so Resume is decided on current state, not on the
        // state that was current when the attestation was given.
        var current = PackAwayCheckpoint.HashTarget(PackAwayCheckpoint.TargetFrom(state));
        if (!string.Equals(current, checkpoint.PhysicalTargetHash, StringComparison.Ordinal))
        {
            return CommandResult.Reject("TargetChanged",
                "The saved position no longer matches this match. The rebuild must be checked again.");
        }

        if (!StateHash.MatchesLogical(state, checkpoint.LogicalStateHash))
        {
            return CommandResult.Reject("LogicalStateChanged",
                "The saved game state does not match this checkpoint. Resuming could corrupt the match.");
        }

        context.Emit(new PackedGameResumed(checkpoint.CheckpointId, checkpoint.SuspendedTurnPhase));
        return CommandResult.Accept(context.Events);
    }

    // ---- Destination tickets -----------------------------------------------------------------

    private CommandResult HandleTicketSelection(TransitionContext context, CommitTicketSelection command)
    {
        var state = context.State;

        if (command.Envelope.ActorSeatId is not { } seat)
            return CommandResult.Reject("MissingActor", "A ticket selection must name the acting seat.");

        ImmutableArray<TicketId> offered;
        int minimumKeep;
        var isSetup = state.Lifecycle == SessionLifecycle.Setup;

        if (isSetup)
        {
            if (!state.SetupOffers.TryGetValue(seat, out offered))
                return CommandResult.Reject("NoSetupOffer", "This seat has already chosen its opening tickets.");

            minimumKeep = Constants.SetupTicketMinimumKeep;
        }
        else
        {
            if (state.TurnPhase != TurnPhase.AwaitingTicketKeep || state.CurrentTicketOffer is not { } offer)
                return CommandResult.Reject("NoTicketOffer", "No destination-ticket offer is open.");

            if (offer.SeatId != seat)
                return CommandResult.Reject("WrongSeat", "The open ticket offer belongs to another seat.");

            offered = offer.Offered;
            minimumKeep = offer.MinimumKeep;
        }

        var kept = command.Kept;
        if (kept.Distinct().Count() != kept.Length)
            return CommandResult.Reject("DuplicateTicket", "The same ticket was kept twice.");

        if (kept.Any(ticket => !offered.Contains(ticket)))
            return CommandResult.Reject("TicketNotOffered", "A kept ticket was not part of this offer.");

        if (kept.Length < minimumKeep)
            return CommandResult.Reject("TooFewTicketsKept", $"At least {minimumKeep} tickets must be kept.");

        var rejected = offered.Where(ticket => !kept.Contains(ticket)).ToImmutableArray();
        var returnOrder = command.ReturnOrder;
        if (!returnOrder.IsDefaultOrEmpty)
        {
            if (returnOrder.Length != rejected.Length || returnOrder.Any(ticket => !rejected.Contains(ticket)) ||
                returnOrder.Distinct().Count() != returnOrder.Length)
            {
                return CommandResult.Reject("InvalidReturnOrder",
                    "The return order must list exactly the tickets that were not kept.");
            }

            rejected = returnOrder;
        }

        context.Emit(new TicketSelectionCommitted(seat, kept, rejected, isSetup));

        if (!isSetup)
        {
            EndTurn(context, seat, TurnAction.DrawTickets);
            return CommandResult.Accept(context.Events);
        }

        if (state.SetupOffers.Count == 0)
        {
            // DESIGN 6.4: recycle every setup return together, in deterministic seat order.
            var appended = state.Seats
                .SelectMany(s => state.PendingTicketReturns.TryGetValue(s.SeatId, out var tickets)
                    ? tickets
                    : [])
                .ToImmutableArray();

            context.Emit(new SetupReturnsRecycled(appended));
            context.Emit(new SetupCompleted(state.ActiveSeatId));
            context.Emit(new TurnStarted(state.ActiveSeatId, 1));
            RaiseIfNoLegalAction(context);
        }

        return CommandResult.Accept(context.Events);
    }

    private CommandResult HandleRequestTicketOffer(TransitionContext context, RequestTicketOffer command)
    {
        var state = context.State;

        if (RejectIfNotActionableTurnStart(state, command.Envelope.ActorSeatId) is { } rejection)
            return rejection;

        if (state.TicketDeck.Count == 0)
            return CommandResult.Reject("TicketDeckEmpty", "There are no destination tickets left to draw.");

        var offered = state.TicketDeck.Take(Constants.InGameTicketOffer).ToImmutableArray();
        var minimumKeep = Math.Min(Constants.InGameTicketMinimumKeep, offered.Length);

        context.Emit(new TicketOfferCreated(state.ActiveSeatId, offered, minimumKeep));
        return CommandResult.Accept(context.Events);
    }

    // ---- Train cards -------------------------------------------------------------------------

    private CommandResult HandleSelectTrainCard(TransitionContext context, SelectTrainCard command)
    {
        var state = context.State;

        if (state.Lifecycle != SessionLifecycle.Active)
            return CommandResult.Reject("WrongPhase", "The match has not started.");

        if (command.Envelope.ActorSeatId != state.ActiveSeatId)
            return CommandResult.Reject("NotActiveSeat", "Only the active seat may draw.");

        var isSecondPick = state.TurnPhase == TurnPhase.AwaitingSecondTrainCard;
        if (state.TurnPhase != TurnPhase.TurnStart && !isSecondPick)
            return CommandResult.Reject("WrongPhase", "This seat cannot draw a train card right now.");

        if (!isSecondPick && state.CurrentTurnAction != TurnAction.None)
            return CommandResult.Reject("ActionAlreadyChosen", "This turn has already started a different action.");

        var seat = state.ActiveSeatId;
        bool endsTurn;

        if (command.Slot is { } slot)
        {
            if (slot < 0 || slot >= state.FaceUp.Count)
                return CommandResult.Reject("UnknownMarketSlot", "That market slot does not exist.");

            if (state.FaceUp[slot] is not { } card)
                return CommandResult.Reject("EmptyMarketSlot", "That market slot is empty.");

            var kind = Catalog.KindOf(card);

            // DESIGN 6.1: a face-up locomotive consumes the turn and cannot be the second pick.
            if (isSecondPick && kind == TrainCardKind.Locomotive)
            {
                return CommandResult.Reject("LocomotiveCannotBeSecondPick",
                    "A face-up locomotive cannot be taken as the second card of a turn.");
            }

            endsTurn = isSecondPick || kind == TrainCardKind.Locomotive;
            context.Emit(new FaceUpCardTaken(seat, slot, card, kind, endsTurn));
            MaintainMarket(context);
        }
        else
        {
            if (state.TrainDeck.Count == 0 && state.TrainDiscard.Count == 0)
                return CommandResult.Reject("NoCardsToDraw", "The draw pile and the discards are both empty.");

            var card = TakeTopCard(context)
                       ?? throw new InvalidOperationException("A card was expected but the supply was empty.");

            context.Emit(new BlindCardDrawn(seat, card));
            endsTurn = isSecondPick;
        }

        // A market decision raised during refill must not be overwritten by EndTurn.
        if (state.RulesDecision is not null)
            return CommandResult.Accept(context.Events);

        if (endsTurn)
            EndTurn(context, seat, TurnAction.DrawTrainCards);
        else if (!SecondPickPossible(state))
        {
            if (HasAccepted(state, RulesContinuations.NoSelectableSecondDraw))
            {
                EndTurn(context, seat, TurnAction.DrawTrainCards);
                return CommandResult.Accept(context.Events);
            }

            context.Emit(new RulesDecisionRaised(
                "NoSelectableSecondDraw",
                "The first train card is saved, but no legal second card is available. " +
                "The match is paused until the depleted-supply policy is resolved."));
        }

        return CommandResult.Accept(context.Events);
    }

    private static bool SecondPickPossible(GameState state) =>
        state.TrainDeck.Count + state.TrainDiscard.Count > 0 ||
        state.FaceUp.Any(card => card is not null && state.Catalog.KindOf(card.Value) != TrainCardKind.Locomotive);

    // ---- Route claims --------------------------------------------------------------------------

    private CommandResult HandlePlanClaim(TransitionContext context, PlanClaim command)
    {
        var state = context.State;

        if (RejectIfNotActionableTurnStart(state, command.Envelope.ActorSeatId) is { } rejection)
            return rejection;

        var seat = state.ActiveSeatId;

        if (!Manifest.TryGetRoute(command.RouteId, out var route))
            return CommandResult.Reject("UnknownRoute", "That route is not on the supported board.");

        if (state.RouteOwners.ContainsKey(route.RouteId))
            return CommandResult.Reject("RouteAlreadyClaimed", "That route already has an owner.");

        if (state.IsParallelLaneBlockedFor(route, seat))
            return CommandResult.Reject("ParallelLaneClosed", "The parallel lane rule closes that route.");

        if (state.TrainStock[seat] < route.Length)
            return CommandResult.Reject("NotEnoughTrains", "This seat does not have enough trains left.");

        if (ValidatePayment(state, seat, route, command.Payment) is { } paymentRejection)
            return paymentRejection;

        context.Emit(new ClaimPlanned(
            new OperationId(command.Envelope.CommandId.Value),
            seat,
            route.RouteId,
            route.Length,
            command.Payment,
            state.BoardRevision,
            command.Envelope.ExpectedStateVersion));

        return CommandResult.Accept(context.Events);
    }

    /// <summary>
    /// DESIGN 6.2: payments are validated for ownership, exact expenditure and route compatibility.
    /// A coloured route needs its own colour; a grey route accepts any single colour; locomotives
    /// substitute in either case.
    /// </summary>
    private CommandResult? ValidatePayment(
        GameState state, SeatId seat, RouteDefinition route, ImmutableArray<CardId> payment)
    {
        if (payment.IsDefaultOrEmpty)
            return CommandResult.Reject("PaymentMissing", "A claim must name the cards that pay for it.");

        if (payment.Distinct().Count() != payment.Length)
            return CommandResult.Reject("DuplicateCardInPayment", "The same card was offered twice.");

        if (payment.Length != route.Length)
        {
            return CommandResult.Reject("PaymentLengthMismatch",
                $"{route.Length} cards are needed for that route, not {payment.Length}.");
        }

        var available = state.AvailableCardsOf(seat).ToHashSet();
        if (payment.Any(card => !available.Contains(card)))
        {
            return CommandResult.Reject("CardNotAvailable",
                "A card in that payment is not in this seat's hand, or is already reserved.");
        }

        var colors = payment
            .Select(Catalog.KindOf)
            .Where(kind => kind != TrainCardKind.Locomotive)
            .Distinct()
            .ToArray();

        if (colors.Length > 1)
            return CommandResult.Reject("MixedColorPayment", "A route is paid with one colour plus locomotives.");

        if (route.RequiredCardKind is { } required && colors.Length == 1 && colors[0] != required)
            return CommandResult.Reject("WrongColorPayment", $"That route must be paid in {required} or locomotives.");

        return null;
    }

    private CommandResult HandleSubmitClaimEvidence(TransitionContext context, SubmitClaimEvidence command)
    {
        var state = context.State;

        if (state.PendingClaim is not { } claim)
            return CommandResult.Reject("NoPendingClaim", "There is no claim waiting for its trains.");

        if (claim.OperationId != command.OperationId)
            return CommandResult.Reject("OperationMismatch", "That evidence belongs to a different operation.");

        if (command.Envelope.ActorSeatId != claim.SeatId)
            return CommandResult.Reject("WrongSeat", "That claim belongs to another seat.");

        if (state.TurnPhase != TurnPhase.AwaitingPhysicalPlacement)
        {
            return CommandResult.Reject("WrongPhase",
                "This claim is being cancelled; restore the board instead of confirming it.");
        }

        switch (command.Evidence)
        {
            case EvidenceKind.ManualAttestation:
                if (state.VerificationMode != VerificationMode.Manual)
                    return CommandResult.Reject("ManualVerificationNotSelected",
                        "Manual attestation requires an explicitly selected manual verification mode.");
                if (string.IsNullOrWhiteSpace(command.Operator) || string.IsNullOrWhiteSpace(command.Reason))
                    return CommandResult.Reject("AttestationDetailsMissing",
                        "Manual verification must record the operator and the whole-board confirmation.");
                break;

            case EvidenceKind.CameraAutomatic:
                // Existing matches were created in Manual mode. A camera observation may establish
                // the physical placement in those matches without rewriting their saved setup.
                if (string.IsNullOrWhiteSpace(command.Operator) || string.IsNullOrWhiteSpace(command.Reason))
                    return CommandResult.Reject("CameraEvidenceDetailsMissing",
                        "Camera verification must record the detector and its evidence summary.");
                break;

            default:
                return CommandResult.Reject("UnsupportedEvidenceKind", "The placement evidence type is unknown.");
        }

        if (claim.BaseBoardRevision != state.BoardRevision)
            return CommandResult.Reject("StaleBoardRevision", "The board changed after this claim was planned.");

        var route = Manifest.Route(claim.RouteId);

        // Revalidate everything the plan depended on (DESIGN 8.3).
        if (state.RouteOwners.ContainsKey(route.RouteId))
            return CommandResult.Reject("RouteAlreadyClaimed", "That route was claimed while this one was pending.");

        if (state.IsParallelLaneBlockedFor(route, claim.SeatId))
            return CommandResult.Reject("ParallelLaneClosed", "The parallel lane rule now closes that route.");

        if (state.TrainStock[claim.SeatId] < route.Length)
            return CommandResult.Reject("NotEnoughTrains", "This seat no longer has enough trains.");

        var hand = state.HandOf(claim.SeatId).ToHashSet();
        if (claim.ReservedCards.Any(card => !hand.Contains(card)))
            return CommandResult.Reject("ReservedCardMissing", "A reserved card is no longer in the seat's hand.");

        var points = Constants.ScoreForLength(route.Length);

        if (command.Evidence == EvidenceKind.CameraAutomatic)
            context.Emit(new CameraVerificationRecorded(
                claim.OperationId, claim.SeatId, claim.RouteId,
                command.Operator, command.Reason, _time.GetUtcNow()));
        else
            context.Emit(new ManualVerificationRecorded(
                claim.OperationId, claim.SeatId, claim.RouteId,
                command.Operator, command.Reason, _time.GetUtcNow()));

        context.Emit(new ClaimCommitted(
            claim.OperationId,
            claim.SeatId,
            claim.RouteId,
            claim.ReservedCards,
            [.. claim.ReservedCards.Select(Catalog.KindOf)],
            points,
            state.TrainStock[claim.SeatId] - route.Length,
            command.Evidence));

        EndTurn(context, claim.SeatId, TurnAction.ClaimRoute);
        return CommandResult.Accept(context.Events);
    }

    private CommandResult HandleCancelPendingClaim(TransitionContext context, CancelPendingClaim command)
    {
        var state = context.State;

        if (state.PendingClaim is not { } claim)
            return CommandResult.Reject("NoPendingClaim", "There is no claim to cancel.");

        if (claim.OperationId != command.OperationId)
            return CommandResult.Reject("OperationMismatch", "That cancellation names a different operation.");

        if (command.Envelope.ActorSeatId != claim.SeatId)
            return CommandResult.Reject("WrongSeat", "That claim belongs to another seat.");

        if (!command.TrainsWerePlaced && state.VerificationMode != VerificationMode.Manual)
            return CommandResult.Reject("ManualVerificationNotSelected",
                "Releasing the reservation without camera evidence requires manual verification mode.");

        if (state.TurnPhase != TurnPhase.AwaitingPhysicalPlacement)
            return CommandResult.Reject("WrongPhase", "This claim is already being restored.");

        context.Emit(new ClaimCancellationRequested(claim.OperationId, command.TrainsWerePlaced));

        // DESIGN 8.4: with nothing placed the reservation is released at once; otherwise the
        // operator must remove the new trains first.
        if (!command.TrainsWerePlaced)
            context.Emit(new ClaimCancelled(claim.OperationId, claim.SeatId, claim.RouteId));

        return CommandResult.Accept(context.Events);
    }

    private CommandResult HandleConfirmRestored(TransitionContext context, ConfirmBeforeStateRestored command)
    {
        var state = context.State;

        if (state.PendingClaim is not { } claim)
            return CommandResult.Reject("NoPendingClaim", "There is no claim waiting to be restored.");

        if (claim.OperationId != command.OperationId)
            return CommandResult.Reject("OperationMismatch", "That confirmation names a different operation.");

        if (command.Envelope.ActorSeatId != claim.SeatId)
            return CommandResult.Reject("WrongSeat", "That claim belongs to another seat.");

        if (state.VerificationMode != VerificationMode.Manual)
            return CommandResult.Reject("ManualVerificationNotSelected",
                "Confirming restoration without camera evidence requires manual verification mode.");

        if (state.TurnPhase != TurnPhase.RestoreBeforeState)
            return CommandResult.Reject("WrongPhase", "No board restoration is in progress.");

        context.Emit(new ClaimCancelled(claim.OperationId, claim.SeatId, claim.RouteId));
        return CommandResult.Accept(context.Events);
    }

    // ---- Shared turn machinery ------------------------------------------------------------------

    private CommandResult? RejectIfNotActionableTurnStart(GameState state, SeatId? actor)
    {
        if (state.Lifecycle != SessionLifecycle.Active)
            return CommandResult.Reject("WrongPhase", "The match has not started.");

        if (actor != state.ActiveSeatId)
            return CommandResult.Reject("NotActiveSeat", "It is not this seat's turn.");

        if (state.TurnPhase != TurnPhase.TurnStart)
            return CommandResult.Reject("WrongPhase", "This seat is in the middle of another action.");

        // DESIGN 7.2 invariant 3: only one foreground game operation is active.
        if (state.PendingClaim is not null)
            return CommandResult.Reject("OperationInProgress", "A claim is still waiting for its trains.");

        if (state.CurrentTurnAction != TurnAction.None)
            return CommandResult.Reject("ActionAlreadyChosen", "This turn has already started a different action.");

        return null;
    }

    private void EndTurn(TransitionContext context, SeatId seat, TurnAction action)
    {
        var state = context.State;
        context.Emit(new TurnCompleted(seat, state.TurnNumber, action));

        // DESIGN 6.1: the trigger also gets one more turn, so this is scheduled after the
        // completed turn is recorded and before any remaining count is consumed.
        if (state.FinalRound is null && state.TrainStock[seat] <= Constants.FinalRoundTrainThreshold)
        {
            context.Emit(new FinalRoundStarted(
                seat,
                state.TurnNumber,
                state.Seats.ToImmutableDictionary(s => s.SeatId, _ => 1)));
        }

        if (state.FinalRound is { } finalRound && finalRound.IsExhausted)
        {
            context.Emit(new FinalScoringCompleted(FinalScoring.Compute(state)));
            return;
        }

        // DESIGN 6.4: the accepted pass policy must terminate. Once every seat has passed in a row,
        // no seat can do anything at all, so the match is scored rather than circling the table.
        if (state.ConsecutivePasses >= state.Seats.Length)
        {
            context.Emit(new FinalScoringCompleted(FinalScoring.Compute(state)));
            return;
        }

        var nextIndex = (state.ActiveSeatIndex + 1) % state.Seats.Length;
        context.Emit(new TurnStarted(state.Seats[nextIndex].SeatId, state.TurnNumber + 1));
        RaiseIfNoLegalAction(context);
    }

    /// <summary>DESIGN 6.4: no undocumented forced pass. The condition is saved and surfaced.</summary>
    private void RaiseIfNoLegalAction(TransitionContext context)
    {
        var state = context.State;
        var view = Projector.ProjectSeat(state, state.ActiveSeatId);

        if (LegalActionCalculator.For(view, Manifest).Any) return;

        if (HasAccepted(state, RulesContinuations.NoLegalAction))
        {
            // The disclosed policy: this seat passes. EndTurn's consecutive-pass guard is what
            // stops a table where nobody can act from circling forever.
            EndTurn(context, state.ActiveSeatId, TurnAction.None);
            return;
        }

        context.Emit(new RulesDecisionRaised(
            "NoLegalAction",
            $"{state.ActiveSeat.DisplayName} has no legal action under {Manifest.ProfileId}: " +
            "no card can be drawn, no destination tickets remain, and no route is affordable."));
    }

    // ---- Supply maintenance -----------------------------------------------------------------------

    /// <summary>
    /// Refills the market and applies the three-locomotive reset, bounding repeated refresh so an
    /// impossible supply composition pauses with its exact cause instead of hanging (DESIGN 6.4).
    /// </summary>
    private void MaintainMarket(TransitionContext context)
    {
        var state = context.State;

        for (var reset = 0; ; reset++)
        {
            FillEmptySlots(context);

            var filled = state.FaceUp.Count(card => card is not null);
            if (filled < state.FaceUp.Count && !HasAccepted(state, RulesContinuations.PartialMarketSupply))
            {
                context.Emit(new RulesDecisionRaised(
                    "PartialMarketSupply",
                    $"Only {filled} of {state.FaceUp.Count} market slots can be filled. " +
                    "The match is paused with all revealed cards preserved until the depleted-supply policy is resolved."));
                return;
            }

            // Both reset decisions disclose the same match-long policy. Once either is accepted,
            // future refills must not consume randomness or reset again, even if supply improves.
            if (HasAccepted(state, RulesContinuations.MarketResetImpossible) ||
                HasAccepted(state, RulesContinuations.MarketResetUnstable)) return;

            var locomotives = state.FaceUp.Count(
                card => card is { } value && Catalog.KindOf(value) == TrainCardKind.Locomotive);

            if (locomotives < Constants.LocomotiveMarketResetThreshold) return;

            var normalCards = state.TrainDeck.Concat(state.TrainDiscard)
                .Concat(state.FaceUp.Where(card => card is not null).Select(card => card!.Value))
                .Count(card => Catalog.KindOf(card) != TrainCardKind.Locomotive);
            if (normalCards < filled - Constants.LocomotiveMarketResetThreshold + 1)
            {
                context.Emit(new RulesDecisionRaised(
                    "MarketResetImpossible",
                    "The available train-card supply cannot form a market with fewer than " +
                    $"{Constants.LocomotiveMarketResetThreshold} locomotives. The match is paused with the exact supply preserved."));
                return;
            }

            if (reset >= MaximumMarketResets)
            {
                context.Emit(new RulesDecisionRaised(
                    "MarketResetUnstable",
                    $"The face-up market still held {locomotives} locomotives after {MaximumMarketResets} " +
                    "replacements. The bounded refresh attempt did not stabilize; the match is paused with its exact supply preserved."));
                return;
            }

            var discarded = state.FaceUp.Where(card => card is not null).Select(card => card!.Value).ToImmutableArray();
            context.Emit(new MarketReset(discarded, reset + 1));
        }
    }

    private void FillEmptySlots(TransitionContext context)
    {
        var state = context.State;

        for (var slot = 0; slot < state.FaceUp.Count; slot++)
        {
            if (state.FaceUp[slot] is not null) continue;

            if (TakeTopCard(context) is not { } card) return; // The supply is exhausted.

            context.Emit(new MarketRefilled(slot, card, Catalog.KindOf(card)));
        }
    }

    /// <summary>
    /// Peeks the top of the draw pile, reshuffling the discards first when it is empty. Returns null
    /// when no card exists anywhere in the supply.
    /// </summary>
    private CardId? TakeTopCard(TransitionContext context)
    {
        var state = context.State;

        if (state.TrainDeck.Count == 0)
        {
            if (state.TrainDiscard.Count == 0) return null;

            var order = state.TrainDiscard.ToList();
            context.Random.Shuffle(order);
            context.Emit(new DeckReshuffled([.. order], context.Random.State.ToWire()));
        }

        return state.TrainDeck.Count > 0 ? state.TrainDeck[0] : null;
    }

    /// <summary>
    /// Accumulates the events of one transaction while applying each to a detached copy, so every
    /// subsequent decision in the same transaction sees the effect of the previous event.
    /// </summary>
    private sealed class TransitionContext(GameState working, DeterministicRandom random)
    {
        public GameState State { get; } = working;

        public DeterministicRandom Random { get; } = random;

        public List<GameEvent> Events { get; } = [];

        public void Emit(GameEvent domainEvent)
        {
            Events.Add(domainEvent);
            GameReducer.Apply(State, domainEvent);
        }
    }
}
