using System.Text.RegularExpressions;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Guards the installed runtime's LAN-only contract independently of the online build toolchain.
/// These checks do not claim that the browser or operating system makes no background requests.
/// </summary>
public sealed class OfflineRuntimeContractTests
{
    private static readonly string ShellDirectory = Path.Combine(AppContext.BaseDirectory, "companion-web");
    private static readonly Uri LaptopOrigin = new("http://192.168.50.2:8080/");

    [Fact]
    public void ShippedPageAndStylesUseOnlyBundledLaptopAssets()
    {
        var page = File.ReadAllText(Path.Combine(ShellDirectory, "index.html"));
        var references = Regex.Matches(page, "(?:src|href)\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value).ToList();
        Assert.NotEmpty(references);
        var css = File.ReadAllText(Path.Combine(ShellDirectory, "app.css"));
        Assert.DoesNotContain("@import", css, StringComparison.OrdinalIgnoreCase);
        references.AddRange(Regex.Matches(css, "url\\(\\s*[\"']?([^\\)\"']+)[\"']?\\s*\\)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Trim()));
        foreach (var reference in references)
        {
            var destination = new Uri(LaptopOrigin, reference);
            Assert.Equal(LaptopOrigin.GetLeftPart(UriPartial.Authority), destination.GetLeftPart(UriPartial.Authority));
            Assert.StartsWith("/companion/", destination.AbsolutePath, StringComparison.Ordinal);
            var fileName = destination.AbsolutePath["/companion/".Length..];
            var asset = Path.Combine(ShellDirectory, fileName.Length == 0 ? "index.html" : fileName);
            Assert.True(File.Exists(asset), $"The installed companion is missing {reference}.");
        }
    }
}
