namespace CopilotSessionTray.Core.Models;

/// <summary>Settings shared by every sticker.</summary>
/// <param name="Visible">Whether stickers are shown at all; pins are kept while this is off.</param>
/// <param name="Opacity">How opaque stickers are, from <see cref="MinOpacity"/> to <see cref="MaxOpacity"/>.</param>
public sealed record StickerSettings(bool Visible = true, double Opacity = StickerSettings.DefaultOpacity)
{
    public const double MinOpacity = 0.2;
    public const double MaxOpacity = 1.0;
    public const double DefaultOpacity = 0.9;

    public static double ClampOpacity(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinOpacity, MaxOpacity) : DefaultOpacity;
}
