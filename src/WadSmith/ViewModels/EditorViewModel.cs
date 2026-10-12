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
            new("Light tool", "Tools", K("L", ""), "lamp|brightness|light level|glow|dynamic light", () => Tool = Canvas.EditorTool.Light),
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
            new("Paste", "Edit", "Ctrl+V", "", () => PasteCommand.Execute(null), () => Editor.Editing.Copying.Clipboard is not null),
            new("Duplicate", "Edit", K("Ctrl+D", ""), "copy in place|clone", () => DuplicateCommand.Execute(null), Selected),
            new("Delete", "Edit", "Delete", "remove|erase|delete item", () => DeleteCommand.Execute(null), Selected),
            new("Join the selected rooms", "Edit", "J", "merge|join sectors|merge sectors|combine", Session.JoinRooms, Selected),
            new("Turn the selection left", "Edit", "Ctrl+[", "rotate|anticlockwise|counter-clockwise", () => Session.TurnSelection(90), Selected),
            new("Turn the selection right", "Edit", "Ctrl+]", "rotate|clockwise", () => Session.TurnSelection(-90), Selected),
            new("Mirror the selection left–right", "Edit", "Shift+H", "flip horizontally", () => Session.FlipSelection(leftToRight: true), Selected),
            new("Mirror the selection top–bottom", "Edit", "Shift+V", "flip vertically", () => Session.FlipSelection(leftToRight: false), Selected),
            new("Select nothing", "Edit", K("Esc", "C"), "deselect|clear selection", () => Session.Selection.Clear(), Selected),

            new("Show the plan alone", "View", "", "2d|top down|map only", () => Layout = MapLayout.Plan),
            new("Show the plan and the 3D view", "View", "", "split|side by side|both", () => Layout = MapLayout.Split),
            new("Show the 3D view alone", "View", K("Tab", "W"), "3d|visual mode|full screen|and back", () => Toggle3DCommand.Execute(null)),
            new("Show floors on the plan", "View", "", "floor textures|flats", () => IsFloors = true),
            new("Show ceilings on the plan", "View", "", "ceiling textures|flats", () => IsCeilings = true),
            new("Show heights as colours", "View", "", "floor heights|elevation", () => ShowHeights = !ShowHeights),
            new("Show only the walls", "View", "", "lines|automap|wireframe|linedefs", () => IsLines = true),
            new("Show the player's size", "View", K("G", ""), "fit|player fit|ghost|clearance", () => ShowFit = !ShowFit),
            new("Show the whole map", "View", "Home", "zoom to fit|fit map|overview|center in screen", () => FitMap?.Invoke()),
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
            new("Explain this map", "Panels", "", "about|summary|statistics|stats|key flow|monster count|what is in it|map analysis", () => ExplainMapCommand.Execute(null)),
            new("Problems", "Panels", K("", "F4"), "errors|map analysis|check|error checker", () => ShowProblems = !ShowProblems),
            new("Keep a version", "Panels", "", "snapshot|save a copy|backup", () => KeepVersionCommand.Execute(null)),

            new("Play", "Play", "F9", "test|test map|run|launch|gzdoom", () => PlayCommand.Execute(null), () => !Playing),
            new("Play from here", "Play", K("Shift+F9", "Ctrl+F9"), "test from current position|start here", () => PlayFromHereCommand.Execute(null), () => !Playing),
        };
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
            Room: F("Room", "Drag to draw a room. It snaps to the grid and to corners; drawn against or over other rooms, it shares their walls and never removes any", "Room tool"),
            Door: F("Door", "Click the wall between two rooms to make it a door that opens when used, with the game's door and track textures", "Door tool"),
            Window: F("Window", "Drag along a wall (or click it) for a window: through a thick wall to the room behind, or in a wall two rooms share, which becomes a wall closed but for the window", "Window tool"),
            Teleport: F("Teleport", "Click a room to step on, then the spot the player arrives at: stepping onto the room sends them there", "Teleport tool"),
            Light: F("Light", "Click in a room for a light, which the 3D view shows; its colour, reach and flicker are in the Inspector", "Light tool"),
            Stairs: F("Stairs", "Drag from the bottom of a staircase to its top: steps that climb from one floor to the other, never more than the player can step up", "Stairs tool"),
            Measure: F("Measure", "Drag a line to measure it: below the map, the floors and ceilings along it as a cross-section, with the player for scale and every step the player can't take", "Measure tool"),
            Objects: F("Objects", "The game's monsters, items and decorations. Drag one onto the map to place it", "Objects shelf"),
            Play: F("Play", "Plays this map in GZDoom. WadSmith checks it first and keeps a version; what the game says about it comes back to Problems. The arrow chooses the difficulty", "Play"),
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
            ? string.Create(CultureInfo.InvariantCulture, $"{d.Corners} corner{(d.Corners == 1 ? "" : "s")}{(d.Side > 0 ? $" · this side {d.Side:0}" : "")} · click the first corner to close, or double-click or press Enter to finish (a line from wall to wall across a room splits it); Shift keeps sides to 15° steps, Backspace takes a corner back, Esc lets go")
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
        if (value == Canvas.MapViewMode.Heights) Guide?.Happened(GuideEvents.Heights);
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
        var config = WadSmith.Core.Engines.EngineConfig.Prepare(data, Environment.GetEnvironmentVariable);
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
        if (engines.Count == 0)
        {
            Hint("GZDoom is not installed. Install it from your system's software centre or package manager (Arch: pacman -S gzdoom; Debian or Ubuntu: apt install gzdoom; any: flatpak install flathub org.zdoom.GZDoom), then press Play again. Editing works meanwhile.");
            return;
        }

        await Session.FlushAsync().ConfigureAwait(true);
        await Session.RefreshMapInfoAsync().ConfigureAwait(true);
        if (!force)
        {
            var (geometry, resources, look, family, fit) = (Session.Geometry, Session.Resources, Session.Look, Session.Engine, Session.FitBaseline);
            var blocking = await Task.Run(() => WadSmith.Editor.Checks.Problems.Check(geometry, resources, look, family, fit).Where(p => p.Severity == WadSmith.Editor.Checks.Severity.Error).ToList()).ConfigureAwait(true);
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
        var config = WadSmith.Core.Engines.EngineConfig.Prepare(data, Environment.GetEnvironmentVariable);
        var log = Path.Combine(data, "engine", "last-play.log");
        var (request, why) = WadSmith.Core.Engines.PlayRequests.For(Session.Project, Session.Slot, games, config, log, Skill + 1);
        if (request is null)
        {
            Hint(why);
            return;
        }

        _waitingFrom = null;
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
        Status = settings.CoachTips ? Keyed(Tips.FirstOrDefault(t => !settings.CoachSeen.Contains(t.Id)).Text ?? "") : "";
    }
}

/// <summary>A small card that celebrates a first (D33): what happened, in a sentence or two.</summary>
internal sealed record Celebration(string Heading, string Text);
