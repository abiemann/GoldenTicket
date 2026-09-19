using System.IO;
using System.Security.Cryptography;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private async Task<PackAwayCheckpoint?> FindLatestCompletedGameSaveAsync(SessionId sessionId)
    {
        var restored = await _store.RestoreAsync(sessionId, _manifest, _catalog, CancellationToken.None);
        foreach (var checkpoint in CheckpointJournalRewind.VerifiedNewestFirst(restored.Journal))
        {
            try
            {
                var attachment = await _checkpointPhotoStore.ReadReferenceAsync(checkpoint);
                if (attachment is null) continue;
                CryptographicOperations.ZeroMemory(attachment.PngBytes);
                return checkpoint;
            }
            catch (InvalidDataException)
            {
                // The immutable photo envelope is durable completion evidence. A missing or
                // invalid envelope cannot complete Save Game, even after an application restart.
                // Other I/O failures propagate: an unreadable drive must never authorize deletion.
            }
        }
        return null;
    }
}
