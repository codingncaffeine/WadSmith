namespace WadSmith.Tests;

/// <summary>
/// The headless app session is set up once, before any test runs (<see cref="UiSessionFixture"/>). Set up lazily by the
/// first window test instead, Avalonia's dispatcher was reset while workers of tests running in parallel posted to it, and
/// a post in that moment bound the dispatcher to the worker's thread: the session then failed to start with "The calling
/// thread cannot access this object because a different thread owns it" (CI, 2026-10-12).
/// </summary>
public sealed class HeadlessSessionTests
{
    [Fact]
    public async Task PostsFromAnotherThreadWhileATestStartsDoNotBreakTheSession()
    {
        // About a thousand posts a second: enough to land in any set-up window, not a flood the dispatcher cannot drain
        // (a tight loop queued jobs faster than they ran and the test's own job never came up).
        var stop = false;
        var poster = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                WadSmith.App.ViewModels.MainViewModel.PostToUi(() => { });
                Thread.Sleep(1);
            }
        });
        poster.Start();
        Thread.Sleep(50);
        try
        {
            var ok = await Ui.Run(() => "the session answered");
            Assert.Equal("the session answered", ok);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            poster.Join();
        }
    }
}
