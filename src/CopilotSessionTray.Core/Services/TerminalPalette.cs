using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// The colors a terminal can be opened in. The nine of them were chosen to look as different from
/// each other as possible (both the dark pane backgrounds and the bright tab colors), and they come
/// in an order that keeps terminals opened one after another far apart. Red is left out on purpose:
/// in this app it only ever means that a session is waiting for you.
/// </summary>
public static class TerminalPalette
{
    public static IReadOnlyList<TerminalColor> Colors { get; } =
    [
        new("Blue", "#082D4B", "#5CAAFA"),
        new("Orange", "#462105", "#FC8635"),
        new("Green", "#073305", "#24BF2C"),
        new("Pink", "#55083A", "#FB78C3"),
        new("Gold", "#312B05", "#B9A717"),
        new("Indigo", "#281F66", "#AF96FA"),
        new("Cyan", "#063037", "#25B5CD"),
        new("Orchid", "#4C0B54", "#ED76FB"),
        new("Emerald", "#053221", "#22BC86"),
    ];

    /// <summary>How many colors there are.</summary>
    public static int Count => Colors.Count;

    /// <summary>Maps any number, including a negative or too large one from an edited file, to a palette slot.</summary>
    public static int Wrap(int index) => ((index % Colors.Count) + Colors.Count) % Colors.Count;

    public static TerminalColor At(int index) => Colors[Wrap(index)];
}
