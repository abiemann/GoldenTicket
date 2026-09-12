using System.Collections.Immutable;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Model;

/// <summary>
/// The fixed identity of every physical train card in the match supply. DESIGN 5.1: identical
/// cards still carry distinct instance ids, so conservation can be proved and a replayed journal
/// refers to exactly the same objects.
/// </summary>
public sealed class CardCatalog
{
    private readonly TrainCardKind[] _kinds;

    private CardCatalog(TrainCardKind[] kinds)
    {
        _kinds = kinds;
        All = [.. Enumerable.Range(0, kinds.Length).Select(i => new CardId(i))];
    }

    /// <summary>Every card instance, in deterministic manifest order.</summary>
    public ImmutableArray<CardId> All { get; }

    public int Count => _kinds.Length;

    /// <summary>
    /// Builds the catalogue in stable card-kind order, matching the manifest's semantic hash.
    /// Reordering equivalent definition rows cannot change the identity of saved card instances.
    /// </summary>
    public static CardCatalog FromManifest(BoardManifest manifest)
    {
        var kinds = new List<TrainCardKind>(manifest.TotalTrainCards);
        foreach (var definition in manifest.TrainCardDefinitions.OrderBy(definition => definition.CardKind))
        {
            for (var i = 0; i < definition.Multiplicity; i++)
                kinds.Add(definition.CardKind);
        }

        return new CardCatalog([.. kinds]);
    }

    public TrainCardKind KindOf(CardId card)
    {
        if ((uint)card.Value >= (uint)_kinds.Length)
            throw new ArgumentOutOfRangeException(nameof(card), card, "Unknown card instance.");

        return _kinds[card.Value];
    }

    public bool IsLocomotive(CardId card) => KindOf(card) == TrainCardKind.Locomotive;

    /// <summary>Counts a collection of instances by kind. Used for payment validation and views.</summary>
    public ImmutableDictionary<TrainCardKind, int> CountByKind(IEnumerable<CardId> cards)
    {
        var counts = new Dictionary<TrainCardKind, int>();
        foreach (var card in cards)
        {
            var kind = KindOf(card);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }

        return counts.ToImmutableDictionary();
    }
}
