using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

    private readonly TrayViewModel _viewModel;

    private readonly TrayIconRenderer _iconRenderer = LoadIconRenderer();

    /// <summary>
    /// Drives the "working" glow, a soft light that breathes outward from the folded palms at the
    /// centre of the tray icon. Ticks only while <see cref="TrayViewModel.IsAnyWorking"/> is true
    /// so an idle app spends no CPU on an animation nobody can see.
    /// </summary>
    private readonly DispatcherTimer _pulseTimer;

    private readonly System.Diagnostics.Stopwatch _pulseClock = new();

    private readonly DispatcherTimer _iconSizeWatchTimer;

    private int _renderedIconSizePx;

    private const double PulsePeriodMs = 1600;

    private const double PulseFloor = 0.25;

    /// <summary>
    /// The native icon handle (HICON) currently assigned to <see cref="TrayIcon"/>'s <c>Icon</c>
    /// property, tracked so <see cref="UpdateTrayIcon"/> can destroy it itself once replaced — see
    /// that method's own remarks for why this manual tracking is necessary at all (a real,
    /// confirmed handle-leak gotcha in both .NET and H.NotifyIcon, not a hypothetical one).
    /// <see cref="IntPtr.Zero"/> until the very first call.
    /// </summary>
    private IntPtr _currentTrayIconHandle;

    private static TrayIconRenderer LoadIconRenderer()
    {
        var uri = new Uri("pack://application:,,,/Assets/TrayIcon.Watermark.WaitingForInput.png");
        var resourceInfo = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Tray icon glyph asset not found: {uri}");
        using var stream = resourceInfo.Stream;
        using var glyph = new System.Drawing.Bitmap(stream);
        return new TrayIconRenderer(glyph);
    }

    public MainWindow(TrayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            TogglePopup();
        };

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pulseTimer.Tick += (_, _) => UpdateTrayIcon();

        _iconSizeWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _iconSizeWatchTimer.Tick += (_, _) =>
        {
            if (TrayIconRenderer.GetShellIconSizePx() != _renderedIconSizePx)
            {
                UpdateTrayIcon();
            }
        };

        // See UpdateTrayIcon's own doc comment for why this replaces an IconSource XAML binding.
        _viewModel.PropertyChanged += OnViewModelPropertyChangedForTrayIcon;
    }

    private void OnViewModelPropertyChangedForTrayIcon(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrayViewModel.IsAnyWorking))
        {
            UpdatePulseTimer();
            UpdateTrayIcon();
        }
    }

    private void UpdatePulseTimer()
    {
        if (!_viewModel.IsAnyWorking)
        {
            _pulseTimer.Stop();
        }
        else if (!_pulseTimer.IsEnabled)
        {
            _pulseClock.Restart();
            _pulseTimer.Start();
        }
    }

    private double CurrentGlow()
    {
        var phase = _pulseClock.Elapsed.TotalMilliseconds % PulsePeriodMs / PulsePeriodMs;
        var wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase);
        return PulseFloor + (1 - PulseFloor) * wave;
    }

    /// <summary>
    /// Regenerates and assigns the tray icon directly — <c>TrayIcon.Icon</c> (a plain
    /// <c>System.Drawing.Icon</c>), not <c>TrayIcon.IconSource</c> (an <c>ImageSource</c>, no
    /// longer bound at all; see <c>MainWindow.xaml</c>'s own comment at the <c>TaskbarIcon</c>).
    /// </summary>
    /// <remarks>
    /// <para>
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
    /// <see cref="TrayIconRenderer.Render"/> directly, on the UI thread, exactly once per real
    /// property change or pulse-timer tick (both only ever dispatched on the UI thread, so they
    /// can never overlap each other either) — nothing here is <c>async</c>/fire-and-forget, so a
    /// second call can only ever begin after the first one has fully returned; overlapping calls
    /// are structurally impossible, not just unlikely. This remains true with the pulse timer
    /// added — a timer merely means *more frequent* calls to this same already-safe method, not a
    /// new source of concurrency.
    /// </para>
    /// <para>
    /// <b>A second, independent bug found while researching this redesign: every past icon update
    /// has leaked a native GDI icon handle (HICON)</b> — confirmed directly from both .NET's own
    /// source (<c>System.Drawing.Icon.FromHandle</c> always constructs a <em>non-owning</em>
    /// wrapper; its <c>Dispose()</c> only calls the real <c>DestroyIcon</c> Win32 API if the
    /// wrapper <em>owns</em> the handle, which a <c>FromHandle</c>-created one never does) and
    /// H.NotifyIcon's own source (<c>TaskbarIcon</c>'s <c>Icon</c> property setter dutifully calls
    /// <c>oldValue?.Dispose()</c> on every change — but since that <c>Icon</c> was always created
    /// via the same non-owning <c>FromHandle</c> pattern, that <c>Dispose()</c> is a no-op at the
    /// native level). The old <c>GeneratedIconSource.ToIcon()</c> call site had exactly this same
    /// problem, just never surfaced: at the old, rare "only on a real state transition" update
    /// frequency, leaking one HICON per update was slow enough to be practically unnoticeable; a
    /// ~10/sec pulse-timer tick rate would have exhausted this process's GDI handle budget (a
    /// few thousand, by default) within minutes. Fixed by tracking the native handle
    /// (<see cref="_currentTrayIconHandle"/>) ourselves and destroying the <em>previous</em> one
    /// manually, immediately after the shell has already been told about the new one — safe
    /// because <c>Shell_NotifyIcon</c> (which <c>TrayIcon.Icon</c>'s setter calls into
    /// synchronously) copies the icon's bitmap data internally, so the handle we created it from
    /// is never needed again after that call returns. Verified via an extended soak test
    /// monitoring this process's real GDI object count (<c>GetGuiResources</c>) across several
    /// minutes of continuous pulsing — see IMPLEMENTATION_PLAN.md for the numbers.
    /// </para>
    /// </remarks>
    private void UpdateTrayIcon()
    {
        var glow = _viewModel.IsAnyWorking ? CurrentGlow() : 0;
        _renderedIconSizePx = TrayIconRenderer.GetShellIconSizePx();
        using var bitmap = _iconRenderer.Render(_renderedIconSizePx, glow);
        var newHandle = TrayIconRenderer.CreateIconHandle(bitmap);
        TrayIcon.Icon = System.Drawing.Icon.FromHandle(newHandle); // Shell_NotifyIcon(NIM_MODIFY) happens synchronously inside this setter.

        if (_currentTrayIconHandle != IntPtr.Zero)
        {
            // Only safe to destroy *after* the line above - see this method's own remarks.
            NativeMethods.DestroyIcon(_currentTrayIconHandle);
        }
        _currentTrayIconHandle = newHandle;
    }

    /// <summary>
    /// Forces the Win32 tray icon to exist even though this window is never
    /// shown at startup. Called once from <c>App.OnStartup</c>.
    /// </summary>
    public void InitializeTrayIcon()
    {
        TrayIcon.ForceCreate();
        _iconSizeWatchTimer.Start();

        // The pulse timer is otherwise only started in response to a *change* in IsAnyWorking
        // (OnViewModelPropertyChangedForTrayIcon) - if a session is already working the moment
        // this app starts up (e.g. the real "live" scenario, not just a demo), there's no change
        // event to react to, so it needs this one explicit check here too.
        UpdatePulseTimer();

        UpdateTrayIcon(); // IconSource is no longer bound (see its own comment), so the icon needs
                          // an explicit first render — nothing will "change" to trigger one otherwise.
    }

    /// <summary>Releases the tray icon's native resources on app shutdown.</summary>
    public void ShutdownTrayIcon()
    {
        _pulseTimer.Stop();
        _iconSizeWatchTimer.Stop();
        TrayIcon.Dispose();

        // TrayIcon.Dispose() above also disposes its own Icon property internally, but per
        // UpdateTrayIcon's own remarks that's a no-op at the native level for a FromHandle-created
        // icon - this is the real cleanup. Technically moot in practice (the whole process is
        // about to exit, and Windows reclaims every GDI handle on process exit regardless), but
        // correct and cheap, so there's no reason not to do it properly.
        if (_currentTrayIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_currentTrayIconHandle);
            _currentTrayIconHandle = IntPtr.Zero;
        }
    }

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

    /// <summary>
    /// The one Win32 API this app needs that neither .NET nor H.NotifyIcon actually calls for us —
    /// see <see cref="UpdateTrayIcon"/>'s remarks for the full story of why that's a real, not
    /// hypothetical, handle leak.
    /// </summary>
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
