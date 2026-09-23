using GoldenTicket.Desktop.Services;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class SavedBoardRestoreSessionTests
{
    [Fact]
    public void Completion_belongs_to_one_checkpoint_and_requires_a_new_check_after_failure()
    {
        var session = new SavedBoardRestoreSession("checkpoint-a", [], [], [], null);
        Assert.Equal("checkpoint-a", session.CheckpointId);
        Assert.False(session.TryBeginCompletion());

        var first = session.Observe(Analysis(1));
        Assert.Equal(SavedBoardRestoreStage.CheckingTrains, first?.Observation.Stage);
        Assert.False(session.TryBeginCompletion());
        var confirmed = session.Observe(Analysis(2));
        Assert.Equal(SavedBoardRestoreStage.Confirmed, confirmed?.Observation.Stage);
        Assert.True(session.TryBeginCompletion());
        Assert.False(session.TryBeginCompletion());
        Assert.Null(session.Observe(Analysis(3)));

        session.ResetAfterFailedCompletion();
        Assert.False(session.IsCompleting);
        Assert.False(session.TryBeginCompletion());
        Assert.Equal(SavedBoardRestoreStage.CheckingTrains,
            session.Observe(Analysis(4))?.Observation.Stage);
    }

    [Fact]
    public void Missing_train_target_is_derived_from_the_frozen_checkpoint()
    {
        var routeId = new RouteId("atlanta--raleigh--a");
        var target = new TargetRoute(routeId, new SeatId(1), 2);
        var session = new SavedBoardRestoreSession("checkpoint-b", [],
            [new BoardInventoryRoute(routeId.Value, MarkerColor.Blue, 2)], [target], null);

        session.Observe(Analysis(1));
        var missing = session.Observe(Analysis(2));
        Assert.Equal(BoardInventoryState.MissingTrains, missing?.Observation.Inventory?.State);
        Assert.True(missing?.ShouldUpdateTarget);
        Assert.Equal(routeId, missing?.Target?.RouteId);
        Assert.Equal(2, missing?.Target?.TrainCount);
        Assert.Equal(3, missing?.Target?.SlotMask);

        var stale = session.Observe(Analysis(2));
        Assert.Equal(SavedBoardRestoreStage.WaitingForCamera, stale?.Observation.Stage);
        Assert.False(stale?.ShouldUpdateTarget);
    }

    private static GameTableAnalysis Analysis(long sequence)
    {
        var frame = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
            sequence, 1, DateTimeOffset.UtcNow.AddSeconds(sequence * 1.1));
        return new GameTableAnalysis(frame, [], [], 1, 1, "synthetic-test-model");
    }
}
