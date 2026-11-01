namespace CopilotSessionTray.Core.Services;

/// <summary>
/// How far a mouse-wheel or touchpad event moves a scrolled list or text. WPF moves a full notch for
/// every wheel message however small, so a precision touchpad, which sends many small ones, flies
/// through a list; here the distance follows the size of the event instead.
/// </summary>
public static class WheelScrollMath
{
    /// <summary>The wheel delta of one notch of a classic wheel.</summary>
    public const double NotchDelta = 120;

    /// <summary>The height of one line of scrolling, in device-independent pixels (WPF's own figure for pixel scrolling).</summary>
    public const double LinePixels = 16;

    /// <summary>The vertical offset, in pixels, after one wheel event.</summary>
    /// <param name="offset">The current offset.</param>
    /// <param name="delta">The wheel delta: 120 is one notch away from the user, which scrolls up; negative scrolls down.</param>
    /// <param name="linesPerNotch">Windows' "lines to scroll at a time" setting; negative means one page per notch.</param>
    /// <param name="viewportHeight">The height of the visible area, used as the page size.</param>
    /// <param name="maxOffset">The largest offset (content height minus viewport height).</param>
    public static double Offset(double offset, int delta, int linesPerNotch, double viewportHeight, double maxOffset)
    {
        var upper = double.IsFinite(maxOffset) ? Math.Max(0, maxOffset) : 0;
        var page = double.IsFinite(viewportHeight) ? Math.Max(0, viewportHeight) : 0;
        var step = linesPerNotch < 0 ? page : linesPerNotch * LinePixels;
        var current = double.IsFinite(offset) ? offset : 0;
        return Math.Clamp(current - delta / NotchDelta * step, 0, upper);
    }
}
