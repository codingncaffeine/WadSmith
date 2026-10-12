using WadSmith.Editor.App;

namespace WadSmith.Editor;

/// <summary>
/// Builds something on a worker, one build at a time, always from the newest request: requests that
/// arrive while a build runs replace each other, so a burst of edits costs one more build, not one
/// per edit. Each result is handed to <paramref name="publish"/> on the UI thread.
/// </summary>
/// <remarks>
/// The worker is a thread of its own, not a pool thread: opening a map starts a dozen builds at once, and the pool
/// grows by about a thread a second when it is busy, so on a small machine a panel would update seconds late and a
/// save could miss the exit. The thread stays for a while after its last build and goes away when nothing follows.
/// </remarks>
public sealed class LatestOnWorker<T>(Action<Action> onUiThread, Action<T> publish, string what)
{
    /// <summary>How long the thread waits for the next request before it ends.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(5);

    // A plain object, not a Lock: the thread waits for requests with Monitor.Wait, which needs it.
    private readonly object _gate = new();
    private Func<T>? _pending;
    private Thread? _thread;

    public void Request(Func<T> build)
    {
        lock (_gate)
        {
            _pending = build;
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = $"WadSmith {what}" };
                _thread.Start();
            }
            else
            {
                Monitor.Pulse(_gate);
            }
        }
    }

    private void Loop()
    {
        while (true)
        {
            Func<T> build;
            lock (_gate)
            {
                // Nothing pending: wait for a request; none within the linger time, and the thread ends.
                if (_pending is null && !Monitor.Wait(_gate, Linger) && _pending is null)
                {
                    _thread = null;
                    return;
                }

                if (_pending is null) continue;
                build = _pending;
                _pending = null;
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
