namespace CopilotSessionTray.Core.Models;

/// <summary>A session the user pinned as a sticker.</summary>
/// <param name="SessionId">The pinned session.</param>
/// <param name="PinnedAtUtc">When it was pinned; stickers are arranged oldest pin first.</param>
/// <param name="Placement">Where the user last left the sticker and how big they made it.</param>
public sealed record PinnedSticker(string SessionId, DateTimeOffset PinnedAtUtc, StickerPlacement Placement);
