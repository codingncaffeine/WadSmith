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
        var folders = GameLocator.StandardFolders(Environment.GetEnvironmentVariable).Concat(_services.Settings.Current.GameFolders).Distinct().ToList();
        IReadOnlyList<FoundGame> found;
        try
        {
            found = await Task.Run(() => GameLocator.Scan(folders, cancel: _services.ShuttingDown), _services.ShuttingDown).ConfigureAwait(true);
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
        Status = Games.Count switch
        {
            0 => "No games found yet. WadSmith looks in Steam, GOG and the usual folders; add the folder your games are in.",
            1 => "Found 1 game.",
            _ => $"Found {Games.Count} games.",
        };
        Log.Info($"Game scan: {found.Count} file(s) recognised in {folders.Count} folder(s); {Games.Count} base game(s).");
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
