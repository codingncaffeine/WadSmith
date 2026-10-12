using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using WadSmith.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor.App;

namespace WadSmith.App.ViewModels;

/// <summary>A map offered for remixing: its slot and the name players know it by.</summary>
internal sealed record MapChoice(string Slot, string Title, GameTileViewModel Tile)
{
    public string Label => Title.Length > 0 ? $"{Slot} · {Title}" : Slot;
}

/// <summary>One base game found on this computer.</summary>
internal sealed partial class GameTileViewModel(FoundGame found) : ObservableObject
{
    public FoundGame Found { get; } = found;

    public string Title => Found.Game.Title;

    public string Location => Found.Path;

    [ObservableProperty]
    private Bitmap? _picture;

    [ObservableProperty]
    private IReadOnlyList<MapChoice> _maps = [];

    /// <summary>Starts a remix of one of this game's maps (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<MapChoice>? Remix { get; set; }

    /// <summary>Starts a new map for this game (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<GameTileViewModel>? NewMap { get; set; }

    /// <summary>Starts a new map for this game with the guided first map running (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<GameTileViewModel>? GuidedMap { get; set; }
}

/// <summary>A recently opened project.</summary>
internal sealed record RecentProject(string Folder)
{
    public string Name => Path.GetFileName(Folder.TrimEnd('/'));
}

