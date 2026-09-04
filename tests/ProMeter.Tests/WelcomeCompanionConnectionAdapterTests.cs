using ProMeter.Services;

namespace ProMeter.Tests;

public class WelcomeCompanionConnectionAdapterTests
{
    [Fact]
    public void ConnectionChanged_IsPostedAndDoesNotRunInline()
    {
        var marshal = new RecordingUiMarshal();
        var states = new List<string>();
        var adapter = new WelcomeCompanionConnectionAdapter(marshal, () => true, (state, _, _) => states.Add(state));

        adapter.HandleConnectionChanged(true);

        Assert.Empty(states);
        Assert.Single(marshal.Posted);
        marshal.RunPosted();
        Assert.Equal(["Connected"], states);
    }

    [Fact]
    public void ClosedWindow_IgnoresPostedUpdate()
    {
        var marshal = new RecordingUiMarshal();
        var states = new List<string>();
        var adapter = new WelcomeCompanionConnectionAdapter(marshal, () => true, (state, _, _) => states.Add(state));
        adapter.HandleConnectionChanged(true);
        adapter.Detach();
        marshal.RunPosted();
        Assert.Empty(states);
        Assert.True(adapter.IsClosed);
    }

    [Fact]
    public void DisconnectedWhileRegistered_UpdatesState()
    {
        var marshal = new RecordingUiMarshal();
        var registered = true;
        string? last = null;
        var connected = false;
        var adapter = new WelcomeCompanionConnectionAdapter(
            marshal,
            () => registered,
            (state, isRegistered, isConnected) =>
            {
                last = state;
                registered = isRegistered;
                connected = isConnected;
            });

        adapter.HandleConnectionChanged(false);
        marshal.RunPosted();
        Assert.Equal("Disconnected", last);
        Assert.True(registered);
        Assert.False(connected);
    }

    [Fact]
    public void SetStateThrowing_DoesNotPropagateThroughPostedAction()
    {
        var marshal = new RecordingUiMarshal();
        var adapter = new WelcomeCompanionConnectionAdapter(
            marshal,
            () => true,
            (_, _, _) => throw new InvalidOperationException("다른 스레드가 이 개체를 소유하고 있어 호출 스레드가 해당 개체에 액세스할 수 없습니다."));

        adapter.HandleConnectionChanged(true);
        marshal.RunPosted();
    }

    [Fact]
    public void Detach_StopsFurtherPostedUpdates()
    {
        var marshal = new RecordingUiMarshal();
        var count = 0;
        var adapter = new WelcomeCompanionConnectionAdapter(marshal, () => true, (_, _, _) => count++);
        adapter.HandleConnectionChanged(true);
        marshal.RunPosted();
        Assert.Equal(1, count);

        adapter.Detach();
        adapter.HandleConnectionChanged(false);
        marshal.RunPosted();
        Assert.Equal(1, count);
    }
}
