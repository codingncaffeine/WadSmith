using System.Collections.ObjectModel;
using System.Globalization;
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
using WadSmith.Core.Translation;
using WadSmith.Editor.App;

namespace WadSmith.App.ViewModels;

/// <summary>A map offered for remixing: its slot and the name players know it by.</summary>
internal sealed record MapChoice(string Slot, string Title, GameTileViewModel Tile)
{
    public string Label => Title.Length > 0 ? $"{Slot} · {Title}" : Slot;
}

/// <summary>A map in a map pack found near the games, offered for editing under the game it is for.</summary>
internal sealed record PackMapChoice(MapFile Pack, string Slot, GameTileViewModel Tile)
{
    public string Label => $"{Pack.Name} › {Slot}";
}

/// <summary>One base game found on this computer.</summary>
internal sealed partial class GameTileViewModel(FoundGame found) : ObservableObject
{
    public FoundGame Found { get; } = found;

    public string Title => Found.Game.Title;

    public string Location => Found.Path;

    [ObservableProperty]
    private Bitmap? _picture;

    /// <summary>How the picture fills its box: a wide title screen fills it; a tall cover sits whole over its own blurred copy.</summary>
    [ObservableProperty]
    private Avalonia.Media.Stretch _pictureStretch = Avalonia.Media.Stretch.UniformToFill;

    [ObservableProperty]
    private IReadOnlyList<MapChoice> _maps = [];

    /// <summary>The maps in the map packs found near the games that are for this game (D52), by pack then slot.</summary>
    [ObservableProperty]
    private IReadOnlyList<PackMapChoice> _packMaps = [];

    /// <summary>The official add-ons found for this game (Deathkings for Hexen): each one's title and maps, edited with it loaded.</summary>
    [ObservableProperty]
    private IReadOnlyList<(string Title, IReadOnlyList<PackMapChoice> Maps)> _addOns = [];

    /// <summary>The game's maps as sets, each a card: its own maps, its add-ons', then each collection found near the games.</summary>
    [ObservableProperty]
    private IReadOnlyList<MapSetViewModel> _sets = [];

    /// <summary>"Maps · 20 sets, 1,640 maps", the heading over the cards.</summary>
    [ObservableProperty]
    private string _setsLabel = "Maps";

    /// <summary>What the user typed to shorten the list: on the cards, part of a set's name or a map's; in a set, part of a map's.</summary>
    [ObservableProperty]
    private string _packFilter = "";

    /// <summary>The cards shown: every set, or those matching <see cref="PackFilter"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<MapSetViewModel> _shownSets = [];

    /// <summary>The set whose maps are shown in place of the cards; null shows the cards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingCards))]
    private MapSetViewModel? _openSet;

    public bool ShowingCards => OpenSet is null;

    /// <summary>The open set's maps shown: all of them, or those matching <see cref="PackFilter"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<SetMap> _shownSetMaps = [];

    /// <summary>The map picked in the open set: picking one starts editing it, and the pick clears.</summary>
    [ObservableProperty]
    private SetMap? _chosenMap;

    /// <summary>Asked for the pictures of the sets' cards (set by the welcome screen).</summary>
    public Action<GameTileViewModel>? WantPictures { get; set; }

    partial void OnMapsChanged(IReadOnlyList<MapChoice> value) => MakeSets();

    partial void OnPackMapsChanged(IReadOnlyList<PackMapChoice> value) => MakeSets();

    partial void OnAddOnsChanged(IReadOnlyList<(string Title, IReadOnlyList<PackMapChoice> Maps)> value) => MakeSets();

    partial void OnPictureChanged(Bitmap? value)
    {
        if (Sets.FirstOrDefault(s => s.Key == "own") is { } own) own.Picture = value;
    }

    partial void OnPictureStretchChanged(Avalonia.Media.Stretch value)
    {
        if (Sets.FirstOrDefault(s => s.Key == "own") is { } own) own.PictureStretch = value;
    }

    partial void OnPackFilterChanged(string value) => ShowSets();

    partial void OnOpenSetChanged(MapSetViewModel? oldValue, MapSetViewModel? newValue)
    {
        // A search typed over the cards found the set; inside it, its maps are shown whole. The same set brought up to date
        // by a later look keeps what was typed in it.
        if (newValue is not null && newValue.Key != oldValue?.Key) PackFilter = "";
        ShowSets();
    }

    partial void OnChosenMapChanged(SetMap? value)
    {
        if (value is null) return;
        ChosenMap = null;
        if (value.Own is { } own) _ = Remix?.ExecuteAsync(own);
        else if (value.Pack is { } pack) _ = EditPackMap?.ExecuteAsync(pack);
    }

    /// <summary>Opens a set's card: its maps in place of the cards.</summary>
    [RelayCommand]
    private void ShowSet(MapSetViewModel set) => OpenSet = set;

    /// <summary>Back from a set's maps to the cards.</summary>
    [RelayCommand]
    private void BackToSets() => OpenSet = null;

