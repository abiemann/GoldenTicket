using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;

internal static partial class Program
{
    private static async Task VerifyAiStyleSelection()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        try
        {
            await model.Game.ActivateSelectedAsync();
            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            foreach (var choice in model.Game.SeatChoices.Skip(1).Take(3))
            {
                model.Game.CycleSeat(choice);
                model.Game.CycleSeat(choice);
            }
            model.Game.ToggleAiStyle(model.Game.SeatChoices[2]);

            void CheckBadges(UserControl view)
            {
                var badges = Descendants<Button>(view).Where(button => button.Tag as string == "AiStyleBadge").ToArray();
                if (badges.Length != 5 || badges.Count(IsElementShown) != 3)
                    throw new InvalidOperationException("Only the three computer portraits should show an AI style badge.");
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view)
                    .TransformBounds(new Rect(element.RenderSize));
                foreach (var badge in badges.Where(IsElementShown))
                {
                    var choice = (GameSeatChoice)badge.DataContext;
                    var portrait = Descendants<Image>(view).Single(image => image.Tag as string == "CharacterPortrait" &&
                        image.DataContext == choice);
                    var image = (Image)badge.Content;
                    var portraitBounds = Bounds(portrait);
                    var badgeBounds = Bounds(badge);
                    var state = choice.IsAggressive ? "aggressive" : "standard";
                    if (!AutomationProperties.GetName(badge).Contains(state) ||
                        !badge.Focusable || image.Source is not DrawingImage ||
                        !ReferenceEquals(image.Source, view.FindResource(choice.IsAggressive ? "AggressiveAiFace" : "StandardAiFace")))
                        throw new InvalidOperationException("The badge must show and announce its actual computer style.");
                    if (Math.Abs(badgeBounds.Right - portraitBounds.Right) > 5 ||
                        Math.Abs(badgeBounds.Bottom - portraitBounds.Bottom) > 5 ||
                        badgeBounds.Left < portraitBounds.Left + portraitBounds.Width / 2 ||
                        badgeBounds.Top < portraitBounds.Top + portraitBounds.Height / 2)
                        throw new InvalidOperationException("The badge must overlap the portrait's bottom-right corner.");
                }
            }

            await RenderSizes("game-computer-styles", () => new GameScreenView { DataContext = model }, CheckBadges,
                [(1280, 800), (1000, 620), (875, 680)]);
            var view = new GameScreenView { DataContext = model };
            await Arrange(view, 1000, 620);
            var choiceToToggle = model.Game.SeatChoices[1];
            var button = Descendants<Button>(view).Single(candidate => candidate.Tag as string == "AiStyleBadge" &&
                candidate.DataContext == choiceToToggle);
            var imageToSpin = (Image)button.Content;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (SystemParameters.ClientAreaAnimation)
            {
                if (!model.Game.IsFaceFlipping || model.Game.CanPlay || imageToSpin.RenderTransform is not RotateTransform)
                    throw new InvalidOperationException("A style change must spin its badge and wait before starting a match.");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // Duplicate input while spinning is ignored.
            }
            await Task.Delay(450);
            await Arrange(view, 1000, 620);
            if (!choiceToToggle.IsAggressive || choiceToToggle.Role != CharacterRole.Computer ||
                model.Game.SelectedSeatCount != 4 || model.Game.IsFaceFlipping || !model.Game.CanPlay)
                throw new InvalidOperationException("Changing AI style must not change the role or leave the roster locked.");
            CheckBadges(view);

            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(450);
            if (choiceToToggle.IsAggressive || choiceToToggle.Role != CharacterRole.Computer ||
                !model.Game.SeatChoices[2].IsAggressive)
                throw new InvalidOperationException("Accessible activation must toggle only the selected computer's style.");
            var roleButton = Descendants<Button>(view).Single(candidate => candidate.Tag as string == "CharacterRole" &&
                candidate.DataContext == choiceToToggle);
            model.Game.ToggleAiStyle(choiceToToggle);
            foreach (var expectedRole in new[] { CharacterRole.Unselected, CharacterRole.Human, CharacterRole.Computer })
            {
                double? opacityWhenRoleChanges = null;
                void ObserveRoleChange(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(GameSeatChoice.IsComputer)) opacityWhenRoleChanges = button.Opacity;
                }
                choiceToToggle.PropertyChanged += ObserveRoleChange;
                try
                {
                    roleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    if (SystemParameters.ClientAreaAnimation && (!model.Game.IsFaceFlipping || button.IsEnabled))
                        throw new InvalidOperationException("The badge must not accept clicks while its portrait is changing.");
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (model.Game.IsFaceFlipping && DateTime.UtcNow < deadline) await Task.Delay(20);
                    await Arrange(view, 1000, 620);
                    if (SystemParameters.ClientAreaAnimation && opacityWhenRoleChanges != 0)
                        throw new InvalidOperationException($"The badge must be transparent as the portrait becomes {expectedRole}; opacity was {opacityWhenRoleChanges}.");
                    if (model.Game.IsFaceFlipping || choiceToToggle.Role != expectedRole ||
                        IsElementShown(button) != choiceToToggle.IsComputer || button.Opacity != 1 || !button.IsEnabled ||
                        button.HasAnimatedProperties || choiceToToggle.Difficulty != AiDifficulty.Standard ||
                        !model.Game.SeatChoices[2].IsAggressive)
                        throw new InvalidOperationException("Portrait changes must restore badge visibility and interaction without affecting other players.");
                }
                finally { choiceToToggle.PropertyChanged -= ObserveRoleChange; }
            }
            Results.Add(new { screen = "computer-style-interaction", passed = true,
                spin = SystemParameters.ClientAreaAnimation, badgeFade = SystemParameters.ClientAreaAnimation,
                accessibleActivation = true });
        }
        finally { await model.DisposeToolsAsync(); }
    }
}
