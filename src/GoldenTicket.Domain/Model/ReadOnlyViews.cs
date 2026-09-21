using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace GoldenTicket.Domain.Model;

// The BCL read-only collection wrappers implement ICollection.SyncRoot, which can expose their
// mutable backing collection. These live domain views expose only read/enumeration interfaces.
internal sealed class ReadOnlyListView<T>(IReadOnlyList<T> source) : IReadOnlyList<T>
{
    public int Count => source.Count;
    public T this[int index] => source[index];
    public IEnumerator<T> GetEnumerator() => source.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class ReadOnlyDictionaryView<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> source)
    : IReadOnlyDictionary<TKey, TValue> where TKey : notnull
{
    public int Count => source.Count;
    public TValue this[TKey key] => source[key];
    public bool ContainsKey(TKey key) => source.ContainsKey(key);
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        source.TryGetValue(key, out value);

    // Dictionary.KeyCollection and ValueCollection also expose the dictionary via SyncRoot.
    public IEnumerable<TKey> Keys
    {
        get { foreach (var pair in source) yield return pair.Key; }
    }

    public IEnumerable<TValue> Values
    {
        get { foreach (var pair in source) yield return pair.Value; }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => source.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
