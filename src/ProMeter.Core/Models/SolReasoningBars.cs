namespace ProMeter.Models;

/// <summary>
/// Relative bar lengths (0-1) for the Sol Reasoning card. This is a plain count-comparison
/// visualization, not a quota progress meter: fractions are relative to the largest observed
/// category in the same snapshot, never to a denominator/limit.
/// </summary>
public readonly record struct SolBarSet(
    double Today,
    double ThisWeek,
    double Medium,
    double High,
    double ExtraHigh)
{
    public static SolBarSet Compute(ReasoningStats stats)
    {
        var max = Math.Max(stats.Today, Math.Max(stats.ThisWeek, Math.Max(stats.Medium, Math.Max(stats.High, stats.ExtraHigh))));
        if (max <= 0)
        {
            return new SolBarSet(0, 0, 0, 0, 0);
        }

        return new SolBarSet(
            Fraction(stats.Today, max),
            Fraction(stats.ThisWeek, max),
            Fraction(stats.Medium, max),
            Fraction(stats.High, max),
            Fraction(stats.ExtraHigh, max));
    }

    private static double Fraction(int value, int max) => max <= 0 ? 0 : Math.Clamp(value / (double)max, 0, 1);
}
