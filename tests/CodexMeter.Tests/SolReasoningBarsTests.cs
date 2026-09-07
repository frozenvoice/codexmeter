using CodexMeter.Models;

namespace CodexMeter.Tests;

public class SolReasoningBarsTests
{
    [Fact]
    public void AllZero_ProducesEmptyBarsWithoutDivideByZero()
    {
        var bars = SolBarSet.Compute(new ReasoningStats());
        Assert.Equal(0, bars.Today);
        Assert.Equal(0, bars.ThisWeek);
        Assert.Equal(0, bars.Medium);
        Assert.Equal(0, bars.High);
        Assert.Equal(0, bars.ExtraHigh);
    }

    [Fact]
    public void OneCategoryFull_GetsMaximumFraction_OthersScaleRelative()
    {
        var stats = new ReasoningStats { Today = 5, ThisWeek = 5, Medium = 0, High = 0, ExtraHigh = 5 };
        var bars = SolBarSet.Compute(stats);
        Assert.Equal(1.0, bars.Today);
        Assert.Equal(1.0, bars.ThisWeek);
        Assert.Equal(1.0, bars.ExtraHigh);
        Assert.Equal(0, bars.Medium);
        Assert.Equal(0, bars.High);
    }

    [Fact]
    public void Mixed_ScalesRelativeToLargestCategory_NotAQuotaDenominator()
    {
        var stats = new ReasoningStats { Today = 2, ThisWeek = 10, Medium = 4, High = 1, ExtraHigh = 0 };
        var bars = SolBarSet.Compute(stats);
        Assert.Equal(0.2, bars.Today, 3);
        Assert.Equal(1.0, bars.ThisWeek, 3);
        Assert.Equal(0.4, bars.Medium, 3);
        Assert.Equal(0.1, bars.High, 3);
        Assert.Equal(0, bars.ExtraHigh);
        Assert.True(bars.Today <= 1 && bars.ThisWeek <= 1 && bars.Medium <= 1 && bars.High <= 1 && bars.ExtraHigh <= 1);
    }

    [Fact]
    public void NegativeInputsNeverProduceNegativeFractions()
    {
        var stats = new ReasoningStats { Today = -3, ThisWeek = 5 };
        var bars = SolBarSet.Compute(stats);
        Assert.True(bars.Today >= 0);
    }
}
