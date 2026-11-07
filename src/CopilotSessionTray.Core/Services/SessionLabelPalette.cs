using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.Core.Services;

/// <summary>
/// The colors a session label can take. Red is left out on purpose: in this app it only ever
/// means that a session is waiting for you.
/// </summary>
public static class SessionLabelPalette
{
    private const string Ink = "#1F2933";

    private const string White = "#FFFFFF";

    public static IReadOnlyList<LabelColor> Colors { get; } =
    [
        new("#1565C0", White),
        new("#FFC107", Ink),
        new("#7B1FA2", White),
        new("#00796B", White),
        new("#546E7A", White),
        new("#FB8C00", Ink),
        new("#3949AB", White),
        new("#C0CA33", Ink),
        new("#00ACC1", Ink),
        new("#6D4C41", White),
    ];

    /// <summary>How many colors there are.</summary>
    public static int Count => Colors.Count;

    /// <summary>Maps any number, including a negative or too large one from an edited file, to a palette slot.</summary>
    public static int Wrap(int index) => ((index % Colors.Count) + Colors.Count) % Colors.Count;

    public static LabelColor At(int index) => Colors[Wrap(index)];
}
