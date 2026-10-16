namespace CopilotSessionTray.Core.Models;

/// <summary>A rectangle in device-independent pixels, independent of any UI framework.</summary>
public readonly record struct StickerRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}
