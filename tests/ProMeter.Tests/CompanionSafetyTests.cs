using ProMeter.Companion;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class CanonicalTargetPolicyTests
{
    [Theory]
    [InlineData("/backend-api/%2e%2e/evil")]
    [InlineData("/backend-api/.%2e/evil")]
    [InlineData("/backend-api/%2e./evil")]
    [InlineData("/backend-api/%2F%2Fevil")]
    [InlineData("/backend-api/%5Cevil")]
    [InlineData("/backend-api/%zz")]
    [InlineData("/backend-api/../evil")]
    public void EncodedAndLiteralTraversal_IsRejected(string path)
    {
        Assert.False(CanonicalTargetPolicy.TryValidate(path, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ValidEncodedQuery_IsAccepted()
    {
        Assert.True(BackendTargetPolicy.TryValidate(
            "/backend-api/conversations/abc/messages?before=cursor%3Dvalue&include_has_versions=true&num_turns=100",
            out var canonical,
            out _));
        Assert.Contains("before=", canonical, StringComparison.Ordinal);
    }
}

public class CompanionHostManifestTests
{
    [Fact]
    public void MissingHost_FailsClosed()
    {
        var result = CompanionHostManifest.TryCreate(@"C:\missing\prometer.exe", "abcdefghijklmnopabcdefghijklmnop", null);
        Assert.False(result.Ok);
        Assert.Contains("missing", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedExtensionId_FailsClosed()
    {
        var host = Path.Combine(Path.GetTempPath(), "prometer-companion-host.exe");
        File.WriteAllText(host, "placeholder");
        try
        {
            var result = CompanionHostManifest.TryCreate(host, "NOT-A-VALID-ID", null);
            Assert.False(result.Ok);
            Assert.Contains("malformed", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(host);
        }
    }

    [Fact]
    public void ValidManifest_ContainsOnlyApprovedOrigins()
    {
        var host = Path.Combine(Path.GetTempPath(), "prometer-companion-host.exe");
        File.WriteAllText(host, "placeholder");
        const string chrome = "abcdefghijklmnopabcdefghijklmnop";
        const string edge = "ponmlkjihgfedcbaponmlkjihgfedcba";
        try
        {
            var result = CompanionHostManifest.TryCreate(host, chrome, edge);
            Assert.True(result.Ok);
            Assert.NotNull(result.ManifestJson);
            Assert.True(CompanionHostManifest.HasOnlyApprovedOrigins(result.ManifestJson, result.AllowedOrigins));
            Assert.Contains(chrome, result.ManifestJson, StringComparison.Ordinal);
            Assert.DoesNotContain("prometer.exe", Path.GetFileName(result.HostPath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(host);
        }
    }
}

public class SettingsMigrationTests
{
    [Fact]
    public void MissingAuthTransport_PreservesWebView2()
    {
        var migrated = SettingsMigration.FromJson("""{"version":1,"autoSync":true,"firstRunCompleted":true}""");
        Assert.Equal(AuthTransportKind.WebView2, migrated.AuthTransport);
        Assert.Equal(2, migrated.Version);
    }

    [Fact]
    public void ExplicitBrowserCompanion_IsPreserved()
    {
        var migrated = SettingsMigration.FromJson("""{"version":1,"authTransport":0,"firstRunCompleted":true}""");
        Assert.Equal(AuthTransportKind.BrowserCompanion, migrated.AuthTransport);
    }

    [Fact]
    public void NewInstall_DefaultsToBrowserCompanion()
    {
        var settings = AppSettings.CreateDefaults();
        Assert.Equal(2, settings.Version);
        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
    }

    [Fact]
    public void ExplicitWebView2_IsPreserved()
    {
        var migrated = SettingsMigration.FromJson("""{"version":1,"authTransport":1,"firstRunCompleted":true}""");
        Assert.Equal(AuthTransportKind.WebView2, migrated.AuthTransport);
    }
}

public class CompanionCallerOriginTests
{
    private const string ChromeId = "abcdefghijklmnopabcdefghijklmnop";
    private const string OtherId = "ponmlkjihgfedcbaponmlkjihgfedcba";
    private static readonly CompanionPairingState Registered = new()
    {
        Token = "pairing-token-for-origin-tests-aa",
        ChromeExtensionId = ChromeId
    };

    [Fact]
    public void ProductionEntrypoint_AcceptsTopLevelChromeWindowsArgs()
    {
        string[] args = ["chrome-extension://" + ChromeId + "/", "--parent-window=1234"];
        Assert.True(NativeMessagingHost.ShouldRun(args, Registered));
        Assert.True(CompanionCallerOrigin.IsAllowed(args, Registered));
    }

    [Fact]
    public void ProductionEntrypoint_AcceptsGetCommandLineArgsShape()
    {
        string[] args = ["prometer-companion-host.exe", "chrome-extension://" + ChromeId + "/", "--parent-window=1234"];
        Assert.True(NativeMessagingHost.ShouldRun(args, Registered));
        Assert.True(CompanionCallerOrigin.IsAllowed(args, Registered));
    }

    [Fact]
    public void ProductionEntrypoint_RejectsInvalidArgsWithoutConnecting()
    {
        NativeMessagingHost.Run([]);
        NativeMessagingHost.Run(["--parent-window=1234"]);
        Assert.False(NativeMessagingHost.ShouldRun(["--parent-window=1234"], Registered));
    }

    [Fact]
    public void UnregisteredValidId_IsRejected()
    {
        Assert.False(CompanionCallerOrigin.IsAllowed(
            ["chrome-extension://" + OtherId + "/", "--parent-window=1"],
            Registered));
    }

    [Fact]
    public void MalformedId_IsRejected()
    {
        Assert.False(CompanionCallerOrigin.IsAllowed(
            ["prometer-companion-host.exe", "chrome-extension://NOT-VALID/"],
            Registered));
    }

    [Fact]
    public void MissingOrigin_IsRejected()
    {
        Assert.False(CompanionCallerOrigin.IsAllowed([]));
        Assert.False(CompanionCallerOrigin.IsAllowed(["prometer-companion-host.exe"]));
        Assert.False(CompanionCallerOrigin.IsAllowed(["prometer-companion-host.exe", "--parent-window=9"], Registered));
    }

    [Fact]
    public void TwoDifferentExtensionOrigins_AreRejected()
    {
        Assert.False(CompanionCallerOrigin.IsAllowed(
            ["chrome-extension://" + ChromeId + "/", "chrome-extension://" + OtherId + "/"],
            Registered));
    }

    [Fact]
    public void QueryOrFragmentOnOrigin_IsRejected()
    {
        Assert.False(CompanionCallerOrigin.IsAllowed(["chrome-extension://" + ChromeId + "/?x=1"], Registered));
        Assert.False(CompanionCallerOrigin.IsAllowed(["chrome-extension://" + ChromeId + "/#frag"], Registered));
    }

    [Fact]
    public void HttpsUrl_IsRejected()
    {
        Assert.False(CompanionCallerOrigin.IsAllowed(
            ["prometer-companion-host.exe", "https://chatgpt.com/"],
            Registered));
    }
}

public class CompanionOperationAllowlistTests
{
    [Fact]
    public void PutPatchDeleteAndArbitraryPost_AreRejected()
    {
        Assert.False(CompanionOperationRouter.TryMap("PUT", "/backend-api/models", null, out _, out _, out _));
        Assert.False(CompanionOperationRouter.TryMap("PATCH", "/backend-api/models", null, out _, out _, out _));
        Assert.False(CompanionOperationRouter.TryMap("DELETE", "/backend-api/conversation/abc", null, out _, out _, out _));
        Assert.False(CompanionOperationRouter.TryMap("POST", "/backend-api/models", "{}", out _, out _, out _));
    }

    [Fact]
    public void ProviderPaths_MapToClosedOperations()
    {
        Assert.True(CompanionOperationRouter.TryMap("GET", ChatGptEndpoints.Session, null, out var session, out _, out _));
        Assert.Equal(CompanionOperation.GetSessionStatus, session);
        Assert.True(CompanionOperationRouter.TryMap("GET", ChatGptEndpoints.ConversationTurns("conv-1"), null, out var head, out _, out _));
        Assert.Equal(CompanionOperation.GetConversationHead, head);
        Assert.True(CompanionOperationRouter.TryMap("GET", ChatGptEndpoints.ConversationFull("conv-1"), null, out var full, out _, out _));
        Assert.Equal(CompanionOperation.GetConversationFull, full);
        Assert.True(CompanionOperationRouter.TryMap("POST", ChatGptEndpoints.ConversationInit, CompanionOperationRouter.QuotaInitBody, out var quota, out _, out _));
        Assert.Equal(CompanionOperation.GetQuotaInit, quota);
    }
}
