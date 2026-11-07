namespace CopilotSessionTray.Core.Models;

/// <summary>The colors that make one terminal easy to tell from the others.</summary>
/// <param name="Name">What the color is called.</param>
/// <param name="Background">The dark fill of the terminal pane, as <c>#RRGGBB</c>.</param>
/// <param name="Accent">The bright color of the terminal's tab, as <c>#RRGGBB</c>.</param>
public readonly record struct TerminalColor(string Name, string Background, string Accent);
