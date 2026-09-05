namespace ProMeter.Codex;

public enum RefreshIndicatorTransition
{
    None,
    Started,
    Stopped
}

public sealed class RefreshIndicatorController
{
    public const double DurationSeconds = 0.9;
    public const double StripDurationSeconds = 1.1;

    public bool IsAnimating { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public double Angle { get; private set; }

    public RefreshIndicatorTransition Apply(bool active)
    {
        if (active)
        {
            if (IsAnimating)
            {
                return RefreshIndicatorTransition.None;
            }

            IsAnimating = true;
            StartCount++;
            return RefreshIndicatorTransition.Started;
        }

        if (!IsAnimating && Angle == 0)
        {
            return RefreshIndicatorTransition.None;
        }

        IsAnimating = false;
        Angle = 0;
        StopCount++;
        return RefreshIndicatorTransition.Stopped;
    }

    public RefreshIndicatorTransition Reset()
    {
        if (!IsAnimating && Angle == 0)
        {
            return RefreshIndicatorTransition.None;
        }

        IsAnimating = false;
        Angle = 0;
        StopCount++;
        return RefreshIndicatorTransition.Stopped;
    }
}
