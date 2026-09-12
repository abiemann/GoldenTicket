using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GoldenTicket.Domain.Model;

/// <summary>
/// One claimed route in the saved physical target, in the words the rebuild instructions use
/// (DESIGN 19.8: "List routes by seat/colour, endpoint cities, lane, train count").
/// </summary>
public sealed record TargetRoute(RouteId RouteId, SeatId SeatId, int Length);

/// <summary>
/// A save that has been requested but has not yet produced a checkpoint (DESIGN 19.8 step 1). It
/// pins the identity, name and suspended operation of the capture, so a restart in the middle
/// resumes the same request instead of opening a second one.
/// </summary>
public sealed record PackAwayRequest(
    CommandId RequestId,
    CheckpointId CheckpointId,
    string Name,
    TurnPhase SuspendedTurnPhase,
    OperationId? PendingOperationId);

/// <summary>
/// The immutable record of one saved game (DESIGN 7.1, 19.8). Its source state, physical target and
/// image references never change after it is committed; only <see cref="Status"/> moves, from
/// committed to verified or faulted.
///
/// This build produces <see cref="TargetProvenance.LogicalStateOnly"/> checkpoints: the target is
/// the committed route ownership map, with no photograph and no uncommitted physical progress.
/// DESIGN 19.8 defines exactly that as the state-only path, and the camera milestones add the
/// verified photograph and the pending placement mask to the same protocol.
/// </summary>
public sealed record PackAwayCheckpoint(
    CheckpointId CheckpointId,
    SessionId SessionId,
    string Name,
    DateTimeOffset CreatedAt,
    int FormatVersion,
    long SourceStateVersion,
    long SourceJournalSequence,
    long BoardRevision,
    string ProfileId,
    string ManifestHash,
    string LogicalStateHash,
    TurnPhase SuspendedTurnPhase,
    OperationId? PendingOperationId,
    ImmutableArray<TargetRoute> PhysicalTarget,
    string PhysicalTargetHash,
    TargetProvenance TargetProvenance,
    string? PhotoHash,
    CheckpointStatus Status)
{
    public const int CurrentFormatVersion = 1;

    /// <summary>True once readback has proved the save can be restored (DESIGN 19.8 step 6).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSafeToPackAway => Status == CheckpointStatus.Verified;

    /// <summary>Total trains the target puts on the board, for the reconstruction guidance.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalTrainsOnBoard => PhysicalTarget.Sum(route => route.Length);

    /// <summary>Readback compares the full immutable record, including the target, not just copied hash strings.</summary>
    public bool HasSameContentAs(PackAwayCheckpoint other) =>
        (this with { PhysicalTarget = other.PhysicalTarget, Status = other.Status }) == other &&
        !PhysicalTarget.IsDefault && !other.PhysicalTarget.IsDefault &&
        PhysicalTarget.SequenceEqual(other.PhysicalTarget);

    /// <summary>
    /// A fingerprint of the physical target alone, so a rebuild can be checked against the exact
    /// arrangement that was saved rather than against whatever the match looks like later.
    /// </summary>
    public static string HashTarget(IEnumerable<TargetRoute> target)
    {
        var builder = new StringBuilder();
        foreach (var route in target
                     .OrderBy(route => route.RouteId.Value, StringComparer.Ordinal))
        {
            builder.Append(route.RouteId.Value)
                .Append('=').Append(route.SeatId.Value.ToString(CultureInfo.InvariantCulture))
                .Append('/').Append(route.Length.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Builds the target from the committed ownership map of a frozen state.</summary>
    public static ImmutableArray<TargetRoute> TargetFrom(GameState state) =>
    [
        .. state.RouteOwners
            .OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
            .Select(pair => new TargetRoute(pair.Key, pair.Value, state.Manifest.Route(pair.Key).Length))
    ];
}
