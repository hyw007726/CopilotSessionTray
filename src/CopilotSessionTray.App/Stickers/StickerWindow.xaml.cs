using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CopilotSessionTray.App.ViewModels;
using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App.Stickers;

public partial class StickerWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly double _titleFontSize;
    private readonly double _gistFontSize;
    private readonly double _timeFontSize;

    public StickerWindow(SessionItemViewModel session)
    {
        InitializeComponent();
        Session = session;
        DataContext = session;
        _titleFontSize = TitleText.FontSize;
        _gistFontSize = GistText.FontSize;
        _timeFontSize = TimeText.FontSize;
        SizeChanged += OnSizeChanged;
    }

    public SessionItemViewModel Session { get; }

    public StickerRect PlacedBounds => new(Left, Top, Width, Height);

    public event EventHandler? HideRequested;

    public event EventHandler? ResumeRequested;

    public event EventHandler? PlacementCommitted;

    public void Place(StickerRect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = StickerLayout.FontScale(e.NewSize.Width, e.NewSize.Height);
        var titleFontSize = _titleFontSize * scale;
        var pixel = 1 / VisualTreeHelper.GetDpi(this).DpiScaleY;
        var titleLineHeight = Math.Ceiling(titleFontSize * TitleText.FontFamily.LineSpacing / pixel) * pixel;
        TitleText.FontSize = titleFontSize;
        TitleText.LineHeight = titleLineHeight;
        TitleText.MaxHeight = 2 * titleLineHeight;
        GistText.FontSize = _gistFontSize * scale;
        TimeText.FontSize = _timeFontSize * scale;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            ResumeRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var before = (Left, Top);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if ((Left, Top) != before)
        {
            PlacementCommitted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void RightEdge_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(e.HorizontalChange, 0);

    private void BottomEdge_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(0, e.VerticalChange);

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(e.HorizontalChange, e.VerticalChange);

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e) => PlacementCommitted?.Invoke(this, EventArgs.Empty);

    private void ResizeBy(double width, double height)
    {
        Width = Math.Clamp(Width + width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(Height + height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
