using CodexMeter.Codex;

namespace CodexMeter.Tests;

public class WidgetPlacementTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(5000, 50)]
    [InlineData(double.NaN, 50)]
    [InlineData(50, double.PositiveInfinity)]
    public void MissingMonitorOrInvalidPositionReturnsToPrimary(double left, double top) =>
        Assert.Equal((40d, 40d), WidgetPlacement.Recover(left, top, 180, 60, [Primary]));

    [Fact]
    public void NegativeCoordinateMonitorIsPreserved() =>
        Assert.Equal((-1500d, 50d), WidgetPlacement.Recover(-1500, 50, 180, 60,
            [Primary, new(-1920, 0, 1920, 1040)]));

    [Fact]
    public void PartiallyClippedWidgetMovesEntirelyInsideWorkArea() =>
        Assert.Equal((1732d, 972d), WidgetPlacement.Recover(1900, 1020, 180, 60, [Primary]));

    [Fact]
    public void ResetIgnoresVisibleSecondaryPosition() =>
        Assert.Equal((40d, 40d), WidgetPlacement.Recover(-1500, 50, 180, 60,
            [Primary, new(-1920, 0, 1920, 1040)], reset: true));

    [Fact]
    public void OversizedWidgetRemainsAnchoredToSmallWorkArea() =>
        Assert.Equal((18d, 28d), WidgetPlacement.Recover(20, 30, 180, 60, [new(10, 20, 100, 40)]));
}
