using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

// Full WebView2 OAuth (Google, Microsoft, Apple, auth.openai.com popups and
// NewWindowRequested callbacks) cannot be proven by unit tests and remains
// a manual live-account check.
public class OriginPolicyTests
{
    [Fact]
    public void ChatGptAppOrigin_IsAcceptedForBackendFetch()
    {
        Assert.True(OriginPolicy.IsChatGptAppOrigin("https://chatgpt.com/"));
        Assert.True(OriginPolicy.AllowsBackendFetch("https://chatgpt.com/backend-api/conversations"));
        Assert.True(OriginPolicy.AllowsInteractiveNavigation("https://chatgpt.com/auth/login"));
        Assert.Equal(OriginKind.ChatGptApp, OriginPolicy.Classify("https://www.chatgpt.com/"));
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
        machine.Begin();
        Assert.Equal(LoginNavigationAction.StayOnExternalAuth, machine.Observe("https://accounts.google.com/o/oauth2/auth", true));
        Assert.False(machine.MayNavigateAwayToProbe());
        Assert.Equal(LoginNavigationAction.ProbeSession, machine.Observe("https://chatgpt.com/", true));
        Assert.Equal(LoginNavigationAction.Ignore, machine.Observe("https://chatgpt.com/", false));
        machine.Cancel();
        Assert.Equal(LoginNavigationAction.Ignore, machine.Observe("https://chatgpt.com/", true));
    }
}
