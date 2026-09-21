using System.Threading.Channels;

namespace GoldenTicket.CompanionHost;

/// <summary>Bounded change signals, not an event history or a cache of player data.</summary>
internal sealed class CompanionEventSubscriptions
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Subscription> _active = [];
    internal int Count { get { lock (_sync) return _active.Count; } }

    internal Subscription? Open(string key)
    {
        Subscription? previous;
        Subscription current;
        lock (_sync)
        {
            _active.TryGetValue(key, out previous);
            if (previous is null && _active.Count >= 16) return null;
            current = new(this, key);
            _active[key] = current;
        }
        previous?.Stop();
        return current;
    }

    internal void Publish()
    {
        lock (_sync)
            foreach (var subscription in _active.Values) subscription.Changes.Writer.TryWrite(true);
    }

    internal bool Remove(Subscription subscription)
    {
        lock (_sync)
            return _active.TryGetValue(subscription.Key, out var current) && ReferenceEquals(current, subscription) &&
                _active.Remove(subscription.Key);
    }

    internal void StopAll()
    {
        Subscription[] subscriptions;
        lock (_sync) { subscriptions = [.. _active.Values]; _active.Clear(); }
        foreach (var subscription in subscriptions) subscription.Stop();
    }

    internal sealed class Subscription(CompanionEventSubscriptions owner, string key) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        internal string Key => key;
        internal Channel<bool> Changes { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
        internal CancellationToken Stopped => _stop.Token;
        internal void Stop()
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { /* An old connection completed while its replacement opened. */ }
        }
        public void Dispose() { owner.Remove(this); _stop.Dispose(); }
    }
}
