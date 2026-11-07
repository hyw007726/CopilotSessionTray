namespace CopilotSessionTray.Core.Models;

/// <summary>The colors of a session label.</summary>
/// <param name="Background">The fill, as <c>#RRGGBB</c>.</param>
/// <param name="Foreground">The text color that reads on the fill, as <c>#RRGGBB</c>.</param>
public readonly record struct LabelColor(string Background, string Foreground);
