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
    /// The tray icon's constant base artwork — the same gold Guanyin glyph used elsewhere in the
    /// app (<see cref="TrayViewModel"/>'s watermark images, <see cref="ViewModels.SessionItemViewModel.StatusIconSource"/>),
    /// loaded once as a GDI+ <see cref="System.Drawing.Bitmap"/> (not a WPF <c>ImageSource</c> —
    /// <see cref="ComposeTrayIconBitmap"/> draws with <see cref="System.Drawing.Graphics"/>, not
    /// WPF) from the same embedded <c>pack://</c> resource those other call sites use. Never
    /// disposed until <see cref="ShutdownTrayIcon"/> — reused as a draw source on every single
    /// <see cref="UpdateTrayIcon"/>/timer-tick call, same "load once, draw many times" pattern as
    /// every other cached image asset in this app.
    /// </summary>
    private readonly System.Drawing.Bitmap _glyphBitmap = LoadGlyphBitmap();

    /// <summary>
    /// Drives the "working" ring's rotation (2026-10-06, user request: "a spinning circle
    /// surrounding the golden guanyin glyph" for a working session). Ticks only while
    /// <see cref="TrayViewModel.IsAnyWorking"/> is true — started/stopped in
    /// <see cref="OnViewModelPropertyChangedForTrayIcon"/> and <see cref="InitializeTrayIcon"/> —
    /// rather than running indefinitely, so an idle app never spends any CPU/battery on an
    /// animation nobody can see.
    /// </summary>
    private readonly DispatcherTimer _spinTimer;

    /// <summary>Current rotation of the working-ring's arc, in degrees. Reset to 0 every time the ring starts (see <see cref="OnViewModelPropertyChangedForTrayIcon"/>) purely so each spin-up looks the same, not because a continuing angle would be wrong.</summary>
    private double _spinAngleDegrees;

    /// <summary>
    /// The native icon handle (HICON) currently assigned to <see cref="TrayIcon"/>'s <c>Icon</c>
    /// property, tracked so <see cref="UpdateTrayIcon"/> can destroy it itself once replaced — see
    /// that method's own remarks for why this manual tracking is necessary at all (a real,
    /// confirmed handle-leak gotcha in both .NET and H.NotifyIcon, not a hypothetical one).
    /// <see cref="IntPtr.Zero"/> until the very first call.
    /// </summary>
    private IntPtr _currentTrayIconHandle;

    private static System.Drawing.Bitmap LoadGlyphBitmap()
    {
        var uri = new Uri("pack://application:,,,/Assets/TrayIcon.Watermark.WaitingForInput.png");
        var resourceInfo = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Tray icon glyph asset not found: {uri}");
        using var stream = resourceInfo.Stream;
        using var original = new System.Drawing.Bitmap(stream);
        return CropToVisibleContent(original);
    }

    /// <summary>
    /// Crops away the fully-transparent border around the figure in the baked glyph PNG (measured
    /// ~5-10% per side — the artwork itself doesn't quite reach the edges of its own 512x512
    /// canvas) before it's ever drawn — otherwise that baked-in padding compounds with
    /// <see cref="ComposeTrayIconBitmap"/>'s own margin, making the figure noticeably smaller
    /// than it needs to be (2026-10-07, user request: "make the icon... as large as possible,
    /// right now it seems smaller compared to other tray icons"). Runs once at startup (this
    /// bitmap is cached and reused for every draw — see <see cref="_glyphBitmap"/>'s own doc
    /// comment), so the plain, safe <see cref="System.Drawing.Bitmap.GetPixel"/> scan is fine
    /// despite not being the fastest way to inspect pixels — no need for `unsafe`/`LockBits` for
    /// a one-time cost.
    /// </summary>
    private static System.Drawing.Bitmap CropToVisibleContent(System.Drawing.Bitmap source)
    {
        int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).A > 10)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return (System.Drawing.Bitmap)source.Clone(); // defensive only - shouldn't happen for real art.
        }

        var cropRect = new System.Drawing.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return source.Clone(cropRect, source.PixelFormat);
    }

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

        // ~8fps (120ms/tick): deliberately coarse, not 60fps-smooth — a 128px source canvas
        // rendered down to a 16-32px taskbar icon doesn't benefit from finer-grained timing, and
        // every tick means one more icon regeneration (see UpdateTrayIcon's remarks). 10
        // degrees/tick -> a full revolution every 36 ticks * 120ms = 4.3s (2026-10-06, slowed down
        // from an initial 24 degrees/tick/~1.8s per the user's "make it spin slower" feedback) —
        // same update frequency/resource cost as before, just a smaller step each time, which also
        // makes the gradient (see ComposeTrayIconBitmap's remarks) read more smoothly besides.
        _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _spinTimer.Tick += (_, _) =>
        {
            _spinAngleDegrees = (_spinAngleDegrees + 10) % 360;
            UpdateTrayIcon();
        };

        // See UpdateTrayIcon's own doc comment for why this replaces an IconSource XAML binding.
        _viewModel.PropertyChanged += OnViewModelPropertyChangedForTrayIcon;
    }

    private void OnViewModelPropertyChangedForTrayIcon(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrayViewModel.IsAnyWorking))
        {
            if (_viewModel.IsAnyWorking)
            {
                _spinAngleDegrees = 0;
                _spinTimer.Start();
            }
            else
            {
                _spinTimer.Stop();
            }

            UpdateTrayIcon();
        }
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
    /// <see cref="ComposeTrayIconBitmap"/> directly, on the UI thread, exactly once per real
    /// property change or spin-timer tick (both only ever dispatched on the UI thread, so they
    /// can never overlap each other either) — nothing here is <c>async</c>/fire-and-forget, so a
    /// second call can only ever begin after the first one has fully returned; overlapping calls
    /// are structurally impossible, not just unlikely. This remains true with the 2026-10-06
    /// glyph+ring+dot redesign's spin timer added — a timer merely means *more frequent* calls to
    /// this same already-safe method, not a new source of concurrency.
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
    /// ~8/sec spin-timer tick rate would have exhausted this process's GDI handle budget (a
    /// few thousand, by default) within minutes. Fixed by tracking the native handle
    /// (<see cref="_currentTrayIconHandle"/>) ourselves and destroying the <em>previous</em> one
    /// manually, immediately after the shell has already been told about the new one — safe
    /// because <c>Shell_NotifyIcon</c> (which <c>TrayIcon.Icon</c>'s setter calls into
    /// synchronously) copies the icon's bitmap data internally, so the handle we created it from
    /// is never needed again after that call returns. Verified via an extended soak test
    /// monitoring this process's real GDI object count (<c>GetGuiResources</c>) across several
    /// minutes of continuous spinning — see IMPLEMENTATION_PLAN.md for the numbers.
    /// </para>
    /// </remarks>
    private void UpdateTrayIcon()
    {
        using var bitmap = ComposeTrayIconBitmap(_viewModel.IsAnyWorking, _spinAngleDegrees);
        var newHandle = bitmap.GetHicon();
        TrayIcon.Icon = System.Drawing.Icon.FromHandle(newHandle); // Shell_NotifyIcon(NIM_MODIFY) happens synchronously inside this setter.

        if (_currentTrayIconHandle != IntPtr.Zero)
        {
            // Only safe to destroy *after* the line above - see this method's own remarks.
            NativeMethods.DestroyIcon(_currentTrayIconHandle);
        }
        _currentTrayIconHandle = newHandle;
    }

    /// <summary>
    /// Composites the tray icon from scratch every call: an optional rotating gold-to-white
    /// gradient ring drawn first (as a background "halo"), the constant gold glyph
    /// (<see cref="_glyphBitmap"/>) drawn on top of it while any session is working (2026-10-06,
    /// user request — replacing the flat green/orange/gray circle + unread-count/working-dot text
    /// glyph this app used until then: "no numbers are needed as they are too small to read" at
    /// tray-icon size, and the gold glyph can simply always be shown with a ring overlay on top
    /// instead of swapping it out for a flat color; the ring itself was originally a flat green,
    /// then changed the same day to this gold-to-white gradient per further feedback, so it reads
    /// as part of the same gold theme as the glyph it surrounds rather than borrowing the app's
    /// unrelated green "working" color). The ring is drawn *behind*, not after, the glyph — see
    /// this method's own remarks for why (2026-10-07 follow-up). A separate small red "unread" dot
    /// overlay used to be drawn last, on top of everything — removed 2026-10-07 (the user: "I
    /// don't really need the red dot behaviour in the tray icon or unread-related features", since
    /// the dismissable balloon notification already serves that acknowledgment purpose).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caller (<see cref="UpdateTrayIcon"/>) is responsible for disposing the returned
    /// <see cref="System.Drawing.Bitmap"/> and for destroying the HICON extracted from it — this
    /// method itself only disposes the GDI+ objects (<see cref="System.Drawing.Graphics"/>,
    /// pens/brushes) it creates internally for a single draw pass.
    /// </para>
    /// <para>
    /// <b>Why the ring is drawn as many small arc segments instead of one <c>DrawArc</c> call</b>:
    /// GDI+ has no built-in way to paint a gradient *along the length of a curved stroke* (a
    /// <see cref="System.Drawing.Drawing2D.LinearGradientBrush"/> paints across a flat 2D area in
    /// screen-space, not along an arc's own path, and would look visually inconsistent as the ring
    /// rotates underneath a screen-space-fixed gradient). Approximating a stroke gradient by
    /// drawing many short, individually-colored segments — interpolating from gold at the ring's
    /// leading edge to white at its trailing edge — is the standard technique for this in GDI+.
    /// <see cref="RingSegmentCount"/> short segments, each slightly overlapping its neighbor
    /// (<see cref="RingSegmentOverlapDegrees"/>) to hide any seam from floating-point rounding
    /// between adjacent arcs, is fine-grained enough to look smooth at this icon's small size —
    /// confirmed by rendering and visually inspecting the result, not just assumed.
    /// </para>
    /// </remarks>
    private System.Drawing.Bitmap ComposeTrayIconBitmap(bool isWorking, double spinAngleDegrees)
    {
        const int size = 128; // matches the previous GeneratedIconSource.Size default/convention.
        var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.Clear(System.Drawing.Color.Transparent);

        // Ring drawn FIRST, *behind* the glyph (2026-10-07) - not just a z-order tweak: once the
        // glyph's own margin was tightened (below) to make the figure as large as possible, the
        // ring's existing near-edge positioning started visibly cutting across the figure's own
        // silhouette (confirmed by rendering - a visible gold/white bar across the lotus seat).
        // Drawing it behind the glyph instead means any part of the ring that falls under the
        // figure's opaque pixels is naturally occluded by it, while the part that falls in the
        // glyph's own (still-transparent) background shows through - reading as a halo glowing
        // from behind the figure rather than a ring competing with it for the same space.
        if (isWorking)
        {
            const float ringMargin = size * 0.045f;
            var ringRect = new System.Drawing.RectangleF(ringMargin, ringMargin, size - ringMargin * 2, size - ringMargin * 2);
            const float segmentSweep = RingSweepDegrees / RingSegmentCount;

            for (var i = 0; i < RingSegmentCount; i++)
            {
                var t = (float)i / (RingSegmentCount - 1); // 0 at the leading edge (gold) -> 1 at the trailing edge (white).
                var segmentColor = LerpColor(RingGoldColor, System.Drawing.Color.White, t);
                using var segmentPen = new System.Drawing.Pen(segmentColor, size * 0.085f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                };
                var segmentStart = (float)spinAngleDegrees + i * segmentSweep;
                graphics.DrawArc(segmentPen, ringRect, segmentStart, segmentSweep + RingSegmentOverlapDegrees);
            }
        }

        // 4% margin (down from 12%, 2026-10-07 per user feedback: "make the icon as large as
        // possible, right now it seems smaller compared to other tray icons") - just enough to
        // avoid edge-clipping/anti-aliasing artifacts at the very border.
        const float glyphMargin = size * 0.04f;
        var glyphRect = new System.Drawing.RectangleF(glyphMargin, glyphMargin, size - glyphMargin * 2, size - glyphMargin * 2);
        graphics.DrawImage(_glyphBitmap, glyphRect);

        return bitmap;
    }

    /// <summary>Total sweep of the working ring's visible arc, in degrees — see <see cref="ComposeTrayIconBitmap"/>.</summary>
    private const float RingSweepDegrees = 110f;

    /// <summary>
    /// How many short, individually-colored arcs <see cref="ComposeTrayIconBitmap"/> draws to
    /// approximate a gold-to-white gradient along the ring's length — see that method's own
    /// remarks for why this many small segments, rather than one <c>DrawArc</c> call, is
    /// necessary at all.
    /// </summary>
    private const int RingSegmentCount = 24;

    /// <summary>Extra overlap between adjacent ring segments, in degrees, purely to hide any seam from floating-point rounding between them.</summary>
    private const float RingSegmentOverlapDegrees = 0.6f;

    /// <summary>The "gold" end of the working ring's gradient — the exact same gold as the glyph it surrounds (<c>#DAA520</c>, sampled from <c>TrayIcon.Watermark.WaitingForInput.png</c>), not GDI+'s own slightly different built-in <see cref="System.Drawing.Color.Gold"/>, so the ring reads as part of the same gold theme rather than a second, subtly different gold.</summary>
    private static readonly System.Drawing.Color RingGoldColor = System.Drawing.Color.FromArgb(0xDA, 0xA5, 0x20);

    /// <summary>Linear interpolation between two colors (including alpha) at <paramref name="t"/> ∈ [0, 1] — used to approximate a gradient along the working ring's arc; see <see cref="ComposeTrayIconBitmap"/>'s remarks.</summary>
    private static System.Drawing.Color LerpColor(System.Drawing.Color from, System.Drawing.Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return System.Drawing.Color.FromArgb(
            (int)(from.A + (to.A - from.A) * t),
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));
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

        // The spin timer is otherwise only started in response to a *change* in IsAnyWorking
        // (OnViewModelPropertyChangedForTrayIcon) - if a session is already working the moment
        // this app starts up (e.g. the real "live" scenario, not just a demo), there's no change
        // event to react to, so it needs this one explicit check here too.
        if (_viewModel.IsAnyWorking)
        {
            _spinTimer.Start();
        }

        UpdateTrayIcon(); // IconSource is no longer bound (see its own comment), so the icon needs
                          // an explicit first render — nothing will "change" to trigger one otherwise.
        _notificationService.AttachTrayIcon(TrayIcon);
    }

    /// <summary>Releases the tray icon's native resources on app shutdown.</summary>
    public void ShutdownTrayIcon()
    {
        _spinTimer.Stop();
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
        _glyphBitmap.Dispose();
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
