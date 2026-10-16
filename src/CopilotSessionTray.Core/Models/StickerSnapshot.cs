namespace CopilotSessionTray.Core.Models;

/// <summary>Everything persisted about stickers: the shared settings and the pinned sessions, oldest pin first.</summary>
public sealed record StickerSnapshot(StickerSettings Settings, IReadOnlyList<PinnedSticker> Pinned);