    private void MakeSets()
    {
        // A set kept across a later look keeps its picture; the open set stays open while it is still there.
        var old = Sets.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var sets = MapSetViewModel.For(this);
        foreach (var s in sets)
        {
            if (s.Key == "own")
            {
                s.Picture = Picture;
                s.PictureStretch = PictureStretch;
            }
            else if (old.TryGetValue(s.Key, out var was) && s.Packs.SequenceEqual(was.Packs))
            {
                s.Picture = was.Picture;
                s.PictureStretch = was.PictureStretch;
                s.PictureAsked = was.PictureAsked;
            }
        }

        Sets = sets;
        var mapCount = sets.Sum(s => s.Maps.Count);
        SetsLabel = sets.Count == 1 ? $"Maps · {mapCount:N0}" : $"Maps · {sets.Count} sets, {mapCount:N0} maps";
        var open = OpenSet is { } o ? sets.FirstOrDefault(s => s.Key == o.Key) : null;
        if (!ReferenceEquals(open, OpenSet)) OpenSet = open;
        ShowSets();
        WantPictures?.Invoke(this);
    }

    private void ShowSets()
    {
        var filter = PackFilter.Trim();
        ShownSets = OpenSet is null && filter.Length > 0 ? [.. Sets.Where(s => s.Matches(filter))] : Sets;
        var maps = OpenSet?.Maps ?? [];
        ShownSetMaps = filter.Length == 0 ? maps : [.. maps.Where(m => m.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Plays the game as it is, from its title screen (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<GameTileViewModel>? PlayGame { get; set; }

    /// <summary>Plays one of its sets as it is (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<MapSetViewModel>? PlaySet { get; set; }

    /// <summary>Starts a project from a map in a pack found near the games (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<PackMapChoice>? EditPackMap { get; set; }

    /// <summary>The user's own maps for this game, newest first.</summary>
    public ObservableCollection<ProjectChoice> Projects { get; } = [];

    /// <summary>"Your maps · 3", the button that opens the list.</summary>
    [ObservableProperty]
    private string _projectsLabel = "Your maps";

    /// <summary>What a map started from this card plays in (D51): the titles, and the one chosen.</summary>
    public IReadOnlyList<string> TargetTitles { get; } = [.. Targets.All.Select(t => t.Title)];

    [ObservableProperty]
    private int _targetIndex;

    /// <summary>The chosen target's id.</summary>
    public string Target => Targets.All[Math.Clamp(TargetIndex, 0, Targets.All.Count - 1)].Id;

    /// <summary>Starts a remix of one of this game's maps (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<MapChoice>? Remix { get; set; }

    /// <summary>Starts a new map for this game (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<GameTileViewModel>? NewMap { get; set; }

    /// <summary>Opens "Make me a map" for this game (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IRelayCommand<GameTileViewModel>? MakeMap { get; set; }

    /// <summary>Whether WadSmith can make maps for this game (the Doom, Heretic, Hexen and Strife families).</summary>
    public bool CanMakeMap => WadSmith.Editor.Generate.GeneratorSettings.CanMake(Found.Game);

    /// <summary>What the Make me a map tile says: what it does, or that it is not for this game yet.</summary>
    public string MakeMapNote => CanMakeMap ? "Pick its size, keys, bosses and monsters; WadSmith builds the whole map." : $"Not for {Title} yet.";

    /// <summary>The line under the game's name in the list of games: how many of the user's maps are for it.</summary>
    [ObservableProperty]
    private string _railNote = "";

    /// <summary>Starts a new map for this game with the guided first map running (set by the welcome screen).</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<GameTileViewModel>? GuidedMap { get; set; }
}

/// <summary>One of the user's own maps (a project) on a game's tile: open it, or rename, copy or delete it.</summary>
internal sealed partial class ProjectChoice(WelcomeViewModel owner, Project.Found found) : ObservableObject
{
    public Project.Found Found { get; } = found;

    public string Folder => Found.Folder;

    public string Game => Found.Info.Game;

    [ObservableProperty]
    private string _name = found.Info.Name;

    /// <summary>When it was last changed, in words.</summary>
    public string When => Found.Modified.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Whether the name is being edited in place.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _newName = found.Info.Name;

    [RelayCommand]
    private Task OpenAsync() => owner.OpenFolderAsync(Folder);

    [RelayCommand]
    private void Rename()
    {
        NewName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        IsRenaming = false;
        if (NewName.Trim().Length == 0 || NewName.Trim() == Name) return;
        await owner.RenameProjectAsync(this, NewName.Trim()).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task CopyAsync() => owner.CopyProjectAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteProjectAsync(this);
}

/// <summary>A recently opened project.</summary>
internal sealed record RecentProject(string Folder)
{
    public string Name => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
        _watcher = new GameFolderWatcher(() => MainViewModel.PostToUi(FilesChanged));
        services.ShuttingDown.Register(_watcher.Dispose);
        RefreshRecent();
        _ = ScanAsync();
    }

    /// <summary>Watches the game folders, so a WAD put in one is listed without being asked.</summary>
    private readonly GameFolderWatcher _watcher;

    /// <summary>The folders watched now, so the watch changes only when they do.</summary>
    private IReadOnlyList<string> _watching = [];

    /// <summary>Files changed in the game folders while another page showed.</summary>
    private bool _changedAway;

    /// <summary>Files arrived, moved or went in a game folder: the start screen looks again now, or when it shows again.</summary>
    private void FilesChanged()
    {
        if (ReferenceEquals(_main.Page, this)) _ = ScanAsync(quiet: true);
        else _changedAway = true;
    }

    /// <summary>The start screen shows again (back from the editor): it looks again when files changed meanwhile.</summary>
    public void Shown()
    {
        if (!_changedAway) return;
        _changedAway = false;
        _ = ScanAsync(quiet: true);
    }

    /// <summary>The window is used again: a quiet look, at most every half minute, for what a watch cannot see (a folder around one added, a drive mounted since).</summary>
    public void WindowUsedAgain()
    {
        if (ReferenceEquals(_main.Page, this) && _scanGate.CurrentCount > 0 && DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(30)) _ = ScanAsync(quiet: true);
    }

    /// <summary>The window closed: the folders are no longer watched.</summary>
    public void StopWatching() => _watcher.Dispose();

    public ObservableCollection<GameTileViewModel> Games { get; } = [];

    /// <summary>The game whose picture, actions and maps the start screen shows; remembered for the next start.</summary>
    [ObservableProperty]
    private GameTileViewModel? _selectedGame;

    /// <summary>Whether the start screen shows the maps for games not found instead of a game.</summary>
    [ObservableProperty]
    private bool _showingOther;

    partial void OnSelectedGameChanged(GameTileViewModel? value)
    {
        if (value is null) return;
        LoadSetPictures(value);
        ShowingOther = false;
        if (_choosing || _services.Settings.Current.LastGame == value.Found.Game.Id) return;
        _services.Settings.Current.LastGame = value.Found.Game.Id;
        _services.Settings.Save();
    }

    // Set while the scan fills the list, so the game chosen for the user is not saved as their choice.
    private bool _choosing;

    /// <summary>Shows the maps for games not found right now.</summary>
    [RelayCommand]
    private void ShowOtherMaps()
    {
        SelectedGame = null;
        ShowingOther = true;
    }

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

    /// <summary>The user's maps whose game is not among the tiles (its files not found right now), so they are still one click away.</summary>
    public ObservableCollection<ProjectChoice> OtherProjects { get; } = [];

    [ObservableProperty]
    private string _projectsNote = "";

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
        _ = RefreshProjectsAsync();
    }

    /// <summary>Lists the user's maps (read on a worker) under the tile of their game, the rest under the tiles.</summary>
    public async Task RefreshProjectsAsync()
    {
        var folder = _services.Paths.Projects;
        // The maps made for the user (D50) sit in a folder of their own: the chosen one, the default "Made maps", and the
        // folders of maps opened lately, so maps made before the folder was changed stay on their cards.
        string[] places = [folder, _services.MadeMapsFolder, _services.DefaultMadeMapsFolder, .. _services.Settings.Current.Recent.Select(Path.GetDirectoryName).OfType<string>()];
        IReadOnlyList<Project.Found> found;
        try
        {
            found = await Task.Run(() => (IReadOnlyList<Project.Found>)[.. places.Distinct(StringComparer.Ordinal).SelectMany(Project.List).DistinctBy(p => p.Folder, StringComparer.Ordinal).OrderByDescending(p => p.Modified)]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The maps in {folder} could not be listed: {ex.Message}");
            return;
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in Games)
        {
            tile.Projects.Clear();
            foreach (var p in found.Where(p => p.Info.Game == tile.Found.Game.Id))
            {
                tile.Projects.Add(new ProjectChoice(this, p));
                placed.Add(p.Folder);
            }

            tile.ProjectsLabel = tile.Projects.Count == 0 ? "Your maps" : $"Your maps · {tile.Projects.Count}";
            tile.RailNote = tile.Projects.Count switch { 0 => "", 1 => "1 of your maps", var n => $"{n} of your maps" };
        }

        OtherProjects.Clear();
        foreach (var p in found.Where(p => !placed.Contains(p.Folder))) OtherProjects.Add(new ProjectChoice(this, p));
    }

    /// <summary>Gives a map a new name; its folder stays.</summary>
    internal async Task RenameProjectAsync(ProjectChoice choice, string name)
    {
        try
        {
            await Task.Run(() => Project.Open(choice.Folder).Rename(name)).ConfigureAwait(true);
            choice.Name = name;
            ProjectsNote = $"Renamed to {name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or ArgumentException)
        {
            ProjectsNote = $"The map could not be renamed: {ex.Message}";
        }

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    /// <summary>A whole copy of a map beside it, named after it with "copy", ready to open.</summary>
    internal async Task CopyProjectAsync(ProjectChoice choice)
    {
        var name = $"{choice.Name} copy";
        try
        {
            var to = UniqueFolder(name);
            await Task.Run(() => Project.Copy(choice.Folder, to, name)).ConfigureAwait(true);
            ProjectsNote = $"Copied as {name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
        {
            ProjectsNote = $"The map could not be copied: {ex.Message}";
        }

        await RefreshProjectsAsync().ConfigureAwait(true);
    }

    /// <summary>Moves a map to the Deleted folder inside the maps folder: it can be brought back by moving it out again.</summary>
    internal async Task DeleteProjectAsync(ProjectChoice choice)
    {
        try
        {
            // Into the Deleted folder beside it: a made map's goes into its own folder's.
            var projects = Path.GetDirectoryName(choice.Folder) ?? _services.Paths.Projects;
            var where = await Task.Run(() => Project.Delete(choice.Folder, projects)).ConfigureAwait(true);
            _services.Settings.Current.Recent.RemoveAll(r => string.Equals(r, choice.Folder, StringComparison.Ordinal));
            _services.Settings.Save();
            ProjectsNote = $"{choice.Name} moved to {where}. To bring it back, move the folder out of Deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProjectsNote = $"The map could not be deleted: {ex.Message}";
        }

        await RefreshProjectsAsync().ConfigureAwait(true);
        RefreshRecent();
    }

    /// <summary>Looks for the games again (after the game folders changed).</summary>
    public Task RescanAsync() => ScanAsync();

    /// <summary>Opens Preferences over the window.</summary>
    public IRelayCommand OpenPreferencesCommand => _main.OpenPreferencesCommand;

    private readonly SemaphoreSlim _scanGate = new(1, 1);

    /// <summary>Looks waiting for the one under way to finish.</summary>
    private int _scansWaiting;

    /// <summary>When the last look finished (at first, when WadSmith started), so using the window again looks only now and then.</summary>
    private DateTime _lastScan = DateTime.UtcNow;

    /// <summary>
    /// Looks for the games and maps: when the user asks (shown and awaited), or, <paramref name="quiet"/>, after a change seen
    /// in the game folders or the window used again: then the page stays as it is (the tiles kept with their pictures and
    /// searches, only their lists of maps brought up to date). Looks never overlap; a quiet look asked for while another
    /// waits to start adds nothing, as that one sees the change too.
    /// </summary>
    private async Task ScanAsync(bool quiet = false)
    {
        if (quiet && _scansWaiting > 0) return;
        _scansWaiting++;
        await _scanGate.WaitAsync().ConfigureAwait(true);
        _scansWaiting--;
        try
        {
            await ScanOnceAsync(quiet).ConfigureAwait(true);
        }
        finally
        {
            _lastScan = DateTime.UtcNow;
            _scanGate.Release();
        }
    }

    private async Task ScanOnceAsync(bool quiet)
    {
        if (!quiet)
        {
            Scanning = true;
            Status = "Looking for your games…";
        }

        var added = _services.Settings.Current.GameFolders.ToList();
        var targets = GameLocator.StandardTargets(Environment.GetEnvironmentVariable, _services.Settings.Current.EnginePath).Concat(added.Select(f => new ScanTarget(f, GameLocator.DefaultDepth, Added: true))).ToList();
        var extracted = ExtractedFolder;
        var cachePath = Path.Combine(_services.Paths.Cache, "game-scan.json");
        var packCachePath = Path.Combine(_services.Paths.Cache, "pack-scan.json");
        var progress = new Progress<string>(s =>
        {
            if (!quiet) Status = s;
        });
        ScanReport report;
        IReadOnlyList<FoundGame> found;
        IReadOnlyList<PackFit> packs;
        IReadOnlyList<(FoundGame Game, MapFile File)> addOns;
        try
        {
            (report, found, packs, addOns, ScanMemory) = await Task.Run(() =>
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

                // The map packs near the games, matched to the game each is for (D52); a game's textures are read once.
                var started = System.Diagnostics.Stopwatch.StartNew();
                if (r.MapFiles.Any()) ((IProgress<string>)progress).Report("Reading the map packs near your games…");
                var names = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
                IReadOnlySet<string> NamesOf(string path)
                {
                    if (!names.TryGetValue(path, out var set))
                    {
                        try
                        {
                            set = MapPacks.TextureNamesOf(path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                        {
                            Log.Warn($"The textures of {Path.GetFileName(path)} could not be listed: {ex.Message}");
                            set = new HashSet<string>();
                        }

                        names[path] = set;
                    }

                    return set;
                }

                var packCache = PackCache.Load(packCachePath);
                var fits = MapPacks.Fit(r.MapFiles, usable, NamesOf, packCache, _services.ShuttingDown);
                try
                {
                    packCache.Save(packCachePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"The map packs' memory could not be saved: {ex.Message}");
                }

                Log.Info($"Game scan: {fits.Count} of {r.MapFiles.Count()} map pack(s) fit a game found, {packCache.Hits} remembered, in {started.ElapsedMilliseconds} ms.");
                // The official add-ons found (Deathkings over Hexen): their maps are offered under the game they load over.
                var addOnMaps = new List<(FoundGame, MapFile)>();
                foreach (var a in usable.Where(u => u.Usable && u.Game.AddOnFor is not null && u.Game.Supported).DistinctBy(u => u.Game.Id))
                {
                    try
                    {
                        var maps = MapLocator.Find(WadContainer.Open(a.Path)).Select(m => m.Name).ToList();
                        if (maps.Count > 0) addOnMaps.Add((a, new MapFile(a.Path, null, maps)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                    {
                        Log.Warn($"The maps of {a.Game.Title} could not be listed: {ex.Message}");
                    }
                }

                return (r, usable, fits, (IReadOnlyList<(FoundGame, MapFile)>)addOnMaps, (cache.Hits, cache.Count));
            }, _services.ShuttingDown).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _services.Games = found;

        // A tile already shown is kept (its pictures, its maps, what was typed in its search); only its list of maps found
        // near the games is brought up to date. A game new to the list gets a tile of its own.
        var shown = Games.ToDictionary(t => TileKey(t.Found), StringComparer.Ordinal);
        var tiles = new List<GameTileViewModel>();
        var fresh = new List<GameTileViewModel>();
        foreach (var g in found.Where(g => g.Game.IsBaseGame && g.Game.Supported).GroupBy(g => g.Game.Id).Select(g => g.First()).OrderBy(g => g.Game.Title, StringComparer.Ordinal))
        {
            if (!shown.TryGetValue(TileKey(g), out var tile))
            {
                tile = new GameTileViewModel(g) { Remix = RemixCommand, NewMap = NewMapCommand, GuidedMap = GuidedMapCommand, EditPackMap = EditPackMapCommand, MakeMap = MakeMapCommand, WantPictures = LoadSetPictures, PlayGame = PlayGameCommand, PlaySet = PlaySetCommand };
                fresh.Add(tile);
            }

            var pickFrom = tile;
            tile.PackMaps = [.. packs.Where(f => f.Games.Any(x => x.Game.Id == g.Game.Id)).OrderBy(f => f.File.Name, StringComparer.OrdinalIgnoreCase).SelectMany(f => f.File.Maps.Order(StringComparer.OrdinalIgnoreCase).Select(m => new PackMapChoice(f.File, m, pickFrom)))];
            tile.AddOns = [.. addOns.Where(a => a.Game.Game.AddOnFor == g.Game.Id).Select(a => (a.Game.Game.Title, (IReadOnlyList<PackMapChoice>)[.. a.File.Maps.Select(m => new PackMapChoice(a.File, m, pickFrom))]))];
            tiles.Add(tile);
        }

        var sameTiles = tiles.SequenceEqual(Games);
        if (!sameTiles)
        {
            Games.Clear();
            foreach (var tile in tiles) Games.Add(tile);
        }

        foreach (var tile in fresh) _ = LoadTileAsync(tile);

        // The game chosen last time, else the first that every way of starting a map is open for.
        _choosing = true;
        SelectedGame = Games.FirstOrDefault(t => t.Found.Game.Id == (SelectedGame?.Found.Game.Id ?? _services.Settings.Current.LastGame)) ?? Games.FirstOrDefault(t => t.CanMakeMap) ?? Games.FirstOrDefault();
        _choosing = false;
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

        // The tiles exist now: the user's maps go onto them (listed earlier, before the scan, they all fell through to the bottom).
        // A quiet look that kept every tile leaves them as they are.
        if (!(quiet && sameTiles)) await RefreshProjectsAsync().ConfigureAwait(true);

        // The folders the user added and WadSmith's own games folder are watched for files arriving, moving or going.
        IReadOnlyList<string> watch = [.. added, Path.Combine(_services.Paths.Data, "games")];
        if (!watch.SequenceEqual(_watching, StringComparer.Ordinal))
        {
            _watching = watch;
            _ = Task.Run(() => _watcher.Watch(watch));
        }
        Log.Info($"Game scan: {report.Games.Count} game file(s) in {report.Folders.Count} folder(s), {found.Count} usable; {Games.Count} base game(s).");
        foreach (var f in report.Folders.Where(f => !f.Empty || added.Contains(f.Folder, StringComparer.Ordinal)))
        {
            Log.Info($"Game scan: {f.Folder}: {f.Wads} WAD(s), {f.Archives} archive(s), {f.Games.Count} game(s), {f.MapFiles.Count} map file(s)");
        }
    }

    /// <summary>What makes a tile the same tile in a later look: the game and the file it stands for.</summary>
    private static string TileKey(FoundGame found) => $"{found.Game.Id}|{found.Path}";

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
    /// <summary>Plays a game as it is, from its title screen (right-click on it in the list of games).</summary>
    [RelayCommand]
    private Task PlayGameAsync(GameTileViewModel tile) => PlayAsIsAsync(tile, null);

    /// <summary>Plays a set as it is: the game itself, an add-on or a pack loaded over it (right-click on its card).</summary>
    [RelayCommand]
    private Task PlaySetAsync(MapSetViewModel set) => PlayAsIsAsync(set.Tile, set.Key == "own" ? null : set);

    /// <summary>
    /// Starts the first engine found on <paramref name="tile"/>'s game with <paramref name="set"/>'s pack, with the engine's
    /// own settings and saves, and leaves it running: WadSmith does not wait for it or read what it says.
    /// </summary>
    private async Task PlayAsIsAsync(GameTileViewModel tile, MapSetViewModel? set)
    {
        var name = set?.Title ?? tile.Title;
        if (set is { CanPlay: false })
        {
            Notice = $"{name} holds {set.Packs.Count:N0} WADs: open its card and pick a map to edit, or play one WAD of it from your engine.";
            return;
        }

        var chosen = _services.Settings.Current.EnginePath;
        var packs = Path.Combine(_services.Paths.Cache, "packs");
        try
        {
            var (engine, files) = await Task.Run(() =>
            {
                var engines = WadSmith.Core.Engines.EngineLocator.Find(chosen, Environment.GetEnvironmentVariable);
                IReadOnlyList<string> files = set is null ? [] : [.. set.Packs.Select(p => MapPacks.Materialise(p, packs))];
                return (engines.FirstOrDefault(), files);
            }, _services.ShuttingDown).ConfigureAwait(true);
            if (engine is null)
            {
                Notice = WadSmith.Core.Engines.EngineLocator.NoEngine;
                return;
            }

            var (program, arguments) = WadSmith.Core.Engines.PlayCommand.ForGame(engine, tile.Found.Path, files);
            var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(tile.Found.Path) ?? "" };
            foreach (var a in arguments) start.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("the engine did not start");
            Log.Info($"Playing {name} as it is: {program} {string.Join(' ', arguments)}");
            Status = $"Playing {name} in {engine.Name}.";
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Playing {name} failed: {ex.Message}");
            Notice = $"{name} could not be played: {ex.Message}";
        }
    }

    /// <summary>Makes the card pictures of the chosen game's map sets, one at a time on a worker; one made before comes from the cache.</summary>
    private void LoadSetPictures(GameTileViewModel tile)
    {
        if (!ReferenceEquals(tile, SelectedGame)) return;
        var wanted = tile.Sets.Where(s => !s.PictureAsked && s.Packs.Count > 0).ToList();
        if (wanted.Count == 0) return;
        foreach (var s in wanted) s.PictureAsked = true;
        _ = MakeSetPicturesAsync(tile, wanted);
    }

    private async Task MakeSetPicturesAsync(GameTileViewModel tile, IReadOnlyList<MapSetViewModel> sets)
    {
        var folder = Path.Combine(_services.Paths.Cache, "set-pictures");
        foreach (var set in sets)
        {
            try
            {
                var (bitmap, title) = await Task.Run(() =>
                {
                    var made = SetPictures.For(set.Packs, tile.Found.Path, tile.Found.Game.MapNamespace, folder);
                    return made is null ? (null, false) : (new Bitmap(made.File), made.TitleScreen);
                }, _services.ShuttingDown).ConfigureAwait(true);
                if (bitmap is null) continue;
                set.PictureStretch = title ? Avalonia.Media.Stretch.UniformToFill : Avalonia.Media.Stretch.Uniform;
                set.Picture = bitmap;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                Log.Warn($"The picture for {set.Title} could not be made: {ex.Message}");
            }
        }
    }

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

        // The game's cover from its article (D53), fetched once when allowed and online: it replaces the title screen when it arrives.
        if (!_services.Settings.Current.CoverPictures || _services.Http is not { } http || tile.Found.Game.Wiki is null) return;
        try
        {
            var cover = await Task.Run(async () =>
            {
                var file = await CoverArt.FetchAsync(tile.Found.Game, Path.Combine(_services.Paths.Cache, "covers"), http, _services.ShuttingDown).ConfigureAwait(false);
                return file is null ? null : new Bitmap(file);
            }, _services.ShuttingDown).ConfigureAwait(true);
            if (cover is null) return;
            tile.PictureStretch = cover.Size.Width >= cover.Size.Height ? Avalonia.Media.Stretch.UniformToFill : Avalonia.Media.Stretch.Uniform;
            tile.Picture = cover;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            Log.Warn($"The cover picture for {tile.Title} could not be shown: {ex.Message}");
        }
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
    private Task RemixAsync(MapChoice m) => RemixMapAsync(m.Tile.Found, m.Tile.Title, m.Slot, m.Title, m.Tile.Target);

    /// <summary>Starts a project from <paramref name="slot"/> of a base game and opens it: the tile's Remix, and Remix on a map opened to learn from.</summary>
    internal async Task RemixMapAsync(FoundGame found, string gameTitle, string slot, string mapTitle, string target = Targets.GZDoom)
    {
        await StartAsync(() =>
        {
            var folder = UniqueFolder($"{gameTitle} {slot} remix");
            var notes = new List<string>();
            var engine = _services.EngineData ?? throw new CorruptDataException("Remixing a classic map needs UZDoom or GZDoom installed: WadSmith reads the engine's own rules for what each line does.");
            var project = Remix.Create(folder, found.Path, slot, engine, notes, mapTitle.Length > 0 ? $"{mapTitle} remix" : null, target);
            foreach (var n in notes) Log.Info($"Remix note: {n}");
            return (project, slot);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Starts a project from a map in a pack found near the games or opened by the user (D52): the pack goes with the project
    /// as an add-on. A pack no longer where it was found is said so, and the games and maps are looked for again.
    /// </summary>
    [RelayCommand]
    private async Task EditPackMapAsync(PackMapChoice choice)
    {
        CloseOffered();
        var name = Path.GetFileName(choice.Pack.Path);
        if (!await Task.Run(() => File.Exists(choice.Pack.Path)).ConfigureAwait(true))
        {
            Notice = $"{name} is no longer in {Path.GetDirectoryName(choice.Pack.Path)}: it was moved or deleted. WadSmith is looking for your maps again and lists it where it is now, if that is in one of your game folders; or open it with Open a map file… at the top.";
            Log.Warn($"{choice.Pack.Path} is gone; looking again.");
            _ = ScanAsync(quiet: true);
            return;
        }

        await StartAsync(() =>
        {
            var engine = _services.EngineData ?? throw new CorruptDataException("Editing a map from a pack needs UZDoom or GZDoom installed: WadSmith reads the engine's own rules for what each line does.");
            var pack = MapPacks.Materialise(choice.Pack, Path.Combine(_services.Paths.Cache, "packs"));
            var folder = UniqueFolder($"{Path.GetFileNameWithoutExtension(pack)} {choice.Slot}");
            var notes = new List<string>();
            var project = Remix.CreateFromPack(folder, pack, choice.Slot, choice.Tile.Found.Game, engine, notes, target: choice.Tile.Target);
            foreach (var n in notes) Log.Info($"Pack map note: {n}");
            return (project, choice.Slot);
        }, said: (project, slot) => $"Editing {slot} of {choice.Pack.Name} in a project of your own, {project.Info.Name}. {name} itself is never changed; it is loaded with your map, so its own pictures and things are there.").ConfigureAwait(true);
    }

    /// <summary>What the start screen has to say about the last thing tried (why a map or a project did not open, and what to do); empty when nothing.</summary>
    [ObservableProperty]
    private string _notice = "";

    [RelayCommand]
    private void CloseNotice() => Notice = "";

    /// <summary>The maps of a file the user opened, or of the map files in a folder that is no project, to pick one from.</summary>
    public ObservableCollection<PackMapChoice> Offered { get; } = [];

    /// <summary>What the offered maps are: "RELAY.WAD: 3 maps for The Ultimate Doom. Pick one to edit it."</summary>
    [ObservableProperty]
    private string _offeredTitle = "";

    [RelayCommand]
    private void CloseOffered()
    {
        Offered.Clear();
        OfferedTitle = "";
    }

    /// <summary>"Open a map file…": a WAD, PK3 or ZIP from anywhere (a friend's), its maps opened or offered.</summary>
    [RelayCommand]
    private async Task OpenMapFileAsync()
    {
        if (PickFile is null) return;
        var file = await PickFile("Open a map file: a WAD, PK3 or ZIP with maps in it, a friend's for instance").ConfigureAwait(true);
        if (file is null) return;
        await OpenMapFilesAsync([file], Path.GetFileName(file)).ConfigureAwait(true);
    }

    /// <summary>
    /// The maps in <paramref name="files"/> (called <paramref name="what"/> to the user), matched to the games found as the maps
    /// near the games are: one map opens at once, several are offered under the bar to pick from; with none, or none for a
    /// game found, the notice says so and what to do.
    /// </summary>
    internal async Task OpenMapFilesAsync(IReadOnlyList<string> files, string what)
    {
        Notice = "";
        CloseOffered();
        var games = _services.Games ?? [];
        IReadOnlyList<PackFit> fits;
        IReadOnlyList<string> slots;
        string? baseGame;
        try
        {
            (fits, slots, baseGame) = await Task.Run(() =>
            {
                var packs = new List<MapFile>();
                string? game = null;
                foreach (var file in files)
                {
                    packs.AddRange(GameLocator.MapFilesIn(file, out var g));
                    game ??= g?.Title;
                }

                IReadOnlySet<string> NamesOf(string path)
                {
                    try
                    {
                        return MapPacks.TextureNamesOf(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                    {
                        return new HashSet<string>();
                    }
                }

                return (MapPacks.Fit(packs, games, NamesOf), (IReadOnlyList<string>)[.. packs.SelectMany(p => p.Maps)], game);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
        {
            Notice = $"{what} could not be read: {ex.Message}";
            return;
        }

        if (slots.Count == 0)
        {
            Notice = baseGame is { } g
                ? $"{what} is {g} itself: a game to make maps for, not a map. Add it with Add games → Pick a game file…."
                : $"{what} holds no maps.";
            return;
        }

        GameTileViewModel? TileFor(PackFit fit) =>
            Games.FirstOrDefault(t => ReferenceEquals(t, SelectedGame) && fit.Games.Any(g => g.Game.Id == t.Found.Game.Id)) ?? Games.FirstOrDefault(t => fit.Games.Any(g => g.Game.Id == t.Found.Game.Id));
        var choices = fits.SelectMany(f => TileFor(f) is { } tile ? f.File.Maps.Order(StringComparer.OrdinalIgnoreCase).Select(m => new PackMapChoice(f.File, m, tile)) : []).ToList();
        if (choices.Count == 0)
        {
            var named = string.Join(", ", slots.Take(3)) + (slots.Count > 3 ? "…" : "");
            Notice = $"{what} holds {(slots.Count == 1 ? "a map" : $"{slots.Count} maps")} ({named}), but not for a game WadSmith found: maps named like E1M1 are Doom's or Heretic's, like MAP01 Doom II's, Hexen's or Strife's. Add the game it is for with Add games, then open it again.";
            return;
        }

        SelectedGame = choices[0].Tile;
        if (choices.Count == 1)
        {
            await EditPackMapAsync(choices[0]).ConfigureAwait(true);
            return;
        }

        var for_ = string.Join(" and ", choices.Select(c => c.Tile.Title).Distinct(StringComparer.Ordinal));
        OfferedTitle = $"{what}: {choices.Count} maps for {for_}. Pick one to edit it; the file itself is never changed.";
        foreach (var c in choices) Offered.Add(c);
    }

    /// <summary>The map and game files in a folder and in the folders in it: what a folder that is no project is offered as.</summary>
    private static IReadOnlyList<string> MapFilesInFolder(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                .Where(GameLocator.IsMapOrGameFile).Order(StringComparer.OrdinalIgnoreCase).Take(64)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Opens "Make me a map" (D50) for the tile's game.</summary>
    [RelayCommand]
    private void MakeMap(GameTileViewModel tile) => _main.OpenMakeMap(tile.Found, tile.Target);

    [RelayCommand]
    private Task NewMapAsync(GameTileViewModel tile) => NewMap(tile, guide: false);

    [RelayCommand]
    private Task GuidedMapAsync(GameTileViewModel tile) => NewMap(tile, guide: true);

    private async Task NewMap(GameTileViewModel tile, bool guide)
    {
        await StartAsync(() =>
        {
            var folder = UniqueFolder("My map");
            var project = Project.Create(folder, Path.GetFileName(folder), tile.Found.Game.Id, tile.Target);
            var slot = tile.Found.Game.MapNames == "ExMy" ? "E1M1" : "MAP01";
            project.SaveMap(ClassicTranslation.ForProject(project, StarterRoom.Create(slot, tile.Found.Game.Engine), _services.EngineData), origin: new MapOrigin { Kind = "new" });
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

    /// <summary>
    /// Opens a project folder; a folder that is no project but holds map files (a friend's WAD put in a folder) has its maps
    /// opened or offered as Open a map file does, and one holding neither is said so, with what to do.
    /// </summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder("Choose a WadSmith project folder, or a folder with map files in it").ConfigureAwait(true);
        if (folder is null) return;
        Notice = "";
        var (isProject, files) = await Task.Run(() => File.Exists(Path.Combine(folder, Project.SidecarName)) ? (true, (IReadOnlyList<string>)[]) : (false, MapFilesInFolder(folder))).ConfigureAwait(true);
        if (isProject)
        {
            await OpenRecentAsync(new RecentProject(folder)).ConfigureAwait(true);
            return;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (files.Count == 0)
        {
            Notice = $"{name} is not a WadSmith project, and holds no map files. A project is a folder WadSmith makes, with wadsmith.json in it: yours are under Your maps for their game. A WAD, PK3 or ZIP of maps, a friend's for instance, opens with Open a map file… at the top.";
            return;
        }

        await OpenMapFilesAsync(files, files.Count == 1 ? Path.GetFileName(files[0]) : $"The map files in {name}").ConfigureAwait(true);
    }

    /// <summary>
    /// Runs the project work and reads the map on a worker, then opens the editor on the UI thread, which says
    /// <paramref name="said"/> (what was opened) when given; what went wrong is the start screen's notice.
    /// </summary>
    private async Task StartAsync(Func<(Project Project, string Slot)> work, bool guide = false, Func<Project, string, string>? said = null)
    {
        if (Busy) return;
        Busy = true;
        Notice = "";
        try
        {
            var session = await Task.Run(() =>
            {
                var (project, slot) = work();
                return EditorSession.Open(project, slot, MainViewModel.HooksFor(_services.ResourcesFor(project), _services.EngineData));
            }).ConfigureAwait(true);
            _main.OpenEditor(session, said?.Invoke(session.Project, session.Slot), guide: guide);
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            Notice = ex.Message;
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
