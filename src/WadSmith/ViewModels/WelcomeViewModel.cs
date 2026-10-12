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

    public ObservableCollection<RecentProject> Recent { get; } = [];

    [ObservableProperty]
    private bool _scanning;

    [ObservableProperty]
    private string _status = "Looking for your games…";

    [ObservableProperty]
    private bool _noGames;

    [ObservableProperty]
    private bool _busy;

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
            var tile = new GameTileViewModel(g) { Remix = RemixCommand, NewMap = NewMapCommand };
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
    private async Task RemixAsync(MapChoice m)
    {
        var t = m.Tile;
        await StartAsync(() =>
        {
            var folder = UniqueFolder($"{t.Title} {m.Slot} remix");
            var notes = new List<string>();
            var engine = _services.EngineData ?? throw new CorruptDataException("Remixing a classic map needs GZDoom installed: WadSmith reads the engine's own rules for what each line does.");
            var project = Remix.Create(folder, t.Found.Path, m.Slot, engine, notes, m.Title.Length > 0 ? $"{m.Title} remix" : null);
            foreach (var n in notes) Log.Info($"Remix note: {n}");
            return (project, m.Slot);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NewMapAsync(GameTileViewModel tile)
    {
        await StartAsync(() =>
        {
            var folder = UniqueFolder("My map");
            var project = Project.Create(folder, Path.GetFileName(folder), tile.Found.Game.Id);
            var slot = tile.Found.Game.MapNames == "ExMy" ? "E1M1" : "MAP01";
            project.SaveMap(StarterRoom.Create(slot, tile.Found.Game.Engine), origin: new MapOrigin { Kind = "new" });
            return (project, slot);
        }).ConfigureAwait(true);
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
    private async Task StartAsync(Func<(Project Project, string Slot)> work)
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
            _main.OpenEditor(session);
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
