using GoldenTicket.Domain.Manifest;
using System.Text.Json.Nodes;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Structural checks on the reviewed data package. DESIGN 6.3 is explicit that these cannot replace
/// the physical audit, so they assert the shape of the data and the honesty of its audit record -
/// not that the map is correct.
/// </summary>
public class ManifestTests
{
    private static BoardManifest Manifest => TestManifest.Manifest;

    [Fact]
    public void ClassicProfileHasTheEditionsComponents()
    {
        Assert.Equal("ttr-us-classic-en-v1", Manifest.ProfileId);
        Assert.Equal(36, Manifest.Cities.Length);
        Assert.Equal(100, Manifest.Routes.Length);
        Assert.Equal(30, Manifest.Tickets.Length);
        Assert.Equal(110, Manifest.TotalTrainCards);
    }

    [Fact]
    public void TrainCardSupplyIsTwelveOfEachColourPlusFourteenLocomotives()
    {
        var colours = Manifest.TrainCardDefinitions
            .Where(definition => definition.CardKind != TrainCardKind.Locomotive)
            .ToList();

        Assert.Equal(8, colours.Count);
        Assert.All(colours, definition => Assert.Equal(12, definition.Multiplicity));

        var locomotives = Manifest.TrainCardDefinitions
            .Single(definition => definition.CardKind == TrainCardKind.Locomotive);

        Assert.Equal(14, locomotives.Multiplicity);
    }

    [Fact]
    public void RecordedDataHashMatchesTheReviewedContent()
    {
        Assert.False(string.IsNullOrEmpty(Manifest.DataHash), "The data package must record its hash.");
        Assert.Equal(ManifestLoader.ComputeDataHash(Manifest), Manifest.DataHash);
    }

