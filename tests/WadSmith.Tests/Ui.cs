using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using WadSmith.App;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor.App;

// The headless session starts before any test runs (see UiSessionFixture).
[assembly: AssemblyFixture(typeof(WadSmith.Tests.UiSessionFixture))]

namespace WadSmith.Tests;

/// <summary>
/// Starts the headless app session once, before the first test of the assembly, so its one set-up of Avalonia's
/// dispatcher cannot meet a post from another test's worker ("The calling thread cannot access this object because a
/// different thread owns it").
/// </summary>
public sealed class UiSessionFixture : IAsyncLifetime
{
    // Awaited, never blocked on: xunit runs fixtures on its own synchronization context, and a constructor that waited
    // for the session's thread here deadlocked the whole run.
    public ValueTask InitializeAsync() => new(Ui.EnsureStartedAsync());

    public ValueTask DisposeAsync() => default;
}

/// <summary>
/// The app on Avalonia's headless platform: the real window and pages, nothing on anybody's desktop. Avalonia is set up
/// once per process, so every UI test shares one session; it is never disposed from a test, because an await on it
/// resumes on the session's own thread and disposing waits for that thread.
/// </summary>
internal static class Ui
{
    // Set up once for the assembly: with the default per-test isolation the session set the app up again on every
    // dispatch, resetting Avalonia's dispatcher each time, and a post from another test's worker in that moment
    // re-created it on the worker's thread.
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>Starts the session and sets the app up now (the session sets it up at its first dispatch), once per process.</summary>
    public static Task EnsureStartedAsync() => Session.Value.Dispatch(() => 0, CancellationToken.None);

    /// <summary>Runs a UI test's body on the session's thread.</summary>
    public static Task<T> Run<T>(Func<T> body) => Session.Value.Dispatch(body, TestContext.Current.CancellationToken);

    /// <summary>The main window on a fresh profile in <paramref name="profile"/> that knows the games in <paramref name="games"/>, once the scan is done.</summary>
    public static (MainWindow Window, MainViewModel Main) Start(string profile, string games)
    {
        var paths = AppPaths.Resolve(profile, _ => null);
        paths.EnsureCreated();
        var settings = SettingsStore.Load(paths.SettingsFile);
        settings.Current.GameFolders.Add(games);
        App.App.Services = new AppServices(paths, settings, new LaunchOptions(profile, null));
        var vm = new MainViewModel(App.App.Services);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        for (var i = 0; i < 400 && vm.Welcome.Scanning; i++) Pump(0.05);
        Pump(0.5);
        return (window, vm);
    }

    /// <summary>A new map in the first game found, open in the editor.</summary>
    public static EditorViewModel NewMap(MainViewModel vm)
    {
        var tile = vm.Welcome.Games.FirstOrDefault() ?? throw new InvalidOperationException("No game was found.");
        Wait(tile.NewMap!.ExecuteAsync(tile));
        Pump(1.0);
        return (EditorViewModel)vm.Page!;
    }

    /// <summary>Lets the app work (its dispatcher and render timer) for a while.</summary>
    public static void Pump(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// Lets the app work until <paramref name="done"/> holds or <paramref name="seconds"/> have passed, and says which. A test
    /// waits for what it is about to check, never for a fixed while: a slow machine then only makes it wait longer.
    /// </summary>
    public static bool Until(Func<bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > until) return false;
            Pump(0.02);
        }

        return true;
    }

    /// <summary>Pumps until the task is done: the app's continuations run on this thread, so it cannot be awaited.</summary>
    public static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("A step did not finish.");
            Pump(0.02);
        }

        task.GetAwaiter().GetResult();
    }
}

/// <summary>Builds the app for <see cref="Ui"/>.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}
