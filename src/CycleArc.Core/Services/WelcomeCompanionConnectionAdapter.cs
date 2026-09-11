namespace CycleArc.Services;

public interface IUiMarshal
{
    void Post(Action action);
}

public sealed class RecordingUiMarshal : IUiMarshal
{
    public List<Action> Posted { get; } = [];

    public void Post(Action action) => Posted.Add(action);

    public void RunPosted()
    {
        var actions = Posted.ToList();
        Posted.Clear();
        foreach (var action in actions)
        {
            action();
        }
    }
}

public sealed class WelcomeCompanionConnectionAdapter
{
    private readonly IUiMarshal _marshal;
    private readonly Func<bool> _isRegistered;
    private readonly Action<string, bool, bool> _setState;
    private int _closed;

    public WelcomeCompanionConnectionAdapter(
        IUiMarshal marshal,
        Func<bool> isRegistered,
        Action<string, bool, bool> setState)
    {
        _marshal = marshal;
        _isRegistered = isRegistered;
        _setState = setState;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void HandleConnectionChanged(bool connected)
    {
        _marshal.Post(() => Apply(connected));
    }

    public void Detach() => Interlocked.Exchange(ref _closed, 1);

    private void Apply(bool connected)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        try
        {
            var registered = _isRegistered();
            if (connected)
            {
                _setState(UiText.CompanionConnected, true, true);
                return;
            }

            if (registered)
            {
                _setState(UiText.CompanionDisconnected, true, false);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
