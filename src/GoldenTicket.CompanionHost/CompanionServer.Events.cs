using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GoldenTicket.CompanionHost;

public sealed partial class CompanionServer
{
    public const string AssetsVersion = "13";
    private readonly CompanionEventSubscriptions _events = new();
    private Func<HttpContext, bool>? _streamAllowed;
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    internal int EventSubscriberCount => _events.Count;

    /// <summary>Coalesce public desktop changes for connected browsers without blocking the UI.</summary>
    public void NotifyGameChanged() => _events.Publish();

    private ControllerCredentials? StreamCredentials(HttpContext context, bool heartbeat = false) =>
        _authority.Authenticate(context.Request.Cookies[CookieName], context.Request.Headers["X-GoldenTicket-Tab"], heartbeat);

    private object UnpairedSession(HttpContext context) => new
    {
        paired = false,
        pending = _authority.IsPending(context.Request.Cookies[CookieName], context.Request.Headers["X-GoldenTicket-Tab"]),
        controllerGeneration = _authority.Generation,
        apiVersion = ApiVersion,
        assetsVersion = AssetsVersion
    };

    private async Task<object> ReadSessionAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var credentials = StreamCredentials(context, heartbeat: true);
        if (credentials is null) return UnpairedSession(context);
        var snapshot = await bridge.ReadPublicAsync(cancellationToken);
        if (!snapshot.CanControl) _authority.InvalidatePrivateGrants();
        if (StreamCredentials(context) != credentials) return UnpairedSession(context);
        return new { paired = true, pending = false, csrf = credentials.Csrf, controllerGeneration = credentials.Generation,
            handoffGeneration = _authority.Generation, apiVersion = ApiVersion, assetsVersion = AssetsVersion, snapshot };
    }

    private async Task StreamEventsAsync(HttpContext context)
    {
        var tab = context.Request.Headers["X-GoldenTicket-Tab"].ToString();
        if (tab.Length is < 20 or > 100 || !tab.All(char.IsAsciiLetterOrDigit))
        { context.Response.StatusCode = 400; return; }
        using var subscription = _events.Open((context.Request.Cookies[CookieName] ?? "") + ":" + tab);
        if (subscription is null) { context.Response.StatusCode = 429; return; }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, subscription.Stopped);
        var token = stop.Token;
        ControllerCredentials? lastCredentials = null;
        long lastGeneration = -1;
        bool lastPending = false;
        string? lastJson = null;
        try
        {
            // Reconnecting resumes covered; no private hand or replay buffer crosses this stream.
            if (StreamCredentials(context, heartbeat: true) is not null) _authority.InvalidatePrivateGrants();
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var changed = subscription.Changes.Reader.WaitToReadAsync(token).AsTask();
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(2), token);
            var dirty = true;
            while (!token.IsCancellationRequested)
            {
                if (_streamAllowed?.Invoke(context) == false) break;
                if (dirty)
                {
                    var session = await ReadSessionAsync(context, token);
                    var json = JsonSerializer.Serialize(session, session.GetType(), EventJson);
                    if (json != lastJson) { await SendEventAsync(context, "session", json, token); lastJson = json; }
                    lastCredentials = StreamCredentials(context);
                    lastGeneration = _authority.Generation;
                    lastPending = _authority.IsPending(context.Request.Cookies[CookieName], tab);
                    dirty = false;
                }
                await Task.WhenAny(changed, heartbeat);
                token.ThrowIfCancellationRequested();
                if (changed.IsCompleted)
                {
                    while (subscription.Changes.Reader.TryRead(out _)) { }
                    changed = subscription.Changes.Reader.WaitToReadAsync(token).AsTask();
                    dirty = true;
                }
                if (heartbeat.IsCompleted)
                {
                    if (_streamAllowed?.Invoke(context) == false) break;
                    var credentials = StreamCredentials(context, heartbeat: true);
                    var pending = _authority.IsPending(context.Request.Cookies[CookieName], tab);
                    if (credentials != lastCredentials || _authority.Generation != lastGeneration || pending != lastPending)
                        dirty = true;
                    // No bridge/snapshot read for an unchanged game's heartbeat.
                    await SendEventAsync(context, "heartbeat", "{}", token);
                    heartbeat = Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            stop.Cancel();
            // An old socket closing must not invalidate its replacement's freshly revealed hand.
            if (_events.Remove(subscription) && StreamCredentials(context) is not null) InvalidatePrivateGrants();
        }
    }

    private static async Task SendEventAsync(HttpContext context, string name, string json, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await context.Response.WriteAsync($"event: {name}\ndata: {json}\n\n", deadline.Token);
        await context.Response.Body.FlushAsync(deadline.Token);
    }
}
