using CycleArc.Providers.ChatGpt;

namespace CycleArc.Tests;

// Google, Microsoft, and Apple WebView2 login is unsupported. These tests
// only cover origin classification, not embedded social OAuth.
public class OriginPolicyTests
{
    [Fact]
    public void HttpsChatGpt_IsAccepted()
    {
        Assert.True(OriginPolicy.IsChatGptAppOrigin("https://chatgpt.com"));
        Assert.True(OriginPolicy.IsChatGptAppOrigin("https://chatgpt.com/"));
        Assert.True(OriginPolicy.AllowsBackendFetch("https://chatgpt.com/backend-api/conversations"));
        Assert.True(OriginPolicy.AllowsInteractiveNavigation("https://chatgpt.com/auth/login"));
        Assert.Equal(OriginKind.ChatGptApp, OriginPolicy.Classify("https://www.chatgpt.com/"));
    }

    [Fact]
    public void HttpChatGpt_IsRejected()
    {
        Assert.Equal(OriginKind.None, OriginPolicy.Classify("http://chatgpt.com"));
        Assert.False(OriginPolicy.AllowsBackendFetch("http://chatgpt.com/"));
    }

    [Fact]
    public void NonDefaultHttpsPort_IsRejected()
    {
        Assert.Equal(OriginKind.None, OriginPolicy.Classify("https://chatgpt.com:4443/"));
        Assert.False(OriginPolicy.AllowsBackendFetch("https://chatgpt.com:4443/backend-api/conversations"));
    }

    [Fact]
    public void AuthOpenAi_IsInteractiveOnly()
    {
        const string uri = "https://auth.openai.com/log-in";
        Assert.True(OriginPolicy.IsInteractiveAuthOrigin(uri));
        Assert.True(OriginPolicy.AllowsInteractiveNavigation(uri));
        Assert.False(OriginPolicy.AllowsBackendFetch(uri));
        Assert.False(OriginPolicy.IsChatGptAppOrigin(uri));
    }

    [Fact]
    public void GoogleAccounts_AreInteractiveOnly()
    {
        const string uri = "https://accounts.google.com/o/oauth2/v2/auth";
        Assert.True(OriginPolicy.AllowsInteractiveNavigation(uri));
        Assert.False(OriginPolicy.AllowsBackendFetch(uri));
    }

    [Fact]
    public void SubstringLookalikeHost_IsRejected()
    {
        const string uri = "https://chatgpt.com.evil.example/login";
        Assert.Equal(OriginKind.None, OriginPolicy.Classify(uri));
        Assert.False(OriginPolicy.AllowsBackendFetch(uri));
        Assert.False(OriginPolicy.AllowsInteractiveNavigation(uri));
        Assert.False(OriginPolicy.IsChatGptAppOrigin(uri));
    }

    [Fact]
    public void UnrelatedOrigin_IsRejected()
    {
        Assert.Equal(OriginKind.None, OriginPolicy.Classify("https://example.com/"));
        Assert.False(OriginPolicy.AllowsBackendFetch("https://example.com/backend-api/conversations"));
        Assert.False(OriginPolicy.AllowsInteractiveNavigation("https://news.ycombinator.com/"));
    }

    [Fact]
    public void LoginMachine_DoesNotProbeOnExternalOAuth()
    {
        var machine = new LoginNavigationMachine();
        machine.BeginOrJoin();
        Assert.Equal(LoginNavigationAction.StayOnExternalAuth, machine.Observe("https://accounts.google.com/o/oauth2/auth", true));
        Assert.False(machine.MayNavigateAwayToProbe());
        Assert.Equal(LoginNavigationAction.ProbeSession, machine.Observe("https://chatgpt.com/", true));
        Assert.Equal(LoginNavigationAction.Ignore, machine.Observe("https://chatgpt.com/", false));
        machine.Cancel();
        Assert.Equal(LoginNavigationAction.Ignore, machine.Observe("https://chatgpt.com/", true));
    }
}
