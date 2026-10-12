using Avalonia;
using WadSmith.Core;
using WadSmith.Editor.App;

namespace WadSmith.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"WadSmith {BuildInfo.Stamp}");
            Console.WriteLine(BuildInfo.Licence);
            return 0;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine("""
                Usage: wadsmith [options] [PROJECT_FOLDER]

                  --portable DIR   Keep settings, logs and the asset library in DIR instead of the usual folders.
                  --version        Print the version and licence, then exit.

                The command-line tools are in wadsmith-cli.
                """);
            return 0;
        }

        var options = LaunchOptions.Parse(args);
        var paths = AppPaths.Resolve(options.PortableRoot, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        Log.Initialize(BuildInfo.Stamp, paths.Logs);
        if (paths.Portable) Log.Info($"Portable profile at {Path.GetDirectoryName(paths.Config)}");

        // Read before the UI thread starts: nothing that touches the disk runs on it.
        var settings = SettingsStore.Load(paths.SettingsFile);
        App.Services = new AppServices(paths, settings, options);
        App.Services.Sound = (Audio.ISoundPlayer?)Audio.SdlSoundPlayer.TryCreate() ?? new Audio.SilentPlayer();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Log.Crash("startup", ex));
            return 1;
        }
        finally
        {
            App.Services.Shutdown();
            if (!settings.Flush(TimeSpan.FromSeconds(3))) Console.Error.WriteLine("Settings were still being written at exit.");
            Log.Flush(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Used by <see cref="Main"/>, the designer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        WindowingBackend.Apply(
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace());
}

/// <summary>What the command line asked for.</summary>
internal sealed record LaunchOptions(string? PortableRoot, string? OpenPath)
{
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        string? portable = null, open = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--portable" && i + 1 < args.Count) portable = args[++i];
            else if (!args[i].StartsWith('-')) open = args[i];
        }

        return new LaunchOptions(portable, open);
    }
}
