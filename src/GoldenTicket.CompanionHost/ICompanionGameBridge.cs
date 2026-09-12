using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.CompanionHost;

/// <summary>The only network-to-game boundary. The desktop may dispatch all three operations to
/// its UI thread. No referee state, journal, physical evidence or administrative command crosses it.</summary>
public interface ICompanionGameBridge
{
    Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default);
    Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion,
        CancellationToken cancellationToken = default);
    Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record CompanionPublicSnapshot(PublicView? Game, int? RevealSeatId, bool CanControl,
    string Message, string? ProfileId, string? ManifestHash, IReadOnlyList<CompanionRoute> Routes);
public sealed record CompanionRoute(string Id, string Label, int Length, string Color);
public sealed record CompanionTicket(string Id, string Label, int Points);
public sealed record CompanionPrivateSnapshot(SeatView View, LegalActions Actions,
    IReadOnlyList<CompanionTicket> HeldTickets, IReadOnlyList<CompanionTicket> OfferedTickets,
    int MinimumKeep);
/// <summary>Explicit allowlist: drawTrain, drawTickets, keepTickets and planClaim. All others rejected.</summary>
public sealed record CompanionCommand(string CommandId, string SessionId, long ExpectedStateVersion,
    string Kind, int? Slot = null, string? RouteId = null, PaymentOption? Payment = null,
    string[]? KeptTickets = null, string[]? ReturnedTickets = null);
public sealed record CompanionCommandReceipt(bool Accepted, bool Duplicate, long StateVersion,
    string? Code, string Message);
