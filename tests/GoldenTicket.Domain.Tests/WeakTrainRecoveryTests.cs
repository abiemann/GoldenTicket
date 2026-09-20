using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class WeakTrainRecoveryTests
{
    // Chicago–Duluth's weak middle detection in model-board pixel coordinates.
    private static readonly PieceModelBox Seed = new(PieceCandidateKind.Train, 1155, 427, 60, 20, .48);

    [Fact]
    public void Weak_proposal_selects_a_centered_same_scale_view_but_is_never_evidence_itself()
    {
        var region = Assert.Single(WeakTrainRecovery.Select([Seed], .3));
        Assert.Equal(865, region.X);
        Assert.Equal(117, region.Y);
        Assert.Equal(Seed, region.Seed);
        Assert.Null(WeakTrainRecovery.Accept(region, [], [Seed], .3));
        Assert.Null(WeakTrainRecovery.Accept(region, [Seed], [Seed], .3));

        var output = new float[PieceModelGeometry.OutputRows * PieceModelGeometry.OutputColumns];
        new float[] { 320, 320, 60, 20, .9f, .9f, .1f }.CopyTo(output, 0);
        var retry = new List<PieceModelBox>();
        PieceModelGeometry.Decode(output, region.X, region.Y, .3, retry);
        var accepted = Assert.IsType<PieceModelBox>(WeakTrainRecovery.Accept(region, retry, [Seed], .3));
        Assert.Equal(Seed.X, accepted.X);
        Assert.Equal(Seed.Y, accepted.Y);
        Assert.Equal(Seed.Width, accepted.Width);
        Assert.Equal(Seed.Height, accepted.Height);
        Assert.Equal(.81, accepted.Confidence, precision: 6);
    }

    [Fact]
    public void Selection_is_deterministic_bounded_and_deduplicates_weak_boxes()
    {
        var best = Seed with { Confidence = .52 };
        var duplicate = best with { X = best.X + 1, Confidence = .51 };
        var second = Seed with { X = 700, Confidence = .49 };
        var third = Seed with { X = 400, Confidence = .45 };
        PieceModelBox[] boxes = [third, duplicate, second, best];
        var selected = WeakTrainRecovery.Select(boxes, .3);
        Assert.Equal(new[] { best, second }, selected.Select(region => region.Seed));
        Assert.Equal(selected, WeakTrainRecovery.Select(boxes.Reverse().ToArray(), .3));

        var tied = new[] { Seed with { X = 900 }, Seed with { X = 700 }, Seed };
        Assert.Equal(new[] { 700d, 900d }, WeakTrainRecovery.Select(tied, .3).Select(region => region.Seed.X));
    }

    [Fact]
    public void Selection_ignores_unsupported_proposals_strong_overlaps_and_existing_grid_views()
    {
        PieceModelBox[] ineligible =
        [
            Seed with { Confidence = .299 }, Seed with { Confidence = .55 },
            Seed with { Kind = PieceCandidateKind.PlayerMarker }, Seed with { Width = 4 },
            Seed with { Width = 129 }, Seed with { X = double.NaN },
            Seed with { X = 1900 }, Seed with { Confidence = double.NaN },
            Seed with { X = 1314, Y = 310 } // Centered retry would repeat baseline tile (1024, 0).
        ];
        Assert.Empty(WeakTrainRecovery.Select(ineligible, .3));
        foreach (var kind in new[] { PieceCandidateKind.Train, PieceCandidateKind.PlayerMarker })
            Assert.Empty(WeakTrainRecovery.Select([Seed, Seed with { Kind = kind, Confidence = .9 }], .3));

        var edge = Seed with { X = 1840, Y = 400 };
        var clamped = Assert.Single(WeakTrainRecovery.Select([edge], .3));
        Assert.Equal(1280, clamped.X);
        Assert.Equal(90, clamped.Y);
    }

    [Fact]
    public void Recovery_never_lowers_the_gameplay_or_manifest_confidence_floor()
    {
        var region = Assert.Single(WeakTrainRecovery.Select([Seed], .3));
        Assert.Null(WeakTrainRecovery.Accept(region, [Seed with { Confidence = .549 }], [Seed], .3));
        Assert.NotNull(WeakTrainRecovery.Accept(region, [Seed with { Confidence = .55 }], [Seed], .3));
        Assert.Empty(WeakTrainRecovery.Select([Seed], .5));
        Assert.Empty(WeakTrainRecovery.Select([Seed with { Confidence = .7 }], .75));
        Assert.Empty(WeakTrainRecovery.Select([Seed with { Confidence = .8 }], .75));
        Assert.Null(WeakTrainRecovery.Accept(region, [Seed with { Confidence = .7 }], [Seed], .75));

        var stricterSeed = Seed with { Confidence = .52 };
        var stricter = Assert.Single(WeakTrainRecovery.Select([stricterSeed], .5));
        Assert.NotNull(WeakTrainRecovery.Accept(stricter, [stricterSeed with { Confidence = .55 }], [stricterSeed], .5));
    }

    [Fact]
    public void Retry_rejects_neighbors_changed_class_size_drift_and_tile_edge_fragments()
    {
        var region = Assert.Single(WeakTrainRecovery.Select([Seed], .3));
        var strong = Seed with { Confidence = .9 };
        PieceModelBox[] rejected =
        [
            strong with { X = strong.X + 70 }, // An adjacent physical train.
            strong with { Kind = PieceCandidateKind.PlayerMarker },
            strong with { X = strong.X + 7 }, // Similar overlap but too much center drift.
            strong with { X = strong.X + 15, Width = 30 },
            strong with { X = strong.X - 20, Width = 100 },
            strong with { X = region.X + 1 },
            strong with { Y = region.Y + 1 },
            strong with { Confidence = double.NaN }
        ];
        foreach (var retry in rejected)
            Assert.Null(WeakTrainRecovery.Accept(region, [retry], [Seed], .3));

        // This consistent seed is itself near a bounded retry's edge: confidence and
        // spatial agreement cannot authorize a tile-clipped detection.
        var nearEdge = Seed with { X = 1 };
        Assert.Null(WeakTrainRecovery.Accept(new(0, 117, nearEdge),
            [nearEdge with { Confidence = .9 }], [nearEdge], .3));
    }

    [Fact]
    public void Recovery_keeps_one_duplicate_but_rejects_distinct_competing_matches()
    {
        var region = Assert.Single(WeakTrainRecovery.Select([Seed], .3));
        var best = Seed with { Confidence = .9 };
        var duplicate = best with { X = best.X + 1, Confidence = .8 };
        Assert.Equal(best, WeakTrainRecovery.Accept(region, [duplicate, best], [Seed], .3));

        // Both overlap the seed by more than half and stay within 6px, but are not
        // duplicates of each other. Neither may be selected to conceal the ambiguity.
        var above = best with { Y = best.Y - 6 };
        var below = best with { Y = best.Y + 6, Confidence = .8 };
        Assert.Null(WeakTrainRecovery.Accept(region, [above, below], [Seed], .3));
        Assert.Null(WeakTrainRecovery.Accept(region, [best], [Seed, best with { X = best.X + 5 }], .3));
    }

    [Fact]
    public void Accepted_retry_uses_normal_nms_without_removing_a_touching_train()
    {
        var neighbor = Seed with { X = Seed.X + Seed.Width, Confidence = .95 };
        var region = Assert.Single(WeakTrainRecovery.Select([Seed, neighbor], .3));
        var recovered = Assert.IsType<PieceModelBox>(WeakTrainRecovery.Accept(region,
            [Seed with { Confidence = .85 }], [Seed, neighbor], .3));
        var merged = PieceModelGeometry.Merge([Seed, neighbor, recovered], 1920, 1200, .45);
        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { .95, .85 }, merged.Select(candidate => candidate.Confidence));
    }
}
