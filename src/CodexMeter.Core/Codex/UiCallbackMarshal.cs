namespace CodexMeter.Codex;

public static class UiCallbackMarshal
{
    public static bool CanPost(bool shutdownStarted, bool shutdownFinished, bool ownerClosed) =>
        !shutdownStarted && !shutdownFinished && !ownerClosed;

    public static bool TryPost(
        bool shutdownStarted,
        bool shutdownFinished,
        bool ownerClosed,
        Action beginInvoke)
    {
        if (!CanPost(shutdownStarted, shutdownFinished, ownerClosed))
        {
            return false;
        }

        beginInvoke();
        return true;
    }
}
