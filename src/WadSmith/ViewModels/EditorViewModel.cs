using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core.Games;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.App.ViewModels;

/// <summary>What the editor's right-hand panel shows.</summary>
internal enum SidePanel
{
    Inspector,
    Textures,
    Objects,
    Versions,
    Find,
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
        ("draw", "Tip: choose {label:Room} at the top left of the map{or:Room tool} and drag to draw a room. Drawn against another room, the two share a wall."),
        ("door", "Tip: choose Door{or:Door tool} and click the wall between two rooms to put a door there. Draw a short corridor between rooms first for a classic doorway."),
        ("undo", "Tip: Ctrl+Z takes back the last change and Ctrl+Y brings it back. Nothing needs saving; WadSmith keeps every change."),
        ("heights", "Tip: the view buttons at the top show floors, ceilings, heights as colours, or just the walls."),
        ("orbit", "Tip: in the 3D view, drag to turn around the map, right-drag to move it, and scroll to come closer."),
        ("walk", "Tip: choose Walk at the top of the 3D view, click it, and use {move} to walk through the map as the player would."),
        ("tab", "Tip: press {key:Show the 3D view alone} to fill the window with the 3D view, and {key:Show the 3D view alone} again to come back."),
        ("palette", "Tip: press Ctrl+K and type a few letters to find anything WadSmith can do, with its shortcut."),
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
        Inspector = new InspectorViewModel(session, services.Words);
        // A remix's narrow gaps and low openings are its author's: the passage checks speak only of the user's own.
        if (session.Origin is { Kind: "remix", Game: { } origin, Map: { } original } && services.EngineData is { } engineData
            && (session.Resources?.Game.Id == origin ? session.Resources.Files[^1] : services.Games?.FirstOrDefault(g => g.Game.Id == origin)?.Path) is { } file)
        {
            session.UseOriginal(() => WadSmith.Core.Projects.Remix.Original(file, original, engineData, []).Map);
        }

        Problems = new ProblemsViewModel(session);
        TextureShelf = ShelfViewModel.Textures(session.Resources, () => services.OtherGameOpeners(session.Resources), session.Project.CopiedNow);
        TextureShelf.AddLabel = "Add a picture or material…";
        TextureShelf.AddTip = "Brings in a picture of your own as a texture for walls, doors and floors: a still picture, an animated GIF, WebP or PNG (GZDoom plays it at its own pace), a video's first ten seconds (MP4, WebM, MKV or MOV, through your ffmpeg), or any picture of a PBR material set (a colour picture with its normal, roughness, metal or occlusion maps, as ambientCG and Poly Haven give them; GZDoom lights it by its maps).";
        TextureShelf.Add = () => _ = AddMaterialAsync();
        session.TexturesChanged += TextureShelf.Refresh;
        session.MapsAdded += () =>
        {
            _projectMaps = null;
            OnPropertyChanged(nameof(ProjectMaps));
            OnPropertyChanged(nameof(MapChooserLabel));
        };
        _looks = [.. Look.Available(session.Engine, session.Resources)];
        LookNames = [.. _looks.Select(l => l.Name)];
        ObjectShelf = ShelfViewModel.Objects(session.Resources, () => WadSmith.Editor.Editing.Prefabs.In(session.Project.Folder), () => services.OtherGameOpeners(session.Resources), ModelNumbers, DecorationNumbers);
        ObjectShelf.AddLabel = "Add a 3D model or picture…";
        ObjectShelf.AddTip = "Brings in a 3D model of your own (OBJ with its .mtl and pictures, glTF/GLB, MD3, IQM, Quake's MD2 and MDL, or MagicaVoxel and KVX voxels, or a block from a block texture set) as a prop for this map: it is copied into the project, GZDoom draws it, and it waits under Your models. You can also drop a model file onto the plan. A picture (PNG, JPEG, or an animated GIF, WebP or PNG) becomes a decoration that shows it from every side, waiting under Your pictures.";
        ObjectShelf.Add = () => _ = AddModelAsync();
        session.PrefabsChanged += ObjectShelf.Refresh;

