using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
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

    /// <summary>The games the last scan found (the welcome screen scans at start).</summary>
    public IReadOnlyList<FoundGame>? Games
    {
        get => Volatile.Read(ref _games);
        set => Volatile.Write(ref _games, value);
    }

    private IReadOnlyList<FoundGame>? _games;

    /// <summary>The project's game opened for drawing; scans the game folders first when no scan has finished. Call on a worker.</summary>
    public GameResources? ResourcesFor(Project project)
    {
        var games = Games ?? GameLocator.Scan(GameLocator.StandardFolders(Environment.GetEnvironmentVariable).Concat(Settings.Current.GameFolders).Distinct(), cancel: ShuttingDown);
        try
        {
            return GameResources.Load(project.Info.Game, project.Folder, games, EngineData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
        {
            Log.Warn($"The game files for {project.Info.Name} could not be read: {ex.Message}");
            return null;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<GameResources?>> _others = new();

    /// <summary>
    /// The user's other games, opened for their pictures, for "From your other games" on the shelves (D43): every supported
    /// game the scan found except <paramref name="own"/> and the game it is an add-on for. Call on a worker.
    /// </summary>
    public IReadOnlyList<GameResources> OtherGames(GameResources? own)
    {
        var games = Games ?? [];
        var found = new List<GameResources>();
        foreach (var g in games.Where(g => g.Game.Supported && g.Game.Id != own?.Game.Id && g.Game.Id != own?.Game.AddOnFor).DistinctBy(g => g.Game.Id))
        {
            try
            {
                if (_others.GetOrAdd(g.Game.Id, id => new Lazy<GameResources?>(() => GameResources.Load(id, null, games, EngineData))).Value is { } r) found.Add(r);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
            {
                Log.Warn($"{g.Game.Title} could not be read for its pictures: {ex.Message}");
            }
        }

        return found;
    }

    public void Shutdown() => _shutdown.Cancel();

    private static string? EngineDataFinder()
    {
        var found = Core.Games.EngineData.Find();
        Log.Info(found is null ? "GZDoom's data file was not found; remixing classic maps needs it." : $"GZDoom data: {found}");
        return found;
    }
}
