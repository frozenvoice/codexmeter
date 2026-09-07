using CodexMeter.Codex;

namespace CodexMeter.UI;

public static class DesktopWorkAreas
{
    public static IReadOnlyList<ScreenRect> For(Window window)
    {
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return System.Windows.Forms.Screen.AllScreens.OrderByDescending(screen => screen.Primary).Select(screen =>
        {
            var area = screen.WorkingArea;
            var topLeft = transform.Transform(new System.Windows.Point(area.Left, area.Top));
            var bottomRight = transform.Transform(new System.Windows.Point(area.Right, area.Bottom));
            return new ScreenRect((int)topLeft.X, (int)topLeft.Y,
                (int)Math.Max(1, bottomRight.X - topLeft.X), (int)Math.Max(1, bottomRight.Y - topLeft.Y));
        }).ToArray();
    }
}
