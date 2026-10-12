using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using WadSmith.App;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>
/// The app on Avalonia's headless platform: the real window and pages, nothing on anybody's desktop. Avalonia is set up
/// once per process, so every UI test shares one session; it is never disposed from a test, because an await on it
/// resumes on the session's own thread and disposing waits for that thread.
/// </summary>
internal static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

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
