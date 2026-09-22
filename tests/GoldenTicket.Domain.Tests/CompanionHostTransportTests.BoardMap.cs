using System.Net;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.CompanionHost;

namespace GoldenTicket.Domain.Tests;

public partial class CompanionHostTransportTests
{
    [Fact]
    public async Task BoardPreviewRequiresCurrentControllerButNoPrivateRevealAndDoesNotCache()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ContinuationHost.Create(await CreateActiveContinuationGame(token), token);
        var image = MakeBoardImage();
        var reads = 0;
        host.Bridge.BoardImageReader = (id, _) =>
        {
            reads++;
            return Task.FromResult<CompanionBoardImage?>(id == image.Id ? image : null);
        };
        var path = "/api/board-image/" + image.Id;
        host.Client.DefaultRequestHeaders.Remove("X-GoldenTicket-Tab");
        using var noTab = await host.Client.GetAsync(path, token);
        Assert.Equal(HttpStatusCode.Unauthorized, noTab.StatusCode);
        Assert.Equal(0, reads);
        host.Client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", host.Credentials.Tab);
        host.Server.InvalidatePrivateGrants();
        using var received = await host.Client.GetAsync(path, token);
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        Assert.Equal("image/jpeg", received.Content.Headers.ContentType?.MediaType);
        Assert.True(received.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", received.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Null(received.Content.Headers.ContentDisposition);
        Assert.Equal(image.Jpeg, await received.Content.ReadAsByteArrayAsync(token));
        using var missing = await host.Client.GetAsync("/api/board-image/" + Guid.NewGuid().ToString("N"), token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var beforeInvalid = reads;
        using var invalidId = await host.Client.GetAsync("/api/board-image/not-an-image", token);
        Assert.Equal(HttpStatusCode.NotFound, invalidId.StatusCode);
        Assert.Equal(beforeInvalid, reads);
        host.Server.RevokeController();
        using var revoked = await host.Client.GetAsync(path, token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("not-jpeg")]
    [InlineData("oversize")]
    public async Task InvalidBoardPreviewIsNotReturned(string problem)
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ContinuationHost.Create(await CreateActiveContinuationGame(token), token);
        var original = MakeBoardImage();
        var image = problem switch
        {
            "mismatch" => original with { Id = Guid.NewGuid().ToString("N") },
            "not-jpeg" => original with { Jpeg = new byte[128] },
            _ => original with { Jpeg = new byte[CompanionBoardImage.MaximumBytes + 1] }
        };
        host.Bridge.BoardImageReader = (_, _) => Task.FromResult<CompanionBoardImage?>(image);
        using var response = await host.Client.GetAsync("/api/board-image/" + original.Id, token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(token));
    }

    [Fact]
    public async Task RevocationDuringBoardReadCannotReturnThePreview()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ContinuationHost.Create(await CreateActiveContinuationGame(token), token);
        var image = MakeBoardImage();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<CompanionBoardImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Bridge.BoardImageReader = async (_, cancellationToken) =>
        {
            entered.SetResult();
            return await finish.Task.WaitAsync(cancellationToken);
        };
        var download = host.Client.GetAsync("/api/board-image/" + image.Id, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        host.Server.RevokeController();
        finish.SetResult(image);
        using var response = await download;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(token));
    }

    [Fact]
    public async Task MapFramesAndCorrectedDotsPushWithoutChangingTheGameRevision()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        var image = MakeBoardImage();
        host.Bridge.BoardMap = new(image.Id, [new(526, 338, 1), new(518, 367, 2)]);
        using var response = await host.OpenEvents(token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var initial = await ReadEvent(reader, token);
        var map = initial.Data.GetProperty("snapshot").GetProperty("boardMap");
        Assert.Equal(image.Id, map.GetProperty("imageId").GetString());
        Assert.Equal(2, map.GetProperty("targets").GetArrayLength());
        Assert.DoesNotContain(Convert.ToBase64String(image.Jpeg), initial.Data.GetRawText());

        var nextImage = Guid.NewGuid().ToString("N");
        host.Bridge.BoardMap = new(nextImage, [new(518, 367, 2)]);
        host.Server.NotifyGameChanged();
        var update = (await ReadEvent(reader, token)).Data.GetProperty("snapshot");
        Assert.Equal(game.Public.StateVersion, update.GetProperty("game").GetProperty("stateVersion").GetInt64());
        Assert.Equal(nextImage, update.GetProperty("boardMap").GetProperty("imageId").GetString());
        Assert.Equal(2, update.GetProperty("boardMap").GetProperty("targets")[0].GetProperty("number").GetInt32());
        host.Bridge.BoardMap = null;
        host.Server.NotifyGameChanged();
        var cleared = (await ReadEvent(reader, token)).Data.GetProperty("snapshot");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cleared.GetProperty("boardMap").ValueKind);
    }

    private static CompanionBoardImage MakeBoardImage()
    {
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96,
            PixelFormats.Bgr24, null, new byte[12], 6)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new(Guid.NewGuid().ToString("N"), stream.ToArray());
    }
}
