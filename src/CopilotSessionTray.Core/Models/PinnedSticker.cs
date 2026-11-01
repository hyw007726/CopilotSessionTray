namespace CopilotSessionTray.Core.Models;

/// <summary>A session the user put on the desktop as a sticker.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="PinnedAtUtc">When the sticker was added; stickers are arranged oldest first.</param>
/// <param name="Placement">Where the user last left the sticker and how big they made it.</param>
/// <param name="HiddenAtStatus">The session's status when the user closed the sticker with its X: it stays hidden until the status changes. <see langword="null"/> while the sticker is shown.</param>
public sealed record PinnedSticker(string SessionId, DateTimeOffset PinnedAtUtc, StickerPlacement Placement, SessionStatus? HiddenAtStatus = null);
