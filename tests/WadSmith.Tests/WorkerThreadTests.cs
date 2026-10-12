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
    public void ABurstOfWorkersBuildsOffThePoolAndEachEndsOnItsNewestRequest()
    {
        using var _ = StarvedPool();
        var published = new List<(int Worker, int Value, bool Pool)>();
        var done = new CountdownEvent(24);
        var workers = Enumerable.Range(0, 24).Select(i => new LatestOnWorker<(int, bool)>(a => a(), r =>
        {
            lock (published) published.Add((i, r.Item1, r.Item2));
            if (r.Item1 == i) done.Signal();
        }, $"worker {i}")).ToList();

        // Each worker gets two requests at once. The first may be built if the worker starts before the second arrives
        // (that is allowed); what must hold is that each worker ends on its newest request, with at most that one before it.
        foreach (var (w, i) in workers.Select((w, i) => (w, i)))
        {
            w.Request(() => (-1, Thread.CurrentThread.IsThreadPoolThread));
            w.Request(() => (i, Thread.CurrentThread.IsThreadPoolThread));
        }

        var started = DateTime.UtcNow;
        Assert.True(done.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), $"only {published.Count(p => p.Value == p.Worker)} of 24 workers built their newest request in 3 s with the pool starved");
        var took = DateTime.UtcNow - started;
        Assert.True(took < TimeSpan.FromSeconds(2), $"24 builds took {took.TotalMilliseconds:0} ms");
        lock (published)
        {
            Assert.All(published, p => Assert.False(p.Pool, $"worker {p.Worker} built on a pool thread"));
            foreach (var g in published.GroupBy(p => p.Worker))
            {
                var values = g.Select(p => p.Value).ToList();
                Assert.True(values.SequenceEqual([g.Key]) || values.SequenceEqual([-1, g.Key]), $"worker {g.Key} published {string.Join(", ", values)}");
            }
        }
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
