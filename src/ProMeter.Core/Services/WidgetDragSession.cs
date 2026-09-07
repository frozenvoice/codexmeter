namespace ProMeter.Services;

/// <summary>Tracks a captured pointer in stable screen coordinates, independent of window motion.</summary>
public sealed class WidgetDragSession(double left, double top, double screenX, double screenY)
{
    public bool IsDragging { get; private set; }
    public (double Left, double Top) Move(double currentScreenX, double currentScreenY)
    {
        var dx = currentScreenX - screenX;
        var dy = currentScreenY - screenY;
        IsDragging |= WidgetInteraction.IsDrag(dx, dy);
        return IsDragging ? (left + dx, top + dy) : (left, top);
    }
}
