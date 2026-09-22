using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Testing;

internal static partial class Program
{
    private static async Task VerifySavedMatchDeletion()
    {
        BindingLog.Context = "saved-match-deletion-interactions";
        var bindingErrorsBefore = BindingLog.ErrorCount;
        var checks = new List<string>();
        var store = new InMemorySessionStore();
        var manifest = ManifestLoader.LoadClassicUs();
        MainViewModel MakeModel() => new(manifest, store,
            camera: new CameraViewModel(capture: new FakeCameraCapture()));
        var firstSource = MakeModel();
        var secondSource = MakeModel();
        var picker = MakeModel();
        SetupView? view = null;
        try
        {
            await SaveNamedFixture(firstSource, "Friday evening trains");
            await SaveNamedFixture(secondSource, "Sunday return journey");
            var summaries = await store.ListSessionsAsync(CancellationToken.None);
            var firstId = summaries.Single(summary => summary.LatestCheckpointName == "Friday evening trains").SessionId;
            var secondId = summaries.Single(summary => summary.LatestCheckpointName == "Sunday return journey").SessionId;
            if (summaries.Count != 2 || firstId == secondId)
                throw new InvalidOperationException("Deletion needs two distinct real saved sessions in its isolated in-memory store.");
            await picker.LoadSavedSessionsCommand.ExecuteAsync(null);

            void VerifyUnselected(UserControl candidate)
            {
                if (picker.Setup.SelectedSavedSession is not null || DeleteButton(candidate).IsEnabled ||
                    ResumeButton(candidate).IsEnabled || Confirmation(candidate).Visibility != Visibility.Collapsed ||
                    Descendants<CheckBox>(SavedList(candidate)).Any(box => box.IsChecked == true))
                    throw new InvalidOperationException("Unselected saved matches must keep Delete and Resume disabled and hide confirmation.");
            }
            await RenderSizes("setup-saved-delete-unselected-synthetic",
                () => new SetupView { DataContext = picker }, VerifyUnselected);
            checks.Add("Two real saved matches begin unselected with the bound Delete and Resume buttons disabled.");

            view = new SetupView { DataContext = picker };
            await Arrange(view, 1000, 620);
            await SelectMatch(firstId);
            var firstDescription = picker.Setup.SelectedSavedSession!.Description;
            await InvokeButton(DeleteButton(view));
            VerifyConfirmation(view, firstId, firstDescription);
            if ((await store.ListSessionsAsync(CancellationToken.None)).Count != 2)
                throw new InvalidOperationException("Requesting deletion must not remove a saved match before confirmation.");
            await RenderSizes("setup-saved-delete-confirmation-synthetic",
                () => new SetupView { DataContext = picker },
                candidate => VerifyConfirmation(candidate, firstId, firstDescription));
            checks.Add("The real Delete button opens confirmation naming the selected match and warning that its saved board photos are removed.");

            await InvokeButton((Button)view.FindName("CancelSavedMatchDeletionButton"));
            await Arrange(view, 1000, 620);
            var afterCancel = await store.ListSessionsAsync(CancellationToken.None);
            if (picker.IsSavedMatchDeleteConfirmationOpen || Confirmation(view).Visibility != Visibility.Collapsed ||
                picker.Setup.SelectedSavedSession?.SessionId != firstId || !DeleteButton(view).IsEnabled ||
                afterCancel.Count != 2 || !afterCancel.Any(summary => summary.SessionId == firstId) ||
                !afterCancel.Any(summary => summary.SessionId == secondId))
                throw new InvalidOperationException("Cancel must close confirmation and preserve both saved sessions and the selected match.");
            checks.Add("The bound Cancel button closes confirmation without changing either saved session or the selection.");

            await InvokeButton(DeleteButton(view));
            await SelectMatch(secondId);
            if (picker.IsSavedMatchDeleteConfirmationOpen || picker.SavedMatchPendingDeletion is not null ||
                Confirmation(view).Visibility != Visibility.Collapsed || picker.ConfirmDeleteSavedMatchCommand.CanExecute(null) ||
                picker.Setup.SelectedSavedSession?.SessionId != secondId || !DeleteButton(view).IsEnabled ||
                (await store.ListSessionsAsync(CancellationToken.None)).Count != 2)
                throw new InvalidOperationException("Choosing another saved match must clear the old confirmation without deleting either session.");
            checks.Add("Changing the checked match clears the pending confirmation and disables its stale confirm command.");

            await SelectMatch(firstId);
            var oldRemainingRow = picker.Setup.SavedSessions.Single(row => row.SessionId == secondId);
            await InvokeButton(DeleteButton(view));
            VerifyConfirmation(view, firstId, firstDescription);
            await ConfirmDeletion();
            var remaining = await store.ListSessionsAsync(CancellationToken.None);
            if (remaining.Count != 1 || remaining[0].SessionId != secondId ||
                picker.Setup.SavedSessions.Count != 1 || picker.Setup.SavedSessions[0].SessionId != secondId ||
                picker.Setup.SelectedSavedSession?.SessionId != secondId ||
                ReferenceEquals(oldRemainingRow, picker.Setup.SelectedSavedSession) ||
                Descendants<CheckBox>(SavedList(view)).Single().IsChecked != true ||
                !DeleteButton(view).IsEnabled || !ResumeButton(view).IsEnabled ||
                picker.IsSavedMatchDeleteConfirmationOpen || Confirmation(view).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Confirm must delete exactly the named selected session, refresh the rows, and check the sole remaining match.");
            checks.Add("The bound confirmation deletes exactly its selected session and refreshes the list with the sole remaining match checked and usable.");

            var remainingDescription = picker.Setup.SelectedSavedSession.Description;
            await InvokeButton(DeleteButton(view));
            VerifyConfirmation(view, secondId, remainingDescription);
            await ConfirmDeletion();
            if ((await store.ListSessionsAsync(CancellationToken.None)).Count != 0 ||
                picker.Setup.SavedSessions.Count != 0 || SavedList(view).Items.Count != 0 ||
                picker.Setup.SelectedSavedSession is not null || picker.IsSavedMatchDeleteConfirmationOpen ||
                Confirmation(view).Visibility != Visibility.Collapsed || DeleteButton(view).IsEnabled ||
                ResumeButton(view).IsEnabled || picker.ConfirmDeleteSavedMatchCommand.CanExecute(null))
                throw new InvalidOperationException("Deleting the final saved match must empty the picker, clear confirmation and selection, and disable Resume and Delete.");
            checks.Add("Deleting the final selected match leaves an empty list with Resume, Delete, and confirmation disabled.");

            if (BindingLog.ErrorCount != bindingErrorsBefore)
                throw new InvalidOperationException("Saved-match deletion views or interactions emitted WPF binding errors or warnings.");
            await File.WriteAllTextAsync(Path.Combine(Output, "saved-match-deletion-interactions.json"),
                JsonSerializer.Serialize(new
                {
                    Fixture = "Two real named sessions in a synthetic in-memory store; fake camera capture; no user saves or native input.",
                    Checks = checks,
                    RenderSizes = new[] { "1280x800", "1000x620" },
                    BindingErrors = BindingLog.ErrorCount - bindingErrorsBefore,
                    Passed = true
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Saved-match deletion: {checks.Count} real bound UI checks passed with no binding errors.");

            ListBox SavedList(DependencyObject candidate) => Descendants<ListBox>(candidate)
                .Single(list => ReferenceEquals(list.ItemsSource, picker.Setup.SavedSessions));
            Button ResumeButton(DependencyObject candidate) => Descendants<Button>(candidate)
                .Single(button => ReferenceEquals(button.Command, picker.ResumeMatchCommand));

            async Task SelectMatch(SessionId id)
            {
                var checkbox = Descendants<CheckBox>(SavedList(view)).Single(box =>
                    box.DataContext is SavedSessionRow row && row.SessionId == id);
                var toggle = new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle) as IToggleProvider
                    ?? throw new InvalidOperationException("Saved-match checkboxes must support accessible toggling.");
                toggle.Toggle();
                await Arrange(view, 1000, 620);
                if (picker.Setup.SelectedSavedSession?.SessionId != id ||
                    Descendants<CheckBox>(SavedList(view)).Count(box => box.IsChecked == true) != 1)
                    throw new InvalidOperationException("Checking a saved match must select exactly that session.");
            }

            void VerifyConfirmation(UserControl candidate, SessionId id, string description)
            {
                var confirmation = Confirmation(candidate);
                var text = Descendants<TextBlock>(confirmation).Select(block => block.Text).ToArray();
                var expectedName = id == firstId ? "Friday evening trains" : "Sunday return journey";
                if (!picker.IsSavedMatchDeleteConfirmationOpen || picker.SavedMatchPendingDeletion?.SessionId != id ||
                    picker.SavedMatchDeleteDescription != description || confirmation.Visibility != Visibility.Visible ||
                    !description.StartsWith(expectedName + "  ·  ", StringComparison.Ordinal) ||
                    !text.Contains(description) || !text.Any(value => value.Contains("saved board photos", StringComparison.Ordinal)) ||
                    !((Button)candidate.FindName("CancelSavedMatchDeletionButton")).IsEnabled ||
                    !((Button)candidate.FindName("ConfirmSavedMatchDeletionButton")).IsEnabled || DeleteButton(candidate).IsEnabled)
                    throw new InvalidOperationException("The visible confirmation must name exactly the pending saved match and expose enabled Cancel and Delete match actions.");
            }

            async Task ConfirmDeletion()
            {
                await InvokeButton((Button)view.FindName("ConfirmSavedMatchDeletionButton"));
                await (picker.ConfirmDeleteSavedMatchCommand.ExecutionTask
                    ?? throw new InvalidOperationException("Invoking the real confirmation button must execute its bound deletion command."));
                await Arrange(view, 1000, 620);
            }
        }
        finally
        {
            if (view is not null)
            {
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                view.DataContext = null;
            }
            await firstSource.DisposeToolsAsync();
            await secondSource.DisposeToolsAsync();
            await picker.DisposeToolsAsync();
        }

        static Button DeleteButton(UserControl candidate) => (Button)candidate.FindName("DeleteSavedMatchButton");
        static Border Confirmation(UserControl candidate) => (Border)candidate.FindName("DeleteSavedMatchConfirmation");

        static async Task InvokeButton(Button button)
        {
            var invoke = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("Saved-match deletion buttons must support accessible invocation.");
            invoke.Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        static async Task SaveNamedFixture(MainViewModel source, string name)
        {
            source.Setup.ManualVerificationAccepted = true;
            await source.StartMatchCommand.ExecuteAsync(null);
            await source.CommitTicketsCommand.ExecuteAsync(null);
            source.Table.SaveName = name;
            await source.SaveAndPackAwayCommand.ExecuteAsync(null);
            if (!source.Table.IsPackedAway)
                throw new InvalidOperationException($"The named deletion fixture failed to save: {source.Status}");
        }
    }
}
