using WadSmith.Editor.App;

namespace WadSmith.App;

/// <summary>Everything the application shares: its folders, settings and what it found on this computer.</summary>
internal sealed class AppServices(AppPaths paths, SettingsStore settings, LaunchOptions options)
{
    private readonly CancellationTokenSource _shutdown = new();

    public AppPaths Paths { get; } = paths;

    public SettingsStore Settings { get; } = settings;

    public LaunchOptions Options { get; } = options;

    /// <summary>Cancelled when the application closes, so background work stops.</summary>
    public CancellationToken ShuttingDown => _shutdown.Token;

    /// <summary>GZDoom's own data file, needed to remix classic maps; null when GZDoom is not installed.</summary>
    public string? EngineData { get; } = EngineDataFinder();

    public void Shutdown() => _shutdown.Cancel();

    private static string? EngineDataFinder()
    {
        var found = Core.Games.EngineData.Find();
        Log.Info(found is null ? "GZDoom's data file was not found; remixing classic maps needs it." : $"GZDoom data: {found}");
        return found;
    }
}
