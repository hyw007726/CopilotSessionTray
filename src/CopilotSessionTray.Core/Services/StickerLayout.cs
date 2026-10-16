using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>Pure geometry for stickers, in device-independent pixels.</summary>
public static class StickerLayout
{
    public const double DefaultWidth = 240;
    public const double DefaultHeight = 124;
    public const double MinWidth = 170;
    public const double MinHeight = 88;
    public const double MinFontScale = 0.85;
    public const double Margin = 8;
    public const double Gap = 8;

    private const double GrabWidth = 48;
    private const double GrabHeight = 24;
    private const double CascadeStep = 24;
    private const double Epsilon = 0.001;

    /// <summary>
    /// Finds the first free spot for a sticker of the given size, scanning down the left edge of
    /// <paramref name="workArea"/> and wrapping to the next column when a column fills up. Falls
    /// back to a cascade from the top-left corner when nothing fits.
    /// </summary>
    public static StickerRect FindSlot(StickerRect workArea, double width, double height, IReadOnlyCollection<StickerRect> occupied)
    {
        var left = workArea.Left + Margin;
        var top = workArea.Top + Margin;
        var right = workArea.Right - Margin;
        var bottom = workArea.Bottom - Margin;

        for (var x = left; x + width <= right + Epsilon; x += width + Gap)
        {
            var y = top;
            while (y + height <= bottom + Epsilon)
            {
                var candidate = new StickerRect(x, y, width, height);
                var blocking = occupied.Where(other => Conflicts(candidate, other)).ToList();
                if (blocking.Count == 0)
                {
                    return candidate;
                }

                y = blocking.Max(other => other.Bottom) + Gap;
            }
        }

        var offset = occupied.Count % 8 * CascadeStep;
        return new StickerRect(left + offset, top + offset, width, height);
    }

    /// <summary>Applies the default size to unset parts and keeps the result between the minimum and the work area.</summary>
    public static (double Width, double Height) ClampSize(double? width, double? height, StickerRect workArea) => (
        Math.Clamp(width ?? DefaultWidth, MinWidth, Math.Max(MinWidth, workArea.Width)),
        Math.Clamp(height ?? DefaultHeight, MinHeight, Math.Max(MinHeight, workArea.Height)));

    /// <summary>The factor a sticker's text is scaled by at the given size; 1 at the default size.</summary>
    public static double FontScale(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return 1;
        }

        var widthRatio = width / DefaultWidth;
        var heightRatio = height / DefaultHeight;
        return Math.Max(MinFontScale, 2 * widthRatio * heightRatio / (widthRatio + heightRatio));
    }

    /// <summary>
    /// Whether enough of a sticker's top edge lies inside <paramref name="screen"/> for the user to
    /// grab it and reach its close button.
    /// </summary>
    public static bool IsReachable(StickerRect screen, StickerRect sticker)
    {
        var overlapWidth = Math.Min(screen.Right, sticker.Right) - Math.Max(screen.Left, sticker.Left);
        var topInside = sticker.Top >= screen.Top - Epsilon && sticker.Top + GrabHeight <= screen.Bottom + Epsilon;
        var closeInside = sticker.Right - GrabWidth >= screen.Left - Epsilon && sticker.Right <= screen.Right + Epsilon;
        return topInside && closeInside && overlapWidth >= Math.Min(GrabWidth, sticker.Width);
    }

    private static bool Conflicts(StickerRect a, StickerRect b) =>
        a.Left < b.Right + Gap && b.Left < a.Right + Gap && a.Top < b.Bottom + Gap && b.Top < a.Bottom + Gap;
}
