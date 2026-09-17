using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopMultiHumanPhoneSetupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhoneSetupAppearsAtTheTableOnlyWhenMoreThanOneHumanPlays(bool secondHuman)
    {
        var model = NewMatch(new InMemorySessionStore(), secondHuman);
        try
        {
            await model.StartMatchAsync();

            Assert.Equal(Screen.Table, model.Screen);
            Assert.Equal(secondHuman, model.CanConnectPhone);
            Assert.Equal(secondHuman, model.ShowMultiHumanPhoneSetup);
            if (secondHuman) Assert.Null(model.PrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task ResumingAMultiHumanMatchShowsPhoneSetupAgain()
    {
        var store = new InMemorySessionStore();
        var original = NewMatch(store, secondHuman: true);
        await original.StartMatchAsync();
        await original.DisposeToolsAsync();

        var restored = new MainViewModel(TestManifest.Manifest, store);
        try
        {
            await restored.LoadSavedSessionsAsync();
            restored.Setup.SelectedSavedSession = Assert.Single(restored.Setup.SavedSessions);
            await restored.ResumeMatchAsync();

            Assert.Equal(Screen.Table, restored.Screen);
            Assert.True(restored.CanConnectPhone);
            Assert.True(restored.ShowMultiHumanPhoneSetup);
            Assert.Null(restored.PrivateSeat);
        }
        finally { await restored.DisposeToolsAsync(); }
    }

    private static MainViewModel NewMatch(ISessionStore store, bool secondHuman)
    {
        var model = new MainViewModel(TestManifest.Manifest, store);
        model.Setup.ManualVerificationAccepted = true;
        model.Setup.Seats[1].IsComputer = !secondHuman;
        return model;
    }
}
