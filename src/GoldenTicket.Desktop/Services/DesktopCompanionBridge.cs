using System.Windows.Threading;
using GoldenTicket.CompanionHost;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.Services;

/// <summary>One serialized path from the HTTP server to the desktop coordinator. Local UI
/// operations remain responsible for their existing busy/storage gates; remote mutations set the
/// same gate for their entire durable command and AI continuation.</summary>
internal sealed class DesktopCompanionBridge(
    ICompanionGameBridge inner,
    Func<Dispatcher?> dispatcher,
    Func<bool> beginCommand,
    Action endCommand,
    Action beforePrivateRead,
    Action commandFaulted,
    Func<CompanionBoardInteraction?>? boardInteraction = null,
    Func<SeatId, CompanionCommand, CancellationToken, Task<CompanionCommandReceipt?>>? interceptCommand = null,
    Func<CompanionGuidance?>? guidance = null,
    Func<CompanionBoardMap?>? boardMap = null,
    Func<string, CompanionBoardImage?>? boardImage = null) : ICompanionGameBridge
{
    private readonly SemaphoreSlim _serial = new(1, 1);

    public Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default) =>
        OnDispatcherAsync(async () =>
        {
            var snapshot = await inner.ReadPublicAsync(cancellationToken);
            return snapshot with
            {
                BoardInteraction = boardInteraction?.Invoke(), Guidance = guidance?.Invoke(),
                BoardMap = boardMap?.Invoke()
            };
        }, cancellationToken);

    public Task<CompanionBoardImage?> ReadBoardImageAsync(string id, CancellationToken cancellationToken = default) =>
        OnDispatcherAsync(() => Task.FromResult(boardImage?.Invoke(id)), cancellationToken);

    public Task<CompanionResultImage?> ReadResultImageAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync(() => inner.ReadResultImageAsync(id, cancellationToken), cancellationToken);

    public Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            beforePrivateRead();
            return await inner.ReadPrivateAsync(seat, expectedVersion, cancellationToken);
        }, cancellationToken);

    public Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            if (!beginCommand())
                return new CompanionCommandReceipt(false, false, command.ExpectedStateVersion,
                    "LaptopBusy", "Wait for the laptop, then hide and reveal your hand again.");
            try
            {
                // Keep the authorization cancellation alive until the coordinator admits the
                // command. Its durable command id handles retry; uncertain writes remain governed
                // by the coordinator's storage-fault protection.
                if (interceptCommand is not null &&
                    await interceptCommand(seat, command, cancellationToken) is { } intercepted)
                    return intercepted;
                cancellationToken.ThrowIfCancellationRequested();
                return await inner.ExecuteAsync(seat, command, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                commandFaulted();
                throw;
            }
            finally { endCommand(); }
        }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken);
        try { return await OnDispatcherAsync(action, cancellationToken); }
        finally { _serial.Release(); }
    }

    // Public updates remain responsive during durable commands and between-turn board checks.
    // The dispatcher still protects UI-owned state; only private reads and mutations serialize.
    private async Task<T> OnDispatcherAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ui = dispatcher();
        if (ui is { HasShutdownStarted: true } || ui is { HasShutdownFinished: true })
            throw new OperationCanceledException("The desktop is closing.");
        return ui is not null && !ui.CheckAccess()
            ? await ui.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task.Unwrap()
            : await action();
    }
}
