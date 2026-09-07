using CodexMeter.Providers.ChatGpt;

namespace CodexMeter.Tests;

public class BackendTargetPolicyTests
{
    [Fact]
    public void ApprovedRelativePaths_AreAccepted()
    {
        Assert.True(BackendTargetPolicy.TryValidate("/backend-api/conversations", out var conversations, out _));
        Assert.Equal("/backend-api/conversations", conversations);
        Assert.True(BackendTargetPolicy.TryValidate("/api/auth/session", out var session, out _));
        Assert.Equal("/api/auth/session", session);
    }

    [Fact]
    public void SchemeRelativeTarget_IsRejected()
    {
        Assert.False(BackendTargetPolicy.TryValidate("//evil.example/x", out _, out var error));
        Assert.Contains("scheme-relative", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbsoluteExternalUrl_IsRejected()
    {
        Assert.False(BackendTargetPolicy.TryValidate("https://evil.example/x", out _, out var error));
        Assert.Contains("absolute", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DotSegmentTraversal_IsRejected()
    {
        Assert.False(BackendTargetPolicy.TryValidate("/backend-api/../evil", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void BackslashTarget_IsRejected()
    {
        Assert.False(BackendTargetPolicy.TryValidate("/backend-api\\conversations", out _, out _));
    }

    [Fact]
    public void ControlCharacterTarget_IsRejected()
    {
        Assert.False(BackendTargetPolicy.TryValidate("/backend-api/conversations\u0001", out _, out _));
    }
}
