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

    /// <summary>
    /// The playing engine's own data file (uzdoom.pk3 or gzdoom.pk3), needed for the Objects shelf and to remix classic maps;
    /// null when no engine is installed. Found before the window opens, and again when Preferences change the engine.
    /// </summary>
    public string? EngineData
    {
        get => Volatile.Read(ref _engineData);
        private set => Volatile.Write(ref _engineData, value);
    }

    private string? _engineData = EngineDataFinder(paths, settings.Current);

    private int _engineDataAsked;

    /// <summary>Finds the engine data again for the engine now chosen (Preferences changed it); a later change's answer wins. Call on a worker.</summary>
    public void RefreshEngineData()
    {
        var asked = Interlocked.Increment(ref _engineDataAsked);
        var found = EngineDataFinder(Paths, Settings.Current);
        if (asked == Volatile.Read(ref _engineDataAsked)) EngineData = found;
    }

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
        var games = Games ?? GameLocator.Scan(GameLocator.StandardFolders(Environment.GetEnvironmentVariable, Settings.Current.EnginePath).Concat(Settings.Current.GameFolders).Distinct(), cancel: ShuttingDown);
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
        return [.. games.Where(g => (g.Game.Supported || g.Game.Engine is "build" or "quake") && g.Game.Id != own?.Game.Id && g.Game.Id != own?.Game.AddOnFor).DistinctBy(g => g.Game.Id).Select(g => (Func<GameResources?>)(() =>
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

    /// <summary>What WadSmith keeps of the free sources (D61): Realm667's lists, the texture packs, previews and downloads, under the data folder.</summary>
    public Core.Library.LibraryStore Library => _library ??= new Core.Library.LibraryStore(Path.Combine(Paths.Data, "library"), Http);

    private Core.Library.LibraryStore? _library;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Core.Graphics.Palette?>> _palettes = new();

    /// <summary>
    /// The colours of a game family ("doom", "heretic", "hexen", "strife") from a game of it found here, for downloads drawn in
    /// that family's colours (D61); null when none of that family was found. Reads a file the first time: call on a worker.
    /// </summary>
    public Core.Graphics.Palette? FamilyPalette(string family) => _palettes.GetOrAdd(family, f => new Lazy<Core.Graphics.Palette?>(() =>
    {
        foreach (var game in (Games ?? []).Where(g => g.Game.Engine == f && g.Game.Supported && g.Game.IsBaseGame))
        {
            try
            {
                if (Core.Containers.ResourceContainer.Open(game.Path).Find("PLAYPAL") is { Size: >= 768 } playpal) return new Core.Graphics.Palette(playpal.Read());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
            {
                Log.Warn($"{game.Game.Title}'s colours could not be read from {game.Path}: {ex.Message}");
            }
        }

        return null;
    })).Value;

    /// <summary>A texture pack fetched from a free source, opened for its pictures over a game of its family (D61); null when no such game was found. Call on a worker.</summary>
    public GameResources? OpenPack(Core.Library.LibraryItem item, IReadOnlyList<string> files, string? ownGameId)
    {
        try
        {
            return GameResources.LoadPack(item, files, ownGameId, Games ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
        {
            Log.Warn($"The texture pack {item.Title} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Where a free game WadSmith fetches goes: its own folder under WadSmith's games folder.</summary>
    public string FreeGameFolder(FreeGames.Release release) => Path.Combine(Paths.Data, "games", release.Folder);

    /// <summary>Whether a free game is among the games found (fetched by WadSmith or the user's own copy).</summary>
    public bool HasFreeGame(FreeGames.Release release) => (Games ?? []).Any(g => release.Files.Any(file => string.Equals(Path.GetFileName(g.Path), file, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Fetches a free game (D61, §3.2): its release pinned by checksum, unpacked into WadSmith's games folder, which joins the
    /// folders searched; the caller scans again. Call it from the UI thread. Needs the internet (<see cref="Http"/>).
    /// </summary>
    public async Task InstallFreeGameAsync(FreeGames.Release release, IProgress<(long Done, long? Total)>? progress, CancellationToken cancel)
    {
        var http = Http ?? throw new IOException("WadSmith is set not to use the internet.");
        var folder = FreeGameFolder(release);
        // Back on the caller's thread (the UI) before the settings change.
        await FreeGames.InstallAsync(release, folder, http, progress, cancel).ConfigureAwait(true);
        var list = Settings.Current.GameFolders;
        if (!list.Contains(folder, StringComparer.Ordinal))
        {
            list.Add(folder);
            Settings.Save();
        }

        Log.Info($"{release.Title} {release.Version} unpacked into {folder}");
    }

    /// <summary>The games the last scan found, scanning first when none has finished. Call on a worker.</summary>
    public IReadOnlyList<FoundGame> FoundGames() => Games ?? GameLocator.Scan(GameLocator.StandardFolders(Environment.GetEnvironmentVariable, Settings.Current.EnginePath).Concat(Settings.Current.GameFolders).Distinct(), cancel: ShuttingDown);

    public void Shutdown()
    {
        _shutdown.Cancel();
        Sound.Dispose();
    }

    /// <summary>The engine data of the engine that plays (an AppImage's taken out into the cache once), else the first found.</summary>
    private static string? EngineDataFinder(AppPaths paths, AppSettings settings)
    {
        var env = (Func<string, string?>)Environment.GetEnvironmentVariable;
        var engines = Core.Engines.EngineLocator.Find(settings.EnginePath, env);
        var found = Core.Games.EngineData.For(engines, [.. settings.GameFolders, Path.GetDirectoryName(settings.EnginePath) ?? ""], env, Path.Combine(paths.Cache, "engine-data"));
        Log.Info(found is null ? "No engine data file (uzdoom.pk3 or gzdoom.pk3) was found: the Objects shelf has no monsters or items, and remixing classic maps needs it." : $"Engine data: {found} (plays in {(engines.Count > 0 ? engines[0].Name + " at " + engines[0].Choice : "no engine found")})");
        return found;
    }
}
