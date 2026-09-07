using CodexMeter.Codex;

namespace CodexMeter.Tests;

public class UiCallbackMarshalTests
{
    [Fact]
    public void TryPost_InvokesBeginInvokeWhenDispatcherIsAlive()
    {
        var posted = 0;
        Assert.True(UiCallbackMarshal.CanPost(false, false, false));
        Assert.True(UiCallbackMarshal.TryPost(false, false, false, () => posted++));
        Assert.Equal(1, posted);
    }

    [Fact]
    public void TryPost_SkipsWhenShutdownOrClosed()
    {
        var posted = 0;
        Action beginInvoke = () => posted++;
        Assert.False(UiCallbackMarshal.TryPost(true, false, false, beginInvoke));
        Assert.False(UiCallbackMarshal.TryPost(false, true, false, beginInvoke));
        Assert.False(UiCallbackMarshal.TryPost(false, false, true, beginInvoke));
        Assert.Equal(0, posted);
        Assert.False(UiCallbackMarshal.CanPost(false, false, true));
    }

    [Fact]
    public void QueuedCallback_IsIgnoredAfterOwnerCloses()
    {
        Action? queued = null;
        Assert.True(UiCallbackMarshal.TryPost(false, false, false, () => queued = () => throw new InvalidOperationException("ui")));
        Assert.NotNull(queued);
        Assert.False(UiCallbackMarshal.CanPost(false, false, ownerClosed: true));
    }
}