    [Fact]
    public void RuntimeProfilePinsPhysicalInstructionsWithoutChangingTheLegacySaveHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(ManifestLoader.LocateClassicUs()))!;
            var route = document["routes"]!.AsArray().First(node => node!["parallelGroupId"] is not null)!;
            route["displayLaneLabel"] = "the other physical lane";
            File.WriteAllText(path, document.ToJsonString());

            // The old checksum deliberately has no display labels. Runtime must still reject
            // changed physical instructions without invalidating already saved matches.
            var candidate = ManifestLoader.Load(path);
            Assert.Equal(Manifest.DataHash, candidate.DataHash);
            Assert.NotEqual(ManifestLoader.ComputeInstructionHash(Manifest),
                ManifestLoader.ComputeInstructionHash(candidate));
            Assert.Throws<InvalidDataException>(() => ManifestLoader.LoadClassicUs(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParallelLanesNeedDistinctNonemptyDisplayLabels()
    {
        var path = Path.GetTempFileName();
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(ManifestLoader.LocateClassicUs()))!;
            var lanes = document["routes"]!.AsArray()
                .Where(node => node!["parallelGroupId"]?.GetValue<string>() == "atlanta--new-orleans")
                .ToArray();
            Assert.Equal(2, lanes.Length);
            lanes[1]!["displayLaneLabel"] = lanes[0]!["displayLaneLabel"]!.GetValue<string>();
            File.WriteAllText(path, document.ToJsonString());

            Assert.Throws<InvalidDataException>(() => ManifestLoader.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("profileId", "another-game")]
    [InlineData("schemaVersion", "2")]
    [InlineData("rulesPolicyVersion", "2")]
    public void RuntimeLoaderRejectsUnsupportedProfilesEvenWhenTheirChecksumIsValid(string field, string value)
    {
        var path = Path.GetTempFileName();
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(ManifestLoader.LocateClassicUs()))!;
            document["dataHash"] = "";
            document[field] = field == "profileId" ? JsonValue.Create(value) : JsonValue.Create(int.Parse(value));
            File.WriteAllText(path, document.ToJsonString());
            var candidate = ManifestLoader.Load(path);
            document["dataHash"] = ManifestLoader.ComputeDataHash(candidate);
            File.WriteAllText(path, document.ToJsonString());

            // Developer loading/checksum calculation remains possible; the runtime stays pinned.
            Assert.Equal(document["dataHash"]!.GetValue<string>(), ManifestLoader.Load(path).DataHash);
            Assert.Throws<InvalidDataException>(() => ManifestLoader.LoadClassicUs(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeLoaderRequiresAHashButDeveloperLoaderCanPrepareOne(bool omitted)
    {
        var path = Path.GetTempFileName();
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(ManifestLoader.LocateClassicUs()))!.AsObject();
            if (omitted) document.Remove("dataHash");
            else document["dataHash"] = "";
            File.WriteAllText(path, document.ToJsonString());

            Assert.Equal("", ManifestLoader.Load(path).DataHash);
            Assert.Throws<InvalidDataException>(() => ManifestLoader.LoadClassicUs(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EveryRouteLengthIsScorable()
    {
        Assert.All(Manifest.Routes, route =>
        {
            Assert.InRange(route.Length, 1, 6);
            Assert.True(Manifest.RulesConstants.ScoreForLength(route.Length) > 0);
        });
    }

    [Fact]
    public void ScoringLadderMatchesThePinnedProfile()
    {
        var expected = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 4, [4] = 7, [5] = 10, [6] = 15 };
        foreach (var (length, points) in expected)
            Assert.Equal(points, Manifest.RulesConstants.ScoreForLength(length));
    }

    [Fact]
    public void EveryCityIsConnectedToTheRestOfTheBoard()
    {
        var reachable = new HashSet<CityId> { Manifest.Cities[0].StableId };
        var frontier = new Queue<CityId>([Manifest.Cities[0].StableId]);

        while (frontier.TryDequeue(out var city))
        {
            foreach (var route in Manifest.RoutesAt(city))
            {
                var next = route.OtherEndpoint(city);
                if (reachable.Add(next)) frontier.Enqueue(next);
            }
        }

        Assert.Equal(Manifest.Cities.Length, reachable.Count);
    }

    [Fact]
    public void EveryParallelGroupHasExactlyTwoLanesWithMatchingEndpointsAndLength()
    {
        var groups = Manifest.Routes
            .Where(route => route.ParallelGroupId is not null)
            .GroupBy(route => route.ParallelGroupId!)
            .ToList();

        Assert.Equal(22, groups.Count);

        foreach (var group in groups)
        {
            var lanes = group.ToList();
            Assert.Equal(2, lanes.Count);
            Assert.Single(lanes.Select(lane => lane.Length).Distinct());
            Assert.Single(lanes.Select(lane => (lane.CityA, lane.CityB)).Distinct());
            Assert.All(lanes, lane => Assert.NotNull(lane.DisplayLaneLabel));
        }
    }

    [Fact]
    public void SingleRoutesAreNotDuplicatedBetweenTheSameCities()
    {
        var duplicates = Manifest.Routes
            .Where(route => route.ParallelGroupId is null)
            .GroupBy(route => (route.CityA, route.CityB))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryTicketConnectsTwoDistinctKnownCitiesForPositivePoints()
    {
        Assert.All(Manifest.Tickets, ticket =>
        {
            Assert.NotEqual(ticket.CityA, ticket.CityB);
            Assert.False(Manifest.RoutesAt(ticket.CityA).IsEmpty);
            Assert.False(Manifest.RoutesAt(ticket.CityB).IsEmpty);
            Assert.InRange(ticket.Points, 1, 30);
        });
    }

    /// <summary>
    /// DESIGN 6.3: the application must not imply the map has been verified. If a reviewer ever marks
    /// this audited, they must also record who and when.
    /// </summary>
    [Fact]
    public void AuditRecordIsHonestAboutItsOwnStatus()
    {
        if (Manifest.DataAudit.IsAudited)
        {
            Assert.False(string.IsNullOrWhiteSpace(Manifest.DataAudit.Reviewer));
            Assert.False(string.IsNullOrWhiteSpace(Manifest.DataAudit.ReviewedOn));
        }
        else
        {
            Assert.Null(Manifest.DataAudit.Reviewer);
            Assert.Contains("physical", Manifest.DataAudit.Note, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// DESIGN 6.3 forbids shipping placeholder geometry. Until a controlled capture exists, the
    /// package carries no coordinates at all.
    /// </summary>
    [Fact]
    public void NoPlaceholderGeometryIsShipped()
    {
        var json = File.ReadAllText(ManifestLoader.LocateClassicUs());
        Assert.Contains("\"geometryProfile\": null", json);
    }
}
