using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class WheelScrollMathTests
{
    [Theory]
    [InlineData(0, -120, 3, 48)]
    [InlineData(100, -120, 3, 148)]
    [InlineData(100, 120, 3, 52)]
    [InlineData(100, -240, 3, 196)]
    [InlineData(100, -120, 1, 116)]
    [InlineData(100, -120, 5, 180)]
    public void ANotch_MovesByTheLinesSetting_TimesSixteenPixels(double offset, int delta, int lines, double expected)
    {
        Assert.Equal(expected, WheelScrollMath.Offset(offset, delta, lines, viewportHeight: 300, maxOffset: 1000));
    }

    [Fact]
    public void SmallEvents_MoveInProportionToTheirSize_SoManyOfThemAddUpToOneNotch()
    {
        var offset = 0.0;
        for (var i = 0; i < 8; i++)
        {
            offset = WheelScrollMath.Offset(offset, -15, 3, 300, 1000);
        }

        Assert.Equal(48, offset, precision: 9);
        Assert.Equal(0.4, WheelScrollMath.Offset(0, -1, 3, 300, 1000), precision: 9);
    }

    [Fact]
    public void AnEventOfZero_MovesNothing()
    {
        Assert.Equal(77, WheelScrollMath.Offset(77, 0, 3, 300, 1000));
    }

    [Theory]
    [InlineData(10, 120, 0)]
    [InlineData(990, -120, 1000)]
    [InlineData(0, 120, 0)]
    [InlineData(1000, -120, 1000)]
    public void TheOffset_StaysBetweenTopAndBottom(double offset, int delta, double expected)
    {
        Assert.Equal(expected, WheelScrollMath.Offset(offset, delta, 3, 300, 1000));
    }

    [Fact]
    public void ANegativeLinesSetting_ScrollsAPageAtATime()
    {
        Assert.Equal(350, WheelScrollMath.Offset(50, -120, -1, viewportHeight: 300, maxOffset: 1000));
        Assert.Equal(200, WheelScrollMath.Offset(350, 60, -1, viewportHeight: 300, maxOffset: 1000));
    }

    [Fact]
    public void ZeroLines_MovesNothing()
    {
        Assert.Equal(50, WheelScrollMath.Offset(50, -120, 0, 300, 1000));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-300)]
    [InlineData(double.PositiveInfinity)]
    public void APageOfNothing_MovesNothing(double viewportHeight)
    {
        Assert.Equal(50, WheelScrollMath.Offset(50, -120, -1, viewportHeight, 1000));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(-5, 0)]
    public void ANonsensicalOffset_IsTreatedAsTheTop(double offset, double expected)
    {
        Assert.Equal(expected, WheelScrollMath.Offset(offset, 0, 3, 300, 1000));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-10)]
    [InlineData(double.NegativeInfinity)]
    public void NothingToScroll_MeansTheOffsetStaysAtTheTop(double maxOffset)
    {
        Assert.Equal(0, WheelScrollMath.Offset(20, -120, 3, 300, maxOffset));
    }
}