        // The free sources (D61) after the games on both choosers; the packs fetched before are found on a worker.
        TextureShelf.Offer(LibrarySources.ForTextures(services, session.Resources, () => main.Welcome.RescanAsync()));
        ObjectShelf.Offer(LibrarySources.ForObjects(services, session.Resources, () => main.Welcome.RescanAsync()));
        session.PropsChanged += ObjectShelf.Refresh;
        if (showVersions) Panel = SidePanel.Versions;
        session.History.Moved += OnHistoryMoved;
        session.StateChanged += _ => UpdateSaveText();
        session.Selection.Changed += () => Learned("select");
        session.GeometryChanged += UpdateHeights;
        session.GeometryChanged += () => Guide?.MapChanged();
        session.GeometryChanged += () =>
        {
            if (WhatIsThis) Explain();
            if (Report is not null) ExplainMapCommand.Execute(null);
        };
        _mapSeen = (session.MapEntry.Title, session.MapEntry.Music);
        session.MapSettingsChanged += MapSettingsSeen;
        session.GeometryChanged += CameraMoved;
        _reports = new(session.OnUiThread, ReportArrived, "the map's explanation");
        Inspector.ExplainMap += () => ExplainMapCommand.Execute(null);
        Inspector.ExportMap += () => OpenExportCommand.Execute(null);
        FindPorts();
        _shelfSounds = new LatestOnWorker<byte[]?>(session.OnUiThread, wav =>
        {
            if (wav is not null) _services.Sound.Play(wav);
        }, "the shelf's sound");
        _profiles = new LatestOnWorker<(EditorSession Session, ElevationProfile Profile)>(session.OnUiThread, r =>
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
        ObjectShelf.Tiles.CollectionChanged += (_, _) => MarkUnusable();
        session.TargetChanged += () =>
        {
            MarkUnusable();
            if (!LightAllowed && Tool == Canvas.EditorTool.Light) Tool = Canvas.EditorTool.Select;
            OnPropertyChanged(nameof(LightAllowed));
            OnPropertyChanged(nameof(LightTip));
        };
        session.KeyToPlaceChanged += UpdateStatus;
        Inspector.LookLike += LookLike;
        Inspector.GridStep = () => GridSize > 0 ? GridSize : GridNow?.Invoke() ?? 64;
        Inspector.KeyFor += colour =>
        {
            Tool = Canvas.EditorTool.Key;
            Session.KeyToPlace = colour;
        };
        Inspector.EditPicture += texture =>
        {
            if (Session.WatchPicture(texture) is { } path) _ = OpenInOtherProgram?.Invoke(path);
        };
        Inspector.CameraFor += face =>
        {
            Tool = Canvas.EditorTool.Camera;
            Session.ScreenToFill = face;
            UpdateStatus();
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
            Guide?.Tell(text);
        };
        session.History.Refused += _ => Hint(EditorSession.ReadOnlyNotice);
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
    [NotifyPropertyChangedFor(nameof(ShowInspector), nameof(ShowTextures), nameof(ShowObjects), nameof(ShowVersions), nameof(ShowFind))]
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

    public bool ShowFind
    {
        get => Panel == SidePanel.Find;
        set => Show(SidePanel.Find, value);
    }

    /// <summary>Find and replace (Ctrl+F): the panel's search, made when it is first shown.</summary>
    public FindViewModel Find => _find ??= new FindViewModel(Session, () => FitSelection?.Invoke());

    private FindViewModel? _find;

    private void Show(SidePanel panel, bool on)
    {
        if (on) Panel = panel;
        else OnPropertyChanged(panel switch { SidePanel.Textures => nameof(ShowTextures), SidePanel.Objects => nameof(ShowObjects), SidePanel.Versions => nameof(ShowVersions), SidePanel.Find => nameof(ShowFind), _ => nameof(ShowInspector) });
    }

    [ObservableProperty]
    private string _versionsNote = "";

    /// <summary>Which views share the map area (D46), remembered between runs.</summary>
    public MapLayout Layout
    {
        get => _services.Settings.Current.Layout;
        set
        {
            if (value == Layout) return;
            if (value == MapLayout.ThreeD)
            {
                _before3D = Layout;
                Guide?.Happened(GuideEvents.ThreeD);
            }

            _services.Settings.Current.Layout = value;
            _services.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlanOnly));
            OnPropertyChanged(nameof(IsSplit));
            OnPropertyChanged(nameof(Is3DOnly));
        }
    }

    // The layout Tab goes back to from the 3D view alone.
    private MapLayout? _before3D;

    public bool IsPlanOnly
    {
        get => Layout == MapLayout.Plan;
        set
        {
            if (value) Layout = MapLayout.Plan;
            else OnPropertyChanged();
        }
    }

    public bool IsSplit
    {
        get => Layout == MapLayout.Split;
        set
        {
            if (value) Layout = MapLayout.Split;
            else OnPropertyChanged();
        }
    }

    public bool Is3DOnly
    {
        get => Layout == MapLayout.ThreeD;
        set
        {
            if (value) Layout = MapLayout.ThreeD;
            else OnPropertyChanged();
        }
    }

    /// <summary>Tab: the 3D view fills the map area, and Tab again brings back the layout before it (Doom Builder's Tab).</summary>
    [RelayCommand]
    private void Toggle3D()
    {
        Layout = Layout == MapLayout.ThreeD ? _before3D ?? MapLayout.Split : MapLayout.ThreeD;
        Learned("tab");
    }

    /// <summary>The command palette while it is open (Ctrl+K); null when closed.</summary>
    [ObservableProperty]
    private CommandPaletteViewModel? _palette;

    // What the palette ran last, newest first, for this run of the app.
    private readonly List<string> _recentCommands = [];

    /// <summary>Shows the whole map on the plan; set by the view.</summary>
    public Action? FitMap { get; set; }

    /// <summary>Brings the selection into view on the plan; set by the view.</summary>
    public Action? FitSelection { get; set; }

    /// <summary>The grid the plan snaps to now (it follows the zoom unless <see cref="GridSize"/> is set); set by the view.</summary>
    public Func<double>? GridNow { get; set; }

    /// <summary>The ways "Select alike" matches: the palette's command, the Inspector's shorter label, and the words it is found by.</summary>
    internal static readonly (Alike By, string Command, string Label, string Words)[] AlikeChoices =
    [
        (Alike.FloorPicture, "Select rooms with the same floor picture", "Same floor picture", "select similar|same flat|same floor texture|select same floor"),
        (Alike.CeilingPicture, "Select rooms with the same ceiling picture", "Same ceiling picture", "select similar|same flat|same ceiling texture|select same ceil"),
        (Alike.FloorHeight, "Select rooms with the floor at the same height", "Same floor height", "select similar|same height|floor height"),
        (Alike.CeilingHeight, "Select rooms with the ceiling at the same height", "Same ceiling height", "select similar|same height|ceiling height"),
        (Alike.Brightness, "Select rooms with the same brightness", "Same brightness", "select similar|same light|light level"),
        (Alike.JoinedFloorHeight, "Select the joined rooms with the floor at the same height", "Joined, same floor height", "select same h|select group|neighbors|neighbours|contiguous|flood"),
        (Alike.WallPicture, "Select walls with the same picture", "Same picture", "select similar|same texture|walls with texture"),
        (Alike.WallsAlong, "Select the walls it runs on into", "The walls it runs on into", "select path|chain|connected walls|outline"),
        (Alike.ObjectKind, "Select objects of the same kind", "Same kind", "select similar|same type|same thing|all of these"),
    ];

    /// <summary>What a selection from the whole map picks: what the Select tool picks, or anything with another tool.</summary>
    private PickOnly PicksNow => Tool == Canvas.EditorTool.Select ? Picks : PickOnly.Anything;

    /// <summary>The grid the plan snaps to, in map units; 0 follows the zoom (64, halved as you come closer).</summary>
    [ObservableProperty]
    private double _gridSize;

    // The grid steps [ and ] go through (Eureka's and Doom Builder's).
    private static readonly double[] GridSteps = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024];

    /// <summary>A finer (−1) or coarser (+1) grid than the one the plan snaps to now, and says which.</summary>
    private void StepGrid(int way)
    {
        var now = GridSize > 0 ? GridSize : GridNow?.Invoke() ?? 64;
        var at = Array.FindIndex(GridSteps, s => s >= now);
        if (at < 0) at = GridSteps.Length - 1;
        if (GridSteps[at] > now && way > 0) at--;
        SetGrid(GridSteps[Math.Clamp(at + way, 0, GridSteps.Length - 1)]);
    }

    private void SetGrid(double size)
    {
        GridSize = size;
        Session.Tell(size > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Grid: {size:0} units. {KeyFor("Finer grid")} and {KeyFor("Coarser grid")} change it; “Grid follows the zoom” in Ctrl+K hands it back to the zoom.")
            : "The grid follows the zoom again: 64 units, finer as you come closer.");
    }

    /// <summary>Asks the user for a 3D model file (set by the view); null when they chose none.</summary>
    public Func<Task<string?>>? PickModel { get; set; }

    /// <summary>Set by the view: where to save an export, given a suggested file name; null when the user cancels.</summary>
    public Func<string, Task<string?>>? PickExportFile { get; set; }

    /// <summary>Asks the user for a picture of a material set (set by the view); null when they chose none.</summary>
    public Func<Task<string?>>? PickMaterial { get; set; }

    /// <summary>"Add a material…": the file picker, then the set brought in as a texture on a worker.</summary>
    private async Task AddMaterialAsync()
    {
        if (PickMaterial is null || await PickMaterial().ConfigureAwait(true) is not { } path) return;
        Session.ImportPicture(path);
    }

    /// <summary>The numbers of the props this project brought in from 3D models (read on the shelf's worker).</summary>
    private IReadOnlyCollection<int> ModelNumbers() =>
        [.. Session.Project.CopiedNow().Where(c => c.Kind == "actor" && c.Game == "model").Select(c => int.Parse(c.Name, System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>The numbers of the decorations this project made from pictures (read on the shelf's worker).</summary>
    private IReadOnlyCollection<int> DecorationNumbers() =>
        [.. Session.Project.CopiedNow().Where(c => c.Kind == "actor" && c.Game == "picture").Select(c => int.Parse(c.Name, System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>"Add a 3D model or picture…": the file picker, then the model brought in as a prop (a picture as a decoration) on a worker.</summary>
    private async Task AddModelAsync()
    {
        if (PickModel is null || await PickModel().ConfigureAwait(true) is not { } path) return;
        Session.ImportModel(path);
    }

    /// <summary>Opens the command palette, or closes it when it is open (Ctrl+K).</summary>
    [RelayCommand]
    private void OpenPalette()
    {
        if (Palette is not null)
        {
            Palette = null;
            return;
        }

        Palette = new CommandPaletteViewModel(Commands(), _recentCommands, () => Palette = null, () => Guide?.Happened(GuideEvents.Palette));
        Learned("palette");
    }

    /// <summary>
    /// Everything the editor can do from the keyboard or the top bar, by name, with its shortcut in the chosen key preset
    /// (WadSmith's, or Ultimate Doom Builder's defaults, D14) and the words people look for it by (classic names included).
    /// This is the keymap: the palette lists it, the editor's keys run it, and a test presses every shortcut it shows.
    /// </summary>
    internal IReadOnlyList<EditorCommand> Commands() => _commands ??= BuildCommands();

    private IReadOnlyList<EditorCommand>? _commands;

    /// <summary>Whether the keys are Ultimate Doom Builder's (Preferences, "Doom Builder keys").</summary>
    public bool ClassicKeys => _services.Settings.Current.DoomBuilderKeys;

    private IReadOnlyList<EditorCommand> BuildCommands()
    {
        bool Selected() => !Session.Selection.IsEmpty;
        // A shortcut in each preset: ours, then Doom Builder's ("" for none). Doom Builder's come from its own
        // defaults (UDBuilder.default.cfg): W visual mode, V/L/S/T the element modes, Ctrl+D draw lines, Shift+D make
        // door, C clear selection, Ctrl+F9 test from here, F4 map analysis, F5 preferences; a key it gives another
        // meaning (G gradient brightness, M make sector) leaves our command without one there.
        string K(string ours, string builder) => ClassicKeys ? builder : ours;
        void Pick(PickOnly only)
        {
            Tool = Canvas.EditorTool.Select;
            Picks = only;
        }

        var skills = SkillNames;
        var list = new List<EditorCommand>
        {
            new("Select tool", "Tools", K("V", ""), "pointer|pick|choose|arrow", () => Pick(PickOnly.Anything)),
            new("Select corners only", "Tools", K("", "V"), "vertices mode|vertex|points", () => Pick(PickOnly.Corners)),
            new("Select walls only", "Tools", K("", "L"), "linedefs mode|linedef|lines", () => Pick(PickOnly.Walls)),
            new("Select rooms only", "Tools", K("", "S"), "sectors mode|sector|areas", () => Pick(PickOnly.Rooms)),
            new("Select objects only", "Tools", K("", "T"), "things mode|thing|monsters|items", () => Pick(PickOnly.Objects)),
            new("Room tool", "Tools", K("R", "Ctrl+D"), "draw a room|draw lines|sector|rectangle|shape|area", () => Tool = Canvas.EditorTool.Room),
            new("Door tool", "Tools", K("D", "Shift+D"), "make a door|make door|doorway|opening", () => Tool = Canvas.EditorTool.Door),
            new("Window tool", "Tools", K("W", ""), "make a window|hole in a wall", () => Tool = Canvas.EditorTool.Window),
            new("Teleport tool", "Tools", K("T", ""), "teleporter|warp|pad", () => Tool = Canvas.EditorTool.Teleport),
            new("Light tool", "Tools", K("L", ""), "lamp|brightness|light level|glow|dynamic light", () => Tool = Canvas.EditorTool.Light, () => LightAllowed),
            new("Stairs tool", "Tools", K("S", ""), "steps|staircase|stair builder", () => Tool = Canvas.EditorTool.Stairs),
            new("Measure tool", "Tools", K("M", ""), "ruler|distance|length|tape", () => Tool = Canvas.EditorTool.Measure),
            new("No drawing mirror", "Tools", "", "symmetry off", () => MirrorChoice = 0),
            new("Mirror drawing left–right", "Tools", "", "symmetry|symmetric|deathmatch", () => MirrorChoice = 1),
            new("Mirror drawing top–bottom", "Tools", "", "symmetry|symmetric", () => MirrorChoice = 2),
            new("Mirror drawing four ways", "Tools", "", "symmetry|symmetric", () => MirrorChoice = 3),
            new("Repeat drawing in a half turn", "Tools", "", "symmetry|rotational", () => MirrorChoice = 4),
            new("Repeat drawing in quarter turns", "Tools", "", "symmetry|rotational", () => MirrorChoice = 5),

            new("Undo", "Edit", "Ctrl+Z", "take back|revert", () => UndoCommand.Execute(null), () => UndoCommand.CanExecute(null)),
            new("Redo", "Edit", "Ctrl+Y", "bring back", () => RedoCommand.Execute(null), () => RedoCommand.CanExecute(null), Also: "Ctrl+Shift+Z"),
            new("Copy", "Edit", "Ctrl+C", "", () => CopyCommand.Execute(null), Selected),
            new("Cut", "Edit", "Ctrl+X", "", () => CutCommand.Execute(null), Selected),
            new("Paste", "Edit", "Ctrl+V", "picture|image|clipboard", () => PasteCommand.Execute(null), () => Editor.Editing.Copying.Clipboard is not null || PictureTarget() is not null),
            new("Duplicate", "Edit", K("Ctrl+D", ""), "copy in place|clone", () => DuplicateCommand.Execute(null), Selected),
            new("Delete", "Edit", "Delete", "remove|erase|delete item", () => DeleteCommand.Execute(null), Selected),
            new("Join the selected rooms", "Edit", "J", "merge|join sectors|merge sectors|combine", Session.JoinRooms, Selected),
            new("Turn the selection left", "Edit", "Ctrl+[", "rotate|anticlockwise|counter-clockwise", () => Session.TurnSelection(90), Selected),
            new("Turn the selection right", "Edit", "Ctrl+]", "rotate|clockwise", () => Session.TurnSelection(-90), Selected),
            new("Mirror the selection left–right", "Edit", "Shift+H", "flip horizontally", () => Session.FlipSelection(leftToRight: true), Selected),
            new("Mirror the selection top–bottom", "Edit", "Shift+V", "flip vertically", () => Session.FlipSelection(leftToRight: false), Selected),
            new("Select nothing", "Edit", K("Esc", "C"), "deselect|clear selection", () => Session.Selection.Clear(), Selected),
            new("Select everything", "Edit", "Ctrl+A", "select all|all of it|whole map", () => Session.SelectAll(PicksNow)),
            new("Invert the selection", "Edit", "Ctrl+I", "invert selection|select the rest|select the others|inverse", () => Session.InvertSelection(PicksNow)),
            new("Bring back the last selection", "Edit", "Ctrl+L", "previous selection|last selection|reselect|lost selection", () => Session.Selection.BringBack(), () => Session.Selection.Previous.Count > 0),
            new("Copy properties", "Edit", "Ctrl+Shift+C", "copy properties|copy settings|match|eyedropper", Session.CopyProperties, () => Session.Selection.Items.Any(e => e is not Vertex)),
            new("Paste properties", "Edit", "Ctrl+Shift+V", "paste properties|apply settings|match", Session.PasteProperties, () => Properties.Held is not null && Selected()),
            new("Snap the selection to the grid", "Edit", "", "quantize|snap to grid|align to grid|tidy", () => Session.SnapSelection(GridSize > 0 ? GridSize : GridNow?.Invoke() ?? 64), Selected),
            new("Line the corners up evenly", "Edit", "", "shape line|straighten|evenly|distribute", () => Session.ShapeCorners(circle: false), Selected),
            new("Space the corners round a circle", "Edit", "", "shape circle|round|arc|make round|circular", () => Session.ShapeCorners(circle: true), Selected),
            new("Swap floor and ceiling pictures", "Edit", "", "swap flats|flip floor and ceiling|swap textures", Session.SwapFlats, () => Session.Selection.OfType<Sector>().Any()),
            new("Split the wall in half", "Edit", "", "split linedef|split line|halve|add a corner|insert vertex", Session.SplitWalls, () => Session.Selection.Items.Any(e => e is Line or Side)),

            new("Show the plan alone", "View", "", "2d|top down|map only", () => Layout = MapLayout.Plan),
            new("Show the plan and the 3D view", "View", "", "split|side by side|both", () => Layout = MapLayout.Split),
            new("Show the 3D view alone", "View", K("Tab", "W"), "3d|visual mode|full screen|and back", () => Toggle3DCommand.Execute(null)),
            new("Show floors on the plan", "View", "", "floor textures|flats", () => IsFloors = true),
            new("Show ceilings on the plan", "View", "", "ceiling textures|flats", () => IsCeilings = true),
            new("Show heights as colours", "View", "", "floor heights|elevation", () => ShowHeights = !ShowHeights),
            new("Show only the walls", "View", "", "lines|automap|wireframe|linedefs", () => IsLines = true),
            new("Show sound zones", "View", "", "sound propagation|where shots are heard|monsters hear|sound blocking|sound view", () => IsSound = true),
            new("Show brightness as greys", "View", "", "light levels|lighting|sector lights|dark rooms|brightness view", () => IsBrightness = true),
            new("Show the player's size", "View", K("G", ""), "fit|player fit|ghost|clearance", () => ShowFit = !ShowFit),
            new("Show the whole map", "View", "Home", "zoom to fit|fit map|overview|center in screen", () => FitMap?.Invoke()),
            new("Put the 3D camera here", "View", "'", "place camera|camera here|stand here|look from here|visual mode here", PutCameraAtPointer),
            new("Show the selection", "View", "Shift+Home", "zoom to selection|frame selected|go to selection|center on selection", () => FitSelection?.Invoke(), Selected),
            new("Finer grid", "View", "[", "grid smaller|smaller grid|decrease grid|grid size", () => StepGrid(-1)),
            new("Coarser grid", "View", "]", "grid larger|larger grid|increase grid|grid size", () => StepGrid(1)),
            new("Grid follows the zoom", "View", "", "automatic grid|auto grid|grid size", () => SetGrid(0), () => GridSize > 0),
            new("Orbit in 3D", "View", "", "turn around|model view|camera", () => CameraMode = ThreeD.CameraMode.Orbit),
            new("Fly in 3D", "View", "", "free camera|noclip|camera", () => CameraMode = ThreeD.CameraMode.Fly),
            new("Walk in 3D", "View", "", "player view|first person|camera|gravity", () => CameraMode = ThreeD.CameraMode.Walk),
            new("The game's view", "View", "G in 3D", "gzdoom picture|engine view|screenshot", () => EngineView = !EngineView),
            new("Command palette", "View", "Ctrl+K", "find a command|search|actions", () => OpenPaletteCommand.Execute(null), Also: "Ctrl+Shift+P"),
            new("What is this?", "View", "Shift+F1", "explain|identify|what's this|describe|help", () => WhatIsThis = !WhatIsThis),

            new("Inspector", "Panels", "", "properties|details", () => ShowInspector = true),
            new("Textures shelf", "Panels", "", "textures|flats|wall pictures", () => ShowTextures = true),
            new("Objects shelf", "Panels", "", "things|monsters|items|prefabs", () => ShowObjects = true),
            new("Versions", "Panels", "", "history|snapshots|backups", () => ShowVersions = true),
            new("Find and replace", "Panels", "Ctrl+F", "find|search|replace|search and replace|where is|texture search", () => ShowFind = true),
            new("Explain this map", "Panels", "", "about|summary|statistics|stats|key flow|monster count|what is in it|map analysis", () => ExplainMapCommand.Execute(null)),
            new("Problems", "Panels", K("", "F4"), "errors|map analysis|check|error checker", () => ShowProblems = !ShowProblems),
            new("Keep a version", "Panels", "", "snapshot|save a copy|backup", () => KeepVersionCommand.Execute(null)),
            new("Next map", "Map", "", "flip map|next level|following map|episode", () => _ = OtherMapAsync(1), () => Session.Project.Info.Maps.Count > 1),
            new("Previous map", "Map", "", "flip map|previous level|last map|episode", () => _ = OtherMapAsync(-1), () => Session.Project.Info.Maps.Count > 1),
            new("Make another like it", "Map", "", "make me a map|generate|random map|procedural|new map like this|reroll", () => _ = MakeAnotherAsync(), () => Session.Origin?.Kind == "generated"),
            new("Export…", "Map", "Ctrl+E", "save as wad|pk3|share|vanilla|boom|mbf21|chocolate|crispy|dsda|woof|classic port|compile|build wad", () => OpenExportCommand.Execute(null)),

            new("Play", "Play", "F9", "test|test map|run|launch|gzdoom", () => PlayCommand.Execute(null), () => !Playing),
            new("Play from here", "Play", K("Shift+F9", "Ctrl+F9"), "test from current position|start here", () => PlayFromHereCommand.Execute(null), () => !Playing),
        };
        list.InsertRange(list.FindIndex(c => c.Name == "Bring back the last selection") + 1, AlikeChoices.Select(a => new EditorCommand(a.Command, "Edit", "", a.Words, () => Session.SelectAlike(a.By), () => Session.Selection.Items.Any(e => Selecting.Fits(e, a.By)))));
        for (var i = 0; i < skills.Length; i++)
        {
            var skill = i;
            list.Add(new($"Play on {skills[i]}", "Play", "", "difficulty|skill", () => Skill = skill));
        }

        list.AddRange(GuideCommands());
        list.Add(new("Feature tour", "Guide", "", "examples|museum|demo|show me everything|learn", () => OpenTourCommand.Execute(null)));
        foreach (var kind in Landmarks.Kinds)
        {
            var named = kind switch { "stairs" => "stairs", "exit" => "an exit", _ => "a " + kind };
            list.Add(new($"Show me {named} in a real map", "Guide", "", $"example|see one|learn|how is it made|{kind}", () => ShowMeOneCommand.Execute(kind), () => ExampleOf(kind) is not null));
        }

        list.Add(new("Preferences", "WadSmith", K("Ctrl+,", "F5"), "settings|options|tips|larger text|classic names|keys", () => OpenPreferencesCommand.Execute(null)));
        list.Add(new("Back to the start screen", "WadSmith", "", "close|other map|home", () => CloseCommand.Execute(null)));
        return list;
    }

    /// <summary>What the Select tool picks: anything, or only corners, walls, rooms or objects (Doom Builder's modes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectLabel))]
    private PickOnly _picks;

    /// <summary>The Select tool's label, naming what it picks when it picks one kind only.</summary>
    public string SelectLabel => _services.Words.Label(Picks switch
    {
        PickOnly.Corners => "Select · corners",
        PickOnly.Walls => "Select · walls",
        PickOnly.Rooms => "Select · rooms",
        PickOnly.Objects => "Select · objects",
        _ => "Select",
    });

    /// <summary>The command a key press runs in the chosen preset (its shortcut or its second key), or null.</summary>
    internal EditorCommand? CommandFor(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers) =>
        Commands().FirstOrDefault(c => KeyChords.Matches(c.Shortcut, key, modifiers) || (c.Also.Length > 0 && KeyChords.Matches(c.Also, key, modifiers)));

    /// <summary>The shortcut of a command in the chosen preset, or "" when it has none.</summary>
    internal string KeyFor(string command) => Commands().FirstOrDefault(c => c.Name == command)?.Shortcut ?? "";

    /// <summary>
    /// A sentence with its keys and names filled in as Preferences say: {key:Command} is the command's key,
    /// {or:Command} is " (or press K)" or nothing, {label:Word} the word's label, {move} the 3D movement keys.
    /// </summary>
    internal string Keyed(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\{(key|or|label|move)(?::([^}]*))?\}", m => m.Groups[1].Value switch
    {
        "key" => KeyFor(m.Groups[2].Value),
        "or" => KeyFor(m.Groups[2].Value) is { Length: > 0 } k ? $" (or press {k})" : "",
        "label" => _services.Words.Label(m.Groups[2].Value),
        _ => ClassicKeys ? "E S D F" : "WASD",
    });

    /// <summary>The labels and tooltips of the controls whose names and keys follow Preferences.</summary>
    public EditorFaces Faces => _faces ??= BuildFaces();

    private EditorFaces? _faces;

    private EditorFaces BuildFaces()
    {
        var words = _services.Words;
        Face F(string label, string tip, string command)
        {
            var key = KeyFor(command);
            return new(words.Label(label), words.Tip(label, key.Length > 0 ? $"{tip} ({key})." : tip + "."));
        }

        return new(
            Select: F("Select", "Click a room, a wall, a corner or a thing to see it in the Inspector; Shift adds or removes", "Select tool"),
            Room: F("Room", "Two ways: drag a rectangle, or click corner by corner and double-click the last corner to finish. It snaps to the grid and to corners; drawn against or over other rooms, it shares their walls and never removes any", "Room tool"),
            Door: F("Door", "Click the wall between two rooms to make it a door that opens when used, with the game's door and track textures", "Door tool"),
            Window: F("Window", "Drag along a wall (or click it) for a window: through a thick wall to the room behind, or in a wall two rooms share, which becomes a wall closed but for the window", "Window tool"),
            Teleport: F("Teleport", "Click a room to step on, then the spot the player arrives at: stepping onto the room sends them there", "Teleport tool"),
            Light: F("Light", "Click in a room for a light, which the 3D view shows; its colour, reach and flicker are in the Inspector", "Light tool"),
            Stairs: F("Stairs", "Drag from the bottom of a staircase to its top: steps that climb from one floor to the other, never more than the player can step up", "Stairs tool"),
            Measure: F("Measure", "Drag a line to measure it: below the map, the floors and ceilings along it as a cross-section, with the player for scale and every step the player can't take", "Measure tool"),
            Objects: F("Objects", "The game's monsters, items and decorations. Drag one onto the map to place it", "Objects shelf"),
            Play: F("Play", "Plays this map in GZDoom, or in a classic port chosen under the arrow's Play in (the map exported for it). WadSmith checks it first and keeps a version; what the game says about it comes back to Problems. The arrow also chooses the difficulty", "Play"),
            Player: F("Player", "Shows the player's size at the pointer, green where the player fits and red where not, and marks in red where the player cannot pass: gaps under 32 across (45 on a slant) and openings under 56 high", "Show the player's size"),
            ThreeD: F("3D", "The 3D view alone, from any angle; the same key again goes back to the plan", "Show the 3D view alone"));
    }


    /// <summary>"Explain this map" (D32) while it is open: the map's summary and sections; null when closed.</summary>
    [ObservableProperty]
    private ReportView? _report;

    private readonly LatestOnWorker<(EditorSession Session, MapReport Report)> _reports;

    /// <summary>Raised to centre both views on a spot with this much round it (a report line's Show).</summary>
    public event Action<MapPoint, double>? PlaceRequested;

    /// <summary>Explains the map on a worker and opens the card; it follows the map's changes while open.</summary>
    [RelayCommand]
    private void ExplainMap()
    {
        var (session, frame, resources) = (Session, Session.Geometry.Frame, Session.Resources);
        _reports.Request(() => (session, MapReporting.Explain(frame, resources)));
    }

    [RelayCommand]
    private void CloseReport() => Report = null;

    /// <summary>The Export card (Phase 12) while it is open; null when closed.</summary>
    [ObservableProperty]
    private ExportViewModel? _export;

    /// <summary>Raised to show elements on the plan (an Export line's Show).</summary>
    public event Action<IReadOnlyList<Element>>? ShowElementsRequested;

    /// <summary>Opens the Export card, which exports for the project's own target first.</summary>
    [RelayCommand]
    private void OpenExport()
    {
        if (Export is not null) return;
        Export = new ExportViewModel(Session, Path.Combine(_services.Paths.Cache, "export"), elements =>
        {
            Session.Selection.Set([.. elements]);
            ShowElementsRequested?.Invoke(elements);
        }, () => Export = null) { PickFile = PickExportFile };
    }

    private void ReportArrived((EditorSession Session, MapReport Report) r)
    {
        if (!ReferenceEquals(r.Session, Session)) return;
        Report = new ReportView(r.Report.Summary, [.. r.Report.Sections.Select(s => new ReportSectionView(s.Heading, [.. s.Lines.Select(l =>
            new ReportItem(l.Text, l.At is { } at ? new RelayCommand(() => PlaceRequested?.Invoke(at, 640)) : null, l.Meter))]))]);
    }

    /// <summary>A game's own map opened to learn from (D28): what it shows and the way back; null for the user's own maps.</summary>
    public PeekInfo? Peek { get; init; }

    /// <summary>A spot the views show when they first lay out (a landmark) instead of the whole map; taken by the view.</summary>
    public (MapPoint At, double Span)? PendingPlace { get; set; }

    [RelayCommand]
    private void BackFromPeek() => _main.BackFromPeek(this);

    /// <summary>"Make another like it" (D50): the page again with the choices this made map was made with, and a new seed.</summary>
    private async Task MakeAnotherAsync()
    {
        var file = Path.Combine(Session.Project.Folder, WadSmith.Editor.Generate.MadeProjects.SettingsFile);
        var like = await Task.Run(() => File.Exists(file) ? WadSmith.Editor.Generate.SettingsJson.Read(File.ReadAllText(file)) : null).ConfigureAwait(true);
        var game = like is null ? null : _services.Games?.FirstOrDefault(g => g.Game.Id == like.GameId);
        if (like is null || game is null)
        {
            Hint(like is null ? "This map's choices were not kept, so another like it cannot be made; Make me a map is on its game's card." : "This map's game was not found, so another like it cannot be made now.");
            return;
        }

        await _main.OpenMakeMapAsync(game, Session.Project.Info.Target, like).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RemixPeekAsync() => _main.RemixPeekAsync(this);

    /// <summary>
    /// The games to show an example from, best first: this map's game, then the others of its family (a Heretic map shows
    /// Heretic), then the rest found on this computer.
    /// </summary>
    private IEnumerable<FoundGame> ExampleGames()
    {
        var games = _services.Games ?? [];
        var own = Peek?.Game.Game ?? games.FirstOrDefault(g => g.Game.Id == Session.Project.Info.Game)?.Game;
        return games.Where(g => !g.Game.Id.EndsWith("-shareware", StringComparison.Ordinal))
            .OrderBy(g => g.Game.Id == own?.Id ? 0 : g.Game.Engine == own?.Engine ? 1 : 2);
    }

    /// <summary>The landmark of <paramref name="kind"/> to show, and the game it is read from; null when no game here has one.</summary>
    internal (Landmark Landmark, FoundGame Game)? ExampleOf(string kind)
    {
        foreach (var game in ExampleGames())
        {
            if (Landmarks.For(kind, [game.Game.Id]) is { } landmark) return (landmark, game);
        }

        return null;
    }

    /// <summary>Opens WadSmith's feature tour (D32) for this map's game, or the first game found.</summary>
    [RelayCommand]
    private async Task OpenTourAsync()
    {
        if (ExampleGames().FirstOrDefault() is not { } game)
        {
            Hint("The feature tour is built for one of your games, and none was found.");
            return;
        }

        await _main.OpenTourAsync(game, Peek?.Back ?? this).ConfigureAwait(true);
    }

    /// <summary>"See it in a real map" (D28): opens a game's own map at a spot that shows <paramref name="kind"/>, read-only.</summary>
    [RelayCommand]
    private async Task ShowMeOneAsync(string kind)
    {
        if (ExampleOf(kind) is not { } found)
        {
            Hint($"None of the games found here has a {kind} in the landmark index.");
            return;
        }

        await _main.PeekAsync(found.Landmark, found.Game, Peek?.Back ?? this).ConfigureAwait(true);
    }

    /// <summary>The landmark kind the open tool makes, for its "See one" button; null for tools that make none.</summary>
    private string? ToolKind => Tool switch
    {
        Canvas.EditorTool.Door => "door",
        Canvas.EditorTool.Window => "window",
        Canvas.EditorTool.Teleport => "teleporter",
        Canvas.EditorTool.Stairs => "stairs",
        _ => null,
    };

    private GuideLink? SeeOneOf(string kind) => ExampleOf(kind) is { } found
        ? new GuideLink($"See one in {found.Game.Game.Title}", $"Opens {found.Game.Game.Title} {found.Landmark.Map} at a {kind}, to look at how it is made. Nothing there changes; a button brings you back.", new RelayCommand(() => ShowMeOneCommand.Execute(kind)))
        : null;

    /// <summary>The button under the tools that shows what the open tool makes in a real map ("See one in Doom II"); null hides it.</summary>
    public GuideLink? SeeOne => ToolKind is { } kind ? SeeOneOf(kind) : null;

    /// <summary>The Guide chapter running now; null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marks))]
    private GuideViewModel? _guide;

    /// <summary>Which control the Guide's open step outlines.</summary>
    public GuideMarks Marks => GuideMarks.For(Guide?.Target ?? GuideTarget.None);

    /// <summary>The Guide's chapters, for the Guide menu.</summary>
    /// <summary>
    /// The choices a right-click on the plan offers for an element: a door (its area, or one of its faces) gets its locks
    /// (Doom's keys) and its looks, the game's own door pictures; anything else gets none, and no menu opens.
    /// </summary>
    public Func<Element, IReadOnlyList<MenuChoice>> ContextMenuFor => element =>
    {
        if (Session.Resources is not { } resources) return [];
        var area = element switch
        {
            Sector s => s,
            Line l => Doors.Recognise(Session.Geometry, l.Front.Sector) is not null ? l.Front.Sector : l.Back is { } b && Doors.Recognise(Session.Geometry, b.Sector) is not null ? b.Sector : null,
            _ => null,
        };
        if (area is null || Doors.Recognise(Session.Geometry, area) is not { } found) return [];
        var choices = new List<MenuChoice>();

        // Doom's keys: lock the door with one, or unlock it; the key itself then goes somewhere the player reaches first.
        if (Session.Look == Look.For("doom"))
        {
            var locked = Keys.ColourOfLock(found.Settings.Lock);
            foreach (var (colour, _, _, _) in Keys.Doom)
            {
                var c = colour;
                choices.Add(new MenuChoice($"{(locked == c ? "✓ " : "")}Lock with the {c} key", $"The player needs the {c} key (the card or the skull) to open this door; the game tells them so. Put that key somewhere they reach first.", () => Session.LockDoor(area, c)));
            }

            if (locked is not null) choices.Add(new MenuChoice("Unlock", "Takes the lock away: the door opens for anyone.", () => Session.LockDoor(area, null)));
            choices.Add(new MenuChoice("-", "", () => { }));
        }

        var looks = Doors.Looks(resources.Game.Engine, resources.Textures.Walls);
        choices.AddRange(looks.Select(t => new MenuChoice($"Looks like {t.Name}", $"{t.Name}, {t.Width} × {t.Height}: the door shows this picture, and its doorway becomes {t.Height} tall.", () =>
        {
            if (Doors.Recognise(Session.Geometry, area) is { } door) Session.History.Record("Change the door's look", e => Doors.Dress(e, Session.Geometry, door, t.Name, (t.Width, t.Height)));
        })));
        return choices;
    };

    public IReadOnlyList<GuideLink> GuideMenu => _guideMenu ??= [.. GuideBook.Chapters.Select(c => new GuideLink(c.Title, c.Summary, new RelayCommand(() => StartGuide(c.Id)))),
        new GuideLink("Feature tour: a map of examples", "A small map with a room for each feature and a sign in each, built for your game. Play it to walk round; Remix it to change it.", OpenTourCommand)];

    private IReadOnlyList<GuideLink>? _guideMenu;

    // The map's name and music when last seen, so the Guide hears which one changed.
    private (string Title, string? Music) _mapSeen;

    /// <summary>
    /// Starts a Guide chapter on this map (§3.9): the guided first map with no id. A chapter already running gives way to
    /// another; the same one is left as it is.
    /// </summary>
    [RelayCommand]
    private void StartGuide(string? chapter)
    {
        var wanted = GuideBook.Chapter(chapter);
        if (Guide?.Chapter == wanted) return;
        var guide = new GuideViewModel(Session, wanted, Keyed, Standing, () => Guide = null, id => StartGuide(id), SeeOneOf);
        guide.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GuideViewModel.Target) && ReferenceEquals(Guide, guide)) OnPropertyChanged(nameof(Marks));
        };
        Guide = guide;
        UpdateStatus();
    }

    /// <summary>One palette entry per Guide chapter; the first map keeps its own name.</summary>
    private IEnumerable<EditorCommand> GuideCommands() => GuideBook.Chapters.Select(c => new EditorCommand(
        c.Id == GuideBook.FirstMap ? c.Title : "Guide: " + c.Title, "Guide", "", "tutorial|help|learn|guide|how do i|" + c.Summary.ToLowerInvariant(),
        () => StartGuide(c.Id), () => Guide?.Chapter.Id != c.Id));

    /// <summary>What holds in the editor now that a Guide step may ask for.</summary>
    private IEnumerable<string> Standing()
    {
        if (_services.Settings.Current.ClassicNames) yield return GuideEvents.ClassicNames;
        if (_services.Settings.Current.DoomBuilderKeys) yield return GuideEvents.BuilderKeys;
    }

    private void MapSettingsSeen()
    {
        var now = (Session.MapEntry.Title, Session.MapEntry.Music);
        if (now.Title != _mapSeen.Title) Guide?.Happened(GuideEvents.MapName);
        if (now.Music != _mapSeen.Music) Guide?.Happened(GuideEvents.MapMusic);
        _mapSeen = now;
    }

    partial void OnSkillChanged(int value) => Guide?.Happened(GuideEvents.Difficulty);

    partial void OnCameraModeChanged(ThreeD.CameraMode value)
    {
        if (value == ThreeD.CameraMode.Walk) Guide?.Happened(GuideEvents.Walk);
    }

    /// <summary>The classic names or the keys changed in Preferences: every label, tooltip, tip and key follows at once.</summary>
    internal void VeteranChanged()
    {
        _commands = null;
        _faces = null;
        OnPropertyChanged(nameof(Faces));
        OnPropertyChanged(nameof(LightTip));
        OnPropertyChanged(nameof(SelectLabel));
        OnPropertyChanged(nameof(ClassicKeys));
        Inspector.Rebuild();
        Guide?.Rewrite();
        foreach (var s in Standing()) Guide?.Happened(s);
        UpdateStatus();
    }

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
    [NotifyPropertyChangedFor(nameof(IsSelectTool), nameof(IsRoomTool), nameof(IsDoorTool), nameof(IsTeleportTool), nameof(IsStairsTool), nameof(IsWindowTool), nameof(IsLightTool), nameof(IsMeasureTool), nameof(SeeOne))]
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

    private readonly LatestOnWorker<byte[]?> _shelfSounds;

    /// <summary>The pointer came over a shelf tile: an object plays its sound (D33), read from the game on a worker.</summary>
    internal void PointedAtShelf(ShelfTile tile)
    {
        if (!_services.Settings.Current.ShelfSounds || tile.Drop.Thing == 0 || tile.Drop.Prefab is not null || (tile.Drop.From ?? Session.Resources) is not { } resources) return;
        var type = tile.Drop.Thing;
        _shelfSounds.Request(() => resources.SoundFor(type));
    }
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

    /// <summary>
    /// Marks the Objects shelf's tiles a project for a classic port cannot use (6b.3): other games' objects, free downloads,
    /// Quake's models and the project's own models and picture decorations, all GZDoom's alone; clears the marks for GZDoom.
    /// </summary>
    private void MarkUnusable()
    {
        var classic = Session.Project.ClassicFormat;
        HashSet<int>? own = null;
        foreach (var tile in ObjectShelf.Tiles)
        {
            string? why = null;
            if (classic)
            {
                own ??= [.. ModelNumbers(), .. DecorationNumbers()];
                why = tile.Drop.Library is not null ? "downloaded objects are defined in GZDoom's language."
                    : tile.Drop.QuakeModel is not null || own.Contains(tile.Drop.Thing) ? "models and decorations of your own are drawn by GZDoom."
                    : tile.Drop.From is not null && tile.Drop.Texture is null ? "objects from other games are carried in as GZDoom actors."
                    : null;
            }

            if (tile.Unusable != why) tile.Unusable = why;
        }
    }

    /// <summary>Whether the Light tool can be used: its lights are GZDoom's, so not in a project for a classic port (6b.3).</summary>
    public bool LightAllowed => !Session.Project.ClassicFormat;

    /// <summary>The Light tool's tooltip, saying first why it is greyed in a project for a classic port.</summary>
    public string LightTip => LightAllowed ? Faces.Light.Tip : "GZDoom only: lights that shine on what is round them are GZDoom's; a classic port lights each room by its brightness alone. " + Faces.Light.Tip;

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
            if (value)
            {
                Tool = Canvas.EditorTool.Select;
                Picks = PickOnly.Anything;
            }
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
            ? string.Create(CultureInfo.InvariantCulture, $"{d.Corners} corner{(d.Corners == 1 ? "" : "s")} so far{(d.Side > 0 ? $" · this side {d.Side:0}" : "")} · double-click the last corner, or click the first one again, to finish the room; Enter finishes an open line (across a room it splits it); Shift keeps sides straight; Esc lets go")
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
        Find.Active = value == SidePanel.Find;
        if (value == SidePanel.Find) Find.Search();
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

    /// <summary>The project's maps, when it has more than one (an episode): each opens in the editor in place of this one.</summary>
    public IReadOnlyList<ProjectMapChoice> ProjectMaps => _projectMaps ??= Session.Project.Info.Maps.Count < 2 ? [] : [.. Session.Project.Info.Maps.Select(m => new ProjectMapChoice(m.Slot, m.Title, m.Slot == Session.Slot, new AsyncRelayCommand(() => _main.SwitchMapAsync(Session.Project, m.Slot))))];

    private IReadOnlyList<ProjectMapChoice>? _projectMaps;

    /// <summary>Opens the project's next (+1) or previous (−1) map in the editor, round from the last to the first (Eureka's FlipMap).</summary>
    private Task OtherMapAsync(int way)
    {
        var maps = Session.Project.Info.Maps;
        var at = maps.FindIndex(m => string.Equals(m.Slot, Session.Slot, StringComparison.OrdinalIgnoreCase));
        return maps.Count < 2 ? Task.CompletedTask : _main.SwitchMapAsync(Session.Project, maps[((at < 0 ? 0 : at) + way + maps.Count) % maps.Count].Slot);
    }

    /// <summary>"MAP01 ▾": the button that lists the project's maps.</summary>
    public string MapChooserLabel => $"{Session.Slot} ▾";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _saveText = "";

    /// <summary>How the map is painted (D45); floors by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeights), nameof(IsFloors), nameof(IsCeilings), nameof(IsLines), nameof(IsBrightness), nameof(IsSound))]
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

    public bool IsSound
    {
        get => ViewMode == Canvas.MapViewMode.Sound;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Sound;
            else OnPropertyChanged();
        }
    }

    public bool IsBrightness
    {
        get => ViewMode == Canvas.MapViewMode.Brightness;
        set
        {
            if (value) ViewMode = Canvas.MapViewMode.Brightness;
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
        if (value == Canvas.MapViewMode.Heights) Guide?.Happened(GuideEvents.Heights);
        if (value == Canvas.MapViewMode.Sound) Session.Tell("Sound zones: rooms of one colour hear each other's shots. A shot carries across one wall that blocks sound into the next zone, never across two; a shut door stops it.");
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

    /// <summary>Opens a file in the computer's own program for it (set by the view); true when it was handed over.</summary>
    public Func<string, Task<bool>>? OpenInOtherProgram { get; set; }

    /// <summary>Reads a picture from the system clipboard (set by the view): a picture copied, or a picture file copied in the file manager; null when it holds none.</summary>
    public Func<Task<(string Name, byte[] Bytes)?>>? ReadClipboardPicture { get; set; }

    /// <summary>
    /// What a pasted picture goes on: the one wall face or one-sided wall selected (as a poster, as a dropped picture goes),
    /// or the one room selected (its floor, its ceiling in the Ceilings view); null for any other selection.
    /// </summary>
    private Editor.Scene.SurfaceTarget? PictureTarget() => Session.Selection.Items is [var one] ? one switch
    {
        Side { OnMap: true, Line.Back: null } face => new Editor.Scene.SurfaceTarget(face, Editor.Scene.WallPart.Middle, false),
        Line { OnMap: true, Back: null } wall => new Editor.Scene.SurfaceTarget(wall.Front, Editor.Scene.WallPart.Middle, false),
        Sector { OnMap: true } room => new Editor.Scene.SurfaceTarget(room, Editor.Scene.WallPart.Middle, IsCeilings),
        _ => null,
    } : null;

    /// <summary>
    /// Pastes: a picture on the system clipboard goes on the selected wall or room (brought in as a texture first); otherwise
    /// what was copied in the editor goes where the pointer is on the plan, or beside the original when the pointer has not been over it.
    /// </summary>
    [RelayCommand]
    private async Task Paste()
    {
        if (PictureTarget() is { } target && ReadClipboardPicture is { } read && await read().ConfigureAwait(true) is { } picture)
        {
            Session.ImportPicture(picture.Name, picture.Bytes, texture =>
            {
                if (Session.PaintSurface(target, texture) is { } why) Session.Tell(why);
            });
            return;
        }

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

    // Play (Phase 4): the engine's difficulty, the checks before, and the engine's say after.
    private static readonly IReadOnlyDictionary<string, string[]> SkillNamesByFamily = new Dictionary<string, string[]>
    {
        ["doom"] = ["I'm too young to die", "Hey, not too rough", "Hurt me plenty", "Ultra-Violence", "Nightmare!"],
        ["heretic"] = ["Thou needeth a wet-nurse", "Yellowbellies-r-us", "Bringest them oneth", "Thou art a smite-meister", "Black plague possesses thee"],
        ["hexen"] = ["Easiest", "Easy", "Normal", "Hard", "Hardest"],
        ["strife"] = ["Training", "Rookie", "Veteran", "Elite", "Bloodbath"],
    };

    private string[] SkillNames => SkillNamesByFamily.GetValueOrDefault(Session.Engine) ?? SkillNamesByFamily["doom"];

    /// <summary>The difficulties in the game's own words, the one Play uses marked.</summary>
    public IReadOnlyList<string> SkillHeaders => [.. SkillNames.Select((n, i) => (i == Skill ? "✓ " : "    ") + n)];

    /// <summary>Which difficulty Play starts at (0 is the easiest); "Hurt me plenty" to begin with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkillHeaders))]
    private int _skill = 2;

    [RelayCommand]
    private void ChooseSkill(string index) => Skill = Math.Clamp(int.Parse(index, CultureInfo.InvariantCulture), 0, 4);

    /// <summary>Whether the game is running now.</summary>
    [ObservableProperty]
    private bool _playing;

    /// <summary>What would break the game, said before it starts (D25); null when nothing is waiting.</summary>
    [ObservableProperty]
    private string? _preflight;

    private IReadOnlyList<WadSmith.Editor.Checks.Problem> _blocking = [];

    /// <summary>Plays the map in the engine (F9): checks first, keeps a version, then starts it.</summary>
    [RelayCommand]
    private Task PlayAsync() => PlayAsync(force: false, from: null);

    // The game's view (D41): GZDoom's own picture from where the 3D camera stands, shown over the 3D view.

    /// <summary>Whether the 3D pane shows the game's own picture from where the camera stands (G, or the Game's view button).</summary>
    [ObservableProperty]
    private bool _engineView;

    /// <summary>The game's picture, or null while none has been taken.</summary>
    [ObservableProperty]
    private Bitmap? _engineViewPicture;

    /// <summary>What is going on with the game's view, under the picture.</summary>
    [ObservableProperty]
    private string _engineViewNote = "";

    /// <summary>Set by the view: where the 3D camera's eye is and which way it looks.</summary>
    public Func<(double X, double Y, double Z, double Yaw, double Pitch)>? ViewCamera { get; set; }

    /// <summary>Set by the view: the 3D pane's size in pixels.</summary>
    public Func<(int Width, int Height)>? ViewSize { get; set; }

    private CancellationTokenSource? _engineViewRun;
    private bool _engineViewAgain;
    private WadSmith.Core.Engines.EngineRenderer _engineRenderer = WadSmith.Core.Engines.EngineRenderer.Vulkan;
    private readonly Dictionary<string, string> _engineViewCache = new(StringComparer.Ordinal);
    private Avalonia.Threading.DispatcherTimer? _engineViewSettle;

    partial void OnEngineViewChanged(bool value)
    {
        if (value)
        {
            _ = RefreshEngineViewAsync();
            return;
        }

        _engineViewRun?.Cancel();
        _engineViewSettle?.Stop();
        EngineViewPicture = null;
        EngineViewNote = "";
    }

    /// <summary>Called when the 3D camera moves or the map changes: a new picture once things settle, while the game's view is on.</summary>
    public void CameraMoved()
    {
        if (!EngineView) return;
        _engineViewSettle ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(700), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _engineViewSettle!.Stop();
            _ = RefreshEngineViewAsync();
        });
        _engineViewSettle.Stop();
        _engineViewSettle.Start();
    }

    /// <summary>Whether a picture is all black (every eighth pixel darker than 2 of 255): what OpenGL draws on a bare private screen.</summary>
    private static bool IsBlack(string png)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        if (bitmap is null) return false;
        for (var y = 0; y < bitmap.Height; y += 8)
        {
            for (var x = 0; x < bitmap.Width; x += 8)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red > 2 || p.Green > 2 || p.Blue > 2) return false;
            }
        }

        return true;
    }

    /// <summary>Takes the game's picture from the camera's spot on a worker (one at a time; a move meanwhile queues one more) and shows it.</summary>
    private async Task RefreshEngineViewAsync()
    {
        if (!EngineView || ViewCamera is null || ViewSize is null) return;
        if (_engineViewRun is not null)
        {
            _engineViewAgain = true;
            return;
        }

        var engines = WadSmith.Core.Engines.EngineLocator.Find(_services.Settings.Current.EnginePath, Environment.GetEnvironmentVariable);
        if (engines.Count == 0)
        {
            EngineViewNote = "GZDoom is not installed, so there is no game's view to show. Install it (Arch: pacman -S gzdoom; Debian or Ubuntu: apt install gzdoom) and try again.";
            return;
        }

        var screen = WadSmith.Core.Engines.EngineView.FindScreen(Environment.GetEnvironmentVariable);
        if (screen is null)
        {
            EngineViewNote = WadSmith.Core.Engines.EngineView.NeedsScreen;
            return;
        }

        var cam = ViewCamera();
        var (w, h) = ViewSize();
        w = Math.Clamp(w & ~1, 320, 1280);
        h = Math.Clamp(h & ~1, 200, 800);

        // The engine's pitch counts down as positive; its player's feet stand an eye height under the camera.
        var spot = new WadSmith.Core.Engines.ViewSpot(cam.X, cam.Y, cam.Yaw, cam.Z - WadSmith.Editor.Scene.Walker.EyeHeight, -cam.Pitch);
        await Session.FlushAsync().ConfigureAwait(true);
        await Session.RefreshMapInfoAsync().ConfigureAwait(true);
        var key = $"{spot.Text}|{w}x{h}|{Session.Map.Version}";
        if (_engineViewCache.TryGetValue(key, out var kept) && File.Exists(kept) && Picture(kept) is { } seen)
        {
            EngineViewPicture = seen;
            EngineViewNote = "The game's own view from here. Move the camera for another; turn Game's view off (G) to go back.";
            return;
        }

        var data = _services.Paths.Data;
        var games = await Task.Run(_services.FoundGames).ConfigureAwait(true);
        var config = WadSmith.Core.Engines.EngineConfig.Prepare(data, Environment.GetEnvironmentVariable, _services.Settings.Current.EnginePath);
        var (request, why) = WadSmith.Core.Engines.PlayRequests.For(Session.Project, Session.Slot, games, config, Path.Combine(data, "engine", "view", "engine-view.log"), 3);
        if (request is null)
        {
            EngineViewNote = why ?? "";
            return;
        }

        var run = _engineViewRun = new CancellationTokenSource();
        EngineViewNote = "Asking GZDoom for its view from here… about fifteen seconds.";
        try
        {
            var folder = Path.Combine(data, "engine", "view");
            if (_engineViewCache.Count == 0 && Directory.Exists(folder))
            {
                foreach (var old in Directory.GetFiles(folder, "view-*.png")) File.Delete(old);
            }

            var renderer = _engineRenderer;
            var png = await Task.Run(() => WadSmith.Core.Engines.EngineView.CaptureAsync(engines[0], request, spot, w, h, data, screen, TimeSpan.FromSeconds(90), run.Token, renderer)).ConfigureAwait(true);
            if (run.IsCancellationRequested) return;
            if (renderer == WadSmith.Core.Engines.EngineRenderer.Vulkan && await Task.Run(() => IsBlack(png)).ConfigureAwait(true))
            {
                // No Vulkan to draw with on this computer: the classic software renderer from now on (its picture is the game's own too).
                _engineRenderer = WadSmith.Core.Engines.EngineRenderer.Software;
                Log.Info("The game's view came back black with Vulkan; the software renderer is used from now on.");
                png = await Task.Run(() => WadSmith.Core.Engines.EngineView.CaptureAsync(engines[0], request, spot, w, h, data, screen, TimeSpan.FromSeconds(90), run.Token, _engineRenderer)).ConfigureAwait(true);
                if (run.IsCancellationRequested) return;
            }

            if (_engineViewCache.Count >= 24)
            {
                foreach (var f in _engineViewCache.Values) File.Delete(f);
                _engineViewCache.Clear();
            }

            var copy = Path.Combine(folder, $"view-{(uint)key.GetHashCode():x8}.png");
            File.Copy(png, copy, overwrite: true);
            _engineViewCache[key] = copy;
            EngineViewPicture = Picture(copy) ?? throw new WadSmith.Core.Engines.EngineViewException("The engine's screenshot could not be read as a picture.");
            EngineViewNote = "The game's own view from here. Move the camera for another; turn Game's view off (G) to go back.";
        }
        catch (WadSmith.Core.Engines.EngineViewException ex)
        {
            EngineViewNote = ex.Message;
            Log.Warn($"The game's view could not be taken: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            EngineViewNote = $"The game's view could not be taken: {ex.Message}";
            Log.Warn($"The game's view could not be taken: {ex.Message}");
        }
        finally
        {
            _engineViewRun = null;
            if (_engineViewAgain && EngineView)
            {
                _engineViewAgain = false;
                _ = RefreshEngineViewAsync();
            }
        }
    }

    /// <summary>Where Play From Here starts: the 3D view's spot (set by the view).</summary>
    public Func<(double X, double Y, double Angle)>? PlaySpot { get; set; }

    /// <summary>Puts the 3D camera at a spot, above a floor at a height; set by the view.</summary>
    public Action<MapPoint, double>? PutCamera { get; set; }

    /// <summary>Puts the 3D camera where the pointer is on the plan (Eureka's ' key), showing the 3D view beside the plan if it was hidden.</summary>
    private void PutCameraAtPointer()
    {
        if (_pointer is not { } at || Session.Geometry.AreaAt(at) is not { } area)
        {
            Session.Tell("Point at a room on the plan, then press ' to stand there in the 3D view.");
            return;
        }

        if (Layout == MapLayout.Plan) Layout = MapLayout.Split;
        PutCamera?.Invoke(at, area.FloorHeight);
    }

    /// <summary>The spot a Play waiting on the pre-flight starts from (Play From Here), so its "Fix them and play" keeps it.</summary>
    private (double X, double Y, double Angle)? _waitingFrom;

    /// <summary>Plays from where the 3D view stands (Shift+F9), when the player fits there.</summary>
    [RelayCommand]
    private async Task PlayFromHereAsync()
    {
        if (PlaySpot?.Invoke() is not { } spot)
        {
            Hint("Play From Here starts where the 3D view stands: turn on 3D.");
            return;
        }

        var fit = WadSmith.Editor.Checks.Fit.At(Session.Geometry, new MapPoint(spot.X, spot.Y));
        if (!fit.Inside || !fit.Fits)
        {
            Hint(!fit.Inside ? "Play From Here starts where the 3D view stands, and that is outside the map: walk or fly the view into a room first." : $"Play From Here starts where the 3D view stands, but the player does not fit there. {fit.Why}");
            return;
        }

        await PlayAsync(force: false, from: spot).ConfigureAwait(true);
    }

    /// <summary>The pre-flight's "Fix them and play": every blocking problem with a fix is fixed (one undo step each).</summary>
    [RelayCommand]
    private async Task FixAndPlayAsync()
    {
        foreach (var p in _blocking.Where(p => p.Fix is not null)) Session.History.Record(p.FixLabel!, p.Fix!);
        await PlayAsync(force: true, from: _waitingFrom).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task PlayAnywayAsync() => PlayAsync(force: true, from: _waitingFrom);

    [RelayCommand]
    private void CancelPlay()
    {
        Preflight = null;
        _waitingFrom = null;
    }

    private async Task PlayAsync(bool force, (double X, double Y, double Angle)? from)
    {
        if (Playing) return;
        Preflight = null;
        var engines = WadSmith.Core.Engines.EngineLocator.Find(_services.Settings.Current.EnginePath, Environment.GetEnvironmentVariable);
        var port = PlayingPort;
        if (port is not null && from is not null)
        {
            Hint($"Play From Here works in GZDoom only; {port.Name} starts the map at the player's start. Choose GZDoom under the Play arrow's Play in.");
            return;
        }

        if (engines.Count == 0 && port is null)
        {
            Hint("GZDoom is not installed. Install it from your system's software centre or package manager (Arch: pacman -S gzdoom; Debian or Ubuntu: apt install gzdoom; any: flatpak install flathub org.zdoom.GZDoom), then press Play again. Editing works meanwhile.");
            return;
        }

        await Session.FlushAsync().ConfigureAwait(true);
        await Session.RefreshMapInfoAsync().ConfigureAwait(true);
        if (!force)
        {
            var (geometry, resources, look, family, fit) = (Session.Geometry, Session.Resources, Session.Look, Session.Engine, Session.FitBaseline);
            var classic = Session.ClassicRules;
            var blocking = await Task.Run(() => WadSmith.Editor.Checks.Problems.Check(geometry, resources, look, family, fit, classic).Where(p => p.Severity == WadSmith.Editor.Checks.Severity.Error).ToList()).ConfigureAwait(true);
            if (blocking.Count > 0)
            {
                _blocking = blocking;
                _waitingFrom = from;
                var fixable = blocking.Count(p => p.Fix is not null);
                Preflight = $"Before you play: {(blocking.Count == 1 ? "1 thing" : $"{blocking.Count} things")} would break the game. {string.Join(" ", blocking.Select(p => p.Sentence))}{(fixable == 0 ? "" : fixable == blocking.Count ? " WadSmith can fix " + (fixable == 1 ? "it." : "them all.") : $" WadSmith can fix {fixable} of them.")}";
                return;
            }
        }

        var data = _services.Paths.Data;
        var games = await Task.Run(_services.FoundGames).ConfigureAwait(true);
        var config = WadSmith.Core.Engines.EngineConfig.Prepare(data, Environment.GetEnvironmentVariable, _services.Settings.Current.EnginePath);
        var log = Path.Combine(data, "engine", "last-play.log");
        var (request, why) = WadSmith.Core.Engines.PlayRequests.For(Session.Project, Session.Slot, games, config, log, Skill + 1);
        if (request is null)
        {
            Hint(why);
            return;
        }

        _waitingFrom = null;
        if (port is not null)
        {
            await PlayInPortAsync(port, request).ConfigureAwait(true);
            return;
        }

        if (from is { } here) request = request with { From = here, FromAddOn = WadSmith.Core.Engines.PlayFrom.Write(data) };

        await Session.SaveVersion("Played").ConfigureAwait(true);
        var played = Session.Map.Frame;
        var exitCode = -1;
        var engine = engines[0];
        var (program, arguments) = WadSmith.Core.Engines.PlayCommand.For(engine, request);
        try
        {
            if (File.Exists(log)) File.Delete(log);
            var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var a in arguments) start.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("the engine did not start");
            Playing = true;
            Guide?.Happened(GuideEvents.Played);
            Hint($"Playing {Session.Slot} in {engine.Name}… WadSmith keeps your changes meanwhile; what the game says comes back to Problems when it closes.");
            await process.WaitForExitAsync(_services.ShuttingDown).ConfigureAwait(true);
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Starting {program} failed: {ex.Message}");
            Hint($"{engine.Name} could not be started: {ex.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Playing = false;
        }

        Session.ReadPlayLog(log, played);
        Hint("Back from the game.");
        if (exitCode == 0) await CelebrateFirstPlay(played, engine.Name).ConfigureAwait(true);
    }

    /// <summary>The first-Play card (D33) while it shows; null otherwise.</summary>
    [ObservableProperty]
    private Celebration? _celebration;

    /// <summary>
    /// The first time a map comes back from the game without an error, a small card says what the user made (D33): once per
    /// profile, unless celebrations are off in Preferences. The counts are read from the map as it was played, on a worker.
    /// </summary>
    internal async Task CelebrateFirstPlay(MapFrame played, string engineName)
    {
        var settings = _services.Settings.Current;
        if (!settings.Celebrations || settings.FirstPlayCelebrated) return;
        settings.FirstPlayCelebrated = true;
        _services.Settings.Save();
        var resources = Session.Resources;
        var game = Core.Games.GameCatalog.Get(Session.Project.Info.Game)?.Title ?? "Doom";
        var (rooms, monsters) = await Task.Run(() => (
            played.Sectors.Count(s => s.Element is not null),
            played.Things.Count(t => t.Element is not null && resources?.Things.Find(t.Type)?.Category == Core.Games.ThingCategory.Monster))).ConfigureAwait(true);
        string Count(int n, string one) => n == 1 ? $"1 {one}" : string.Create(CultureInfo.InvariantCulture, $"{n} {one}s");
        Celebration = new Celebration(
            $"You made a {game} map.",
            $"{ProjectName} just ran in {engineName} with {Count(rooms, "room")}{(monsters > 0 ? " and " + Count(monsters, "monster") : "")}. Every Play keeps a version of the map in Versions, so this one is always there to come back to.");
    }

    [RelayCommand]
    private void CloseCelebration() => Celebration = null;

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

    /// <summary>The symmetry modes, in words, in the order of <see cref="MirrorMode"/>.</summary>
    public IReadOnlyList<string> MirrorModes { get; } = ["No mirror", "Mirror left–right", "Mirror top–bottom", "Mirror four ways", "Half turn", "Quarter turns"];

    /// <summary>
    /// Which symmetry mode is on; choosing one centres it on the selection's middle, or the map's when nothing is selected
    /// (on the grid's 8).
    /// </summary>
    public int MirrorChoice
    {
        get => (int)Session.Mirror.Mode;
        set
        {
            if (value < 0 || value == MirrorChoice) return;
            var mode = (MirrorMode)value;
            var (x0, y0, x1, y1) = Session.Selection.IsEmpty ? Session.Geometry.Extent() : SelectionBox();
            var centre = new MapPoint(Math.Round((x0 + x1) / 2 / 8) * 8, Math.Round((y0 + y1) / 2 / 8) * 8);
            Session.Mirror = mode == MirrorMode.Off ? Mirror.Off : new Mirror(mode, centre);
            OnPropertyChanged();
            OnPropertyChanged(nameof(MirrorButton));
            Hint(mode == MirrorMode.Off ? null : string.Create(CultureInfo.InvariantCulture, $"{MirrorModes[value]} about ({centre.X:0}, {centre.Y:0}): rooms you draw and things you place are repeated there; the dashed lines show the middle."));
        }
    }

    /// <summary>The Mirror button's words: short enough for the tools column, saying which mode is on.</summary>
    public string MirrorButton => Session.Mirror.Mode switch
    {
        MirrorMode.LeftRight => "Mirror ↔",
        MirrorMode.TopBottom => "Mirror ↕",
        MirrorMode.FourWays => "Mirror ✣",
        MirrorMode.HalfTurn => "Turn ½",
        MirrorMode.QuarterTurns => "Turn ¼",
        _ => "Mirror",
    };

    /// <summary>The Mirror menu: the mode's number (as <see cref="MirrorMode"/>).</summary>
    [RelayCommand]
    private void ChooseMirror(string mode) => MirrorChoice = int.Parse(mode, CultureInfo.InvariantCulture);

    /// <summary>The box round the selected rooms, walls, corners and things.</summary>
    private (double, double, double, double) SelectionBox()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y) => (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
        foreach (var e in Session.Selection.Items)
        {
            switch (e)
            {
                case Vertex v: Add(v.X, v.Y); break;
                case Thing t: Add(t.X, t.Y); break;
                case Line l: Add(l.Start.X, l.Start.Y); Add(l.End.X, l.End.Y); break;
                case Side { Line: { } l }: Add(l.Start.X, l.Start.Y); Add(l.End.X, l.End.Y); break;
                case Sector s when Session.Geometry.WallBoundsAt(s.Slot) is { } b: Add(b.MinX, b.MinY); Add(b.MaxX, b.MaxY); break;
            }
        }

        return x0 > x1 ? Session.Geometry.Extent() : (x0, y0, x1, y1);
    }

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

    /// <summary>Called by the canvas when the pointer moves over something new.</summary>
    public void Hover(Element? element, MapPoint at)
    {
        if (at != default) _pointer = at;
        _hovered = element;
        if (WhatIsThis) Explain();
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

    /// <summary>
    /// "What is this?" (D33): while on, whatever the pointer is over on the plan or in the 3D view is explained in a card:
    /// what it is, what it does in the game and how to change it.
    /// </summary>
    [ObservableProperty]
    private bool _whatIsThis;

    /// <summary>The card's explanation while "What is this?" is on; null when it is off.</summary>
    [ObservableProperty]
    private Explanation? _explained;

    // What the pointer is over on the plan or in the 3D view, whichever it moved over last.
    private Element? _hovered;

    private static readonly Explanation Pointing = new("Point at something",
        "Point at anything on the plan or in the 3D view: a room, a wall, a corner or an object. Shift+F1 or the button again turns this off.");

    partial void OnWhatIsThisChanged(bool value) => Explain();

    /// <summary>Called by the 3D view when the pointer comes over something new there.</summary>
    public void Hover3D(Element? element)
    {
        _hovered = element;
        if (WhatIsThis) Explain();
    }

    private void Explain() => Explained = WhatIsThis
        ? Explaining.Explain(_hovered is { OnMap: true } ? _hovered : null, Session.Geometry, Session.Resources) ?? Pointing
        : null;

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
        // The tool in hand, how to use it: the status bar, and the Guide's card while a step is open (the status bar is far from where a first-time mapper looks).
        var how = _drawingText.Length > 0 ? _drawingText : ToolHow();
        if (Guide is { } guide) guide.How = how ?? "";

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

        if (how is not null)
        {
            Status = how;
            return;
        }

        var settings = _services.Settings.Current;
        Status = settings.CoachTips ? Keyed(Tips.FirstOrDefault(t => !settings.CoachSeen.Contains(t.Id)).Text ?? "") : "";
    }

    /// <summary>How to use the tool in hand, in a sentence or two; null for the Select tool (the tips then).</summary>
    private string? ToolHow() => Tool switch
    {
        Canvas.EditorTool.Stairs => "Drag from the bottom of the stairs to the top: the steps climb from the floor where you start to the floor where you end, 16 a step where nothing else decides. Esc goes back to Select.",
        Canvas.EditorTool.Measure => "Drag a line across the map: its length, and below the map the floors and ceilings along it, with what stops the player. Esc goes back to Select.",
        Canvas.EditorTool.Light => "Click in a room for a light: warm, 128 across, 48 above the floor; its colour and reach are in the Inspector. Esc goes back to Select.",
        Canvas.EditorTool.Camera => "Click where the camera stands: it looks away from the screen to begin with; turn it with its knob or its Inspector's Facing. The screen shows its view in the game. Esc goes back to Select.",
        Canvas.EditorTool.Key => Session.KeyToPlace is { } colour ? $"Click where the {colour} key goes, somewhere the player reaches before the door. Esc goes back to Select." : "",
        Canvas.EditorTool.Switch => Session.SwitchTarget is null
            ? "Click the door or lift the switch works. Esc goes back to Select."
            : "Now click the solid wall where the switch goes: a piece of it 64 wide becomes the switch. Esc goes back to Select.",
        Canvas.EditorTool.Teleport => Session.TeleportPad is null
            ? "Click the room to step on: it becomes the teleport pad. Esc goes back to Select."
            : "Now click where the player arrives. Stepping onto the pad sends them there. Esc goes back to Select.",
        Canvas.EditorTool.Door => "Click the dashed line where two rooms meet: a door is built into it, with wall either side when the opening is wider than a door. Drag along the line for a door of the width you want. Esc goes back to Select.",
        Canvas.EditorTool.Window => "Drag along the dashed line where two rooms meet, as wide as the window should be: a sill and a top the player sees over. Esc goes back to Select.",
        Canvas.EditorTool.Room => string.Create(CultureInfo.InvariantCulture, $"Two ways to draw a room: drag a rectangle, or click corner by corner — each click adds a corner, and a double-click on the last corner (or a click back on the first) finishes it. Corners snap to the grid ({_gridStep:0} units) and to corners nearby; drawn against a room, the two open into each other where they meet. Esc lets go."),
        _ => null,
    };
}

/// <summary>A small card that celebrates a first (D33): what happened, in a sentence or two.</summary>
internal sealed record Celebration(string Heading, string Text);

/// <summary>One of a project's maps in the editor's map list: its slot, its name, whether it is the one open, and what opens it.</summary>
internal sealed record ProjectMapChoice(string Slot, string Title, bool Current, System.Windows.Input.ICommand Open)
{
    public string Label => Title.Length > 0 ? $"{Slot} · {Title}" : Slot;
}
