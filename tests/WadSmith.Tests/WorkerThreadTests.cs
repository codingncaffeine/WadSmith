using WadSmith.Editor;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>
/// Background work never waits on the thread pool: a worker's builds and the settings writer run on threads of their
/// own, so a burst of work lands in bounded time even when every pool thread is taken.
/// </summary>
public sealed class WorkerThreadTests
{
    /// <summary>Holds every pool thread busy, so anything queued to the pool waits for the pool to grow.</summary>
    private static IDisposable StarvedPool()
    {
        ThreadPool.GetMinThreads(out var workers, out _);
        var release = new ManualResetEventSlim(false);
        var held = new CountdownEvent(workers);
        for (var i = 0; i < workers; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                held.Signal();
                release.Wait();
            }, null);
        }

        held.Wait(TimeSpan.FromSeconds(5));
        return new Release(release);
    }

    private sealed class Release(ManualResetEventSlim e) : IDisposable
    {
        public void Dispose() => e.Set();
    }

    [Fact]
    public void ABurstOfWorkersBuildsOffThePoolAndTheNewestRequestWins()
    {
        using var _ = StarvedPool();
        var published = new List<(int Worker, int Value, bool Pool)>();
        var done = new CountdownEvent(24);
        var workers = Enumerable.Range(0, 24).Select(i => new LatestOnWorker<(int, bool)>(a => a(), r =>
        {
            lock (published) published.Add((i, r.Item1, r.Item2));
            done.Signal();
        }, $"worker {i}")).ToList();

        // Each worker gets two requests at once: only the last need be built.
        foreach (var (w, i) in workers.Select((w, i) => (w, i)))
        {
            w.Request(() => (-1, Thread.CurrentThread.IsThreadPoolThread));
            w.Request(() => (i, Thread.CurrentThread.IsThreadPoolThread));
        }

        var started = DateTime.UtcNow;
        Assert.True(done.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), $"only {published.Count} of 24 workers published in 3 s with the pool starved");
        var took = DateTime.UtcNow - started;
        Assert.True(took < TimeSpan.FromSeconds(2), $"24 builds took {took.TotalMilliseconds:0} ms");
        Assert.All(published, p => Assert.False(p.Pool, $"worker {p.Worker} built on a pool thread"));
        Assert.Equal(Enumerable.Range(0, 24), published.Select(p => p.Value).Order());
        Assert.DoesNotContain(published, p => p.Value == -1);
    }

    [Fact]
    public void ARequestDuringABuildIsBuiltAfterIt()
    {
        var results = new List<int>();
        var first = new ManualResetEventSlim(false);
        var two = new CountdownEvent(2);
        var worker = new LatestOnWorker<int>(a => a(), r =>
        {
            lock (results) results.Add(r);
            two.Signal();
        }, "the test");
        var started = new ManualResetEventSlim(false);
        worker.Request(() =>
        {
            started.Set();
            first.Wait();
            return 1;
        });

        // Once the first build is under way, two more requests: only the last is built, after it.
        Assert.True(started.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        worker.Request(() => 2);
        worker.Request(() => 3);
        first.Set();
        Assert.True(two.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal([1, 3], results);
    }

    [Fact]
    public void SettingsAreSavedOffThePoolAndFlushWaitsForTheLastSave()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "settings.json");
        var store = SettingsStore.Load(file);
        using (StarvedPool())
        {
            store.Current.ClassicNames = true;
            store.Save();
            store.Current.LargeInterface = true;
            store.Save();
            var started = DateTime.UtcNow;
            Assert.True(store.Flush(TimeSpan.FromSeconds(3)), $"the save did not reach the disk in 3 s with the pool starved: {store.WriterState}");
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
            Assert.Contains("(pool thread: False)", store.WriterState);
        }

        var saved = SettingsStore.Load(file).Current;
        Assert.True(saved.ClassicNames && saved.LargeInterface);
    }
}
