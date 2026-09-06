namespace ProMeter.Codex;

public readonly record struct RingPoint(double X, double Y);

public readonly record struct RingArc(
    RingPoint Start,
    RingPoint End,
    bool IsLargeArc,
    bool Visible,
    bool IsFullCircle);

/// <summary>
/// Pure geometry for a donut/ring gauge. Kept free of any UI framework type so it can be
/// unit tested without WPF, and reused by the WPF code-behind that draws the Path/ArcSegment.
/// </summary>
public static class RingGeometry
{
    private const double StartAngleDegrees = -90;

    public static RingArc ComputeUsedArc(double? usedPercent, double centerX, double centerY, double radius)
    {
        if (usedPercent is null)
        {
            return new RingArc(default, default, false, false, false);
        }

        var clamped = Math.Clamp(usedPercent.Value, 0, 100);
        if (clamped <= 0)
        {
            return new RingArc(default, default, false, false, false);
        }

        if (clamped >= 100)
        {
            return new RingArc(default, default, false, false, true);
        }

        var sweepDegrees = clamped / 100.0 * 360.0;
        var endAngle = StartAngleDegrees + sweepDegrees;
        var start = PointOnCircle(centerX, centerY, radius, StartAngleDegrees);
        var end = PointOnCircle(centerX, centerY, radius, endAngle);
        var isLargeArc = sweepDegrees > 180.0;
        return new RingArc(start, end, isLargeArc, true, false);
    }

    private static RingPoint PointOnCircle(double centerX, double centerY, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new RingPoint(centerX + (radius * Math.Cos(radians)), centerY + (radius * Math.Sin(radians)));
    }
}
