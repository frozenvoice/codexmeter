using System.Windows.Controls;

namespace CodexMeter.UI;

// Current accounts all use Codex. This is a provider label, not a provider selector.
public sealed class CodexProviderBadge : Border
{
    public CodexProviderBadge()
    {
        Tag = "UsageProviderBadge";
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 2, 8, 2);
        BorderThickness = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, "ProviderBadgeBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "ProviderBadgeBorderBrush");
        var label = new TextBlock
        {
            Text = UiText.CodexProviderName, FontSize = 11, FontWeight = FontWeights.SemiBold
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ProviderBadgeTextBrush");
        Child = label;
    }
}
