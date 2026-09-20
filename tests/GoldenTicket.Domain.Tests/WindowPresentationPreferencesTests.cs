using GoldenTicket.Application;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

public sealed class WindowPresentationPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GoldenTicket-window-preferences-" +
        Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "presentation-settings.json");

    [Fact]
    public async Task Window_mode_and_destination_choices_merge_and_survive_restart()
    {
        var window = new WindowPresentation(1375.5, 882.25, false);
        var first = CreateModel();
        try
        {
            first.SaveWindowPresentation(window);
            first.ShowDestinationsWhenViewingTrainCards = false;
            first.DisplayMode = DisplayMode.FullScreen;
        }
        finally { await first.DisposeToolsAsync(); }

        var second = CreateModel();
        try
        {
            Assert.Equal(window, second.SavedWindowPresentation);
            Assert.Equal(DisplayMode.FullScreen, second.DisplayMode);
            Assert.False(second.ShowDestinationsWhenViewingTrainCards);
            second.DisplayMode = DisplayMode.Resizable;
        }
        finally { await second.DisposeToolsAsync(); }

        var maximized = new WindowPresentation(1480, 920, true);
        var third = CreateModel();
        try
        {
            Assert.Equal(window, third.SavedWindowPresentation);
            Assert.Equal(DisplayMode.Resizable, third.DisplayMode);
            Assert.False(third.ShowDestinationsWhenViewingTrainCards);
            third.SaveWindowPresentation(maximized);
            third.ShowDestinationsWhenViewingTrainCards = true;
        }
        finally { await third.DisposeToolsAsync(); }

        var fourth = CreateModel();
        try
        {
            Assert.Equal(maximized, fourth.SavedWindowPresentation);
            Assert.Equal(DisplayMode.Resizable, fourth.DisplayMode);
            Assert.True(fourth.ShowDestinationsWhenViewingTrainCards);
        }
        finally { await fourth.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData("{\"ShowDestinationsWhenViewingTrainCards\":false}", false)]
    [InlineData("{}", true)]
    [InlineData("null", true)]
    [InlineData("{not-json", true)]
    [InlineData("{\"DisplayMode\":\"unexpected\"}", true)]
    [InlineData("{\"ShowDestinationsWhenViewingTrainCards\":false,\"DisplayMode\":777," +
        "\"WindowPresentation\":{\"Width\":-1,\"Height\":900,\"Maximized\":true}}", false)]
    [InlineData("{\"WindowPresentation\":{\"Width\":1400}}", true)]
    public async Task Legacy_and_malformed_preferences_load_safe_defaults_without_rewriting_the_file(
        string json, bool destinations)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, json, TestContext.Current.CancellationToken);
        var model = CreateModel();
        try
        {
            Assert.Null(model.SavedWindowPresentation);
            Assert.Equal(DisplayMode.Resizable, model.DisplayMode);
            Assert.Equal(destinations, model.ShowDestinationsWhenViewingTrainCards);
            Assert.Equal(json, await File.ReadAllTextAsync(SettingsPath, TestContext.Current.CancellationToken));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Oversized_preferences_are_ignored_and_invalid_dimensions_cannot_replace_valid_bounds()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, new string(' ', 4097), TestContext.Current.CancellationToken);
        var window = new WindowPresentation(1400, 880, true);
        var model = CreateModel();
        try
        {
            Assert.Null(model.SavedWindowPresentation);
            Assert.Equal(DisplayMode.Resizable, model.DisplayMode);
            model.SaveWindowPresentation(window);
            foreach (var invalid in new[]
                     {
                         window with { Width = double.NaN }, window with { Height = double.PositiveInfinity },
                         window with { Width = 0 }, window with { Height = -1 },
                         window with { Width = 100_000 }, window with { Height = 100_000 },
                         window with { Width = 1 }, window with { Height = 1 }
                     })
            {
                model.SaveWindowPresentation(invalid);
                Assert.Equal(window, model.SavedWindowPresentation);
            }
        }
        finally { await model.DisposeToolsAsync(); }
        var reopened = CreateModel();
        try { Assert.Equal(window, reopened.SavedWindowPresentation); }
        finally { await reopened.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task In_memory_sessions_keep_display_choices_only_in_the_current_view_model()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        try
        {
            model.SaveWindowPresentation(new(1440, 900, true));
            model.DisplayMode = DisplayMode.FullScreen;
            model.ShowDestinationsWhenViewingTrainCards = false;
            Assert.NotNull(model.SavedWindowPresentation);
        }
        finally { await model.DisposeToolsAsync(); }
        var next = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        try
        {
            Assert.Null(next.SavedWindowPresentation);
            Assert.Equal(DisplayMode.Resizable, next.DisplayMode);
            Assert.True(next.ShowDestinationsWhenViewingTrainCards);
        }
        finally { await next.DisposeToolsAsync(); }
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Preference_write_failure_keeps_the_window_usable_and_reports_the_unsaved_choice()
    {
        Directory.CreateDirectory(SettingsPath); // A directory cannot be replaced by the settings file.
        var model = CreateModel();
        var window = new WindowPresentation(1400, 880, false);
        try
        {
            model.SaveWindowPresentation(window);
            Assert.Equal(window, model.SavedWindowPresentation);
            Assert.Contains("could not be saved", model.Status);
            model.DisplayMode = DisplayMode.FullScreen;
            Assert.Equal(DisplayMode.FullScreen, model.DisplayMode);
            Assert.Contains("could not be saved", model.Status);
            model.ShowDestinationsWhenViewingTrainCards = false;
            Assert.False(model.ShowDestinationsWhenViewingTrainCards);
            Assert.Contains("could not be saved", model.Status);
        }
        finally { await model.DisposeToolsAsync(); }
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private MainViewModel CreateModel() => new(TestManifest.Manifest, new SqliteSessionStore(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
