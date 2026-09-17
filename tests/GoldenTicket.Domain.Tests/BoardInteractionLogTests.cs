using System.Text.Json;
using GoldenTicket.Desktop;

namespace GoldenTicket.Domain.Tests;

[CollectionDefinition("Board interaction log", DisableParallelization = true)]
public sealed class BoardInteractionLogCollection;

[Collection("Board interaction log")]
public sealed class BoardInteractionLogTests
{
    [Fact]
    public void NewLaunchStartsAFreshReadableBoardLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GoldenTicket-board-log-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, BoardInteractionLog.FileName);
        var previous = Path.Combine(directory, BoardInteractionLog.PreviousFileName);
        try
        {
            BoardInteractionLog.Start(directory);
            BoardInteractionLog.Write("placement.frame", new { route = "first-route", matched = 0 });
            BoardInteractionLog.Stop();
            Assert.Contains("first-route", File.ReadAllText(path));

            File.WriteAllText(previous, "stale prior launch");
            BoardInteractionLog.Start(directory);
            Assert.False(File.Exists(previous));
            BoardInteractionLog.Write("placement.frame", new { route = "second-route", matched = 2 });
            BoardInteractionLog.Stop();

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.DoesNotContain("first-route", File.ReadAllText(path));
            using var launch = JsonDocument.Parse(lines[0]);
            using var placement = JsonDocument.Parse(lines[1]);
            Assert.Equal("app.launch", launch.RootElement.GetProperty("eventName").GetString());
            Assert.Equal("placement.frame", placement.RootElement.GetProperty("eventName").GetString());
            Assert.Equal("second-route", placement.RootElement.GetProperty("details").GetProperty("route").GetString());
        }
        finally
        {
            BoardInteractionLog.Stop();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(previous)) File.Delete(previous);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
