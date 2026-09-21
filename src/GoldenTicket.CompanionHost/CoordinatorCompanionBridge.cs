using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;

namespace GoldenTicket.CompanionHost;

/// <summary>Projects one current human seat and routes a deliberately small command allowlist
/// through the same durable coordinator as the laptop. The desktop wrapper supplies dispatch.</summary>
public sealed class CoordinatorCompanionBridge(
    Func<GameCoordinator?> coordinator,
    Func<CancellationToken, Task>? afterAcceptedCommand = null,
    Func<bool>? canControl = null,
    Func<CompanionResultImage?>? resultImage = null) : ICompanionGameBridge
{
    public async Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default)
    {
        var game = coordinator();
        if (game is null) return new(null, null, false, "Start or load a game on the laptop.", null, null, []);
        var view = game.Public;
        int? reveal = null;
        if (view.Lifecycle == SessionLifecycle.Setup)
        {
            var waiting = await game.SeatsAwaitingSetupSelectionAsync(cancellationToken);
            reveal = waiting.Where(id => game.Seats.Any(s => s.SeatId == id && s.Kind == SeatKind.Human))
                .Select(id => (int?)id.Value).FirstOrDefault();
        }
        else if (view.Lifecycle == SessionLifecycle.Active &&
                 view.TurnPhase != TurnPhase.RulesDecisionRequired &&
                 view.SeatOf(view.ActiveSeatId).Kind == SeatKind.Human)
            reveal = view.ActiveSeatId.Value;

        // Async setup reads must not pair an old public revision with a new handoff target.
        if (coordinator() != game || game.Public.StateVersion != view.StateVersion) reveal = null;
        var allowed = !game.StorageFaulted && (canControl?.Invoke() ?? true) && reveal is not null;
        var message = game.StorageFaulted ? "The laptop must reload the saved game before continuing."
            : view.IsGameplaySuspended ? "The game is packed or being rebuilt. Use the laptop to continue."
            : view.TurnPhase == TurnPhase.RulesDecisionRequired ? "A rules decision needs attention on the laptop."
            : view.Lifecycle == SessionLifecycle.Finished ? "Journey complete. See the final result on the laptop."
            : !allowed ? "Follow the current instructions on the laptop."
            : $"Pass this device to {view.SeatOf(new SeatId(reveal!.Value)).DisplayName}.";
        return new(view, allowed ? reveal : null, allowed, message, game.Manifest.ProfileId,
            game.Manifest.DataHash, game.Manifest.Routes.Select(r => new CompanionRoute(r.RouteId.Value,
                game.Manifest.Describe(r.RouteId), r.Length, r.RequiredCardKind?.ToString() ?? "Any color")).ToArray(),
            CurrentResultImage(game, view)?.Info);
    }

    public Task<CompanionResultImage?> ReadResultImageAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = coordinator();
        var image = game is null ? null : CurrentResultImage(game, game.Public);
        return Task.FromResult(image?.Info.Id == id ? image : null);
    }

    private CompanionResultImage? CurrentResultImage(GameCoordinator game, Domain.Projections.PublicView view)
    {
        if (view.Lifecycle != SessionLifecycle.Finished || game.StorageFaulted ||
            game.Seats.Count(s => s.Kind == SeatKind.Human) < 2 || coordinator() != game ||
            game.Public.StateVersion != view.StateVersion) return null;
        var image = resultImage?.Invoke();
        return image is not null && image.SessionId == game.SessionId.Value && image.StateVersion == view.StateVersion &&
               CompanionResultImage.IsValid(image) && coordinator() == game && game.Public.StateVersion == view.StateVersion
            ? image : null;
    }

    public async Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var game = coordinator();
        var snapshot = await ReadPublicAsync(cancellationToken);
        if (game is null || coordinator() != game || !snapshot.CanControl || snapshot.RevealSeatId != seat.Value ||
            snapshot.Game?.StateVersion != expectedVersion) return null;
        var view = await game.GetSeatViewAsync(seat, cancellationToken);
        if (view.Public.StateVersion != expectedVersion || game.Public.StateVersion != expectedVersion ||
            coordinator() != game || game.StorageFaulted || !(canControl?.Invoke() ?? true)) return null;
        var manifest = game.Manifest;
        CompanionTicket Ticket(TicketId id)
        {
            var ticket = manifest.Ticket(id);
            return new(id.Value, $"{manifest.City(ticket.CityA).DisplayName} – {manifest.City(ticket.CityB).DisplayName}", ticket.Points);
        }
        var offer = !view.SetupOffer.IsEmpty ? view.SetupOffer : view.Offer?.Offered ?? [];
        return new(view, LegalActionCalculator.For(view, manifest), view.Tickets.Select(Ticket).ToArray(),
            offer.Select(Ticket).ToArray(), !view.SetupOffer.IsEmpty
                ? manifest.RulesConstants.SetupTicketMinimumKeep : view.Offer?.MinimumKeep ?? 0);
    }

    public async Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command,
        CancellationToken cancellationToken = default)
    {
        CompanionCommandReceipt Refused(string code, string message) =>
            new(false, false, coordinator()?.Public.StateVersion ?? 0, code, message);
        if (!Guid.TryParseExact(command.CommandId, "N", out _) || command.SessionId?.Length > 100 ||
            command.RouteId?.Length > 120 || command.DetectedClaimId?.Length > 100 ||
            command.KeptTickets?.Length > 3 || command.ReturnedTickets?.Length > 3 ||
            (command.KeptTickets ?? []).Concat(command.ReturnedTickets ?? []).Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 120))
            return Refused("InvalidCommand", "That choice is not valid.");
        var game = coordinator();
        var permitted = await ReadPrivateAsync(seat, command.ExpectedStateVersion, cancellationToken);
        if (game is null || permitted is null || coordinator() != game || game.SessionId.Value != command.SessionId)
            return Refused("RefreshRequired", "Hide and reveal again to use the current turn.");
        var envelope = new CommandEnvelope(game.SessionId, new CommandId(command.CommandId), command.ExpectedStateVersion, seat);
        GameCommand? action = command.Kind switch
        {
            "drawTrain" when command.Slot is null || command.Slot is >= 0 and < 5 => new SelectTrainCard(envelope, command.Slot),
            "drawTickets" => new RequestTicketOffer(envelope),
            "keepTickets" when command.KeptTickets is not null => new CommitTicketSelection(envelope,
                [.. command.KeptTickets.Select(id => new TicketId(id))], [.. (command.ReturnedTickets ?? []).Select(id => new TicketId(id))]),
            "planClaim" when command.RouteId is not null && command.Payment is not null => Claim(),
            _ => null
        };
        GameCommand? Claim()
        {
            var route = permitted.Actions.Claims.FirstOrDefault(r => r.RouteId.Value == command.RouteId);
            if (route is null || !route.Payments.Contains(command.Payment!)) return null;
            return new PlanClaim(envelope, route.RouteId, LegalActionCalculator.ResolveCards(permitted.View, command.Payment!));
        }
        if (action is null) return Refused("ActionNotAllowed", "Choose one of the available actions. Physical verification stays on the laptop.");
        var outcome = await game.SubmitAsync(action, cancellationToken);
        if (outcome.IsAccepted && afterAcceptedCommand is not null) await afterAcceptedCommand(cancellationToken);
        return new(outcome.IsAccepted, outcome.WasDuplicate, game.Public.StateVersion,
            outcome.Result.Rejection?.Code, outcome.IsAccepted ? "Choice saved on the laptop." : outcome.Result.Rejection!.Message);
    }
}
