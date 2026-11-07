namespace CopilotSessionTray.Core.Models;

/// <summary>A label the user put on a session, shown under its name in the popup.</summary>
/// <param name="Text">What the label says: one trimmed line.</param>
/// <param name="ColorIndex">The palette slot that colors it; labels with the same text share a slot.</param>
public sealed record SessionLabel(string Text, int ColorIndex);
