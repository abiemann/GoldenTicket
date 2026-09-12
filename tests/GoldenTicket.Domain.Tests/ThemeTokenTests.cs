using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// DESIGN 4.8: canonical theme tokens drive the WPF resource dictionary rather than literal hex
/// values being scattered through the interface. This asserts the two files still agree, and that
/// the contrast pairs the design calculated still hold for the shipped colours.
/// </summary>
public partial class ThemeTokenTests
{
    private static readonly JsonDocument Tokens = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestManifest.RepositoryRoot, "shared", "theme", "tokens.json")));

    private static readonly string PaletteXaml = File.ReadAllText(Path.Combine(
        TestManifest.RepositoryRoot, "src", "GoldenTicket.Desktop", "Theme", "Palette.xaml"));

    [GeneratedRegex("""<Color x:Key="Color\.(?<name>\w+)">(?<hex>#[0-9A-Fa-f]{6})</Color>""")]
    private static partial Regex ColorEntry();

    [Fact]
    public void EveryPaletteTokenAppearsInTheResourceDictionaryWithTheSameValue()
    {
        var xaml = ColorEntry().Matches(PaletteXaml)
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["hex"].Value);

        var palette = Tokens.RootElement.GetProperty("palette");

        foreach (var token in palette.EnumerateObject())
        {
            var expected = token.Value.GetProperty("hex").GetString()!;
            Assert.True(xaml.ContainsKey(token.Name), $"Palette.xaml is missing the token {token.Name}.");
            Assert.Equal(expected, xaml[token.Name], ignoreCase: true);
        }

        Assert.Equal(palette.EnumerateObject().Count(), xaml.Count);
    }

    [Fact]
    public void EverySemanticRoleResolvesToADefinedToken()
    {
        var palette = Tokens.RootElement.GetProperty("palette");

        foreach (var role in Tokens.RootElement.GetProperty("semantic").EnumerateObject())
        {
            var token = role.Value.GetString()!;
            Assert.True(palette.TryGetProperty(token, out _), $"{role.Name} points at unknown token {token}.");
            Assert.Contains($"x:Key=\"{role.Name}\"", PaletteXaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GameplayColoursAreSeparateFromTheDecorativePalette()
    {
        var palette = Tokens.RootElement.GetProperty("palette");
        var players = Tokens.RootElement.GetProperty("gameplay").GetProperty("playerColors");

        // A burgundy button must not be the red player's identity (DESIGN 4.8).
        var decorative = palette.EnumerateObject()
            .Select(token => token.Value.GetProperty("hex").GetString()!.ToUpperInvariant())
            .ToHashSet();

        foreach (var player in players.EnumerateObject())
        {
            var hex = player.Value.GetProperty("hex").GetString()!.ToUpperInvariant();
            Assert.DoesNotContain(hex, decorative);

            // Colour is never the only indicator: each player colour carries a symbol.
            Assert.False(string.IsNullOrWhiteSpace(player.Value.GetProperty("symbol").GetString()));
        }
    }

    [Fact]
    public void EveryPlayerColourHasAnUniqueSymbolAndABrushInTheDictionary()
    {
        var players = Tokens.RootElement.GetProperty("gameplay").GetProperty("playerColors");
        var symbols = new List<string>();

        foreach (var player in players.EnumerateObject())
        {
            symbols.Add(player.Value.GetProperty("symbol").GetString()!);
            Assert.Contains($"x:Key=\"Player.{player.Name}\"", PaletteXaml, StringComparison.Ordinal);
        }

        Assert.Equal(symbols.Count, symbols.Distinct().Count());
        Assert.Equal(Enum.GetValues<PlayerColor>().Length, symbols.Count);
    }

    [Theory]
    [InlineData("Ink", "Parchment", 4.5)]          // primary text
    [InlineData("SecondaryInk", "Parchment", 4.5)] // supporting copy
    [InlineData("Paper", "Burgundy", 4.5)]         // primary button label
    [InlineData("RailBlue", "Parchment", 4.5)]     // links, secondary actions, focus
    [InlineData("Paper", "RailCharcoal", 4.5)]     // text on the privacy curtain
    [InlineData("Ink", "AntiqueGold", 4.5)]        // text on a gold badge
    public void TextPairsMeetTheProjectContrastTarget(string foreground, string background, double target)
    {
        var ratio = ContrastRatio(Hex(foreground), Hex(background));
        Assert.True(ratio >= target,
            $"{foreground} on {background} is {ratio:F2}:1, below the {target:F1}:1 target.");
    }

    /// <summary>
    /// DESIGN 4.8 states plainly that antique gold on parchment is decorative. This records that
    /// limitation so nobody later uses it for small text believing it passes.
    /// </summary>
    [Fact]
    public void AntiqueGoldOnParchmentIsDecorativeOnly()
    {
        Assert.True(ContrastRatio(Hex("AntiqueGold"), Hex("Parchment")) < 3.0);
    }

    private static string Hex(string token) =>
        Tokens.RootElement.GetProperty("palette").GetProperty(token).GetProperty("hex").GetString()!;

    private static double ContrastRatio(string foreground, string background)
    {
        var a = RelativeLuminance(foreground);
        var b = RelativeLuminance(background);
        var (lighter, darker) = a > b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>WCAG relative luminance for an opaque sRGB colour.</summary>
    private static double RelativeLuminance(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);

        double Channel(int shift)
        {
            var srgb = ((value >> shift) & 0xFF) / 255.0;
            return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(16)) + (0.7152 * Channel(8)) + (0.0722 * Channel(0));
    }
}
