using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App;

public partial class BalloonWindow : Window
{
    public const double BodyWidth = 200;
    public const double BodyHeight = 160;
    public const double OverallHeight = 244;

    private const double Inset = 34;
    private const double TextWidth = 144;
    private const double TitleLineHeight = 17;
    private const double BodyTwoLinesHeight = 30;
    private const double BodyThreeLinesHeight = 45;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly Color DarkText = Color.FromRgb(0x20, 0x20, 0x20);

    private readonly Action<BalloonWindow> _clicked;
    private int _topAnimationId;

    public BalloonWindow(SessionNotification notification, Color color, Action<BalloonWindow> clicked)
    {
        InitializeComponent();
        _clicked = clicked;
        SessionId = notification.SessionId;
        BalloonColor = color;
        ApplyColor(color);
        Update(notification);

        MouseEnter += (_, _) => AnimateHover(1.06, 140);
        MouseLeave += (_, _) => AnimateHover(1.0, 200);
        MouseLeftButtonUp += (_, _) => _clicked(this);
    }

    public string SessionId { get; }

    public Color BalloonColor { get; }

    public bool IsPopping { get; private set; }

    public void Update(SessionNotification notification)
    {
        Title = notification.Title;
        TitleText.Text = notification.Title;
        BodyText.Text = notification.Body;
        HintText.Text = notification.ElapsedWorking is { } worked
            ? $"Worked {SessionText.FormatDuration(worked)} · Click to pop"
            : "Click to pop";

        TitleText.Measure(new Size(TextWidth, double.PositiveInfinity));
        BodyText.MaxHeight = TitleText.DesiredSize.Height <= TitleLineHeight + 1 ? BodyThreeLinesHeight : BodyTwoLinesHeight;
    }

    public void Launch(double rightEdge, double fromTop, double toTop)
    {
        Left = rightEdge - BodyWidth - Inset;
        Top = fromTop - Inset;
        Show();

        var rise = TimeSpan.FromMilliseconds(Math.Clamp(900 + Math.Abs(fromTop - toTop) * 0.9, 900, 2400));
        AnimateTop(toTop - Inset, rise, new SineEase { EasingMode = EasingMode.EaseOut });

        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        var inflate = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
            FillBehavior = FillBehavior.Stop,
        };
        PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, inflate);
        PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, inflate);

        Drift.BeginAnimation(TranslateTransform.XProperty, KeyFrames(rise, (0, 0), (0.25, -14), (0.55, 12), (0.8, -6), (1, 0)));
        Tilt.BeginAnimation(RotateTransform.AngleProperty, KeyFrames(rise, (0, 0), (0.25, -5), (0.55, 4.5), (0.8, -2), (1, 0)));

        var settle = KeyFrames(TimeSpan.FromMilliseconds(460), (0, 0), (0.3, -7), (1, 0));
        settle.BeginTime = rise;
        Drift.BeginAnimation(TranslateTransform.YProperty, settle);
    }

    public void MoveTo(double top)
    {
        if (Math.Abs(Top - (top - Inset)) < 0.5)
        {
            return;
        }

        AnimateTop(top - Inset, TimeSpan.FromMilliseconds(380), new CubicEase { EasingMode = EasingMode.EaseInOut });
    }

    public void Wobble()
    {
        Tilt.BeginAnimation(RotateTransform.AngleProperty, KeyFrames(TimeSpan.FromMilliseconds(650), (0, 0), (0.2, -9), (0.45, 7), (0.7, -3.5), (1, 0)));
        Drift.BeginAnimation(TranslateTransform.YProperty, KeyFrames(TimeSpan.FromMilliseconds(400), (0, 0), (0.4, -8), (1, 0)));
    }

    public void Pop()
    {
        if (IsPopping)
        {
            return;
        }

        IsPopping = true;
        IsHitTestVisible = false;

        var grow = new DoubleAnimation(1.28, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
    }

    private void AnimateTop(double target, TimeSpan duration, IEasingFunction easing)
    {
        var id = ++_topAnimationId;
        var animation = new DoubleAnimation(Top, target, duration) { EasingFunction = easing };
        animation.Completed += (_, _) =>
        {
            if (id != _topAnimationId)
            {
                return;
            }

            BeginAnimation(TopProperty, null);
            Top = target;
        };
        BeginAnimation(TopProperty, animation);
    }

    private void AnimateHover(double scale, double milliseconds)
    {
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static DoubleAnimationUsingKeyFrames KeyFrames(TimeSpan duration, params (double At, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.Stop };
        var spline = new KeySpline(0.42, 0, 0.58, 1);
        foreach (var (at, value) in frames)
        {
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(value, KeyTime.FromPercent(at), spline));
        }

        return animation;
    }

    private void ApplyColor(Color color)
    {
        var light = Blend(color, Colors.White, 0.42);
        var dark = Blend(color, Colors.Black, 0.30);

        var body = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.32, 0.26),
            Center = new Point(0.44, 0.38),
            RadiusX = 0.78,
            RadiusY = 0.82,
        };
        body.GradientStops.Add(new GradientStop(light, 0));
        body.GradientStops.Add(new GradientStop(color, 0.5));
        body.GradientStops.Add(new GradientStop(dark, 1));
        body.Freeze();

        Body.Fill = body;
        Body.Stroke = Solid(Blend(color, Colors.Black, 0.45));
        Knot.Fill = Solid(dark);

        var text = Solid(TextColorFor(color));
        TitleText.Foreground = text;
        BodyText.Foreground = text;
        HintText.Foreground = text;
    }

    private static Color TextColorFor(Color background) =>
        Contrast(Colors.White, background) >= 3.2 ? Colors.White : DarkText;

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
