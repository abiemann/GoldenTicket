using System.Collections.Immutable;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.Domain.Engine;

/// <summary>
/// One legal way to pay for a route. DESIGN 4.3: a player chooses among alternatives rather than
/// having the application silently spend valuable wild cards.
/// </summary>
public sealed record PaymentOption(TrainCardKind Color, int ColorCards, int Locomotives)
{
    public int Total => ColorCards + Locomotives;

    public bool IsAllLocomotives => ColorCards == 0;

    public string Describe() => IsAllLocomotives
        ? $"{Locomotives} locomotive{(Locomotives == 1 ? "" : "s")}"
        : Locomotives == 0
            ? $"{ColorCards} {Color}"
            : $"{ColorCards} {Color} + {Locomotives} locomotive{(Locomotives == 1 ? "" : "s")}";
}

/// <summary>A route this seat could claim right now, with every legal payment.</summary>
public sealed record LegalClaim(
    RouteId RouteId,
    int Length,
    TrainCardKind? RequiredCardKind,
    ImmutableArray<PaymentOption> Payments);

/// <summary>
/// The actions offered for the current state version. DESIGN 6.2: descriptors are tied to the state
/// version they were produced from, so a stale market-slot click cannot draw its replacement.
/// </summary>
public sealed record LegalActions(
    long StateVersion,
    bool CanDrawBlindTrainCard,
    ImmutableArray<int> DrawableFaceUpSlots,
    bool CanRequestTicketOffer,
    ImmutableArray<LegalClaim> Claims,
    bool MustCommitTicketSelection,
    bool MustResolvePendingClaim)
{
    public bool Any =>
        CanDrawBlindTrainCard || !DrawableFaceUpSlots.IsEmpty || CanRequestTicketOffer || !Claims.IsEmpty;
}

/// <summary>
/// Derives the legal actions from a <see cref="SeatView"/> alone. Keeping this reachable from the
/// seat projection - never from <see cref="Model.GameState"/> - is what lets DESIGN 5.3 guarantee
/// that a computer opponent cannot consult information it is not entitled to.
/// </summary>
public static class LegalActionCalculator
{
    public static LegalActions For(SeatView view, BoardManifest manifest)
    {
        var publicView = view.Public;
        var version = publicView.StateVersion;

        if (publicView.Lifecycle == SessionLifecycle.Setup)
        {
            return new LegalActions(
                version, false, [], false, [],
                MustCommitTicketSelection: !view.SetupOffer.IsEmpty,
                MustResolvePendingClaim: false);
        }

        if (!view.IsActive || publicView.Lifecycle != SessionLifecycle.Active)
            return new LegalActions(version, false, [], false, [], false, false);

        switch (publicView.TurnPhase)
        {
            case TurnPhase.AwaitingTicketKeep:
                return new LegalActions(version, false, [], false, [], view.Offer is not null, false);

            case TurnPhase.AwaitingPhysicalPlacement:
            case TurnPhase.RestoreBeforeState:
                return new LegalActions(version, false, [], false, [], false, true);

            case TurnPhase.AwaitingSecondTrainCard:
                // DESIGN 6.1: a face-up locomotive cannot be the second pick; a blind one counts normally.
                return new LegalActions(
                    version,
                    CanDrawBlindTrainCard: CanDrawBlind(publicView),
                    DrawableFaceUpSlots: FaceUpSlots(publicView, excludeLocomotives: true),
                    CanRequestTicketOffer: false,
                    Claims: [],
                    MustCommitTicketSelection: false,
                    MustResolvePendingClaim: false);

            case TurnPhase.TurnStart:
                return new LegalActions(
                    version,
                    CanDrawBlindTrainCard: CanDrawBlind(publicView),
                    DrawableFaceUpSlots: FaceUpSlots(publicView, excludeLocomotives: false),
                    CanRequestTicketOffer: publicView.TicketDeckCount > 0,
                    Claims: ClaimsFor(view, manifest),
                    MustCommitTicketSelection: false,
                    MustResolvePendingClaim: false);

            default:
                return new LegalActions(version, false, [], false, [], false, false);
        }
    }

    private static bool CanDrawBlind(PublicView view) => view.TrainDeckCount + view.TrainDiscardCount > 0;