/// <summary>
/// The first screen: the games found on this computer, each ready to remix one of its maps or to
/// start a new map with; recent projects; and a way to add folders WadSmith did not look in.
/// Nothing here reads a disk on the UI thread.
/// </summary>
internal sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public WelcomeViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        RefreshRecent();
        _ = ScanAsync();
    }

    public ObservableCollection<GameTileViewModel> Games { get; } = [];

    /// <summary>Whether the Guide's invitations show on the tiles ("Guided first map", D34); the chapter stays in the Guide menu and Ctrl+K.</summary>
    public bool GuidePrompts => _services.Settings.Current.GuidePrompts;

    /// <summary>Whether the one quiet question (§3.2 step 5) is still to be asked.</summary>
    public bool AskExperience => !_services.Settings.Current.AskedAboutExperience;

    /// <summary>Shows the teaching layer's settings again after they changed (Preferences closed, the question answered).</summary>
    public void TeachingChanged()
    {
        OnPropertyChanged(nameof(GuidePrompts));
        OnPropertyChanged(nameof(AskExperience));
    }

    /// <summary>
    /// "Have you made Doom maps before?" (§3.2, D34), asked once. Yes turns the coach and the Guide's invitations off, tooltips
    /// to brief and classic names on; No leaves the teaching on. Either way the editor is the same, and Preferences → Teaching
    /// changes any of it later.
    /// </summary>
    [RelayCommand]
    private void AnswerExperience(string answer)
    {
        var settings = _services.Settings.Current;
        settings.AskedAboutExperience = true;
        if (answer == "yes")
        {
            settings.CoachTips = false;
            settings.GuidePrompts = false;
            settings.Tooltips = TooltipLength.Brief;
            settings.ClassicNames = true;
            _services.Words.Classic = true;
        }

        _services.Settings.Save();
        TeachingChanged();
    }

    public ObservableCollection<RecentProject> Recent { get; } = [];

    [ObservableProperty]
    private bool _scanning;

    [ObservableProperty]
    private string _status = "Looking for your games…";

    [ObservableProperty]
    private bool _noGames;

    /// <summary>Whether the user has pointed WadSmith at any folder (then "no games" is about those folders, not about owning none).</summary>
    [ObservableProperty]
    private bool _foldersAdded;

    /// <summary>The no-game card's heading and text: what the folders held, what a base game is, and the Freedoom offer.</summary>
    [ObservableProperty]
    private string _noGamesHeading = "";

    [ObservableProperty]
    private string _noGamesText = "";

    [ObservableProperty]
    private bool _busy;

    /// <summary>Whether Freedoom is being fetched now.</summary>
    [ObservableProperty]
    private bool _gettingFreedoom;

    /// <summary>How the fetch is going, in words; what went wrong when it did.</summary>
    [ObservableProperty]
    private string _freedoomStatus = "";

    /// <summary>The fetch, 0 to 100.</summary>
    [ObservableProperty]
    private double _freedoomProgress;

    /// <summary>Where Freedoom goes: WadSmith's own games folder, added to the folders searched.</summary>
    public string FreedoomFolder => Path.Combine(_services.Paths.Data, "games", "freedoom");

    /// <summary>
    /// Fetches Freedoom (§3.2: the offer when no game is found): the release pinned by checksum, unpacked into WadSmith's
    /// games folder, which is then searched like any other; the tiles fill as soon as it is in.
    /// </summary>
    [RelayCommand]
    private async Task GetFreedoomAsync()
    {
        if (GettingFreedoom) return;
        GettingFreedoom = true;
        FreedoomProgress = 0;
        FreedoomStatus = "Getting Freedoom…";
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WadSmith");
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                FreedoomStatus = p.Total is { } total ? $"Getting Freedoom… {p.Done / 1048576.0:0} of {total / 1048576.0:0} MB" : $"Getting Freedoom… {p.Done / 1048576.0:0} MB";
                FreedoomProgress = p.Total is > 0 and var t ? 100.0 * p.Done / t : 0;
            });
            await Freedoom.InstallAsync(FreedoomFolder, http, progress, _services.ShuttingDown).ConfigureAwait(true);
            var list = _services.Settings.Current.GameFolders;
            if (!list.Contains(FreedoomFolder, StringComparer.Ordinal))
            {
                list.Add(FreedoomFolder);
                _services.Settings.Save();
            }

            FreedoomStatus = "Freedoom is in: pick Remix a map or New map on its tile.";
            Log.Info($"Freedoom {Freedoom.Version} unpacked into {FreedoomFolder}");
            await ScanAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FreedoomStatus = $"Freedoom could not be fetched: {ex.Message} Try again later, or put freedoom1.wad and freedoom2.wad in a folder and add it.";
            Log.Warn($"Fetching Freedoom failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            FreedoomStatus = "";
        }
        finally
        {
            GettingFreedoom = false;
        }
    }

    /// <summary>Set by the view: asks the user for a folder (null when cancelled).</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the view: asks the user for a game file (null when cancelled).</summary>
    public Func<string, Task<string?>>? PickFile { get; set; }

    /// <summary>How the last scan's memory of archives did: how many were answered from memory, how many it knows.</summary>
    public (int Remembered, int Known) ScanMemory { get; private set; }

    /// <summary>Where a base game found inside an archive is taken out to, so the engine has a file to play.</summary>
    public string ExtractedFolder => Path.Combine(_services.Paths.Data, "games", "extracted");

    public bool CanRemixClassicMaps => _services.EngineData is not null;

    public void RefreshRecent()
    {
        Recent.Clear();
        foreach (var r in _services.Settings.Current.Recent.Where(r => File.Exists(Path.Combine(r, Project.SidecarName)))) Recent.Add(new RecentProject(r));
    }

    /// <summary>Looks for the games again (after the game folders changed).</summary>
    public Task RescanAsync() => ScanAsync();

    /// <summary>Opens Preferences over the window.</summary>
    public IRelayCommand OpenPreferencesCommand => _main.OpenPreferencesCommand;

    private async Task ScanAsync()
    {
        Scanning = true;
        Status = "Looking for your games…";
        var added = _services.Settings.Current.GameFolders.ToList();
        var targets = GameLocator.StandardTargets(Environment.GetEnvironmentVariable).Concat(added.Select(f => new ScanTarget(f, GameLocator.DefaultDepth, Added: true))).ToList();
        var extracted = ExtractedFolder;
        var cachePath = Path.Combine(_services.Paths.Cache, "game-scan.json");
        var progress = new Progress<string>(s => Status = s);
        ScanReport report;
        IReadOnlyList<FoundGame> found;
        try
        {
            (report, found, ScanMemory) = await Task.Run(() =>
            {
                var cache = ArchiveCache.Load(cachePath);
                var r = GameLocator.Report(targets, cache, progress, _services.ShuttingDown);
                var usable = GameLocator.Usable(r.Games, extracted, (copy, ex) => Log.Warn($"{copy.Game.Title} could not be taken out of {copy.Path}: {ex.Message}"));
                try
                {
                    cache.Save(cachePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"The game scan's memory could not be saved: {ex.Message}");
                }

                Log.Info($"Game scan: {cache.Hits} archive(s) remembered, {cache.Count} known.");
                return (r, usable, (cache.Hits, cache.Count));
            }, _services.ShuttingDown).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _services.Games = found;
        Games.Clear();
        foreach (var g in found.Where(g => g.Game.IsBaseGame && g.Game.Supported).GroupBy(g => g.Game.Id).Select(g => g.First()).OrderBy(g => g.Game.Title, StringComparer.Ordinal))
        {
            var tile = new GameTileViewModel(g) { Remix = RemixCommand, NewMap = NewMapCommand, GuidedMap = GuidedMapCommand };
            Games.Add(tile);
            _ = LoadTileAsync(tile);
        }

        Scanning = false;
        NoGames = Games.Count == 0;
        FoldersAdded = added.Count > 0;
        Status = Games.Count switch
        {
            0 when added.Count == 0 => "No games found yet. WadSmith looks in Steam, GOG, GZDoom's folders and the usual places, and inside ZIP files. Pick a game file, or the folder your games are in.",
            0 => "No base game in the folders you added yet.",
            1 => "Found 1 game." + FoundAround(report, added, Games.Count),
            _ => $"Found {Games.Count} games." + FoundAround(report, added, Games.Count),
        };
        NoGamesHeading = added.Count == 0 ? "No game found yet" : "No base game in the folders you added";
        NoGamesText = NoGamesWords(report, added);
        Log.Info($"Game scan: {report.Games.Count} game file(s) in {report.Folders.Count} folder(s), {found.Count} usable; {Games.Count} base game(s).");
        foreach (var f in report.Folders.Where(f => !f.Empty || added.Contains(f.Folder, StringComparer.Ordinal)))
        {
            Log.Info($"Game scan: {f.Folder}: {f.Wads} WAD(s), {f.Archives} archive(s), {f.Games.Count} game(s), {f.MapFiles.Count} map file(s)");
        }
    }

    /// <summary>" They are in gzdoom, above the folder you added (DOOM WADs)." when games were found around a folder the user added rather than in it.</summary>
    private static string FoundAround(ScanReport report, IReadOnlyList<string> added, int total)
    {
        // The games in the folders the user added themselves: those need no explaining.
        var inAdded = new HashSet<string>(report.Folders.Where(f => added.Any(a => string.Equals(Path.GetFullPath(a), Path.GetFullPath(f.Folder), StringComparison.Ordinal))).SelectMany(f => f.Games).Select(g => g.Game.Id), StringComparer.Ordinal);
        foreach (var folder in added)
        {
            var around = GameLocator.Around(folder).Skip(1).Select(t => Path.GetFullPath(t.Folder)).ToList();
            var near = report.Folders.Where(f => f.Games.Count > 0 && around.Contains(Path.GetFullPath(f.Folder), StringComparer.Ordinal)).ToList();
            var only = near.SelectMany(f => f.Games.Select(g => g.Game.Id)).Where(id => !inAdded.Contains(id)).Distinct().ToList();
            if (only.Count == 0) continue;
            var where = Path.GetFileName(near[0].Folder.TrimEnd(Path.DirectorySeparatorChar));
            var which = only.Count >= total ? (total == 1 ? "It is" : "They are") : $"{only.Count} of them are";
            return $" {which} in {where}, above the folder you added ({Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}).";
        }

        return "";
    }

    /// <summary>The no-game card: what each added folder held, what a base game is, and the Freedoom offer.</summary>
    private static string NoGamesWords(ScanReport report, IReadOnlyList<string> added)
    {
        static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
        var lines = new List<string>();
        foreach (var folder in added)
        {
            var f = report.Folders.FirstOrDefault(r => string.Equals(Path.GetFullPath(r.Folder), Path.GetFullPath(folder), StringComparison.Ordinal));
            if (f is null) lines.Add($"{folder}: not found.");
            else if (f.Empty) lines.Add($"{folder}: nothing in it — no WAD or archive.");
            else
            {
                var maps = f.MapFiles.Count == 0 ? "" : $", {Count(f.MapFiles.Count, "map file")} ({string.Join(", ", f.MapFiles.Select(m => m.Name).Order(StringComparer.Ordinal).Take(3))}{(f.MapFiles.Count > 3 ? ", …" : "")})";
                lines.Add($"{folder}: {Count(f.Wads, "WAD")} and {Count(f.Archives, "archive")} looked at{maps}; no base game.");
            }
        }

        var intro = added.Count == 0
            ? "WadSmith looked in Steam, GOG, GZDoom's folders and the usual places, and inside ZIP files there, and found no game."
            : string.Join("\n", lines);
        return intro + "\n\nA base game is a file such as DOOM.WAD, DOOM2.WAD, HERETIC.WAD, HEXEN.WAD or STRIFE1.WAD, loose or inside a ZIP. Pick that file, or the folder it is in: WadSmith looks in the folders beside and above it too."
            + "\n\nNo copy of any of these? Freedoom plays like Doom and Doom II and is free to share; WadSmith can fetch it (about 30 MB).";
    }

    /// <summary>The title picture and the map list, read on a worker.</summary>
    private async Task LoadTileAsync(GameTileViewModel tile)
    {
        var engine = _services.EngineData;
        var (picture, maps) = await Task.Run(() =>
        {
            try
            {
                var wad = WadContainer.Open(tile.Found.Path);
                var stack = new ResourceStack([wad]);
                var palette = PictureLoader.PaletteOf(stack);
                RgbaImage? title = null;
                foreach (var name in new[] { "TITLEPIC", "TITLE" })
                {
                    if (wad.Find(name) is { } e)
                    {
                        try
                        {
                            title = PictureLoader.Load(e.Read(), e.Namespace, palette, name).Image;
                            break;
                        }
                        catch (CorruptDataException)
                        {
                        }
                    }
                }

                var titles = MapTitles.For(tile.Found.Game, wad, engine);
                var list = MapLocator.Find(wad).Select(m => new MapChoice(m.Name, titles.GetValueOrDefault(m.Name, ""), tile)).ToList();
                return (title, (IReadOnlyList<MapChoice>)list);
            }
            catch (Exception ex) when (ex is CorruptDataException or IOException)
            {
                Log.Warn($"Could not read {tile.Found.Path}: {ex.Message}");
                return (null, []);
            }
        }).ConfigureAwait(true);

        tile.Maps = maps;
        if (picture is not null) tile.Picture = Imaging.ToBitmap(picture);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder("Choose the folder your games are in").ConfigureAwait(true);
        if (folder is null) return;
        await AddFolderAndScanAsync(folder).ConfigureAwait(true);
    }

    /// <summary>The user picks a game file (DOOM2.WAD, a ZIP holding one…): its folder is added and looked in, with the folders around it.</summary>
    [RelayCommand]
    private async Task AddGameFileAsync()
    {
        if (PickFile is null) return;
        var file = await PickFile("Pick a game file: DOOM.WAD, DOOM2.WAD, HERETIC.WAD, HEXEN.WAD, STRIFE1.WAD, or a ZIP holding one").ConfigureAwait(true);
        if (file is null || Path.GetDirectoryName(file) is not { Length: > 0 } folder) return;
        await AddFolderAndScanAsync(folder).ConfigureAwait(true);
    }

    private async Task AddFolderAndScanAsync(string folder)
    {
        var list = _services.Settings.Current.GameFolders;
        if (!list.Contains(folder, StringComparer.Ordinal))
        {
            list.Add(folder);
            _services.Settings.Save();
        }

        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RemixAsync(MapChoice m) => RemixMapAsync(m.Tile.Found, m.Tile.Title, m.Slot, m.Title);

    /// <summary>Starts a project from <paramref name="slot"/> of a base game and opens it: the tile's Remix, and Remix on a map opened to learn from.</summary>
    internal async Task RemixMapAsync(FoundGame found, string gameTitle, string slot, string mapTitle)
    {
        await StartAsync(() =>
        {
            var folder = UniqueFolder($"{gameTitle} {slot} remix");
            var notes = new List<string>();
            var engine = _services.EngineData ?? throw new CorruptDataException("Remixing a classic map needs GZDoom installed: WadSmith reads the engine's own rules for what each line does.");
            var project = Remix.Create(folder, found.Path, slot, engine, notes, mapTitle.Length > 0 ? $"{mapTitle} remix" : null);
            foreach (var n in notes) Log.Info($"Remix note: {n}");
            return (project, slot);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task NewMapAsync(GameTileViewModel tile) => NewMap(tile, guide: false);

    [RelayCommand]
    private Task GuidedMapAsync(GameTileViewModel tile) => NewMap(tile, guide: true);

    private async Task NewMap(GameTileViewModel tile, bool guide)
    {
        await StartAsync(() =>
        {
            var folder = UniqueFolder("My map");
            var project = Project.Create(folder, Path.GetFileName(folder), tile.Found.Game.Id);
            var slot = tile.Found.Game.MapNames == "ExMy" ? "E1M1" : "MAP01";
            project.SaveMap(StarterRoom.Create(slot, tile.Found.Game.Engine), origin: new MapOrigin { Kind = "new" });
            return (project, slot);
        }, guide).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(RecentProject recent)
    {
        await StartAsync(() =>
        {
            var project = Project.Open(recent.Folder);
            var slot = project.Info.Maps.FirstOrDefault()?.Slot ?? throw new CorruptDataException("This project has no maps yet.");
            return (project, slot);
        }).ConfigureAwait(true);
    }

    /// <summary>Opens a project folder named on the command line.</summary>
    public Task OpenFolderAsync(string folder) => OpenRecentAsync(new RecentProject(Path.GetFullPath(folder)));

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder("Choose a WadSmith project folder").ConfigureAwait(true);
        if (folder is null) return;
        await OpenRecentAsync(new RecentProject(folder)).ConfigureAwait(true);
    }

    /// <summary>Runs the project work and reads the map on a worker, then opens the editor on the UI thread.</summary>
    private async Task StartAsync(Func<(Project Project, string Slot)> work, bool guide = false)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var session = await Task.Run(() =>
            {
                var (project, slot) = work();
                return EditorSession.Open(project, slot, MainViewModel.HooksFor(_services.ResourcesFor(project), _services.EngineData));
            }).ConfigureAwait(true);
            _main.OpenEditor(session, guide: guide);
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            Status = ex.Message;
            Log.Warn(ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private string UniqueFolder(string name)
    {
        Directory.CreateDirectory(_services.Paths.Projects);
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '-' : c));
        var folder = Path.Combine(_services.Paths.Projects, safe);
        for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(_services.Paths.Projects, $"{safe} {n}");
        return folder;
    }
}
