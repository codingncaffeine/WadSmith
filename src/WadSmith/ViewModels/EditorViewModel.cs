using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.App.ViewModels;

/// <summary>What the editor's right-hand panel shows.</summary>
internal enum SidePanel
{
    Inspector,
    Textures,
    Objects,
    Versions,
}

/// <summary>
/// A map open for editing: the canvas, the Inspector, undo and redo, the save state in plain words,
/// and the status strip, which describes what is under the pointer or, when nothing is, gives the
/// coach's next tip (D33). Tips stop once the user has done what they teach.
/// </summary>
internal sealed partial class EditorViewModel : ObservableObject
{
    private static readonly (string Id, string Text)[] Tips =
    [
        ("zoom", "Tip: scroll to zoom in and out; drag with the right mouse button to move around. Home shows the whole map."),
        ("select", "Tip: click a room, a wall or a thing to see what it is in the Inspector on the right."),
        ("edit", "Tip: change a number in the Inspector, such as a floor height, and watch the map follow."),
        ("draw", "Tip: choose Room at the top left of the map (or press R) and drag to draw a room. Drawn against another room, the two share a wall."),
        ("door", "Tip: choose Door (or press D) and click the wall between two rooms to put a door there. Draw a short corridor between rooms first for a classic doorway."),
        ("undo", "Tip: Ctrl+Z takes back the last change and Ctrl+Y brings it back. Nothing needs saving; WadSmith keeps every change."),
        ("heights", "Tip: the view buttons at the top show floors, ceilings, heights as colours, or just the walls."),
        ("orbit", "Tip: in the 3D view, drag to turn around the map, right-drag to move it, and scroll to come closer."),
        ("walk", "Tip: choose Walk at the top of the 3D view, click it, and use WASD to walk through the map as the player would."),
    ];

    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private string _hoverText = "";
    private string _drawingText = "";
    private string _notice = "";
    private bool _goingBack;

