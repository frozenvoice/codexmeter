using System.Windows.Controls;
using CodexMeter.Providers.Usage;

namespace CodexMeter.UI;

public sealed class UsageProviderBadge : Border
{
    private readonly TextBlock _label;
    private UsageProviderId _provider = UsageProviderId.Codex;
    public UsageProviderId Provider
    {
        get => _provider;
        set
        {
            _provider = value;
            _label.Text = value.Name();
            System.Windows.Automation.AutomationProperties.SetName(this, _label.Text);
        }
    }

    public UsageProviderBadge()
    {
        Tag = "UsageProviderBadge";
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 2, 8, 2);
        BorderThickness = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, "ProviderBadgeBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "ProviderBadgeBorderBrush");
        _label = new TextBlock
        {
            Text = UiText.CodexProviderName, FontSize = 11, FontWeight = FontWeights.SemiBold
        };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "ProviderBadgeTextBrush");
        Child = _label;
    }
}
