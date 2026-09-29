using System.Windows.Controls;
using CopilotSessionTray.App.ViewModels;

namespace CopilotSessionTray.App;

/// <summary>
/// Shared "⤴ Resume" choice-menu logic — used by both <c>MainWindow</c>'s tray popup rows and
/// <see cref="SessionHistoryWindow"/>'s history rows, so the same tested behavior (and the same
/// "the ContextMenu must be assigned to the Button, not just opened" fix — see git history for
/// the earlier bug this caused) isn't duplicated and re-risked in two places.
/// </summary>
internal static class ResumeMenuHelper
{
    /// <summary>
    /// Opens a small choice menu for a "⤴ Resume" button — done in code-behind (not
    /// command/XAML bindings like other row buttons) because a ContextMenu is its own popup
    /// root, not part of the normal visual tree, which makes an ElementName-back-to-root-window
    /// binding pattern unreliable here.
    /// </summary>
    public static void Show(Button button, SessionItemViewModel session, TrayViewModel viewModel)
    {
        var menu = new ContextMenu();

        var historyItem = new MenuItem { Header = "Resume with history" };
        historyItem.Click += (_, _) => viewModel.ResumeSessionCommand.Execute(session);
        menu.Items.Add(historyItem);

        var summaryItem = new MenuItem { Header = "Resume with summary" };
        summaryItem.Click += (_, _) => viewModel.StartSessionFromSummaryCommand.Execute(session);
        menu.Items.Add(summaryItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Cancel" }); // no handler: selecting it just dismisses the menu.

        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
