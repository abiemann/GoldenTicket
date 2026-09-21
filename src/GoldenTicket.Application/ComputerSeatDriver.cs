using System.Collections.Immutable;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Application;

/// <summary>Diagnostics for one computer decision. Contains no private information.</summary>
public sealed record AiDecisionReport(
    SeatId SeatId,
    string DecisionKind,
    TimeSpan Elapsed,
    bool UsedFallback,
    string? Note);

/// <summary>
/// Runs computer seats through the same command pipeline a human uses. DESIGN 4.5: consecutive
/// computer card turns may continue automatically, but the driver never runs past an unresolved
/// physical claim - that needs the operator.
/// </summary>
public sealed class ComputerSeatDriver
{
    /// <summary>Stops a runaway loop if a decision is somehow always rejected.</summary>
    private const int MaximumActionsPerCall = 200;

    private readonly GameCoordinator _coordinator;
    private readonly IAiPolicy _policy;
    private readonly Dictionary<SeatId, DeterministicRandom> _streams = [];
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private Task<AiDecision>? _policyTask;

    /// <param name="aiSeed">
    /// Seeds the per-seat random streams. DESIGN 5.3 keeps these separate from the referee's deck
    /// randomness so no private knowledge can leak between seats through a shared generator, and
    /// DESIGN 5.4 keeps simulation seeds explicit and production seeds from the operating system.
    /// </param>
    public ComputerSeatDriver(GameCoordinator coordinator, IAiPolicy policy, ulong aiSeed)
    {
        _coordinator = coordinator;
        _policy = policy;

        var seatIndex = 0;
        foreach (var seat in coordinator.Seats.Where(seat => seat.IsComputer))
        {
            _streams[seat.SeatId] = new DeterministicRandom(
                DeterministicRandom.SeedFrom(aiSeed ^ (0x9E3779B97F4A7C15UL * (ulong)(++seatIndex))));
        }
    }

    /// <summary>Reports of the decisions taken, newest last. Public information only.</summary>
    public IReadOnlyList<AiDecisionReport> Reports
    {
        get { lock (_reports) return [.. _reports]; }
    }

    private readonly List<AiDecisionReport> _reports = [];

