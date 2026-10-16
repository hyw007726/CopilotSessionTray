namespace CopilotSessionTray.Core.Models;

/// <summary>A sticker's remembered position and size; a part the user never set is null.</summary>
public sealed record StickerPlacement(double? Left = null, double? Top = null, double? Width = null, double? Height = null)
{
    public static StickerPlacement None { get; } = new();

    public bool HasPosition => Left.HasValue && Top.HasValue;

    public bool HasSize => Width.HasValue && Height.HasValue;
}
