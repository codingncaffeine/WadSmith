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

    /// <summary>The friendly and classic names of what is on screen (D3), following the "Show classic names" preference.</summary>
    public Editor.App.Vocabulary Words
    {
        get
        {
            if (_words is null)
            {
                _words = Editor.App.Vocabulary.Load();
                _words.Classic = Settings.Current.ClassicNames;
            }

            return _words;
        }
    }

    private Editor.App.Vocabulary? _words;

    /// <summary>Plays the editor's sounds. Silent unless the app's own start gives it SDL, so tests and captures never make a sound.</summary>
    public Audio.ISoundPlayer Sound { get; set; } = new Audio.SilentPlayer();

    /// <summary>Cancelled when the application closes, so background work stops.</summary>
    public CancellationToken ShuttingDown => _shutdown.Token;

    private readonly Lazy<HttpClient> _http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WadSmith/{typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0"} (+https://github.com/codingncaffeine/WadSmith)");
        return client;
    });

    /// <summary>The web, for what is fetched once (a game's cover picture); null when WADSMITH_OFFLINE is set, as the tests and the gate set it, and nothing is fetched.</summary>
    public HttpClient? Http => Environment.GetEnvironmentVariable("WADSMITH_OFFLINE") is "1" or "true" ? null : _http.Value;

    /// <summary>GZDoom's own data file, needed to remix classic maps; null when GZDoom is not installed.</summary>
    public string? EngineData { get; } = EngineDataFinder(settings.Current);

    /// <summary>Where "Make me a map" puts the maps it makes: the folder chosen in Preferences, else "Made maps" in the projects folder.</summary>
    public string MadeMapsFolder => Settings.Current.MadeMapsFolder is { Length: > 0 } chosen ? chosen : DefaultMadeMapsFolder;

    /// <summary>The "Made maps" folder inside the projects folder: where made maps go until another is chosen.</summary>
    public string DefaultMadeMapsFolder => Path.Combine(Paths.Projects, "Made maps");

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
            return GameResources.Load(project.Info.Game, project.Folder, games, EngineData, project.Info.AddOns);
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
    public IReadOnlyList<GameResources> OtherGames(GameResources? own) => [.. OtherGameOpeners(own).Select(open => open()).OfType<GameResources>()];

    /// <summary>
    /// The user's other games as <see cref="OtherGames"/> lists them, each still to be opened: a shelf opens them on threads of
    /// its own, so the first game's pictures show while the others are read. Each game is opened once, whoever asks first.
    /// </summary>
    public IReadOnlyList<Func<GameResources?>> OtherGameOpeners(GameResources? own)
    {
        var games = Games ?? [];
        return [.. games.Where(g => (g.Game.Supported || g.Game.Engine == "build") && g.Game.Id != own?.Game.Id && g.Game.Id != own?.Game.AddOnFor).DistinctBy(g => g.Game.Id).Select(g => (Func<GameResources?>)(() =>
        {
            try
            {
                return _others.GetOrAdd(g.Game.Id, id => new Lazy<GameResources?>(() => GameResources.Load(id, null, games, EngineData))).Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
            {
                Log.Warn($"{g.Game.Title} could not be read for its pictures: {ex.Message}");
                return null;
            }
        }))];
    }

    /// <summary>The games the last scan found, scanning first when none has finished. Call on a worker.</summary>
    public IReadOnlyList<FoundGame> FoundGames() => Games ?? GameLocator.Scan(GameLocator.StandardFolders(Environment.GetEnvironmentVariable).Concat(Settings.Current.GameFolders).Distinct(), cancel: ShuttingDown);

    public void Shutdown()
    {
        _shutdown.Cancel();
        Sound.Dispose();
    }

    private static string? EngineDataFinder(AppSettings settings)
    {
        var found = Core.Games.EngineData.Find([.. settings.GameFolders, Path.GetDirectoryName(settings.EnginePath) ?? ""]);
        Log.Info(found is null ? "GZDoom's data file was not found: the Objects shelf has no monsters or items, and remixing classic maps needs it." : $"GZDoom data: {found}");
        return found;
    }
}
