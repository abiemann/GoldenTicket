namespace GoldenTicket.ConnectivitySpike;

/// <summary>Unpaired devices may report setup failure, but cannot allocate unlimited host memory.</summary>
internal sealed class BoundedObservationBuffer<T>(int capacity)
{
    private readonly List<T> _items = [];
    private readonly Lock _gate = new();

    internal bool TryAdd(T observation)
    {
        lock (_gate)
        {
            if (_items.Count >= capacity) return false;
            _items.Add(observation);
            return true;
        }
    }

    internal IReadOnlyList<T> Snapshot() { lock (_gate) return _items.ToArray(); }
}
