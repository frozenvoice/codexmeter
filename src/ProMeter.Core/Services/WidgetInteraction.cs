namespace ProMeter.Services;

public static class WidgetInteraction
{
    public const double DragThresholdDip = 6;

    public static bool IsDrag(double startX, double startY, double currentX, double currentY) =>
        IsDrag(currentX - startX, currentY - startY);

    public static bool IsDrag(double deltaX, double deltaY) =>
        Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)) >= DragThresholdDip;

    public static bool IsClick(double startX, double startY, double currentX, double currentY) =>
        !IsDrag(startX, startY, currentX, currentY);
}

public sealed class OnceEventSubscription
{
    private int _count;

    public int Count => _count;

    public bool TrySubscribe(Action subscribe)
    {
        if (Interlocked.Exchange(ref _count, 1) != 0)
        {
            return false;
        }

        subscribe();
        return true;
    }
}
