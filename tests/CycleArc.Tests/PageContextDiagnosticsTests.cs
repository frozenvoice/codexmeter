using System.Xml.Linq;
using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

public class PageContextDiagnosticsTests
{
    [Fact]
    public void ForbiddenResponse_IsNotUnauthorized()
    {
        var response = new ProviderResponse
        {
            Status = 403,
            Error = CompanionDiagnostics.Forbidden403
        };
        Assert.True(response.IsForbidden);
        Assert.False(response.IsUnauthorized);
        Assert.False(response.IsOffline);
        Assert.Equal(CompanionDiagnostics.Forbidden403, DisplayFormatting.StatusLabel(AppSyncStatus.Forbidden));
    }

    [Fact]
    public void NoChatGptTab_IsDistinctFromOfflineAndExpired()
    {
        var response = new ProviderResponse
        {
            Status = 0,
            Error = CompanionDiagnostics.NoChatGptTab
        };
        Assert.True(response.IsChatGptTabRequired);
        Assert.False(response.IsOffline);
        Assert.False(response.IsUnauthorized);
        Assert.False(response.IsPageBridgeUnavailable);
    }

    [Fact]
    public void PageBridgeUnavailable_IsDistinctFromOffline()
    {
        var response = new ProviderResponse
        {
            Status = 0,
            Error = CompanionDiagnostics.PageBridgeUnavailable
        };
        Assert.True(response.IsPageBridgeUnavailable);
        Assert.False(response.IsOffline);
        Assert.False(response.IsUnauthorized);
    }

    [Fact]
    public void ExceptionFlags_MatchDiagnosticMessages()
    {
        var forbidden = new ChatGptProviderException(CompanionDiagnostics.Forbidden403, 403);
        Assert.True(forbidden.IsForbidden);
        Assert.False(forbidden.IsUnauthorized);

        var tab = new ChatGptProviderException(CompanionDiagnostics.NoChatGptTab, 0);
        Assert.True(tab.IsChatGptTabRequired);
        Assert.False(tab.IsOffline);

        var bridge = new ChatGptProviderException(CompanionDiagnostics.PageBridgeUnavailable, 0);
        Assert.True(bridge.IsPageBridgeUnavailable);
        Assert.False(bridge.IsOffline);
    }

    [Fact]
    public async Task Session403_DoesNotReportExpired()
    {
        var transport = new PaginationTests.ScriptedTransport((_, path) =>
        {
            if (path == ChatGptEndpoints.Session)
            {
                return new ProviderResponse { Status = 403, Error = CompanionDiagnostics.Forbidden403 };
            }

            return new ProviderResponse { Status = 500, Error = "unexpected " + path };
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(() =>
            new ChatGptProvider(transport).GetAccountStatusAsync());
        Assert.Equal(403, ex.Status);
        Assert.Equal(CompanionDiagnostics.Forbidden403, ex.Message);
        Assert.DoesNotContain("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authentication required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session401_RemainsAuthenticationRequired()
    {
        var transport = new PaginationTests.ScriptedTransport((_, path) =>
        {
            if (path == ChatGptEndpoints.Session)
            {
                return new ProviderResponse { Status = 401, Error = "unauthorized" };
            }

            return new ProviderResponse { Status = 500, Error = "unexpected " + path };
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(() =>
            new ChatGptProvider(transport).GetAccountStatusAsync());
        Assert.True(ex.IsUnauthorized);
        Assert.False(ex.IsForbidden);
        Assert.Equal("Authentication required.", ex.Message);
    }

    [Fact]
    public void ProjectedSignedOutSession_IsNotTrustedFromAccessToken()
    {
        var signedOut = AccountParser.ParseSession(JsonNode.Parse("""{"signedIn":false,"user":{}}"""));
        Assert.False(signedOut.IsSignedIn);

        var signedIn = AccountParser.ParseSession(JsonNode.Parse("""{"signedIn":true,"user":{"id":"u1","email":"a@b.example"}}"""));
        Assert.True(signedIn.IsSignedIn);
        Assert.Equal("u1", signedIn.UserId);
    }
}

public class ThemeResourceTests
{
    private static readonly string[] RequiredTypes =
    [
        "TextBlock",
        "Label",
        "TextBox",
        "ComboBox",
        "ComboBoxItem",
        "CheckBox",
        "RadioButton",
        "TabControl",
        "TabItem",
        "ListBox",
        "ListView",
        "DataGrid",
        "DataGridColumnHeader",
        "ScrollViewer",
        "ContextMenu",
        "MenuItem"
    ];

    [Fact]
    public void ImplicitControlStyles_HaveReadableForegroundAndBackground()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Themes.xaml");
        Assert.True(File.Exists(path), "Themes.xaml was not copied to the test output");
        var xaml = File.ReadAllText(path);
        Assert.DoesNotContain("Foreground=\"Black\"", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Foreground=\"#000\"", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Foreground=\"#000000\"", xaml, StringComparison.OrdinalIgnoreCase);

        var document = XDocument.Load(path);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var styles = document.Root!.Elements(ns + "Style").ToList();

        foreach (var type in RequiredTypes)
        {
            var style = styles.FirstOrDefault(item =>
                (string?)item.Attribute("TargetType") == type && item.Attribute(x + "Key") is null);
            Assert.NotNull(style);
            Assert.True(HasReadableBrush(style!, "Foreground"), $"{type} is missing a readable Foreground");
            Assert.True(HasReadableBrush(style!, "Background"), $"{type} is missing a readable Background");
        }
    }

    private static bool HasReadableBrush(XElement style, string property)
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var setter = style.Elements(ns + "Setter")
            .FirstOrDefault(item => (string?)item.Attribute("Property") == property);
        var value = (string?)setter?.Attribute("Value");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || value.Contains("#000", StringComparison.OrdinalIgnoreCase)
            || value.Contains("SystemColors", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Contains("DynamicResource", StringComparison.Ordinal)
            || value.Equals("Transparent", StringComparison.Ordinal);
    }
}

public class TrayIconTextTests
{
    [Fact]
    public void UnavailableSnapshot_DoesNotRenderRemainingQuota()
    {
        var snapshot = new QuotaEngine().Build(
            [],
            AppSettings.CreateDefaults(),
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            new CoverageInfo
            {
                ConversationIncomplete = true,
                HistoryLoadedWithoutUsage = true,
                LoadedConversations = 4,
                ZeroEventConversations = 4
            },
            new QuotaMetadataSet(),
            AppSyncStatus.ProviderSchemaMismatch);
        Assert.True(snapshot.DisplayUsageUnavailable);
        Assert.Equal(50, snapshot.Remaining);
        Assert.Equal("?", DisplayFormatting.TrayIconText(snapshot));
        Assert.NotEqual("50", DisplayFormatting.TrayIconText(snapshot));
        Assert.Equal("?", DisplayFormatting.UsageLabel(snapshot));
    }

    [Fact]
    public void AvailableRemaining_RendersNumber()
    {
        var snapshot = new QuotaSnapshot
        {
            Limit = 50,
            Used = 12,
            UsesServerCount = true
        };
        Assert.Equal("38", DisplayFormatting.TrayIconText(snapshot));
    }
}
