namespace CycleArc.Services;

public static class QuotaPeriodCalculator
{
    public static (DateTimeOffset Start, DateTimeOffset End) CurrentPeriod(AppSettings settings, DateTimeOffset now, DateTimeOffset? serverReset = null)
    {
        if (serverReset is DateTimeOffset reset)
        {
            return PeriodContaining(now, reset);
        }

        var zone = ResolveZone(settings.ResetTimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var localAnchor = NextOrSameAnchor(localNow, settings.ResetWeekday, settings.ResetTime);
        if (localNow >= localAnchor)
        {
            localAnchor = localAnchor.AddDays(7);
        }

        var localStart = localAnchor.AddDays(-7);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart.DateTime, DateTimeKind.Unspecified), zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localAnchor.DateTime, DateTimeKind.Unspecified), zone);
        return (new DateTimeOffset(startUtc, TimeSpan.Zero), new DateTimeOffset(endUtc, TimeSpan.Zero));
    }

    public static (DateTimeOffset Start, DateTimeOffset End) PeriodContaining(DateTimeOffset now, DateTimeOffset resetBoundary)
    {
        var end = resetBoundary;
        var start = end.AddDays(-7);
        var guard = 0;
        while (now >= end && guard++ < 5200)
        {
            start = end;
            end = end.AddDays(7);
        }

        guard = 0;
        while (now < start && guard++ < 5200)
        {
            end = start;
            start = start.AddDays(-7);
        }

        return (start, end);
    }

    public static DateTimeOffset LocalDayStart(DateTimeOffset now, AppSettings settings)
    {
        var zone = ResolveZone(settings.ResetTimeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return start.ToUniversalTime();
    }

    public static bool InRange(DateTimeOffset value, DateTimeOffset start, DateTimeOffset end) =>
        value >= start && value < end;

    public static DateTimeOffset LocalWeekStart(DateTimeOffset now, AppSettings settings)
    {
        var zone = ResolveZone(settings.ResetTimeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var days = ((int)local.DayOfWeek - (int)settings.ResetWeekday + 7) % 7;
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset).AddDays(-days);
        return start.ToUniversalTime();
    }

    public static DateOnly LocalDate(DateTimeOffset value, string? timeZoneId)
    {
        var zone = ResolveZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(value, zone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    private static DateTimeOffset NextOrSameAnchor(DateTimeOffset localNow, DayOfWeek weekday, TimeSpan time)
    {
        var days = ((int)weekday - (int)localNow.DayOfWeek + 7) % 7;
        var date = localNow.Date.AddDays(days).Add(time);
        return new DateTimeOffset(date, localNow.Offset);
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }
}
