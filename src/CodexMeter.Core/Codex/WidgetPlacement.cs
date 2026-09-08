namespace CodexMeter.Codex;

public static class WidgetPlacement
{
    // Work areas are ordered with the primary monitor first.
    public static (double Left, double Top) Recover(double left, double top, double width, double height,
        IReadOnlyList<ScreenRect> workAreas, bool reset = false)
    {
        if (workAreas.Count == 0) return (40, 40);
        width = double.IsFinite(width) && width > 0 ? width : 180;
        height = double.IsFinite(height) && height > 0 ? height : 60;
        var area = workAreas[0];
        if (reset || !double.IsFinite(left) || !double.IsFinite(top))
        {
            left = area.X + 40;
            top = area.Y + 40;
        }
        else
        {
            var largestOverlap = 0d;
            foreach (var candidate in workAreas)
            {
                var overlap = Math.Max(0, Math.Min(left + width, candidate.Right) - Math.Max(left, candidate.X))
                    * Math.Max(0, Math.Min(top + height, candidate.Bottom) - Math.Max(top, candidate.Y));
                if (overlap > largestOverlap) { largestOverlap = overlap; area = candidate; }
            }
            if (largestOverlap == 0) { left = area.X + 40; top = area.Y + 40; }
        }
        // A fully visible widget is already valid, even within the flyout's 8px edge margin.
        // Do not move a user-positioned widget just because the application restarted.
        if (left >= area.X && top >= area.Y && left + width <= area.Right && top + height <= area.Bottom)
            return (left, top);
        return FlyoutPlacement.ClampToWorkArea(left, top, width, height, area);
    }
}