    /// <summary>
    /// Plays every computer action that is available right now and returns how many were committed.
    /// Stops as soon as a human, the operator, or a paused rules decision is needed.
    /// </summary>
    public async Task<int> AdvanceAsync(CancellationToken cancellationToken = default)
    {
        await _advanceGate.WaitAsync(cancellationToken);
        try
        {
            return await AdvanceCoreAsync(cancellationToken);
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    private async Task<int> AdvanceCoreAsync(CancellationToken cancellationToken)
    {
        var committed = 0;

        for (var guard = 0; guard < MaximumActionsPerCall; guard++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await NextActionableSeatAsync(cancellationToken) is not { } seat) break;

            var view = await _coordinator.GetSeatViewAsync(seat, cancellationToken);
            var legal = LegalActionCalculator.For(view, _coordinator.Manifest);

            var (decision, report) = await DecideAsync(view, legal, cancellationToken);
            AddReport(report);

            cancellationToken.ThrowIfCancellationRequested();
            if (ToCommand(view, decision) is not { } command) break;

            var outcome = await _coordinator.SubmitAsync(command, cancellationToken);
            if (!outcome.IsAccepted)
            {
                // DESIGN 15.5: a late or invalid result is discarded, not forced through.
                AddReport(new AiDecisionReport(
                    seat, "Rejected", TimeSpan.Zero, UsedFallback: false, outcome.Result.Rejection?.Code));
                break;
            }

            committed++;
        }

        return committed;
    }

    private void AddReport(AiDecisionReport report)
    {
        lock (_reports)
        {
            _reports.Add(report);
            if (_reports.Count > 256) _reports.RemoveRange(0, _reports.Count - 256);
        }
    }

    /// <summary>
    /// The seat a computer decision is owed to right now, or null when the driver must stop.
    /// </summary>
    private async Task<SeatId?> NextActionableSeatAsync(CancellationToken cancellationToken)
    {
        var view = _coordinator.Public;

        if (_coordinator.StorageFaulted || view.Lifecycle == SessionLifecycle.Finished) return null;
        if (view.TurnPhase == TurnPhase.RulesDecisionRequired) return null;

        // DESIGN 9.2: a save in progress, a packed game, or a rebuild disables AI submissions.
        if (view.IsGameplaySuspended) return null;

        if (view.Lifecycle == SessionLifecycle.Setup)
        {
            foreach (var seat in _coordinator.Seats.Where(seat => seat.IsComputer))
            {
                var seatView = await _coordinator.GetSeatViewAsync(seat.SeatId, cancellationToken);
                if (!seatView.SetupOffer.IsEmpty) return seat.SeatId;
            }

            return null;
        }

        // DESIGN 4.5 / 7.2 invariant 3: a claim waiting for physical trains blocks further play.
        if (view.TurnPhase is TurnPhase.AwaitingPhysicalPlacement or TurnPhase.RestoreBeforeState) return null;

        var active = view.SeatOf(view.ActiveSeatId);
        return active.Kind == SeatKind.Computer ? active.SeatId : null;
    }

    private async Task<(AiDecision Decision, AiDecisionReport Report)> DecideAsync(
        SeatView view, LegalActions legal, CancellationToken cancellationToken)
    {
        var seat = _coordinator.Seats.First(s => s.SeatId == view.SeatId);
        var budget = BudgetFor(seat.Difficulty);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // A misbehaving timed-out policy must not create unbounded background work. While it is
        // still running, use legal fallback actions. Its result never reaches the command pipeline.
        if (_policyTask is { IsCompleted: false })
            return (FallbackPolicy.Choose(view, legal),
                new AiDecisionReport(view.SeatId, "Fallback", TimeSpan.Zero, true, "prior decision still stopping"));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget.Deadline);

        try
        {
            // Give each request its own random stream: a late worker cannot mutate the next
            // decision's randomness. Task.Run also bounds a policy that blocks before returning.
            var random = new DeterministicRandom(DeterministicRandom.SeedFrom(_streams[view.SeatId].NextUInt64()));
            var token = deadline.Token;
            _policyTask = Task.Run(async () => await _policy.ChooseAsync(
                view, _coordinator.Manifest, budget, random, token), token);
            _ = _policyTask.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var decision = await _policyTask.WaitAsync(token);
            token.ThrowIfCancellationRequested();

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

            if (!IsLegalDecision(view, legal, decision))
            {
                return (FallbackPolicy.Choose(view, legal),
                    new AiDecisionReport(view.SeatId, "Fallback", elapsed, true, "policy returned no legal decision"));
            }

            return (decision, new AiDecisionReport(view.SeatId, decision.GetType().Name, elapsed, false, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // DESIGN 15.5: on timeout, use a validated legal candidate rather than stalling.
            return (FallbackPolicy.Choose(view, legal),
                new AiDecisionReport(
                    view.SeatId, "Fallback", System.Diagnostics.Stopwatch.GetElapsedTime(started), true,
                    "decision budget expired"));
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (FallbackPolicy.Choose(view, legal),
                new AiDecisionReport(
                    view.SeatId, "Fallback", System.Diagnostics.Stopwatch.GetElapsedTime(started), true,
                    exception.GetType().Name));
        }
    }

    /// <summary>DESIGN 15.3 initial decision budgets. Latency targets, not strength claims.</summary>
    public static DecisionBudget BudgetFor(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Relaxed => new DecisionBudget(difficulty, TimeSpan.FromMilliseconds(750)),
        AiDifficulty.Standard => new DecisionBudget(difficulty, TimeSpan.FromSeconds(2)),
        AiDifficulty.Challenging => new DecisionBudget(difficulty, TimeSpan.FromSeconds(5)),
        AiDifficulty.Aggressive => new DecisionBudget(difficulty, TimeSpan.FromSeconds(2)),
        _ => new DecisionBudget(difficulty, TimeSpan.FromSeconds(2)),
    };

    private static bool IsLegalDecision(SeatView view, LegalActions legal, AiDecision decision)
    {
        return decision switch
        {
            AiKeepTickets keep => legal.MustCommitTicketSelection &&
                !keep.Kept.IsDefault && keep.Kept.Length >= (view.Offer?.MinimumKeep ?? 2) &&
                keep.Kept.Distinct().Count() == keep.Kept.Length &&
                keep.Kept.All(ticket => (!view.SetupOffer.IsEmpty ? view.SetupOffer : view.Offer?.Offered ?? []).Contains(ticket)),
            AiDrawTrainCard { Slot: null } => legal.CanDrawBlindTrainCard,
            AiDrawTrainCard { Slot: { } slot } => legal.DrawableFaceUpSlots.Contains(slot),
            AiDrawTickets => legal.CanRequestTicketOffer,
            AiClaimRoute claim => legal.Claims.Any(candidate => candidate.RouteId == claim.RouteId &&
                candidate.Payments.Contains(claim.Payment)),
            _ => false,
        };
    }

    /// <summary>
    /// Turns a decision into a command addressed to the exact state version it was made against, so
    /// a decision that arrived late is refused by the ordinary version check.
    /// </summary>
    private GameCommand? ToCommand(SeatView view, AiDecision decision)
    {
        var envelope = new CommandEnvelope(
            _coordinator.SessionId, CommandId.New(), view.Public.StateVersion, view.SeatId);

        return decision switch
        {
            AiKeepTickets keep => new CommitTicketSelection(envelope, keep.Kept, []),
            AiDrawTrainCard draw => new SelectTrainCard(envelope, draw.Slot),
            AiDrawTickets => new RequestTicketOffer(envelope),
            AiClaimRoute claim => new PlanClaim(
                envelope, claim.RouteId, LegalActionCalculator.ResolveCards(view, claim.Payment)),
            _ => null,
        };
    }
}

/// <summary>
/// The simple legal policy the coordinator falls back to when a search times out or fails
/// (DESIGN 15.5). It never stalls the match and never needs private information beyond the seat's
/// own view.
/// </summary>
public static class FallbackPolicy
{
    public static AiDecision Choose(SeatView view, LegalActions legal)
    {
        if (legal.MustCommitTicketSelection)
        {
            var offered = !view.SetupOffer.IsEmpty ? view.SetupOffer : view.Offer?.Offered ?? [];
            var minimum = view.Offer?.MinimumKeep ?? 2;
            return new AiKeepTickets([.. offered.Take(Math.Max(1, Math.Min(minimum, offered.Length)))]);
        }

        if (legal.CanDrawBlindTrainCard) return new AiDrawTrainCard(null);
        if (!legal.DrawableFaceUpSlots.IsEmpty) return new AiDrawTrainCard(legal.DrawableFaceUpSlots[0]);

        if (!legal.Claims.IsEmpty)
        {
            var claim = legal.Claims[0];
            return new AiClaimRoute(claim.RouteId, claim.Payments[0]);
        }

        if (legal.CanRequestTicketOffer) return new AiDrawTickets();

        return new AiNoDecision("No legal action is available to this seat.");
    }
}
