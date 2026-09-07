using System.Windows.Threading;

namespace CodexMeter.UI;

public sealed class DispatcherUiMarshal : IUiMarshal
{
    private readonly Dispatcher _dispatcher;

    public DispatcherUiMarshal(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }
}
