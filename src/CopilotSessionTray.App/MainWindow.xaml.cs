using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CopilotSessionTray.App.Services;
using CopilotSessionTray.App.ViewModels;
using H.NotifyIcon;

namespace CopilotSessionTray.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // Windows sends a left-mouse-up for *each* click of a double-click, in addition to the
    // distinct double-click message — confirmed via H.NotifyIcon's own source (it just relays the
    // underlying WM_LBUTTONUP/WM_LBUTTONDBLCLK sequence): TrayLeftMouseUp fires twice before
    // TrayLeftMouseDoubleClick also fires. Debounce with a short timer, standard practice for
    // this exact tray-icon ambiguity, so a double-click doesn't also toggle the popup open/shut
    // right before the "start new task" window appears on top of it.
    private readonly DispatcherTimer _singleClickTimer;

    private readonly NotificationService _notificationService;
    private readonly TrayViewModel _viewModel;

    /// <summary>
    /// Reused across every <see cref="UpdateTrayIcon"/> call rather than constructed fresh each
    /// time — purely to avoid needless allocation; its own property values are overwritten
    /// in-place on every call, so nothing about reuse affects correctness here.
    /// </summary>
    private readonly GeneratedIconSource _iconGenerator = new()
    {
        Foreground = System.Windows.Media.Brushes.White,
        FontWeight = FontWeights.Bold,
    };

    public MainWindow(TrayViewModel viewModel, NotificationService notificationService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _notificationService = notificationService;
        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            TogglePopup();
        };

        // See UpdateTrayIcon's own doc comment for why this replaces an IconSource XAML binding.
        _viewModel.PropertyChanged += OnViewModelPropertyChangedForTrayIcon;
    }

    private void OnViewModelPropertyChangedForTrayIcon(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrayViewModel.IconGlyph) or nameof(TrayViewModel.IconBrush)
            or nameof(TrayViewModel.IconBackgroundSource)
            or nameof(TrayViewModel.IconTextMargin) or nameof(TrayViewModel.IconFontSize))
        {
            UpdateTrayIcon();
        }
    }

    /// <summary>
    /// Regenerates and assigns the tray icon directly — <c>TrayIcon.Icon</c> (a plain
    /// <c>System.Drawing.Icon</c>), not <c>TrayIcon.IconSource</c> (an <c>ImageSource</c>, no
    /// longer bound at all; see <c>MainWindow.xaml</c>'s own comment at the <c>TaskbarIcon</c>).
    /// </summary>
    /// <remarks>
    /// <b>Why this is actually race-free, not just differently risky</b>
    /// (IMPLEMENTATION_PLAN.md §9.6 follow-up): binding <c>IconSource</c> to a
    /// <c>GeneratedIconSource</c> makes <c>TaskbarIcon</c> itself subscribe to that source's
    /// <c>DependencyPropertyChanged</c> event and call the library's own <c>async void</c>
    /// handler — <c>Icon = await newValue.ToIconAsync()</c> — un-awaited and un-cancelled against
    /// any previous in-flight call, on *every single* property change. Two changes close enough
    /// together (confirmed via a real, reproduced crash — not just reasoned about — from
    /// ordinary rapid clicks on "Cycle demo data", not only the since-removed continuous flash
    /// timer this was first found with) race on the same non-thread-safe GDI+
    /// <c>Bitmap</c>/<c>Graphics</c> objects. This method instead calls the *synchronous*
    /// <c>GeneratedIconSource.ToIcon()</c> directly, on the UI thread, exactly once per real
    /// property change (filtered in <see cref="OnViewModelPropertyChangedForTrayIcon"/>) — nothing
    /// here is <c>async</c>/fire-and-forget, so a second call can only ever begin after the first
    /// one has fully returned; overlapping calls are structurally impossible, not just unlikely.
    /// </remarks>
    private void UpdateTrayIcon()
    {
        _iconGenerator.Text = _viewModel.IconGlyph;
        _iconGenerator.Background = _viewModel.IconBrush;
        _iconGenerator.BackgroundSource = _viewModel.IconBackgroundSource;
        _iconGenerator.FontSize = _viewModel.IconFontSize;
        _iconGenerator.TextMargin = _viewModel.IconTextMargin;
        TrayIcon.Icon = _iconGenerator.ToIcon();
    }

    /// <summary>
    /// Forces the Win32 tray icon to exist even though this window is never
    /// shown at startup. Called once from <c>App.OnStartup</c>. Also hands the now-created
    /// <c>TaskbarIcon</c> to <see cref="NotificationService"/> — see
    /// <see cref="Services.NotificationService.AttachTrayIcon"/> for why this can't just be done
    /// via a constructor dependency the other way around instead.
    /// </summary>
    public void InitializeTrayIcon()
    {
        TrayIcon.ForceCreate();
        UpdateTrayIcon(); // IconSource is no longer bound (see its own comment), so the icon needs
                          // an explicit first render — nothing will "change" to trigger one otherwise.
        _notificationService.AttachTrayIcon(TrayIcon);
    }

    /// <summary>Releases the tray icon's native resources on app shutdown.</summary>
    public void ShutdownTrayIcon() => TrayIcon.Dispose();

    protected override void OnClosing(CancelEventArgs e)
    {
        // This is a tray-only app: closing the popup (its ToolWindow "X",
        // or Alt+F4) should just hide it, not exit the whole app. The only
        // real exit path is the tray context menu's "Quit" command.
        e.Cancel = true;
        Hide();
    }

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        _singleClickTimer.Stop();
        _singleClickTimer.Start();
    }

    private void TrayIcon_TrayLeftMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        _singleClickTimer.Stop(); // cancel the pending single-click popup toggle.
        if (DataContext is TrayViewModel viewModel)
        {
            viewModel.StartYoloTaskCommand.Execute(null);
        }
    }

    private void ShowSessions_Click(object sender, RoutedEventArgs e) => TogglePopup(forceShow: true);

    /// <summary>Clicking a finished-session balloon notification reopens the popup — the same as clicking the tray icon itself.</summary>
    private void TrayIcon_TrayBalloonTipClicked(object sender, RoutedEventArgs e) => TogglePopup(forceShow: true);

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void TogglePopup(bool forceShow = false)
    {
        if (IsVisible && !forceShow)
        {
            Hide();
            return;
        }

        PositionNearTray();
        Show();
        Activate();
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Bottom - Height - 8;
    }

    /// <summary>
    /// Opens a small choice menu for the "⤴ Resume" button — see <see cref="ResumeMenuHelper"/>.
    /// </summary>
    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.DataContext is SessionItemViewModel session &&
            DataContext is TrayViewModel viewModel)
        {
            ResumeMenuHelper.Show(button, session, viewModel);
        }
    }
}