    public EditorViewModel(AppServices services, EditorSession session, MainViewModel main, string? message = null, bool showVersions = false)
    {
        _services = services;
        _main = main;
        Session = session;
        _hoverText = message ?? "";
        Inspector = new InspectorViewModel(session);
        // A remix's narrow gaps and low openings are its author's: the passage checks speak only of the user's own.
        if (session.Origin is { Kind: "remix", Game: { } origin, Map: { } original } && services.EngineData is { } engineData
            && (session.Resources?.Game.Id == origin ? session.Resources.Files[^1] : services.Games?.FirstOrDefault(g => g.Game.Id == origin)?.Path) is { } file)
        {
            session.UseOriginal(() => WadSmith.Core.Projects.Remix.Original(file, original, engineData, []).Map);
        }

        Problems = new ProblemsViewModel(session);
        TextureShelf = ShelfViewModel.Textures(session.Resources, () => services.OtherGames(session.Resources));
        _looks = [.. Look.Available(session.Engine, session.Resources)];
        LookNames = [.. _looks.Select(l => l.Name)];
        ObjectShelf = ShelfViewModel.Objects(session.Resources, () => WadSmith.Editor.Editing.Prefabs.In(session.Project.Folder));
        session.PrefabsChanged += ObjectShelf.Refresh;
        if (showVersions) Panel = SidePanel.Versions;
        session.History.Moved += OnHistoryMoved;
        session.StateChanged += _ => UpdateSaveText();
        session.Selection.Changed += () => Learned("select");
        session.GeometryChanged += UpdateHeights;
        _profiles = new LatestOnWorker<(EditorSession Session, ElevationProfile Profile)>(MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || _measure is null) return;
            Profile = r.Profile;
            ProfileText = r.Profile.Describe();
        }, "the elevation profile");

        // An open profile follows the map as it changes.
        session.GeometryChanged += () =>
        {
            if (_measure is { } line) RequestProfile(line.From, line.To);
        };
        session.TeleportPadChanged += UpdateStatus;
        session.SwitchTargetChanged += UpdateStatus;
        session.KeyToPlaceChanged += UpdateStatus;
        Inspector.LookLike += LookLike;
        Inspector.KeyFor += colour =>
        {
            Tool = Canvas.EditorTool.Key;
            Session.KeyToPlace = colour;
        };
        Inspector.SwitchFor += target =>
        {
            Tool = Canvas.EditorTool.Switch;
            Session.SwitchTarget = target;
        };
        Inspector.TeleportFrom += pad =>
        {
            Tool = Canvas.EditorTool.Teleport;
            Session.TeleportPad = pad;
        };
        session.Notice += text =>
        {
            _notice = text;
            UpdateStatus();
        };
        UpdateHeights();
        UpdateSaveText();
        UpdateStatus();
    }

    public EditorSession Session { get; }

    public InspectorViewModel Inspector { get; }

    /// <summary>Opens Preferences over the window.</summary>
    public IRelayCommand OpenPreferencesCommand => _main.OpenPreferencesCommand;

    /// <summary>The kept copies of the map, newest first, while the Versions panel is open.</summary>
    public ObservableCollection<VersionItem> Versions { get; } = [];

    /// <summary>The live checks, listed in the Problems panel.</summary>
    public ProblemsViewModel Problems { get; }

    /// <summary>Whether the Problems panel shows under the map.</summary>
    [ObservableProperty]
    private bool _showProblems;

    /// <summary>The game's textures, to drag onto walls, floors and ceilings.</summary>
    public ShelfViewModel TextureShelf { get; }

    private readonly List<(string Name, Look Look)> _looks;

    /// <summary>The looks new rooms can be drawn with: the game's own, then the room it was last taken from.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _lookNames;

    /// <summary>Which of <see cref="LookNames"/> new rooms are drawn with.</summary>
    [ObservableProperty]
    private int _lookIndex;

    partial void OnLookIndexChanged(int value)
    {
        if (value >= 0 && value < _looks.Count && Session.Look != _looks[value].Look) Session.ChooseLook(_looks[value].Look);
    }

    /// <summary>Draws new rooms like a room on the map: its pictures, heights and light (offered as "Like the room picked").</summary>
    public void LookLike(Sector room)
    {
        var look = Session.LookOf(room);
        const string name = "Like the room picked";
        var at = _looks.FindIndex(l => l.Name == name);
        if (at < 0)
        {
            _looks.Add((name, look));
            at = _looks.Count - 1;
        }
        else
        {
            _looks[at] = (name, look);
        }

        LookNames = [.. _looks.Select(l => l.Name)];
        Session.ChooseLook(look);
        LookIndex = at;
        _notice = $"New rooms will look like this one: {look.Wall} walls, {look.Floor} floor, {look.Ceiling} ceiling.";
        UpdateStatus();
    }

    /// <summary>The game's monsters, items and decorations, to drag onto the map.</summary>
    public ShelfViewModel ObjectShelf { get; }

    /// <summary>What the right-hand panel shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector), nameof(ShowTextures), nameof(ShowObjects), nameof(ShowVersions))]
    private SidePanel _panel;

    public bool ShowInspector
    {
        get => Panel == SidePanel.Inspector;
        set => Show(SidePanel.Inspector, value);
    }

    public bool ShowTextures
    {
        get => Panel == SidePanel.Textures;
        set => Show(SidePanel.Textures, value);
    }

    public bool ShowObjects
    {
        get => Panel == SidePanel.Objects;
        set => Show(SidePanel.Objects, value);
    }

    public bool ShowVersions
    {
        get => Panel == SidePanel.Versions;
        set => Show(SidePanel.Versions, value);
    }

    private void Show(SidePanel panel, bool on)
    {
        if (on) Panel = panel;
        else OnPropertyChanged(panel switch { SidePanel.Textures => nameof(ShowTextures), SidePanel.Objects => nameof(ShowObjects), SidePanel.Versions => nameof(ShowVersions), _ => nameof(ShowInspector) });
    }

    [ObservableProperty]
    private string _versionsNote = "";

    /// <summary>Whether the 3D view shows beside the plan (D46).</summary>
    [ObservableProperty]
    private bool _show3D = true;

    /// <summary>How the 3D camera moves: orbit, fly or walk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOrbit), nameof(IsFly), nameof(IsWalk))]
    private ThreeD.CameraMode _cameraMode;

    public bool IsOrbit
    {
        get => CameraMode == ThreeD.CameraMode.Orbit;
        set
        {
            if (value) CameraMode = ThreeD.CameraMode.Orbit;
            else OnPropertyChanged();
        }
    }

    public bool IsFly
    {
        get => CameraMode == ThreeD.CameraMode.Fly;
        set
        {
            if (value) CameraMode = ThreeD.CameraMode.Fly;
            else OnPropertyChanged();
        }
    }

    public bool IsWalk
    {
        get => CameraMode == ThreeD.CameraMode.Walk;
        set
        {
            if (value) CameraMode = ThreeD.CameraMode.Walk;
            else OnPropertyChanged();
        }
    }

    /// <summary>What a left drag on the plan does (V selects, R draws rooms).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectTool), nameof(IsRoomTool), nameof(IsDoorTool), nameof(IsTeleportTool), nameof(IsStairsTool), nameof(IsWindowTool), nameof(IsLightTool), nameof(IsMeasureTool))]
    private Canvas.EditorTool _tool;

    public bool IsStairsTool
    {
        get => Tool == Canvas.EditorTool.Stairs;
        set
        {
            if (value) Tool = Canvas.EditorTool.Stairs;
            else OnPropertyChanged();
        }
    }

    public bool IsMeasureTool
    {
        get => Tool == Canvas.EditorTool.Measure;
        set
        {
            if (value) Tool = Canvas.EditorTool.Measure;
            else OnPropertyChanged();
        }
    }

    private readonly LatestOnWorker<(EditorSession Session, ElevationProfile Profile)> _profiles;
    private (MapPoint From, MapPoint To)? _measure;

    /// <summary>The floors and ceilings along the line measured last (null when no profile is open).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProfile))]
    private ElevationProfile? _profile;

    /// <summary>The profile in a sentence: how long, the floors, where the player is stopped.</summary>
    [ObservableProperty]
    private string _profileText = "";

    public bool ShowProfile => Profile is not null;

    /// <summary>Called by the canvas when a line has been measured: works out its profile on a worker.</summary>
    public void Measured(MapPoint from, MapPoint to)
    {
        _measure = (from, to);
        RequestProfile(from, to);
    }

    private void RequestProfile(MapPoint from, MapPoint to)
    {
        var session = Session;
        var geometry = session.Geometry;
        _profiles.Request(() => (session, ElevationProfile.Build(geometry, from, to)));
    }

    [RelayCommand]
    private void CloseProfile()
    {
        _measure = null;
        Profile = null;
    }

    public bool IsLightTool
    {
        get => Tool == Canvas.EditorTool.Light;
        set
        {
            if (value) Tool = Canvas.EditorTool.Light;
            else OnPropertyChanged();
        }
    }

    public bool IsWindowTool
    {
        get => Tool == Canvas.EditorTool.Window;
        set
        {
            if (value) Tool = Canvas.EditorTool.Window;
            else OnPropertyChanged();
        }
    }

    public bool IsTeleportTool
    {
        get => Tool == Canvas.EditorTool.Teleport;
        set
        {
            if (value) Tool = Canvas.EditorTool.Teleport;
            else OnPropertyChanged();
        }
    }

    public bool IsDoorTool
    {
        get => Tool == Canvas.EditorTool.Door;
        set
        {
            if (value) Tool = Canvas.EditorTool.Door;
            else OnPropertyChanged();
        }
    }

    public bool IsSelectTool
    {
        get => Tool == Canvas.EditorTool.Select;
        set
        {
            if (value) Tool = Canvas.EditorTool.Select;
            else OnPropertyChanged();
        }
    }

    public bool IsRoomTool
    {
        get => Tool == Canvas.EditorTool.Room;
        set
        {
            if (value) Tool = Canvas.EditorTool.Room;
            else OnPropertyChanged();
        }
    }

    partial void OnToolChanged(Canvas.EditorTool value)
    {
        _notice = "";
        _drawingText = "";
        _hoverText = "";
        UpdateStatus();
    }

    /// <summary>Called by the canvas while a room is drawn corner by corner (null when it ends).</summary>
    public void Corners((int Corners, double Side)? drawing)
    {
        _drawingText = drawing is { } d
            ? string.Create(CultureInfo.InvariantCulture, $"{d.Corners} corner{(d.Corners == 1 ? "" : "s")}{(d.Side > 0 ? $" · this side {d.Side:0}" : "")} · click the first corner to close, or double-click or press Enter to finish (a line from wall to wall across a room splits it); Shift keeps sides straight, Backspace takes a corner back, Esc lets go")
            : "";
        UpdateStatus();
    }

    /// <summary>Called by the canvas while something from a shelf is held over the plan (null when it leaves).</summary>
    public void Hint(string? text)
    {
        _drawingText = text ?? "";
        UpdateStatus();
    }

    /// <summary>Called by the canvas while the selection is being moved (null when the move ends).</summary>
    public void Moving((int Corners, int Things, double Dx, double Dy)? move)
    {
        if (move is not { } m)
        {
            _drawingText = "";
        }
        else
        {
            var what = (m.Corners, m.Things) switch
            {
                (0, 1) => "1 thing",
                (0, var t) => $"{t} things",
                (1, 0) => "1 corner",
                (var c, 0) => $"{c} corners",
                (var c, var t) => $"{c} corners and {t} thing{(t == 1 ? "" : "s")}",
            };
            var across = m.Dx == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(m.Dx):0.#} {(m.Dx > 0 ? "east" : "west")}");
            var up = m.Dy == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(m.Dy):0.#} {(m.Dy > 0 ? "north" : "south")}");
            var by = string.Join(", ", new[] { across, up }.Where(x => x.Length > 0));
            _drawingText = $"Moving {what}{(by.Length > 0 ? $" {by}" : "")} · Shift moves freely, Esc puts it back";
        }

        UpdateStatus();
    }

    /// <summary>Called by the canvas while a room is being drawn, with its size (null when the drawing ends).</summary>
    public void Drawing((double Width, double Height)? size, double step)
    {
        _drawingText = size is { } s
            ? string.Create(CultureInfo.InvariantCulture, $"Room {s.Width:0} × {s.Height:0} · let go to draw it, Esc to stop")
            : "";
        _gridStep = step;
        UpdateStatus();
    }

    private double _gridStep = 64;

    partial void OnPanelChanged(SidePanel value)
    {
        switch (value)
        {
            case SidePanel.Versions:
                _ = RefreshVersionsAsync();
                break;
            case SidePanel.Textures:
                TextureShelf.Open();
                break;
            case SidePanel.Objects:
                ObjectShelf.Open();
                break;
        }
    }

    private async Task RefreshVersionsAsync()
    {
        var project = Session.Project;
        var slot = Session.Slot;
        await Session.OpeningVersion.ConfigureAwait(true);
        var items = await Task.Run(() => MapVersions.List(project, slot).Select(v => new VersionItem(v, Picture(v.Picture), GoBackAsync)).ToList()).ConfigureAwait(true);
        Versions.Clear();
        foreach (var item in items) Versions.Add(item);
        VersionsNote = items.Count == 0
            ? "No versions yet. WadSmith keeps one each time you open the map and whenever you press Keep a version."
            : "WadSmith keeps one each time you open the map and whenever you press Keep a version. Going back keeps the map as it is now first, so nothing is lost.";
    }

    private static Bitmap? Picture(string? path)
    {
        if (path is null) return null;
        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Makes a kept copy the map again: the map as it is now is kept first, then the copy opens in its place.</summary>
    private async Task GoBackAsync(VersionItem item)
    {
        if (_goingBack) return;
        _goingBack = true;
        var project = Session.Project;
        var slot = Session.Slot;
        var hooks = Session.Hooks;
        _hoverText = $"Going back to the version from {item.WhenText}…";
        UpdateStatus();
        try
        {
            await Session.FlushAsync().ConfigureAwait(true);
            await Session.SaveVersion($"Before going back to the version from {EditorSession.Describe(item.Version.When)}").ConfigureAwait(true);
            Session.Dispose();
            var restored = await Task.Run(() => EditorSession.Restore(project, slot, item.Version, hooks)).ConfigureAwait(true);
            _main.OpenEditor(restored, $"Went back to the version from {item.WhenText}. The map as it was is kept as a version too.", showVersions: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException)
        {
            Log.Warn($"Going back to {item.Version.File} failed: {ex.Message}");
            await _main.OpenEditorAsync(project, slot).ConfigureAwait(true);
        }
        finally
        {
            _goingBack = false;
        }
    }

    public string ProjectName => Session.Project.Info.Name;

    public string MapName => Session.Slot;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _saveText = "";

    /// <summary>How the map is painted (D45); floors by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeights), nameof(IsFloors), nameof(IsCeilings), nameof(IsLines))]
    private Canvas.MapViewMode _viewMode;

    public bool ShowHeights
    {
        get => ViewMode == Canvas.MapViewMode.Heights;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Heights;
            else OnPropertyChanged();
        }
    }

    public bool IsFloors
    {
        get => ViewMode == Canvas.MapViewMode.Floors;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Floors;
            else OnPropertyChanged();
        }
    }

    public bool IsCeilings
    {
        get => ViewMode == Canvas.MapViewMode.Ceilings;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Ceilings;
            else OnPropertyChanged();
        }
    }

    public bool IsLines
    {
        get => ViewMode == Canvas.MapViewMode.Lines;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Lines;
            else OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string _undoTip = "Nothing to undo";

    [ObservableProperty]
    private string _heightsLow = "";

    [ObservableProperty]
    private string _heightsHigh = "";

    [ObservableProperty]
    private string _heightsNote = "";

    [ObservableProperty]
    private string _redoTip = "Nothing to redo";

    partial void OnViewModeChanged(Canvas.MapViewMode value)
    {
        if (value != Canvas.MapViewMode.Floors) Learned("heights");
    }

    private void OnHistoryMoved()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        UndoTip = Session.History.UndoLabel is { } u ? $"Undo: {u} (Ctrl+Z)" : "Nothing to undo";
        RedoTip = Session.History.RedoLabel is { } r ? $"Redo: {r} (Ctrl+Y)" : "Nothing to redo";
        _notice = "";
        Learned("edit");
        if (Session.History.UndoLabel == "Draw a room") Learned("draw");
        if (Session.History.UndoLabel == "Make a door") Learned("door");
        if (Session.History.UndoLabel == "Make a teleporter") Learned("teleport");
        if (Session.History.UndoLabel == "Make stairs") Learned("stairs");
    }

    /// <summary>The heights legend's words, from the shapes the worker built (no scan of the map here).</summary>
    private void UpdateHeights()
    {
        const double PlayerHeight = 56;
        if (Session.Geometry.FloorRange is not { } r)
        {
            (HeightsLow, HeightsHigh, HeightsNote) = ("", "", "No areas yet.");
            return;
        }

        HeightsLow = string.Create(CultureInfo.InvariantCulture, $"Lowest {r.Min:0}");
        HeightsHigh = string.Create(CultureInfo.InvariantCulture, $"Highest {r.Max:0}");
        var span = r.Max - r.Min;
        HeightsNote = span == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Every floor is at {r.Min:0}.")
            : string.Create(CultureInfo.InvariantCulture, $"{span:0} units apart, about {span / PlayerHeight:0.#} times the player's height.");
    }

    private void UpdateSaveText()
    {
        SaveText = Session.State switch
        {
            SaveState.Saved => "All changes kept",
            SaveState.Pending or SaveState.Saving => "Keeping your changes…",
            _ => Session.LastError ?? "Your changes could not be kept",
        };
    }

    private bool CanUndo() => Session.History.CanUndo;

    private bool CanRedo() => Session.History.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        Session.History.Undo();
        Session.Selection.Prune();
        Learned("undo");
    }

    /// <summary>Where the pointer last was on the plan (pastes go there).</summary>
    private MapPoint? _pointer;

    [RelayCommand]
    private void Copy() => Session.CopySelection();

    [RelayCommand]
    private void Cut() => Session.Cut();

    /// <summary>Pastes where the pointer is on the plan, or beside the original when the pointer has not been over it.</summary>
    [RelayCommand]
    private void Paste()
    {
        if (_pointer is { } at) Session.Paste(at);
        else if (Editor.Editing.Copying.Clipboard is { } clip) Session.Paste(new MapPoint(clip.Origin.X + Math.Ceiling(clip.Size.Width / 64) * 64 + 64, clip.Origin.Y));
        else Session.Paste(default);
    }

    [RelayCommand]
    private void Duplicate() => Session.Duplicate();

    /// <summary>Deletes the selection so the map stays whole (Delete).</summary>
    [RelayCommand]
    private void Delete() => Session.DeleteSelection();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        Session.History.Redo();
        Session.Selection.Prune();
    }

    [RelayCommand]
    private async Task KeepVersionAsync()
    {
        var kept = await Session.SaveVersion("Kept by hand").ConfigureAwait(true);
        _hoverText = kept is null ? "The newest version already holds the map as it is now." : "A copy of the map as it is now was kept in the project's versions.";
        UpdateStatus();
        if (ShowVersions) await RefreshVersionsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CloseAsync() => await _main.CloseEditorAsync().ConfigureAwait(true);

    /// <summary>Called by the canvas when the pointer moves over something new.</summary>
    /// <summary>Whether the plan shows the player's size at the pointer and marks where the player cannot pass (G).</summary>
    public bool ShowFit
    {
        get => _services.Settings.Current.ShowPlayerFit;
        set
        {
            if (value == ShowFit) return;
            _services.Settings.Current.ShowPlayerFit = value;
            _services.Settings.Save();
            OnPropertyChanged();
            if (value) return;
            _ghostText = "";
            UpdateStatus();
        }
    }

    private string _ghostText = "";

    /// <summary>Called by the canvas as the player's box follows the pointer: whether the player fits there, and why.</summary>
    public void Ghost(WadSmith.Editor.Checks.GhostFit? fit)
    {
        var text = fit?.Why ?? "";
        if (text == _ghostText) return;
        _ghostText = text;
        UpdateStatus();
    }

    public void Hover(Element? element, MapPoint at)
    {
        if (at != default) _pointer = at;
        _hoverText = element switch
        {
            Line l when Tool == Canvas.EditorTool.Door => l.Back is null
                ? "Wall · solid · a door goes in a wall between two rooms"
                : string.Create(CultureInfo.InvariantCulture, $"Wall · between two areas · {l.Length:0} long · click to make it a door"),
            Line l when Tool == Canvas.EditorTool.Window => l.Back is null
                ? string.Create(CultureInfo.InvariantCulture, $"Wall · solid · {l.Length:0} long · drag along it for a window through to the room behind")
                : string.Create(CultureInfo.InvariantCulture, $"Wall · between two areas · {l.Length:0} long · drag along it for a window in it"),
            Line l when Tool == Canvas.EditorTool.Switch && Session.SwitchTarget is not null => l.Back is null
                ? string.Create(CultureInfo.InvariantCulture, $"Wall · solid · {l.Length:0} long · click to put the switch here")
                : "Wall · between two areas · a switch goes on a solid wall",
            Sector s => string.Create(CultureInfo.InvariantCulture, $"Area · floor {s.FloorHeight:0}, ceiling {s.CeilingHeight:0}, light {s.Light} · {s.FloorTexture}"),
            Line l => string.Create(CultureInfo.InvariantCulture, $"Wall · {(l.Back is null ? "solid" : "between two areas")} · {l.Length:0} long{(l.Special != 0 ? " · has an action" : "")} · double-click to add a corner"),
            Thing t => string.Create(CultureInfo.InvariantCulture, $"{ThingName(t.Type)} · facing {t.Angle}°"),
            Vertex v => string.Create(CultureInfo.InvariantCulture, $"Corner at {v.X:0}, {v.Y:0}"),
            _ => "",
        };
        if (element is not null) Learned("zoom");
        UpdateStatus();
    }

    /// <summary>The object's name from the game's own definitions, or its number when the game does not define it.</summary>
    public string ThingName(int type) => Session.Resources?.Things.Find(type)?.Name ?? string.Create(CultureInfo.InvariantCulture, $"Object type {type}");

    /// <summary>Called by the canvas when the view zooms or pans.</summary>
    public void Navigated() => Learned("zoom");

    private void Learned(string id)
    {
        var seen = _services.Settings.Current.CoachSeen;
        if (!seen.Contains(id))
        {
            seen.Add(id);
            _services.Settings.Save();
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_drawingText.Length > 0 || _notice.Length > 0)
        {
            Status = _drawingText.Length > 0 ? _drawingText : _notice;
            return;
        }

        if (_hoverText.Length > 0)
        {
            Status = _ghostText.Length > 0 ? $"{_hoverText} · {_ghostText}" : _hoverText;
            return;
        }

        if (Tool == Canvas.EditorTool.Stairs)
        {
            Status = "Drag from the bottom of the stairs to the top: the steps climb from the floor where you start to the floor where you end, 16 a step where nothing else decides. Esc goes back to Select.";
            return;
        }

        if (Tool == Canvas.EditorTool.Measure)
        {
            Status = "Drag a line across the map: its length, and below the map the floors and ceilings along it, with what stops the player. Esc goes back to Select.";
            return;
        }

        if (Tool == Canvas.EditorTool.Light)
        {
            Status = "Click in a room for a light: warm, 128 across, 48 above the floor; its colour and reach are in the Inspector. Esc goes back to Select.";
            return;
        }

        if (Tool == Canvas.EditorTool.Key)
        {
            Status = Session.KeyToPlace is { } colour ? $"Click where the {colour} key goes, somewhere the player reaches before the door. Esc goes back to Select." : "";
            return;
        }

        if (Tool == Canvas.EditorTool.Switch)
        {
            Status = Session.SwitchTarget is null
                ? "Click the door or lift the switch works. Esc goes back to Select."
                : "Now click the solid wall where the switch goes: a piece of it 64 wide becomes the switch. Esc goes back to Select.";
            return;
        }

        if (Tool == Canvas.EditorTool.Teleport)
        {
            Status = Session.TeleportPad is null
                ? "Click the room to step on: it becomes the teleport pad. Esc goes back to Select."
                : "Now click where the player arrives. Stepping onto the pad sends them there. Esc goes back to Select.";
            return;
        }

        if (Tool == Canvas.EditorTool.Door)
        {
            Status = "Click the wall between two rooms where the door goes: it fills the wall (up to 256 wide), flush on the corridor's side, and opens when used. Esc goes back to Select.";
            return;
        }

        if (Tool == Canvas.EditorTool.Room)
        {
            Status = string.Create(CultureInfo.InvariantCulture, $"Drag to draw a rectangle room, or click corner by corner for any shape. Corners snap to the grid ({_gridStep:0} units) and to corners nearby; drawn against a room, the two share a wall. Esc goes back to Select.");
            return;
        }

        var settings = _services.Settings.Current;
        Status = settings.CoachTips ? Tips.FirstOrDefault(t => !settings.CoachSeen.Contains(t.Id)).Text ?? "" : "";
    }
}
