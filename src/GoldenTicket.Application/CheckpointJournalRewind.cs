using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Application;

/// <summary>Finds a completed checkpoint boundary in a verified journal.</summary>
public static class CheckpointJournalRewind
{
    public static PackAwayCheckpoint? LatestVerified(
        IReadOnlyList<JournaledEvent> journal, CheckpointId? excludedCheckpointId = null)
        => VerifiedNewestFirst(journal).FirstOrDefault(checkpoint => checkpoint.CheckpointId != excludedCheckpointId);

    /// <summary>
    /// Enumerates digital checkpoints after journal verification. Callers must also verify any
    /// required save attachments before treating a checkpoint as a completed user save.
    /// </summary>
    public static IEnumerable<PackAwayCheckpoint> VerifiedNewestFirst(IReadOnlyList<JournaledEvent> journal)
    {
        var seen = new HashSet<CheckpointId>();
        for (var index = journal.Count - 1; index >= 0; index--)
        {
            if (journal[index].Event is not PackAwayCheckpointVerified verified ||
                !seen.Add(verified.CheckpointId)) continue;
            PackAwayCheckpoint? checkpoint = null;
            for (var committedIndex = index - 1; committedIndex >= 0; committedIndex--)
            {
                if (journal[committedIndex].Event is PackAwayCheckpointCommitted committed &&
                    committed.Checkpoint.CheckpointId == verified.CheckpointId)
                {
                    checkpoint = committed.Checkpoint with { Status = CheckpointStatus.Verified };
                    break;
                }
            }
            yield return checkpoint ?? throw new SessionIntegrityException("A verified save has no committed checkpoint.");
        }
    }

    public static RestoredSession AtVerifiedCheckpoint(
        IReadOnlyList<JournaledEvent> journal, CheckpointId checkpointId,
        BoardManifest manifest, CardCatalog catalog)
    {
        var boundary = -1;
        for (var index = 0; index < journal.Count; index++)
            if (journal[index].Event is PackAwayCheckpointVerified verified &&
                verified.CheckpointId == checkpointId)
                boundary = index;
        if (boundary < 0)
            throw new InvalidOperationException("That checkpoint is not a verified saved game.");
        if (boundary + 1 < journal.Count &&
            journal[boundary + 1].StateVersion == journal[boundary].StateVersion)
            throw new SessionIntegrityException("The selected save is not at a complete transaction boundary.");

        var prefix = journal.Take(boundary + 1).ToArray();
        var state = GameReducer.Rebuild(manifest, catalog, prefix);
        if (state.Checkpoint is not { Status: CheckpointStatus.Verified } checkpoint ||
            checkpoint.CheckpointId != checkpointId || state.Lifecycle != SessionLifecycle.PackedAway ||
            state.JournalSequence != prefix.Length)
            throw new SessionIntegrityException("The selected save cannot be restored to a packed game.");
        var problems = InvariantChecker.Check(state);
        if (problems.Count > 0)
            throw new SessionIntegrityException("The selected save failed integrity checks.");
        return new RestoredSession(state, prefix);
    }
}
