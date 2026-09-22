using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using System.Reflection;
using System.Windows.Threading;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopCompanionBridgeTests
{
    [Fact]
    public async Task PublicUpdatesAndBoardImagesRemainResponsiveDuringAnInterceptedCameraCommand()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ProbeBridge();
        var completed = 0;
        var privateReads = 0;
        var guidance = new CompanionBoardInteraction(true, false, null);
        CompanionGuidance? physicalGuidance = new("Computer 1", "Place 2 Green trains on Duluth - Omaha (lane A).");
        var image = new CompanionBoardImage(Guid.NewGuid().ToString("N"), [0xff, 0xd8, 0xff, 0xd9]);
        CompanionBoardMap? map = new(image.Id, [new(440, 350, 1), new(445, 370, 2)]);
        var receipt = new CompanionCommandReceipt(false, false, 1, "BoardCheckRequired", "Check the board.");
        var bridge = Bridge(inner, () => null, () => true,
            () => completed++, () => privateReads++, () => Assert.Fail("The command must not fault."),
            () => guidance, async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return receipt;
            }, () => physicalGuidance, () => map, id => map?.ImageId == id ? image : null);

        var command = bridge.ExecuteAsync(new SeatId(1), Command(), token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            guidance = new(true, true, "The camera is checking the board before drawing cards.");

            var snapshot = await bridge.ReadPublicAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Same(guidance, snapshot.BoardInteraction);
            Assert.Same(physicalGuidance, snapshot.Guidance);
            Assert.Same(map, snapshot.BoardMap);
            Assert.Same(image, await bridge.ReadBoardImageAsync(image.Id, token).WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Equal("Public fixture", snapshot.Message);
            Assert.False(snapshot.CanControl);
            Assert.Null(snapshot.RevealSeatId);
            physicalGuidance = new("Computer 1", "Move Computer 1's Green scoring marker to 3.");
            var changed = await bridge.ReadPublicAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Same(physicalGuidance, changed.Guidance);
            physicalGuidance = null;
            map = null;
            var human = await bridge.ReadPublicAsync(token);
            Assert.Null(human.Guidance);
            Assert.Null(human.BoardMap);
            Assert.Null(await bridge.ReadBoardImageAsync(image.Id, token).WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.False(command.IsCompleted);
            Assert.Equal(3, inner.PublicReads);
            Assert.Equal(0, inner.PrivateReads);
            Assert.Equal(0, privateReads);
            Assert.Equal(0, inner.Commands);
        }
        finally
        {
            release.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(5), token);
        }

        Assert.Same(receipt, await command);
        Assert.Equal(1, completed);
        Assert.Equal(0, inner.Commands);
    }

    [Fact]
    public async Task PrivateReadsAndCommandsStaySerializedWhilePublicUpdatesContinue()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ProbeBridge(async (read, cancellationToken) =>
        {
            if (read != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        var begins = 0;
        var completions = 0;
        var privateReads = 0;
        var bridge = Bridge(inner, () => null, () => { begins++; return true; },
            () => completions++, () => privateReads++, () => Assert.Fail("The command must not fault."));

        var first = bridge.ReadPrivateAsync(new SeatId(1), 1, token);
        Task<CompanionPrivateSnapshot?>? second = null;
        Task<CompanionCommandReceipt>? command = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            second = bridge.ReadPrivateAsync(new SeatId(1), 1, token);
            command = bridge.ExecuteAsync(new SeatId(1), Command(), token);

            await bridge.ReadPublicAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(command.IsCompleted);
            Assert.Equal(1, inner.PrivateReads);
            Assert.Equal(1, privateReads);
            Assert.Equal(0, inner.Commands);
            Assert.Equal(0, begins);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (command is not null) await command.WaitAsync(TimeSpan.FromSeconds(5), token);
        }

        Assert.Equal(2, inner.PrivateReads);
        Assert.Equal(2, privateReads);
        Assert.Equal(1, inner.Commands);
        Assert.Equal(1, begins);
        Assert.Equal(1, completions);
    }

    private static CompanionCommand Command() => new(Guid.NewGuid().ToString("N"), "match", 1, "drawTrain");

    private static ICompanionGameBridge Bridge(ICompanionGameBridge inner, Func<Dispatcher?> dispatcher,
        Func<bool> beginCommand, Action endCommand, Action beforePrivateRead, Action commandFaulted,
        Func<CompanionBoardInteraction?>? boardInteraction = null,
        Func<SeatId, CompanionCommand, CancellationToken, Task<CompanionCommandReceipt?>>? interceptCommand = null,
        Func<CompanionGuidance?>? guidance = null,
        Func<CompanionBoardMap?>? boardMap = null,
        Func<string, CompanionBoardImage?>? boardImage = null)
    {
        var type = typeof(MainViewModel).Assembly.GetType(
            "GoldenTicket.Desktop.Services.DesktopCompanionBridge", throwOnError: true)!;
        return (ICompanionGameBridge)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
            args: [inner, dispatcher, beginCommand, endCommand, beforePrivateRead, commandFaulted,
                boardInteraction, interceptCommand, guidance, boardMap, boardImage], culture: null)!;
    }

    private sealed class ProbeBridge(Func<int, CancellationToken, Task>? privateRead = null) : ICompanionGameBridge
    {
        public int PublicReads { get; private set; }
        public int PrivateReads { get; private set; }
        public int Commands { get; private set; }

        public Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default)
        {
            PublicReads++;
            return Task.FromResult(new CompanionPublicSnapshot(null, null, false, "Public fixture", null, null, []));
        }

        public async Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            PrivateReads++;
            if (privateRead is not null) await privateRead(PrivateReads, cancellationToken);
            return null;
        }

        public Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands++;
            return Task.FromResult(new CompanionCommandReceipt(true, false, 2, null, "Saved."));
        }
    }
}
