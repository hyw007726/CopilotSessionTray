using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using CopilotSessionTray.App.ViewModels;

namespace CopilotSessionTray.App;

/// <summary>
/// First cut of the planned "searchable history view" (IMPLEMENTATION_PLAN.md §5/Phase 6): shows
/// the most recently updated sessions from the real <c>session-store.db</c>, each resumable via
/// the same choice menu as the tray popup's live rows. No search/filter yet — just a plain recent
/// list; full-text search against the already-present <c>search_index</c> FTS table is a natural
/// follow-up, not included here.
///
/// Deliberately plain code-behind (no dedicated view-model, no data-binding for the list itself)
/// to match <c>NewTaskWindow</c>'s pattern: the list is a one-time load-and-display snapshot, not
/// something that needs to react to further changes while this window is open.
/// </summary>
public partial class SessionHistoryWindow : Window
{
    private readonly TrayViewModel _trayViewModel;

    public SessionHistoryWindow(IReadOnlyList<SessionItemViewModel> sessions, TrayViewModel trayViewModel)
    {
        InitializeComponent();
        _trayViewModel = trayViewModel;

        HistoryListBox.ItemsSource = sessions;
        EmptyStateText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is SessionItemViewModel session)
        {
            ResumeMenuHelper.Show(button, session, _trayViewModel);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
