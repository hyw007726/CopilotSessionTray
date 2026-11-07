using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class SessionLabelPaletteTests
{
    public static TheoryData<int> Slots() => new(Enumerable.Range(0, SessionLabelPalette.Count));

    [Theory]
    [MemberData(nameof(Slots))]
    public void EveryColor_ReadsOnItsFill(int slot)
    {
        var color = SessionLabelPalette.At(slot);

        Assert.True(ColorMath.Contrast(color.Background, color.Foreground) >= 4.5, $"{color.Foreground} on {color.Background}");
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void NoColor_IsRed_BecauseRedIsKeptForASessionThatNeedsYou(int slot)
    {
        var background = SessionLabelPalette.At(slot).Background;
        var (hue, saturation) = ColorMath.HueAndSaturation(background);

        Assert.False(ColorMath.IsRed(background), $"hue {hue:0}, saturation {saturation:0.00}");
    }

    [Fact]
    public void EveryFill_IsDifferent()
    {
        var fills = SessionLabelPalette.Colors.Select(color => color.Background).ToList();

        Assert.Equal(fills.Count, fills.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(-1, 9)]
    [InlineData(10, 0)]
    [InlineData(32, 2)]
    [InlineData(int.MinValue, 2)]
    [InlineData(int.MaxValue, 7)]
    public void Wrap_MapsAnyNumberToASlot(int index, int expected)
    {
        Assert.Equal(10, SessionLabelPalette.Count);
        Assert.Equal(expected, SessionLabelPalette.Wrap(index));
        Assert.Equal(SessionLabelPalette.Colors[expected], SessionLabelPalette.At(index));
    }
}
