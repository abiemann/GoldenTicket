using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.CompanionHost;

/// <summary>The only network-to-game boundary. The desktop may dispatch all operations to
/// its UI thread. No referee state, journal, authoritative physical evidence or administrative
/// command crosses it. A public board preview is display-only.</summary>
public interface ICompanionGameBridge
{
    Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default);
    Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion,
        CancellationToken cancellationToken = default);
    Task<CompanionResultImage?> ReadResultImageAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<CompanionResultImage?>(null);
    Task<CompanionBoardImage?> ReadBoardImageAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<CompanionBoardImage?>(null);
    Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record CompanionPublicSnapshot(PublicView? Game, int? RevealSeatId, bool CanControl,
    string Message, string? ProfileId, string? ManifestHash, IReadOnlyList<CompanionRoute> Routes,
    CompanionResultImageInfo? ResultImage = null, CompanionBoardInteraction? BoardInteraction = null,
    CompanionGuidance? Guidance = null, CompanionBoardMap? BoardMap = null);
/// <summary>The current public board instruction, shared with the laptop without revealing a hand.</summary>
public sealed record CompanionGuidance(string Title, string Instruction);
/// <summary>The public upright board crop and laptop's placement cues, in 960 by 600 board coordinates.
/// A missing image means the current computer step is waiting for a usable camera preview.</summary>
public sealed record CompanionBoardMap(string? ImageId, IReadOnlyList<CompanionMapPoint> Targets);
public sealed record CompanionMapPoint(double X, double Y, int Number);
/// <summary>A bounded, transient preview of the public board. Never a screen capture or game evidence.</summary>
public sealed record CompanionBoardImage(string Id, byte[] Jpeg)
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    internal static bool IsValid(CompanionBoardImage image) =>
        Guid.TryParseExact(image.Id, "N", out _) &&
        image.Jpeg is { Length: >= 4 and <= MaximumBytes } bytes &&
        bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9;
}
/// <summary>Public camera guidance only. Legal payments and cards remain in the revealed seat view.</summary>
public sealed record CompanionBoardInteraction(bool UseCameraClaims, bool CardActionsBlocked,
    string? Message, CompanionDetectedRoute? DetectedRoute = null);
public sealed record CompanionDetectedRoute(string ProposalId, string RouteId, string Label,
    int Length, bool Ready);
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
public sealed record CompanionTicket(string Id, string Label, int Points, string? From = null, string? To = null);
public sealed record CompanionPrivateSnapshot(SeatView View, LegalActions Actions,
    IReadOnlyList<CompanionTicket> HeldTickets, IReadOnlyList<CompanionTicket> OfferedTickets,
    int MinimumKeep);
/// <summary>Explicit allowlist: drawTrain, drawTickets, keepTickets, planClaim and payDetectedRoute.
/// Detected-route payment also requires the desktop's current camera proposal and fresh evidence.</summary>
public sealed record CompanionCommand(string CommandId, string SessionId, long ExpectedStateVersion,
    string Kind, int? Slot = null, string? RouteId = null, PaymentOption? Payment = null,
    string[]? KeptTickets = null, string[]? ReturnedTickets = null, string? DetectedClaimId = null);
public sealed record CompanionCommandReceipt(bool Accepted, bool Duplicate, long StateVersion,
    string? Code, string Message, CompanionPrivateContinuation? Continuation = null);
/// <summary>A refreshed private view for the already revealed human's uninterrupted turn.</summary>
public sealed record CompanionPrivateContinuation(string Grant, long HandoffGeneration,
    CompanionPublicSnapshot Snapshot, CompanionPrivateSnapshot Data);
