using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Domain;

namespace GoldenTicket.Domain.Tests;

public sealed class CompanionBoardMapPublisherTests
{
    [Fact]
    public void Publisher_retains_two_images_and_clears_them_when_the_preview_disappears()
    {
        var encodes = 0;
        var publisher = new CompanionBoardMapPublisher(TestManifest.Manifest,
            _ => Task.FromResult(Preview(++encodes)), TimeSpan.Zero, () => null);
        var context = new CompanionBoardMapContext(new SessionId("map-session"), new SeatId(1));
        var firstFrame = Frame();
        var firstTarget = new CompanionMapPoint(12, 34, 1);
        publisher.Update(context, firstFrame, [firstTarget]);
        var first = Assert.IsType<CompanionBoardImage>(publisher.CurrentImage);
        Assert.Equal(new[] { firstTarget }, publisher.BoardMap!.Targets);

        var correctedTarget = new CompanionMapPoint(56, 78, 2);
        publisher.Update(context, firstFrame, [correctedTarget]);
        Assert.Equal(1, encodes);
        Assert.Equal(first.Id, publisher.BoardMap!.ImageId);
        Assert.Equal(new[] { correctedTarget }, publisher.BoardMap.Targets);

        publisher.Update(context, Frame(), [correctedTarget]);
        var second = Assert.IsType<CompanionBoardImage>(publisher.CurrentImage);
        publisher.Update(context, Frame(), [correctedTarget]);
        var third = Assert.IsType<CompanionBoardImage>(publisher.CurrentImage);
        Assert.Equal(3, encodes);
        Assert.Null(publisher.ReadImage(first.Id));
        Assert.Same(second, publisher.ReadImage(second.Id));
        Assert.Same(third, publisher.ReadImage(third.Id));

        publisher.Update(context, null, [correctedTarget]);
        Assert.Null(publisher.CurrentImage);
        Assert.Null(publisher.BoardMap!.ImageId);
        Assert.Empty(publisher.BoardMap.Targets);
        Assert.Empty(publisher.BoardMap.Cities!);
        Assert.Null(publisher.ReadImage(second.Id));
        Assert.Null(publisher.ReadImage(third.Id));
    }

    [Fact]
    public async Task Completion_from_an_old_session_cannot_publish_into_the_new_session()
    {
        var firstEncode = new TaskCompletionSource<CompanionBoardImageEncoder.Preview>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encodes = 0;
        var publisher = new CompanionBoardMapPublisher(TestManifest.Manifest,
            _ => ++encodes == 1 ? firstEncode.Task : Task.FromResult(Preview(encodes)),
            TimeSpan.Zero, () => null);
        publisher.Changed += (_, _) => completed.TrySetResult();
        var firstContext = new CompanionBoardMapContext(new SessionId("first"), new SeatId(1));
        var secondContext = new CompanionBoardMapContext(new SessionId("second"), new SeatId(2));
        var nextFrame = Frame();

        publisher.Update(firstContext, Frame(), []);
        publisher.Update(secondContext, nextFrame, [new(20, 30, 1)]);
        Assert.Null(publisher.CurrentImage);
        Assert.Equal(1, encodes);

        firstEncode.SetResult(Preview(1));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal<CompanionBoardMapContext?>(secondContext, publisher.Context);
        Assert.Null(publisher.CurrentImage);
        Assert.Null(publisher.BoardMap!.ImageId);

        publisher.Update(secondContext, nextFrame, [new(20, 30, 1)]);
        Assert.Equal(2, encodes);
        Assert.NotNull(publisher.CurrentImage);
        Assert.Equal(publisher.CurrentImage.Id, publisher.BoardMap!.ImageId);
    }

    [Fact]
    public void Failed_encode_leaves_a_waiting_map_and_a_new_frame_can_retry()
    {
        var encodes = 0;
        var publisher = new CompanionBoardMapPublisher(TestManifest.Manifest,
            _ => ++encodes == 1
                ? Task.FromException<CompanionBoardImageEncoder.Preview>(new InvalidOperationException("bad frame"))
                : Task.FromResult(Preview(encodes)), TimeSpan.Zero, () => null);
        var context = new CompanionBoardMapContext(new SessionId("retry"), new SeatId(1));
        publisher.Update(context, Frame(), []);
        Assert.Null(publisher.CurrentImage);
        Assert.Null(publisher.BoardMap!.ImageId);

        publisher.Update(context, Frame(), []);
        Assert.Equal(2, encodes);
        Assert.Equal(publisher.CurrentImage!.Id, publisher.BoardMap!.ImageId);
    }

    private static CompanionBoardImageEncoder.Preview Preview(int number) =>
        new([(byte)number], [new("city", "City", number, number)]);

    private static BitmapSource Frame()
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 1, 2, 3, 255 }, 4);
        source.Freeze();
        return source;
    }
}
