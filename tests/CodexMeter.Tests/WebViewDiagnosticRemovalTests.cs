using System.Xml.Linq;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class WebViewDiagnosticRemovalTests
{
    [Fact]
    public void SettingsWindow_NoLongerContainsWebViewDiagnosticControls()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SettingsWindow.xaml")).ToString();
        Assert.DoesNotContain("TestWebViewButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FullWebViewVerificationButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWebViewDefaultButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WebViewResultText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WebViewComparisonText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WebViewTechnicalDetailText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TransportBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView2 fallback", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterButton", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AppAndSettings_NoLongerWireWebViewDiagnosticCallbacks()
    {
        var app = File.ReadAllText(Find("src/CodexMeter/App.xaml.cs"));
        var settings = File.ReadAllText(Find("src/CodexMeter/UI/SettingsWindow.xaml.cs"));
        Assert.DoesNotContain("WebViewDiagnosticRequested", app, StringComparison.Ordinal);
        Assert.DoesNotContain("FullWebViewVerificationRequested", app, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWebViewDefaultRequested", app, StringComparison.Ordinal);
        Assert.DoesNotContain("RunWebViewDiagnosticAsync", app, StringComparison.Ordinal);
        Assert.DoesNotContain("RunFullWebViewVerificationAsync", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyVerifiedWebViewDefault", app, StringComparison.Ordinal);
        Assert.DoesNotContain("WebViewDiagnosticRequested", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("OnTestWebView", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("OnFullWebViewVerification", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("OnUseWebViewDefault", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("_ => _webViewTransport", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowLoginAsync", app, StringComparison.Ordinal);
        Assert.Contains("AuthTransportKind.WebView2", File.ReadAllText(Find("src/CodexMeter/UI/WelcomeWindow.xaml.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void WebView2FallbackTransport_RemainsAvailable()
    {
        Assert.Equal(1, (int)AuthTransportKind.WebView2);
        Assert.Equal("WebView2 fallback", UiText.TransportWebView);
        var transport = File.ReadAllText(Find("src/CodexMeter/WebView/WebViewTransport.cs"));
        Assert.Contains("class WebViewTransport", transport, StringComparison.Ordinal);
        Assert.Contains("IWebViewInteractiveLogin", transport, StringComparison.Ordinal);
        Assert.DoesNotContain("IWebViewDiagnosticHost", transport, StringComparison.Ordinal);
    }

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
