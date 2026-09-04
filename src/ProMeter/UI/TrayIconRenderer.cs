using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ProMeter.Models;
using DrawingColor = System.Drawing.Color;
using Font = System.Drawing.Font;
using Pen = System.Drawing.Pen;
using SolidBrush = System.Drawing.SolidBrush;

namespace ProMeter.UI;

public static class TrayIconRenderer
{
    public static Icon Render(QuotaSnapshot snapshot, TrayIconStyle style, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(DrawingColor.Transparent);

        var remaining = snapshot.Remaining;
        var ratio = snapshot.Limit <= 0 ? 0 : snapshot.Remaining / (double)snapshot.Limit;
        var fill = ratio switch
        {
            <= 0 => DrawingColor.FromArgb(248, 113, 113),
            <= 0.2 => DrawingColor.FromArgb(251, 191, 36),
            _ => DrawingColor.FromArgb(59, 130, 246)
        };

        if (style == TrayIconStyle.ProgressRing)
        {
            using var bg = new Pen(DrawingColor.FromArgb(60, 255, 255, 255), Math.Max(2f, size / 8f));
            using var fg = new Pen(fill, Math.Max(2f, size / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var pad = size / 8f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            graphics.DrawArc(bg, rect, -90, 360);
            graphics.DrawArc(fg, rect, -90, (float)(360 * ratio));
            DrawNumber(graphics, remaining, size, DrawingColor.White);
        }
        else
        {
            using var brush = new SolidBrush(fill);
            graphics.FillEllipse(brush, 1, 1, size - 2, size - 2);
            DrawNumber(graphics, remaining, size, DrawingColor.White);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    private static void DrawNumber(Graphics graphics, int remaining, int size, DrawingColor color)
    {
        var text = remaining >= 100 ? "99+" : remaining.ToString(CultureInfo.InvariantCulture);
        var fontSize = remaining >= 100 ? size * 0.38f : remaining >= 10 ? size * 0.48f : size * 0.62f;
        using var font = new Font("Segoe UI Semibold", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var bounds = graphics.VisibleClipBounds;
        var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(text, font, brush, bounds, format);
    }
}
