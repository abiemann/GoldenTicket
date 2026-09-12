using System.Text.RegularExpressions;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// The spike's device page has no build step on purpose (DESIGN 23 M0: it must be editable during a
/// borrowed-device session), so nothing would otherwise catch a renamed element, an unreachable
/// handler or an asset the content security policy will refuse. These read the shipped files.
///
/// The first of these exists because it has already happened once: a regex written with a real
/// backspace byte instead of the two characters that spell a word boundary, which silently stopped
/// three in-app browsers from ever being detected.
/// </summary>
public sealed class CompanionShellTests
{
    private static readonly string ShellDirectory =
        Path.Combine(TestManifest.RepositoryRoot, "tools", "GoldenTicket.ConnectivitySpike", "wwwroot");

    private static string Read(string name) => File.ReadAllText(Path.Combine(ShellDirectory, name));

    [Theory]
    [InlineData("index.html")]
    [InlineData("app.js")]
    [InlineData("styles.css")]
    [InlineData("sw.js")]
    [InlineData("manifest.webmanifest")]
    public void TheShellHasNoStrayControlCharacters(string file)
    {
        var content = Read(file);

        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character is '\n' or '\r' or '\t') continue;

            Assert.False(
                char.IsControl(character),
                $"{file} holds U+{(int)character:X4} at offset {index}. An escape sequence was " +
                "probably written as the character it names.");
        }
    }

    [Fact]
    public void EveryElementTheScriptLooksUpExistsInThePage()
    {
        var page = Read("index.html");
        var script = Read("app.js");

        var ids = Regex.Matches(page, "id=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet();

        var referenced = Regex.Matches(script, @"getElementById\('([^']+)'\)")
            .Select(match => match.Groups[1].Value)
            .Concat(Regex.Matches(script, @"(?:show|detail)\('([^']+)'").Select(match => match.Groups[1].Value))
            .Distinct();

        foreach (var id in referenced)
        {
            Assert.True(ids.Contains(id), $"app.js looks up '{id}', which index.html does not define.");
        }
    }

    [Fact]
    public void EveryCopyButtonNamesAWhereToReportTo()
    {
        var page = Read("index.html");

        var ids = Regex.Matches(page, "id=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet();
        var targets = Regex.Matches(page, "data-copy=\"([^\"]+)\"").Select(match => match.Groups[1].Value);

        Assert.NotEmpty(targets);
        foreach (var target in targets)
        {
            Assert.True(ids.Contains(target), $"A copy button reports to '{target}', which does not exist.");
        }
    }

    [Fact]
    public void TheShellLoadsNothingFromOutsideItsOwnOrigin()
    {
        // The host serves a policy of default-src 'self' (DESIGN 18.9). An absolute reference would
        // be refused silently, which on a borrowed device looks like a broken laptop.
        var page = Read("index.html");

        foreach (Match match in Regex.Matches(page, "(?:src|href)=\"([^\"]+)\""))
        {
            var reference = match.Groups[1].Value;
            if (reference.StartsWith('#')) continue;

            Assert.False(
                reference.Contains("//", StringComparison.Ordinal),
                $"index.html references {reference}, which is not same-origin.");
        }
    }

    [Fact]
    public void ThePageAndTheServiceWorkerNameTheSameCache()
    {
        // The worker answers from the cache first, so a shell change that does not rename the cache
        // leaves an already-installed device showing the old page for ever. The page reads the same
        // name to report whether the shell really is available offline.
        var worker = Regex.Match(Read("sw.js"), "SHELL_CACHE = '([^']+)'").Groups[1].Value;
        var script = Regex.Match(Read("app.js"), "SHELL_CACHE = '([^']+)'").Groups[1].Value;

        Assert.NotEmpty(worker);
        Assert.Equal(worker, script);
    }

    [Fact]
    public void TheServiceWorkerCachesEveryAssetThePageReferences()
    {
        var page = Read("index.html");
        var cached = Regex.Matches(Read("sw.js"), "'([^']+)',").Select(match => match.Groups[1].Value).ToHashSet();

        foreach (Match match in Regex.Matches(page, "(?:src|href)=\"([^\"]+)\""))
        {
            var reference = match.Groups[1].Value;
            if (reference.StartsWith('#')) continue;

            Assert.True(
                cached.Contains(reference),
                $"index.html loads {reference}, which sw.js does not cache, so the shell would be " +
                "incomplete offline.");
        }
    }

    [Fact]
    public void TheAndroidHandoffTargetsChromeSpecifically()
    {
        var script = Read("app.js");

        Assert.Contains("intent://", script, StringComparison.Ordinal);
        Assert.Contains("package=com.android.chrome", script, StringComparison.Ordinal);
        Assert.Contains("scheme=${scheme}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallationIsPresentedBeforePairing()
    {
        // DESIGN 18.5: a browser tab and the installed app do not reliably share cookies, so a code
        // redeemed in the wrong context is spent for nothing. The page has to say so, and has to put
        // installation first.
        var page = Read("index.html");

        var install = page.IndexOf("Install to the home screen", StringComparison.Ordinal);
        var pair = page.IndexOf("Pairing</h2>", StringComparison.Ordinal);

        Assert.True(install > 0, "The page no longer has an installation step.");
        Assert.True(pair > install, "Pairing is presented before installation.");
        Assert.Contains("pair-blocked", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageTellsPlayersNotToTapThroughACertificateWarning()
    {
        var page = Read("index.html");

        Assert.Contains("Do not tap through a warning", page, StringComparison.Ordinal);
        Assert.Contains("Certificate Trust Settings", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePairingCodeIsNeverDescribedAsPartOfTheQr()
    {
        // DESIGN 18.5: the QR carries only the local landing address. Saying so on the device is how
        // a player knows a photographed screen has not given anything away.
        var page = Read("index.html");

        Assert.Contains("never part of the QR", page, StringComparison.Ordinal);
    }
}
