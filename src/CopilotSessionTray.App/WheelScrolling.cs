using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.App;

/// <summary>
/// Opt-in wheel scrolling for a list or text panel whose scroll offset is in pixels: each wheel or
/// touchpad event moves it in proportion to its size (<see cref="WheelScrollMath"/>) instead of by a
/// full notch, so a precision touchpad does not fly through the content.
/// </summary>
public static class WheelScrolling
{
    public static readonly DependencyProperty IsProportionalProperty = DependencyProperty.RegisterAttached(
        "IsProportional", typeof(bool), typeof(WheelScrolling), new PropertyMetadata(false, OnIsProportionalChanged));

    public static bool GetIsProportional(DependencyObject element) => (bool)element.GetValue(IsProportionalProperty);

    public static void SetIsProportional(DependencyObject element, bool value) => element.SetValue(IsProportionalProperty, value);

    private static void OnIsProportionalChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue)
        {
            target.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || sender is not DependencyObject host)
        {
            return;
        }

        if (FindScrollViewer(host) is not { } viewer || viewer.ScrollableHeight <= 0 || !ScrollsInPixels(viewer, host))
        {
            return;
        }

        viewer.ScrollToVerticalOffset(WheelScrollMath.Offset(viewer.VerticalOffset, e.Delta, SystemParameters.WheelScrollLines, viewer.ViewportHeight, viewer.ScrollableHeight));
        e.Handled = true;
    }

    private static bool ScrollsInPixels(ScrollViewer viewer, DependencyObject host) =>
        host is not ItemsControl items || !viewer.CanContentScroll || VirtualizingPanel.GetScrollUnit(items) == ScrollUnit.Pixel;

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
