using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.CompanionHost;

/// <summary>The only network-to-game boundary. The desktop may dispatch all operations to
/// its UI thread. No referee state, journal, physical evidence or administrative command crosses it.</summary>
public interface ICompanionGameBridge
{
    Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default);
    Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion,
        CancellationToken cancellationToken = default);
    Task<CompanionResultImage?> ReadResultImageAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<CompanionResultImage?>(null);
    Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record CompanionPublicSnapshot(PublicView? Game, int? RevealSeatId, bool CanControl,
    string Message, string? ProfileId, string? ManifestHash, IReadOnlyList<CompanionRoute> Routes,
    CompanionResultImageInfo? ResultImage = null);
public sealed record CompanionResultImageInfo(string Id, string FileName);
/// <summary>An in-memory public standings capture, bound to one completed game revision.</summary>
public sealed record CompanionResultImage(string SessionId, long StateVersion, CompanionResultImageInfo Info, byte[] Png)
{
    internal static bool IsValid(CompanionResultImage image) =>
        image.Info is { } info && Guid.TryParseExact(info.Id, "N", out _) &&
        info.FileName is { Length: > 4 and <= 100 } name && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        image.Png is { Length: >= 33 and <= 16 * 1024 * 1024 } bytes &&
        bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        bytes.AsSpan(8, 8).SequenceEqual(new byte[] { 0, 0, 0, 13, 73, 72, 68, 82 });
}
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