    private static ImmutableArray<int> FaceUpSlots(PublicView view, bool excludeLocomotives)
    {
        var slots = ImmutableArray.CreateBuilder<int>();
        for (var slot = 0; slot < view.FaceUp.Length; slot++)
        {
            if (view.FaceUp[slot] is not { } kind) continue;
            if (excludeLocomotives && kind == TrainCardKind.Locomotive) continue;
            slots.Add(slot);
        }

        return slots.ToImmutable();
    }

    private static ImmutableArray<LegalClaim> ClaimsFor(SeatView view, BoardManifest manifest)
    {
        var stock = view.TrainsRemaining;
        var claims = ImmutableArray.CreateBuilder<LegalClaim>();

        foreach (var route in manifest.Routes)
        {
            if (route.Length > stock) continue;
            if (view.Public.RouteOwners.ContainsKey(route.RouteId)) continue;
            if (IsParallelLaneBlocked(view, manifest, route)) continue;

            var payments = PaymentsFor(view, route.RequiredCardKind, route.Length);
            if (payments.IsEmpty) continue;

            claims.Add(new LegalClaim(route.RouteId, route.Length, route.RequiredCardKind, payments));
        }

        return claims.ToImmutable();
    }

    /// <summary>
    /// DESIGN 6.1: no player owns both parallel routes, and with few enough players either claim
    /// closes its twin for everyone.
    /// </summary>
    private static bool IsParallelLaneBlocked(SeatView view, BoardManifest manifest, RouteDefinition route)
    {
        if (route.ParallelGroupId is null) return false;

        var closesTwin = view.Public.Seats.Length <= manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers;

        foreach (var sibling in manifest.SiblingLanesOf(route))
        {
            if (!view.Public.RouteOwners.TryGetValue(sibling.RouteId, out var owner)) continue;
            if (closesTwin || owner == view.SeatId) return true;
        }

        return false;
    }

    /// <summary>
    /// Every distinct legal payment for a route of <paramref name="length"/>. A coloured route needs
    /// its own colour; a grey route accepts any single colour. Locomotives substitute in both cases.
    /// </summary>
    public static ImmutableArray<PaymentOption> PaymentsFor(
        SeatView view, TrainCardKind? requiredCardKind, int length)
    {
        var locomotives = view.CountOf(TrainCardKind.Locomotive);

        var colors = requiredCardKind is { } required
            ? (IEnumerable<TrainCardKind>)[required]
            : Enum.GetValues<TrainCardKind>().Where(kind => kind != TrainCardKind.Locomotive);

        var seen = new HashSet<PaymentOption>();
        var options = ImmutableArray.CreateBuilder<PaymentOption>();

        void Add(PaymentOption option)
        {
            if (seen.Add(option)) options.Add(option);
        }

        foreach (var color in colors)
        {
            var available = view.CountOf(color);
            var maximumColorCards = Math.Min(available, length);

            for (var colorCards = maximumColorCards; colorCards >= 0; colorCards--)
            {
                var needed = length - colorCards;
                if (needed > locomotives) continue;

                // All-locomotive payments are the same regardless of which colour was considered.
                Add(colorCards == 0
                    ? new PaymentOption(TrainCardKind.Locomotive, 0, length)
                    : new PaymentOption(color, colorCards, needed));
            }
        }

        return options.ToImmutable();
    }

    /// <summary>
    /// Resolves a chosen payment option to concrete card instances, lowest card id first, so the
    /// same option always spends the same cards and a replay is reproducible.
    /// </summary>
    public static ImmutableArray<CardId> ResolveCards(SeatView view, PaymentOption option)
    {
        var cards = ImmutableArray.CreateBuilder<CardId>(option.Total);

        if (option.ColorCards > 0)
        {
            cards.AddRange(view.Available
                .Where(card => card.Kind == option.Color)
                .OrderBy(card => card.Id.Value)
                .Take(option.ColorCards)
                .Select(card => card.Id));
        }

        if (option.Locomotives > 0)
        {
            cards.AddRange(view.Available
                .Where(card => card.Kind == TrainCardKind.Locomotive)
                .OrderBy(card => card.Id.Value)
                .Take(option.Locomotives)
                .Select(card => card.Id));
        }

        if (cards.Count != option.Total)
            throw new InvalidOperationException("The chosen payment cannot be resolved from the available hand.");

        return cards.ToImmutable();
    }
}
