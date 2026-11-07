using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class TerminalPaletteTests
{
    private const string LightText = "#CCCCCC";

    public static TheoryData<int> Slots() => new(Enumerable.Range(0, TerminalPalette.Count));

    [Theory]
    [MemberData(nameof(Slots))]
    public void EveryBackground_IsDarkEnoughForTheDefaultLightText(int slot)
    {
        var color = TerminalPalette.At(slot);

        Assert.True(ColorMath.Contrast(color.Background, LightText) >= 7, $"{LightText} on {color.Background} ({color.Name})");
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void EveryAccent_IsBrightAgainstTheTabStrip_AndReadsWithBlackTabText(int slot)
    {
        var color = TerminalPalette.At(slot);

        Assert.True(ColorMath.Contrast(color.Accent, "#333333") >= 3, $"{color.Accent} ({color.Name})");
        Assert.True(ColorMath.Contrast(color.Accent, "#000000") >= 7, $"{color.Accent} ({color.Name})");
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void NoColor_IsRed_BecauseRedIsKeptForASessionThatNeedsYou(int slot)
    {
        var color = TerminalPalette.At(slot);

        Assert.False(ColorMath.IsRed(color.Background), $"{color.Background} ({color.Name})");
        Assert.False(ColorMath.IsRed(color.Accent), $"{color.Accent} ({color.Name})");
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void AnAccent_AndItsBackground_ShareTheirHue(int slot)
    {
        var color = TerminalPalette.At(slot);

        Assert.True(ColorMath.HueDistance(color.Accent, color.Background) <= 10, $"{color.Accent} / {color.Background} ({color.Name})");
    }

    [Fact]
    public void EveryColor_IsDifferent()
    {
        Assert.Equal(TerminalPalette.Count, TerminalPalette.Colors.Select(color => color.Name).Distinct().Count());
        Assert.Equal(TerminalPalette.Count, TerminalPalette.Colors.Select(color => color.Background).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(TerminalPalette.Count, TerminalPalette.Colors.Select(color => color.Accent).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void NoTwoColors_LookAlike()
    {
        foreach (var first in Enumerable.Range(0, TerminalPalette.Count))
        {
            foreach (var second in Enumerable.Range(first + 1, TerminalPalette.Count - first - 1))
            {
                var a = TerminalPalette.At(first);
                var b = TerminalPalette.At(second);

                Assert.True(ColorMath.Difference(a.Background, b.Background) >= 15, $"{a.Name} and {b.Name} backgrounds");
                Assert.True(ColorMath.Difference(a.Accent, b.Accent) >= 25, $"{a.Name} and {b.Name} accents");
            }
        }
    }

    [Fact]
    public void TheFirstFourColors_AreTheMostDifferentOnes_BecauseThatIsHowManyTerminalsAreUsuallyOpen()
    {
        foreach (var first in Enumerable.Range(0, 4))
        {
            foreach (var second in Enumerable.Range(first + 1, 3 - first))
            {
                var a = TerminalPalette.At(first);
                var b = TerminalPalette.At(second);

                Assert.True(ColorMath.Difference(a.Background, b.Background) >= 35, $"{a.Name} and {b.Name} backgrounds");
                Assert.True(ColorMath.Difference(a.Accent, b.Accent) >= 60, $"{a.Name} and {b.Name} accents");
            }
        }
    }

    [Fact]
    public void ColorsThatComeOneAfterTheOther_AreFarApart_EvenWhenTheLastIsFollowedByTheFirst()
    {
        foreach (var slot in Enumerable.Range(0, TerminalPalette.Count))
        {
            var current = TerminalPalette.At(slot);
            var next = TerminalPalette.At(slot + 1);

            Assert.True(ColorMath.Difference(current.Background, next.Background) >= 30, $"{current.Name} then {next.Name} backgrounds");
            Assert.True(ColorMath.Difference(current.Accent, next.Accent) >= 50, $"{current.Name} then {next.Name} accents");
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(-1, 8)]
    [InlineData(9, 0)]
    [InlineData(32, 5)]
    [InlineData(int.MinValue, 7)]
    [InlineData(int.MaxValue, 1)]
    public void Wrap_MapsAnyNumberToASlot(int index, int expected)
    {
        Assert.Equal(9, TerminalPalette.Count);
        Assert.Equal(expected, TerminalPalette.Wrap(index));
        Assert.Equal(TerminalPalette.Colors[expected], TerminalPalette.At(index));
    }
}
