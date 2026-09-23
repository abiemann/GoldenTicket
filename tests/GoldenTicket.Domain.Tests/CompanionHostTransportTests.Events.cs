using System.Net.Http;
using System.Text.Json;
using GoldenTicket.CompanionHost;

namespace GoldenTicket.Domain.Tests;

public partial class CompanionHostTransportTests
{
    [Fact]
    public async Task PendingStreamReceivesApprovalWithoutFetchingOrExposingTheGameBeforeApproval()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        host.Server.NewPairingCode();
        var tab = Guid.NewGuid().ToString("N");
        var pending = host.Server.Authority.RequestPair(host.Server.Authority.PairingCode, tab, "New tablet")!.Value;
        var reads = host.Bridge.PublicReads;
        using var response = await host.OpenEvents(token, pending.Session, tab);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var waiting = await ReadEvent(reader, token);
        Assert.False(waiting.Data.GetProperty("paired").GetBoolean());
        Assert.True(waiting.Data.GetProperty("pending").GetBoolean());
        Assert.False(waiting.Data.TryGetProperty("snapshot", out _));
        Assert.False(waiting.Data.TryGetProperty("csrf", out _));
        Assert.Equal(reads, host.Bridge.PublicReads);

        Assert.True(host.Server.ApprovePendingController());
        var approved = await ReadEvent(reader, token);
        Assert.Equal("session", approved.Name);
        Assert.True(approved.Data.GetProperty("paired").GetBoolean());
        Assert.False(approved.Data.GetProperty("pending").GetBoolean());
        Assert.Equal(game.SessionId.Value, approved.Data.GetProperty("snapshot").GetProperty("game")
            .GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task EventStreamPushesSameRevisionGuidanceAndIdleHeartbeatsDoNotReadTheGame()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        using var response = await host.OpenEvents(token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var initial = await ReadEvent(reader, token);
        Assert.Equal("session", initial.Name);
        Assert.True(initial.Data.GetProperty("paired").GetBoolean());
        Assert.False(initial.Data.GetProperty("pending").GetBoolean());
        Assert.Equal("16", initial.Data.GetProperty("assetsVersion").GetString());
        Assert.False(initial.Data.TryGetProperty("data", out _));
        Assert.DoesNotContain("heldTickets", initial.Data.GetRawText());
        Assert.DoesNotContain("offeredTickets", initial.Data.GetRawText());
        Assert.DoesNotContain("\"grant\"", initial.Data.GetRawText());
        var revision = initial.Data.GetProperty("snapshot").GetProperty("game").GetProperty("stateVersion").GetInt64();

        host.Bridge.Guidance = new("Computer 1", "Place 2 Green trains on Calgary - Helena.");
        host.Server.NotifyGameChanged();
        var updated = await ReadEvent(reader, token);
        Assert.Equal("session", updated.Name);
        Assert.Equal(revision, updated.Data.GetProperty("snapshot").GetProperty("game").GetProperty("stateVersion").GetInt64());
        Assert.Equal(host.Bridge.Guidance.Instruction, updated.Data.GetProperty("snapshot").GetProperty("guidance").GetProperty("instruction").GetString());
        var reads = host.Bridge.PublicReads;
        var heartbeat = await ReadEvent(reader, token);
        Assert.Equal("heartbeat", heartbeat.Name);
        Assert.Equal(reads, host.Bridge.PublicReads);
        Assert.Equal(1, host.Server.EventSubscriberCount);
    }

    [Fact]
    public async Task StreamSurvivesCommandsButReconnectRevokesTheOldPrivateGrant()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        using var first = await host.OpenEvents(token);
        using var firstReader = new StreamReader(await first.Content.ReadAsStreamAsync(token));
        await ReadEvent(firstReader, token);
        var grant = await host.Reveal(token);
        await ReadEvent(firstReader, token); // authorization changed after reveal
        var receipt = await host.Send(grant, host.Command("drawTrain"), token);
        Assert.NotNull(receipt.Continuation);
        var change = await ReadEvent(firstReader, token);
        Assert.Equal("session", change.Name);
        Assert.Equal(receipt.StateVersion, change.Data.GetProperty("snapshot").GetProperty("game").GetProperty("stateVersion").GetInt64());
        using var replacement = await host.OpenEvents(token);
        using var replacementReader = new StreamReader(await replacement.Content.ReadAsStreamAsync(token));
        await ReadEvent(replacementReader, token);
        Assert.Equal(1, host.Server.EventSubscriberCount);
        Assert.False(host.Server.Authority.ValidateGrant(host.Credentials, receipt.Continuation.Grant, 1,
            game.SessionId.Value, receipt.StateVersion));
        var fresh = await host.Reveal(token);
        Assert.True(host.Server.Authority.ValidateGrant(host.Credentials, fresh, 1, game.SessionId.Value, receipt.StateVersion));
        first.Dispose();
        // Closing the superseded socket must not invalidate the replacement's grant.
        var authorization = await ReadEvent(replacementReader, token);
        Assert.Equal("session", authorization.Name);
        Assert.True(host.Server.Authority.ValidateGrant(host.Credentials, fresh, 1, game.SessionId.Value, receipt.StateVersion));
        host.Server.RevokeController();
        var revoked = await ReadEvent(replacementReader, token);
        Assert.False(revoked.Data.GetProperty("paired").GetBoolean());
        Assert.False(revoked.Data.TryGetProperty("snapshot", out _));
    }

    [Fact]
    public void EventSubscribersCoalesceBurstsAreBoundedAndStopCleanly()
    {
        var events = new CompanionEventSubscriptions();
        var subscriptions = Enumerable.Range(0, 15).Select(i => events.Open(i.ToString())!).ToArray();
        Assert.Null(events.Open("overflow"));
        using var approved = events.Open("approved", approvedController: true)!;
        Assert.Equal(16, events.Count);
        for (var i = 0; i < 1000; i++) events.Publish();
        foreach (var subscription in subscriptions)
        {
            Assert.True(subscription.Changes.Reader.TryRead(out _));
            Assert.False(subscription.Changes.Reader.TryRead(out _));
        }
        using var replacement = events.Open("0")!;
        Assert.True(subscriptions[0].Stopped.IsCancellationRequested);
        subscriptions[0].Dispose();
        Assert.Equal(16, events.Count);
        events.StopAll();
        Assert.Equal(0, events.Count);
        Assert.True(replacement.Stopped.IsCancellationRequested);
        foreach (var subscription in subscriptions.Skip(1)) subscription.Dispose();
    }

    [Fact]
    public void ApprovedControllerCanReconnectWhenAnonymousStreamsFillTheBound()
    {
        var events = new CompanionEventSubscriptions();
        var anonymous = Enumerable.Range(0, 15).Select(i => events.Open($"anonymous-{i}")!).ToArray();
        using var originalController = events.Open("controller-old", approvedController: true)!;
        Assert.Equal(16, events.Count);

        using var replacementController = events.Open("controller-new", approvedController: true)!;
        Assert.Equal(16, events.Count);
        Assert.True(anonymous[0].Stopped.IsCancellationRequested);
        Assert.False(replacementController.Stopped.IsCancellationRequested);

        foreach (var subscription in anonymous) subscription.Dispose();
        events.StopAll();
    }

    private static async Task<(string Name, JsonElement Data)> ReadEvent(StreamReader reader, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var name = "";
        string? data = null;
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            else if (line.Length == 0 && data is not null)
                return (name, JsonSerializer.Deserialize<JsonElement>(data));
        }
        throw new EndOfStreamException("The event stream closed before sending its next event.");
    }
}
