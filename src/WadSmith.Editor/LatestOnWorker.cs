using WadSmith.Editor.App;

namespace WadSmith.Editor;

/// <summary>
/// Builds something on a worker, one build at a time, always from the newest request: requests that
/// arrive while a build runs replace each other, so a burst of edits costs one more build, not one
/// per edit. Each result is handed to <paramref name="publish"/> on the UI thread.
/// </summary>
public sealed class LatestOnWorker<T>(Action<Action> onUiThread, Action<T> publish, string what)
{
    private readonly Lock _gate = new();
    private Func<T>? _pending;
    private bool _running;

    public void Request(Func<T> build)
    {
        lock (_gate)
        {
            _pending = build;
            if (_running) return;
            _running = true;
        }

        _ = Task.Run(Loop);
    }

    private void Loop()
    {
        while (true)
        {
            Func<T>? build;
            lock (_gate)
            {
                build = _pending;
                _pending = null;
                if (build is null)
                {
                    _running = false;
                    return;
                }
            }

            try
            {
                var result = build();
                onUiThread(() => publish(result));
            }
            catch (Exception ex)
            {
                Log.Error($"Building {what} failed", ex);
            }
        }
    }
}
