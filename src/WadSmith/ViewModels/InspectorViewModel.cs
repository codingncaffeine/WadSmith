using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using Avalonia.Media.Imaging;
using WadSmith.Core.Graphics;
using WadSmith.Editor.Smart;

namespace WadSmith.App.ViewModels;

/// <summary>One line of the Inspector: a friendly label, its classic name for the tooltip, and a value.</summary>
internal abstract partial class InspectorRow(string label, string tooltip) : ObservableObject
{
    public string Label { get; private set; } = label;

    public string Tooltip { get; private set; } = tooltip;

    /// <summary>Shows the label by the names chosen in Preferences, with the other name in the tooltip (D3).</summary>
    public void Name(Vocabulary words)
    {
        Tooltip = words.Tip(Label, Tooltip);
        Label = words.Label(Label);
    }

    /// <summary>Re-reads the value after any change to the map (an undo, an edit elsewhere).</summary>
    public abstract void Refresh();
}

/// <summary>One picture to choose from (a door's look): its name, its picture when it has arrived, and whether it is the one in use.</summary>
internal sealed partial class PictureChoice : ObservableObject
{
    private readonly Func<bool> _current;
    private readonly Action _choose;
    private bool _refreshing;

    public PictureChoice(string name, string tooltip, Func<bool> isCurrent, Action choose, Func<RgbaImage?> image, Action<Action> onUi)
    {
        Name = name;
        Tooltip = tooltip;
        _current = isCurrent;
        _choose = choose;
        Refresh();

        // The picture is rendered on a worker and shown when it arrives.
        _ = Task.Run(() =>
        {
            try
            {
                var bitmap = image() is { } i ? Imaging.ToBitmap(ShelfViewModel.Shrink(i)) : null;
                if (bitmap is not null) onUi(() => Picture = bitmap);
            }
            catch (Exception ex) when (ex is WadSmith.Core.CorruptDataException or IOException or InvalidOperationException)
            {
                WadSmith.Editor.App.Log.Warn($"The picture {name} could not be shown: {ex.Message}");
            }
        });
    }

    public string Name { get; }

    public string Tooltip { get; }

    [ObservableProperty]
    private Bitmap? _picture;

    [ObservableProperty]
    private bool _isCurrent;

    partial void OnIsCurrentChanged(bool value)
    {
        if (value && !_refreshing) _choose();
    }

    public void Refresh()
    {
        _refreshing = true;
        IsCurrent = _current();
        _refreshing = false;
    }
}

/// <summary>A choice among pictures (a door's look), shown as a strip of them.</summary>
internal sealed class PictureChoiceRow(string label, string tooltip, IReadOnlyList<PictureChoice> choices) : InspectorRow(label, tooltip)
{
    public IReadOnlyList<PictureChoice> Choices { get; } = choices;

    public override void Refresh()
    {
        foreach (var c in Choices) c.Refresh();
    }
}

/// <summary>A number the user can type or step; every change is one undoable edit.</summary>
internal sealed partial class NumberRow : InspectorRow
{
    private readonly Func<decimal> _get;
    private readonly Action<decimal> _set;
    private readonly Func<string>? _hint;
    private bool _refreshing;

    public NumberRow(string label, string tooltip, Func<decimal> get, Action<decimal> set, decimal step, decimal min = -32768, decimal max = 32767, Func<string>? hint = null, IReadOnlyList<PresetChoice>? presets = null)
        : base(label, tooltip)
    {
        _get = get;
        _set = set;
        _hint = hint;
        Step = step;
        Minimum = min;
        Maximum = max;
        Presets = presets ?? [];
        Refresh();
    }

    /// <summary>The named settings beside the number (D27), in order; none for most numbers.</summary>
    public IReadOnlyList<PresetChoice> Presets { get; }

    public bool HasPresets => Presets.Count > 0;

    public decimal Step { get; }

    public decimal Minimum { get; }

    public decimal Maximum { get; }

    [ObservableProperty]
    private decimal? _value;

    [ObservableProperty]
    private string _hintText = "";

    partial void OnValueChanged(decimal? value)
    {
        if (_refreshing || value is not { } v || v == _get()) return;
        _set(v);
    }

    public override void Refresh()
    {
        _refreshing = true;
        Value = _get();
        HintText = _hint?.Invoke() ?? "";
        foreach (var p in Presets) p.Refresh();
        _refreshing = false;
    }
}

/// <summary>
/// One preset beside a number (D27): a word and a small picture; lit while the number is the preset's, and choosing it
/// sets the number in one undoable edit.
/// </summary>
internal sealed partial class PresetChoice : ObservableObject
{
    private readonly Func<bool> _current;
    private readonly Action _choose;
    private bool _refreshing;

    public PresetChoice(Preset preset, string tooltip, Func<bool> isCurrent, Action choose, bool first, bool last, double pictureHeight = 0, IBrush? swatch = null)
    {
        Preset = preset;
        Tooltip = tooltip;
        _current = isCurrent;
        _choose = choose;
        IsFirst = first;
        IsLast = last;
        PictureHeight = pictureHeight;
        Swatch = swatch;
        Refresh();
    }

    public Preset Preset { get; }

    public string Name => Preset.Name;

    public string Tooltip { get; }

    public bool IsFirst { get; }

    public bool IsLast { get; }

    /// <summary>A room height's picture: a bar as tall as the room, against the tallest preset's.</summary>
    public double PictureHeight { get; }

    public bool HasBar => PictureHeight > 0;

    /// <summary>A brightness's picture: the grey the room's light gives.</summary>
    public IBrush? Swatch { get; }

    public bool HasSwatch => Swatch is not null;

    [ObservableProperty]
    private bool _isCurrent;

    partial void OnIsCurrentChanged(bool value)
    {
        if (_refreshing) return;
        if (value) _choose();
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        IsCurrent = _current();
        _refreshing = false;
    }

    /// <summary>The presets of <paramref name="presets"/> as choices, each set by <paramref name="set"/> and lit while <paramref name="now"/> is its value.</summary>
    public static IReadOnlyList<PresetChoice> For(IReadOnlyList<Preset> presets, Func<double> now, Action<double> set, Func<Preset, string> tooltip, bool bars = false, bool swatches = false)
    {
        var tallest = presets.Max(p => p.Value);
        return [.. presets.Select((p, i) => new PresetChoice(p, tooltip(p), () => now() == p.Value, () => set(p.Value), i == 0, i == presets.Count - 1,
            bars ? Math.Max(3, 16 * p.Value / tallest) : 0,
            swatches ? new SolidColorBrush(Color.FromRgb((byte)p.Value, (byte)p.Value, (byte)p.Value)) : null))];
    }
}

/// <summary>A name (a texture) the user can type; committed when the field loses focus or Enter is pressed.</summary>
internal sealed partial class TextRow : InspectorRow
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Func<string>? _hint;

    /// <summary>Whether it holds a texture's name (in capitals, "-" for none) rather than words as typed.</summary>
    private readonly bool _texture;

    public TextRow(string label, string tooltip, Func<string> get, Action<string> set, Func<string>? hint = null, bool texture = true)
        : base(label, tooltip)
    {
        _get = get;
        _set = set;
        _hint = hint;
        _texture = texture;
        Refresh();
    }

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _hintText = "";

    /// <summary>Called by the view when editing ends.</summary>
    public void Commit()
    {
        var v = _texture ? Value.Trim().ToUpperInvariant() : Value.Trim();
        if (_texture && v.Length == 0) v = "-";
        if (v != _get()) _set(v);
        Value = _get();
    }

    public override void Refresh()
    {
        Value = _get();
        HintText = _hint?.Invoke() ?? "";
    }
}

/// <summary>An on/off setting.</summary>
internal sealed partial class FlagRow : InspectorRow
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private bool _refreshing;

    public FlagRow(string label, string tooltip, Func<bool> get, Action<bool> set)
        : base(label, tooltip)
    {
        _get = get;
        _set = set;
        Refresh();
    }

    [ObservableProperty]
    private bool _value;

    partial void OnValueChanged(bool value)
    {
        if (!_refreshing && value != _get()) _set(value);
    }

    public override void Refresh()
    {
        _refreshing = true;
        Value = _get();
        _refreshing = false;
    }
}

/// <summary>One of a few named choices; every change is one undoable edit.</summary>
internal sealed partial class ChoiceRow : InspectorRow
{
    private readonly Func<int> _get;
    private readonly Action<int> _set;
    private bool _refreshing;

    public ChoiceRow(string label, string tooltip, IReadOnlyList<string> options, Func<int> get, Action<int> set)
        : base(label, tooltip)
    {
        Options = options;
        _get = get;
        _set = set;
        Refresh();
    }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    private int _value;

    partial void OnValueChanged(int value)
    {
        if (!_refreshing && value >= 0 && value != _get()) _set(value);
    }

    public override void Refresh()
    {
        _refreshing = true;
        Value = _get();
        _refreshing = false;
    }
}

/// <summary>A heading that starts a group of rows.</summary>
internal sealed class HeadingRow(string label) : InspectorRow(label, "")
{
    public override void Refresh()
    {
    }
}

/// <summary>A button that does one thing, named by its verb.</summary>
internal sealed class ActionRow(string label, string tooltip, Action act) : InspectorRow(label, tooltip)
{
    public IRelayCommand Command { get; } = new RelayCommand(act);

    public override void Refresh()
    {
    }
}

/// <summary>Something to read, not to change here.</summary>
internal sealed partial class InfoRow(string label, string tooltip, Func<string> get) : InspectorRow(label, tooltip)
{
    [ObservableProperty]
    private string _value = get();

    public override void Refresh() => Value = get();
}

/// <summary>
/// The Inspector: what the selection is, in plain words, and its settings. Labels use the friendly
/// words of the glossary; each tooltip names the classic term as well (D3).
/// </summary>
internal sealed partial class InspectorViewModel : ObservableObject
{
    private const double PlayerHeight = 56;
    private readonly Vocabulary _words;
    private readonly EditorSession _session;
    private Func<string>? _summaryOf;
    private string _kind = "";

    /// <summary>Raised when the user asks to make a room a teleport pad: the editor then waits for the arrival spot.</summary>
    public event Action<Sector>? TeleportFrom;

    /// <summary>Raised when the user asks for a switch that works a door or a lift: the editor then waits for the wall.</summary>
    public event Action<Sector>? SwitchFor;

    /// <summary>Raised by "Explain this map" (D32), shown with nothing selected.</summary>
    public event Action? ExplainMap;

    /// <summary>Raised when the user asks to place the key for a locked door (its colour): the editor then waits for the spot.</summary>
    public event Action<string>? KeyFor;

    /// <summary>Raised when the user asks for new rooms to look like a room.</summary>
    public event Action<Sector>? LookLike;

    /// <summary>The door a wall is a face of (one of the door's own walls, used to open it), or null: a click on the plan lands on a door's face, and the door is what the user means.</summary>
    private Sector? DoorOf(Line l)
    {
        foreach (var side in new[] { l.Back, l.Front })
        {
            if (side is null) continue;
            if (Doors.Recognise(_session.Geometry, side.Sector) is { } door && door.Own.Contains(l)) return side.Sector;
        }

        return null;
    }

    /// <summary>The lift a wall is a trigger of (one of the lift's own walls, used to send it down), or null: as with a door, a click on the plan lands on the wall.</summary>
    private Sector? LiftOf(Line l)
    {
        foreach (var side in new[] { l.Back, l.Front })
        {
            if (side is null) continue;
            if (Lifts.Recognise(_session.Geometry, side.Sector) is { } lift && lift.Triggers.Contains(l)) return side.Sector;
        }

        return null;
    }

    /// <summary>The window a wall belongs to (one side of it is the window's area), or null.</summary>
    private Sector? WindowOf(Line l)
    {
        foreach (var side in new[] { l.Back, l.Front })
        {
            if (side is not null && WallWindows.Recognise(_session.Geometry, side.Sector) is not null) return side.Sector;
        }

        return null;
    }

    /// <summary>What the selection is, as far as the Inspector's layout goes: a door, an exit, a wall, a room…</summary>
    private string KindOf() => _session.Selection.Items switch
    {
        [Sector s] when Platforms.ControlOf(_session.Geometry, s) is not null => "platform control",
        [Sector s] => (Doors.Recognise(_session.Geometry, s) is { } door ? $"door {door.Settings.Lock}" : Lifts.Recognise(_session.Geometry, s) is not null ? "lift" : WallWindows.Recognise(_session.Geometry, s) is not null ? "window" : Secrets.IsSecret(s) ? "secret" : StairsFinder.Recognise(_session.Geometry, s) is { } flight ? $"stairs {flight.Count}" : Rooms.Island(_session.Geometry, s) is not null ? "island" : "room") + $" {Platforms.Of(_session.Geometry, s).Count}" + ClimbKind(s),
        [Line l] => Exits.IsExit(l) ? "exit" : Teleports.IsTeleport(l) ? "teleporter" : LiftOf(l) is not null ? "lift face" : Switches.Recognise(_session.Geometry, l) is not null ? "switch" : DoorOf(l) is { } doorArea ? $"door {Doors.Recognise(_session.Geometry, doorArea)?.Settings.Lock} face" : WindowOf(l) is not null ? "window face" : l.Special == 0 ? "wall" : "wall with an action",
        [Thing t] => Teleports.IsArrival(t) ? "arrival" : Lights.Kind(t.Type) is { } kind ? $"light {kind}" : $"thing {t.Type}",
        [Side { Line: { } l }] => Exits.IsExit(l) ? "exit face" : "face",
        [var one] => one.GetType().Name,
        var many => many.Count.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The climb a room is (D27) as far as its rows go: the floors it joins and whether steps and a ramp are offered.</summary>
    private string ClimbKind(Sector s) => Climbs.Find(_session.Geometry, s) is { } c
        ? string.Create(CultureInfo.InvariantCulture, $" climb {c.Low.FloorHeight} {c.High.FloorHeight} {Climbs.StairsRun(_session.Geometry, c) is not null} {c.Rise / c.Length <= Climbs.SteepestRamp}")
        : "";

    public InspectorViewModel(EditorSession session, Vocabulary? words = null)
    {
        _session = session;
        _words = words ?? Vocabulary.Load();
        _groupsFound = new(session.OnUiThread, r =>
        {
            if (!ReferenceEquals(r.Session, _session)) return;
            var before = string.Join("|", _groups.Select(g => $"{g.Name}{g.Locked}{g.Rooms.Count}{g.Things.Count}"));
            _groups = r.Groups;
            if (_session.Selection.IsEmpty && before != string.Join("|", _groups.Select(g => $"{g.Name}{g.Locked}{g.Rooms.Count}{g.Things.Count}"))) Rebuild();
        }, "the map's groups");
        session.GeometryChanged += FindGroups;
        FindGroups();
        session.Selection.Changed += Rebuild;
        session.MapSettingsChanged += () =>
        {
            if (!_session.Selection.IsEmpty) return;
            // The game's defaults arriving (or a value not on the lists) change the choices; a change of value only refreshes the rows.
            if (MapSettingsKey() != _mapSettingsKey) Rebuild();
            else foreach (var r in Rows) r.Refresh();
        };

        // A room just made (a door a moment ago) is known as what it is once the map's shapes catch up.
        session.GeometryChanged += () =>
        {
            if (KindOf() != _kind)
            {
                Rebuild();
                return;
            }

            // What the shapes tell (where a teleporter sends, what opens a door) is read again from the new ones.
            foreach (var r in Rows) r.Refresh();
            if (_summaryOf is { } summary) Summary = summary();
        };
        session.Map.Changed += _ =>
        {
            // A change can make the selection something else (a wall an exit): then the rows are rebuilt.
            if (KindOf() != _kind)
            {
                Rebuild();
                return;
            }

            foreach (var r in Rows) r.Refresh();
            if (_summaryOf is { } summary) Summary = summary();
        };
        Rebuild();
    }

    private readonly LatestOnWorker<(EditorSession Session, IReadOnlyList<MapGroup> Groups)> _groupsFound;
    private IReadOnlyList<MapGroup> _groups = [];

    /// <summary>The map's groups, read from its shapes on a worker, for the overview shown when nothing is selected.</summary>
    private void FindGroups()
    {
        var session = _session;
        var frame = session.Geometry.Frame;
        _groupsFound.Request(() => (session, Grouping.In(frame)));
    }

    public ObservableCollection<InspectorRow> Rows { get; } = [];

    /// <summary>Asks the user for a 360° picture (set by the view); null when they chose none.</summary>
    public Func<Task<string?>>? PickPanorama { get; set; }

    /// <summary>Asks the user for a Wolfenstein 3D MAPHEAD file (set by the view); null when they chose none.</summary>
    public Func<Task<string?>>? PickGrid { get; set; }

    /// <summary>The grid map file picked and its levels, while the user chooses one; null otherwise.</summary>
    private (string Path, IReadOnlyList<string> Levels)? _grid;

    private int _gridLevel;

    /// <summary>"Bring in a Wolfenstein 3D level…": the file picker, its levels read on a worker, then the level box in the map's settings.</summary>
    private async Task PickGridAsync()
    {
        if (PickGrid is null || await PickGrid().ConfigureAwait(true) is not { } path) return;
        var levels = await _session.GridLevelsAsync(path).ConfigureAwait(true);
        if (levels.Count == 0) return;
        (_grid, _gridLevel) = ((path, levels), 0);
        Rebuild();
    }

    /// <summary>"Make a sky from a 360° picture…": the file picker, then the sky made on a worker and set as the map's.</summary>
    private async Task MakeSkyAsync()
    {
        if (PickPanorama is null || await PickPanorama().ConfigureAwait(true) is not { } path) return;
        _session.ImportSky(path);
    }

    [ObservableProperty]
    private string _heading = "";

    [ObservableProperty]
    private string _summary = "";

    /// <summary>Builds the rows for the selection, then names them as Preferences say (D3).</summary>
    internal void Rebuild()
    {
        BuildRows();
        HeadingTip = _words.Other(Heading) is null ? null : _words.Tip(Heading, "");
        Heading = _words.Label(Heading);
        foreach (var row in Rows) row.Name(_words);
    }

    /// <summary>The heading's other name, as its tooltip.</summary>
    [ObservableProperty]
    private string? _headingTip;

    private void BuildRows()
    {
        Rows.Clear();
        _summaryOf = null;
        _kind = KindOf();
        var items = _session.Selection.Items;
        if (items.Count == 0)
        {
            Heading = "Nothing selected";
            Summary = "Click a room, a wall, a corner or a thing to see what it is and change it.";
            Rows.Add(new ActionRow("Explain this map", "What the map is made of, which keys open which doors, and what the player meets on each difficulty, each with a button that shows where.", () => ExplainMap?.Invoke()));
            AddMapSettings();
            AddGroups();
            return;
        }

        if (items.Count > 1)
        {
            var kinds = items.Select(Name).Distinct().ToList();
            Heading = kinds.Count == 1 ? $"{items.Count} {kinds[0].ToLowerInvariant()}s selected" : $"{items.Count} things selected";
            Summary = string.Join(", ", items.GroupBy(i => Name(i)).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}{(g.Count() > 1 ? "s" : "")}"));
            Rows.Add(new ActionRow("Save as a prefab", "Keeps a copy of the selected rooms and things in this project, under Objects, Prefabs, to drag onto any of its maps.", _session.SavePrefab));
            if (items.All(i => i is Sector)) Rows.Add(new ActionRow("Join them into one room", "The rooms become the first one you selected: the walls between them go, and its floor, ceiling and light hold for all of it (J).", _session.JoinRooms));
            if (items.OfType<Sector>().Where(r => r.OnMap).ToList() is { Count: >= 2 } rooms) AddHeights(rooms);
            AddShaping();
            if (items.OfType<Thing>().Where(t => t.OnMap).ToList() is { Count: >= 2 } things) AddArranging(things);
            AddLikeIt(null);
            AddGrouping(items);
            Rows.Add(new ActionRow("Delete them", "Deletes everything selected so the map stays whole (Delete).", _session.DeleteSelection));
            return;
        }

        switch (items[0])
        {
            case Sector s when Platforms.ControlOf(_session.Geometry, s) is { } held:
                Heading = held.Kind == PlatformKind.Water ? "Water's control room" : "Platform's control room";
                Summary = "This little room outside the map is where the game reads a platform from: its floor is the platform's bottom, its ceiling the top. Change the platform here or in the room it stands in.";
                AddPlatform(held, control: true);
                break;
            case Sector s when Doors.Recognise(_session.Geometry, s) is not null:
                Heading = "Door";
                _summaryOf = () => Doors.Recognise(_session.Geometry, s) is { } d ? Explaining.Describe(d) : "No longer a door.";
                Summary = _summaryOf();
                AddDoor(s);
                AddDoorKey(s);
                Rows.Add(new ActionRow("Add a switch that opens it", "Click a solid wall next: a piece of it becomes a switch the player uses to open this door from there (the door gets a tag; its own walls still open it).", () => SwitchFor?.Invoke(s)));
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s when Lifts.Recognise(_session.Geometry, s) is not null:
                Heading = "Lift";
                _summaryOf = () => Lifts.Recognise(_session.Geometry, s) is { } lift ? Explaining.Describe(lift) : "No longer a lift.";
                Summary = _summaryOf();
                AddLift(s);
                Rows.Add(new ActionRow("Add a switch that lowers it", "Click a solid wall next: a piece of it becomes a switch the player uses to send this lift down from there.", () => SwitchFor?.Invoke(s)));
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s when WallWindows.Recognise(_session.Geometry, s) is not null:
                Heading = "Window";
                _summaryOf = () => WallWindows.Recognise(_session.Geometry, s) is { } window ? Explaining.Describe(window) : "No longer a window: the player can climb into it, or it is closed.";
                Summary = _summaryOf();
                AddWindow(s);
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s when StairsFinder.Recognise(_session.Geometry, s) is not null:
                Heading = "Stairs";
                _summaryOf = () => StairsFinder.Recognise(_session.Geometry, s) is { } st ? Explaining.Describe(st, s) : "No longer stairs.";
                Summary = _summaryOf();
                AddStairs(s);
                Rows.Add(new HeadingRow("THIS STEP"));
                AddSector(s);
                break;
            case Sector s when Secrets.IsSecret(s):
                Heading = "Secret area";
                _summaryOf = () => Secrets.Recognise(_session.Geometry, s) is { } secret ? Explaining.Describe(secret) : "No longer a secret.";
                Summary = _summaryOf();
                AddSecret(s);
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s:
                Heading = "Room area";
                Summary = "Its floor, ceiling and light.";
                AddSector(s);
                AddPlatforms(s);
                Rows.Add(new ActionRow("Fit the floor picture to this room", "Sizes the floor's picture so one copy covers the room corner to corner, its top to the north (UDMF floor scale and panning).", () => _session.FitFlat(s, ceiling: false)));
                Rows.Add(new ActionRow("Fit the ceiling picture to this room", "Sizes the ceiling's picture so one copy covers the room corner to corner (UDMF ceiling scale and panning).", () => _session.FitFlat(s, ceiling: true)));
                Rows.Add(new ActionRow("Swap the floor and ceiling pictures", "The floor takes the ceiling's picture and the ceiling the floor's, with how each is shifted, turned and lit (swap flats).", _session.SwapFlats));
                Rows.Add(new ActionRow("Make it a secret", "Finding it counts toward the secrets at the end of the map, and the walls into it show as solid walls on the in-game map so they do not give it away.", () => _session.MakeSecret(s)));
                Rows.Add(new ActionRow("Save as a prefab", "Keeps a copy of this room, its walls and the things in it in this project, under Objects, Prefabs, to drag onto any of its maps.", _session.SavePrefab));
                Rows.Add(new ActionRow("Draw new rooms like this one", "New rooms take its floor and ceiling pictures, heights, light and the texture of its walls (the Look in the Textures panel).", () => LookLike?.Invoke(s)));
                if (Rooms.Island(_session.Geometry, s) is not null)
                {
                    Rows.Add(new HeadingRow("INSIDE ANOTHER ROOM"));
                    Rows.Add(new ActionRow("Make it a pillar", "It becomes solid: its walls turn to face the room round it.", () => _session.MakeIsland(s, EditorSession.IslandKind.Pillar)));
                    Rows.Add(new ActionRow("Make it a pool", "Its floor goes 16 below the room round it, with the game's water on it.", () => _session.MakeIsland(s, EditorSession.IslandKind.Pool)));
                    Rows.Add(new ActionRow("Make it a platform", "Its floor rises 24 above the room round it, a step the player takes.", () => _session.MakeIsland(s, EditorSession.IslandKind.Platform)));
                }
                Rows.Add(new ActionRow("Make it a lift", "Its floor goes down to the lowest floor beside it when the player uses a wall from below or walks onto it, waits 3 seconds and comes back up (Plat_DownWaitUpStayLip).", () => _session.MakeLift(s)));
                Rows.Add(new ActionRow("Make it a teleport pad", "Stepping onto this room sends the player to a spot you click next (Teleport, with an arrival spot).", () => TeleportFrom?.Invoke(s)));
                break;
            case Line l when LiftOf(l) is { } liftArea:
                // A wall of a lift: the lift is what was meant, with the wall's own details below it.
                Heading = "Lift";
                _summaryOf = () => Lifts.Recognise(_session.Geometry, liftArea) is { } lift ? Explaining.Describe(lift) : "No longer a lift.";
                Summary = _summaryOf();
                AddLift(liftArea);
                Rows.Add(new ActionRow("Add a switch that lowers it", "Click a solid wall next: a piece of it becomes a switch the player uses to send this lift down from there.", () => SwitchFor?.Invoke(liftArea)));
                Rows.Add(new ActionRow("Select the lift's area", "Selects the lift itself, with its area's settings.", () => _session.Selection.Set(liftArea)));
                Rows.Add(new HeadingRow("THIS WALL"));
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } liftBack) AddSide("Back", liftBack);
                break;
            case Line l when WindowOf(l) is { } windowArea:
                // A wall of a window: the window is what was meant, with the wall's own details below it.
                Heading = "Window";
                _summaryOf = () => WallWindows.Recognise(_session.Geometry, windowArea) is { } w ? Explaining.Describe(w) : "No longer a window.";
                Summary = _summaryOf();
                AddWindow(windowArea);
                Rows.Add(new ActionRow("Select the window's area", "Selects the window itself, with its area's settings.", () => _session.Selection.Set(windowArea)));
                Rows.Add(new HeadingRow("THIS WALL"));
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } windowBack) AddSide("Back", windowBack);
                break;
            case Line l when Switches.Recognise(_session.Geometry, l) is not null:
                Heading = "Switch";
                _summaryOf = () => Switches.Recognise(_session.Geometry, l) is { } sw ? Explaining.Describe(sw) : "No longer a switch.";
                Summary = _summaryOf();
                AddSwitch(l);
                Rows.Add(new HeadingRow("THE WALL"));
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } switchBack) AddSide("Back", switchBack);
                break;
            case Line l when Teleports.IsTeleport(l):
                Heading = "Teleporter";
                _summaryOf = () => Teleports.Recognise(_session.Geometry, l) is { } t ? Explaining.Describe(t) : "No longer a teleporter.";
                Summary = _summaryOf();
                AddTeleporter(l);
                Rows.Add(new HeadingRow("THE WALL"));
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } teleBack) AddSide("Back", teleBack);
                break;
            case Thing t when Teleports.IsArrival(t):
                Heading = "Teleport arrival";
                _summaryOf = () =>
                {
                    var n = Teleports.SendersTo(_session.Geometry, t).Count;
                    return $"Whoever a teleporter sends here arrives facing {Compass(t.Angle)}; {(n == 0 ? "no wall sends here yet" : n == 1 ? "1 wall sends here" : $"{n} walls send here")}.";
                };
                Summary = _summaryOf();
                Rows.Add(new ActionRow("Select the walls that send here", "Selects every wall that teleports to this spot.", () =>
                {
                    if (Teleports.SendersTo(_session.Geometry, t) is { Count: > 0 } senders) _session.Selection.Set([.. senders]);
                }));
                AddThing(t);
                break;
            case Line l when DoorOf(l) is { } doorArea:
                // A face of a door: the door is what was meant, with the face's own details below it.
                Heading = "Door";
                _summaryOf = () => Doors.Recognise(_session.Geometry, doorArea) is { } d ? Explaining.Describe(d) : "No longer a door.";
                Summary = _summaryOf();
                AddDoor(doorArea);
                AddDoorKey(doorArea);
                Rows.Add(new ActionRow("Add a switch that opens it", "Click a solid wall next: a piece of it becomes a switch the player uses to open this door from there (the door gets a tag; its own walls still open it).", () => SwitchFor?.Invoke(doorArea)));
                Rows.Add(new ActionRow("Select the door's area", "Selects the door itself, with its area's settings.", () => _session.Selection.Set(doorArea)));
                Rows.Add(new HeadingRow("THIS FACE"));
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } doorBack) AddSide("Back", doorBack);
                break;
            case Line l when Exits.IsExit(l):
                Heading = l.Flag("playeruse") ? "Exit switch" : l.Flag("playercross") ? "Exit line" : "Exit";
                _summaryOf = () => Exits.IsExit(l) ? $"Ends the map when the player {Exits.How(l)}, going on to {(l.Special == Exits.Secret ? "the secret map" : "the next map")}." : "No longer an exit.";
                Summary = _summaryOf();
                AddExit(l);
                Rows.Add(new HeadingRow("THE WALL"));
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } exitBack) AddSide("Back", exitBack);
                break;
            case Line l:
                Heading = l.Back is null ? "Solid wall" : "Opening between two rooms";
                Summary = l.Back is null ? "Nothing can pass through it." : "Players can walk through unless it blocks them.";
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } back) AddSide("Back", back);
                Rows.Add(new ActionRow(l.Back is null ? "Select its face" : "Select the front face", "Shows only the side you see from the front: its textures and how they line up.", () => _session.Selection.Set(l.Front)));
                if (l.Back is { } behind) Rows.Add(new ActionRow("Select the back face", "Shows only the side you see from the back: its textures and how they line up.", () => _session.Selection.Set(behind)));
                if (l.Special == 0)
                {
                    Rows.Add(l.Back is null
                        ? new ActionRow("Make it the exit switch", "The exit switch picture is set into this wall where you clicked it (its middle when it was not clicked): a piece as wide as the picture, in an alcove as tall as the picture with the wall above it when the room is taller; the rest stays wall. The player uses the switch to end the map (Exit_Normal).", () => _session.MakeExit(l, false, _session.Selection.At))
                        : new ActionRow("End the map when walked across", "Crossing this wall ends the map and goes on to the next one (Exit_Normal).", () => _session.MakeExit(l, false)));
                }

                break;
            case Side side:
                var solid = side.Line?.Back is null;
                Heading = "Wall face";
                Summary = solid ? "The side of a solid wall you see from its room: its texture and how it lines up." : "One side of a wall between two areas, as you see it from this side: its textures and how they line up.";
                AddFace(side);
                Rows.Add(new ActionRow("Fit the picture to this wall", "Sizes the picture so one copy fills this face, not repeated or cut: for a poster, a screen or an animated picture (UDMF scale on this side; the wall's own size is unchanged).", () => _session.FitWall(side)));
                if (side.Line is { } wall)
                {
                    Rows.Add(new HeadingRow("THE WHOLE WALL"));
                    AddLine(wall);
                    Rows.Add(new ActionRow("Select the whole wall", "Selects the wall with both its faces, as clicking it on the map does.", () => _session.Selection.Set(wall)));
                    if (wall.Opposite(side) is { } other) Rows.Add(new ActionRow("Select the other face", "Shows the side of this wall you see from the other area.", () => _session.Selection.Set(other)));
                }

                break;
            case Thing t when Lights.Recognise(t) is not null:
                Heading = "Light";
                _summaryOf = () => Lights.Recognise(t) is { } light ? Explaining.Describe(light) : "No longer a light.";
                Summary = _summaryOf();
                AddLight(t);
                break;
            case Thing t when Keys.ColourOf(t.Type, _session.Engine) is { } colour:
                Heading = _session.Resources?.Things.Find(t.Type)?.Name ?? $"The {colour} key";
                _summaryOf = () => Keys.DoorsOpenedBy(_session.Geometry, t.Type, _session.Engine).Select(l => l.Args[0] != 0 ? (object)l.Args[0] : l.Back?.Sector ?? (object)l).Distinct().Count() switch
                {
                    0 => $"No door is locked with the {colour} key yet: lock one in its Inspector (Locked with).",
                    1 => $"Opens the door locked with the {colour} key.",
                    var n => $"Opens the {n} doors locked with the {colour} key.",
                };
                Summary = _summaryOf();
                Rows.Add(new ActionRow("Select the doors it opens", "Selects every wall whose door this key opens.", () =>
                {
                    if (Keys.DoorsOpenedBy(_session.Geometry, t.Type, _session.Engine) is { Count: > 0 } doors) _session.Selection.Set([.. doors]);
                }));
                AddThing(t);
                break;
            case Thing t:
                var known = _session.Resources?.Things.Find(t.Type);
                Heading = known?.Name ?? (t.Type is >= 1 and <= 4 ? $"Player {t.Type} start" : "Thing");
                Summary = t.Type is >= 1 and <= 4 or 11
                    ? "Where the player appears."
                    : known is null ? $"Editor number {t.Type}; the game does not define it." : $"Editor number {t.Type}, {known.Radius:0} units across the middle and {known.Height:0} tall.";
                AddThing(t);
                break;
            case Vertex v:
                Heading = "Corner";
                Summary = "Where walls meet.";
                Rows.Add(new NumberRow("X", "Position east–west, in map units.", () => (decimal)v.X, x => Set(v, "Move corner", e => e.X, (e, val) => e.X = val, (double)x), 8));
                Rows.Add(new NumberRow("Y", "Position north–south, in map units.", () => (decimal)v.Y, y => Set(v, "Move corner", e => e.Y, (e, val) => e.Y = val, (double)y), 8));
                if (Rounding.WallsAt(_session.Geometry, v).Count == 2) AddRounding(v);
                break;
        }

        if (items[0] is Sector) AddShaping();
        AddGrouping(items);
        AddLikeIt(items[0]);

        // Last: deleting it, in words that say what happens to the rest (Delete does the same).
        var (verb, tip) = items[0] switch
        {
            Sector => ("Delete this room", "The room and the things in it go; the walls it shared become its neighbours' solid walls (Delete)."),
            Line { Back: not null } or Side { Line.Back: not null } => ("Delete this wall", "The wall goes and the rooms on its two sides become one room (Delete)."),
            Thing => ("Delete this thing", "Takes it off the map (Delete)."),
            Vertex => ("Delete this corner", "Where two walls meet, they become one straight wall (Delete)."),
            _ => ((string?)null, ""),
        };
        if (verb is not null) Rows.Add(new ActionRow(verb, tip, _session.DeleteSelection));
    }

    /// <summary>Turning, resizing and mirroring the selection; the handles round it on the map do the same by dragging.</summary>
    private void AddShaping()
    {
        Rows.Add(new HeadingRow("TURN AND MIRROR"));
        Rows.Add(new NumberRow("Turn by (°)", "Turns the selection round its middle, counterclockwise; a negative angle turns it clockwise. The knob above it on the map turns it by dragging.", () => 0, a => _session.TurnSelection((double)a), 15, -360, 360));
        Rows.Add(new NumberRow("Resize (%)", "Resizes the selection round its middle; the handles round it on the map resize it by dragging, from the opposite side (Ctrl: from the middle).", () => 100, p => _session.ResizeSelection((double)p / 100), 25, 1, 1000));
        Rows.Add(new ActionRow("Turn left 90°", "A quarter turn counterclockwise round its middle (Ctrl+[).", () => _session.TurnSelection(90)));
        Rows.Add(new ActionRow("Turn right 90°", "A quarter turn clockwise round its middle (Ctrl+]).", () => _session.TurnSelection(-90)));
        Rows.Add(new ActionRow("Mirror left to right", "Flips it in place, east to west (Shift+H). Walls keep facing their rooms, and things turn to face what they faced.", () => _session.FlipSelection(leftToRight: true)));
        Rows.Add(new ActionRow("Mirror top to bottom", "Flips it in place, north to south (Shift+V). Walls keep facing their rooms, and things turn to face what they faced.", () => _session.FlipSelection(leftToRight: false)));
    }

    private static string Name(Element e) => e switch
    {
        Sector => "Area",
        Line => "Wall",
        Side => "Wall face",
        Thing => "Thing",
        Vertex => "Corner",
        _ => "Item",
    };

    private void Set<TElement, TValue>(TElement element, string label, Func<TElement, TValue> get, Action<TElement, TValue> set, TValue value)
        where TElement : Element => _session.History.Run(new SetProperty<TElement, TValue>(element, label, get, set, value));

    private static string HeightTip(Preset p) => string.Create(CultureInfo.InvariantCulture, $"{p.Name}: {p.Words}. Sets the ceiling {p.Value:0} above the floor ({p.Value / PlayerHeight:0.#} players).");

    private static string LightTip(Preset p) => string.Create(CultureInfo.InvariantCulture, $"{p.Name}: {p.Words}. Sets the brightness to {p.Value:0} of 255.");

    private void AddSector(Sector s)
    {
        string Players() => string.Create(CultureInfo.InvariantCulture, $"room is {s.CeilingHeight - s.FloorHeight:0} tall ≈ {(s.CeilingHeight - s.FloorHeight) / PlayerHeight:0.#} players");

        // A ledge the player cannot climb comes first: what to do about it is the room's most useful offer.
        AddClimb(s);
        Rows.Add(new NumberRow("Floor height", "How high the floor is, in map units (heightfloor). The player steps up 24 at most.", () => (decimal)s.FloorHeight, v => Set(s, "Change floor height", e => e.FloorHeight, (e, x) => e.FloorHeight = x, (double)v), 8));
        Rows.Add(new NumberRow("Ceiling height", "How high the ceiling is (heightceiling). The player is 56 tall.", () => (decimal)s.CeilingHeight, v => Set(s, "Change ceiling height", e => e.CeilingHeight, (e, x) => e.CeilingHeight = x, (double)v), 8, hint: Players,
            presets: PresetChoice.For(Presets.RoomHeights, () => s.CeilingHeight - s.FloorHeight, h => Set(s, "Make the room " + Presets.Named(Presets.RoomHeights, h)!.Name.ToLowerInvariant(), e => e.CeilingHeight, (e, x) => e.CeilingHeight = x, s.FloorHeight + h), HeightTip, bars: true)));
        Rows.Add(new NumberRow("Brightness", "0 is dark, 255 full light (lightlevel). Doom's rooms are usually around 160.", () => s.Light, v => Set(s, "Change brightness", e => e.Light, (e, x) => e.Light = x, (int)v), 16, 0, 255,
            presets: PresetChoice.For(Presets.Brightness, () => s.Light, l => Set(s, "Make the room " + Presets.Named(Presets.Brightness, l)!.Name.ToLowerInvariant(), e => e.Light, (e, x) => e.Light = x, (int)l), LightTip, swatches: true)));
        Rows.Add(new TextRow("Light colour", "Tints everything in the room (lightcolor), as #RRGGBB in a paint program's words; #FFFFFF is plain white light. The 3D view shows it as the game does.", () => Lights.Hex(RoomColour(s, "lightcolor", 0xFFFFFF)), text => SetRoomColour(s, "lightcolor", text, 0xFFFFFF, "Change the light colour"), texture: false));
        Rows.Add(new TextRow("Fog colour", "Fog that thickens with distance in the room, thicker the darker the room (fadecolor), as #RRGGBB; #000000 is none. The 3D view shows it as the game does.", () => Lights.Hex(RoomColour(s, "fadecolor", 0)), text => SetRoomColour(s, "fadecolor", text, 0, "Change the fog colour"), texture: false));
        AddMatchNeighbour(s);
        AddSlope(s, ceiling: false);
        AddSlope(s, ceiling: true);
        Rows.Add(new TextRow("Floor texture", "The picture on the floor (texturefloor).", () => s.FloorTexture, v => Set(s, "Change floor texture", e => e.FloorTexture, (e, x) => e.FloorTexture = x, v)));
        Rows.Add(new TextRow("Ceiling texture", "The picture on the ceiling (textureceiling).", () => s.CeilingTexture, v => Set(s, "Change ceiling texture", e => e.CeilingTexture, (e, x) => e.CeilingTexture = x, v)));
        AddFlatPlacement(s, ceiling: false);
        AddFlatPlacement(s, ceiling: true);
        Rows.Add(new FlagRow("Counts as a secret", "Finding this area counts toward the secrets at the end of the map (sector type 1024).", () => (s.Special & 1024) != 0, on => Set(s, on ? "Make a secret" : "Unmake a secret", e => e.Special, (e, x) => e.Special = x, on ? s.Special | 1024 : s.Special & ~1024)));
        // The effect by name (Eureka's sector-type browser); one the list does not name stays as it is, listed by its number.
        var effects = RoomEffects.All.ToList();
        if (!effects.Any(x => x.Number == (s.Special & RoomEffects.Mask))) effects.Add((s.Special & RoomEffects.Mask, RoomEffects.NameOf(s.Special)));
        Rows.Add(new ChoiceRow("Effect", "What the area does to the player or its light: blinking or flickering light, floors that hurt or heal, ice, a door that shuts by itself (sector type).", [.. effects.Select(x => x.Name)],
            () => Math.Max(0, effects.FindIndex(x => x.Number == (s.Special & RoomEffects.Mask))),
            i => Set(s, "Change the room's effect", e => e.Special, (e, x) => e.Special = x, RoomEffects.With(s.Special, effects[i].Number))));
        Rows.Add(new NumberRow("Tag", "A number that links a switch or trigger to this area (sector tag, id).", () => s.Tag, v => Set(s, "Change tag", e => e.Tag, (e, x) => e.Tag = x, (int)v), 1, 0, 65535));
    }

    /// <summary>A lift's settings, each one undoable change to every wall that sets it off.</summary>
    private void AddLift(Sector area)
    {
        Lift? Now() => Lifts.Recognise(_session.Geometry, area);
        void Change(Func<Lift, (LiftSettings Settings, bool Monsters)> to, string label)
        {
            if (Now() is not { } lift) return;
            var (settings, monsters) = to(lift);
            _session.History.Record(label, e => Lifts.Change(e, lift, settings, monsters));
        }

        Rows.Add(new ChoiceRow("Speed", "How fast it goes down and up: Doom's normal lifts or its fast ones (lift speed).", ["Normal", "Fast"],
            () => Now()?.Settings.Fast == true ? 1 : 0,
            v => Change(l => (l.Settings with { Fast = v == 1 }, l.Monsters), "Change lift speed")));
        Rows.Add(new NumberRow("Waits down for", "Seconds it stays down before coming back up (delay, in 35ths of a second).", () => Now() is { } l ? Math.Round((decimal)l.Settings.Delay / 35, 1) : 0,
            v => Change(l => (l.Settings with { Delay = (int)Math.Round(v * 35) }, l.Monsters), "Change how long the lift waits"), 0.5m, 0, 60, () => "seconds"));
        Rows.Add(new FlagRow("Monsters set it off too", "Monsters can ride it down (monsteruse, monstercross).", () => Now()?.Monsters == true,
            on => Change(l => (l.Settings, on), on ? "Let monsters use the lift" : "Keep monsters off the lift")));
        Rows.Add(new InfoRow("Goes down to", "The lowest floor beside it, as the game does it.", () => Now() is { } l ? string.Create(CultureInfo.InvariantCulture, $"{l.GoesDownTo:0}, {area.FloorHeight - l.GoesDownTo:0} down") : "—"));
        Rows.Add(new InfoRow("Set off from", "The walls that set it off: switches used from below, lines walked across at the top.", () => Now() is { } l ? string.Join(", ", Triggers.Ways(l.Triggers)) : "—"));
    }

    private void AddLight(Thing t)
    {
        void Arg(string label, int index, int value) => _session.History.Record(label, e => e.Set(t, x => x.Args[index], (x, v) => x.Args[index] = v, value));
        void Colour((int R, int G, int B) c) => _session.History.Record("Change the light's colour", e =>
        {
            e.Set(t, x => x.Args[0], (x, v) => x.Args[0] = v, c.R);
            e.Set(t, x => x.Args[1], (x, v) => x.Args[1] = v, c.G);
            e.Set(t, x => x.Args[2], (x, v) => x.Args[2] = v, c.B);
        });
        int Preset() => Lights.Colours.ToList().FindIndex(c => (c.R, c.G, c.B) == (t.Args[0], t.Args[1], t.Args[2])) is var i and >= 0 ? i : Lights.Colours.Count;
        string[] kinds = ["Steady", "Pulsing", "Flickering", "Flickering at random"];
        Rows.Add(new ChoiceRow("Colour", "Its colour (the first three arguments: red, green, blue).", [.. Lights.Colours.Select(c => c.Name), "Other"], Preset, v =>
        {
            if (v < Lights.Colours.Count) Colour((Lights.Colours[v].R, Lights.Colours[v].G, Lights.Colours[v].B));
        }));
        Rows.Add(new TextRow("Exact colour", "Its colour as #RRGGBB, as in a paint program.", () => Lights.Hex((t.Args[0], t.Args[1], t.Args[2])), text =>
        {
            if (Lights.ParseHex(text) is { } c) Colour(c);
        }));
        Rows.Add(new NumberRow("Reach", "How far it lights, in map units (its radius, the fourth argument).", () => t.Args[3], v => Arg("Change the light's reach", 3, (int)v), 16, 0, 1024));
        Rows.Add(new ChoiceRow("Shines", "Steadily, or pulsing or flickering between its reach and its second reach (the light's kind).", kinds, () => (int)(Lights.Kind(t.Type) ?? LightKind.Steady), v =>
        {
            var type = Lights.TypeOf((LightKind)v, Lights.Recognise(t)?.Soft ?? true);
            _session.History.Record("Change how the light shines", e => e.Set(t, x => x.Type, (x, value) => x.Type = value, type));
        }));
        Rows.Add(new NumberRow("Second reach", "The other reach a pulsing or flickering light goes to (the fifth argument).", () => t.Args[4], v => Arg("Change the light's second reach", 4, (int)v), 16, 0, 1024));
        Rows.Add(new NumberRow("Height above floor", "How high it hangs above the floor (height).", () => (decimal)t.Height, v => Set(t, "Change the light's height", e => e.Height, (e, x) => e.Height = x, (double)v), 8));
        Rows.Add(new FlagRow("Soft edge", "Fades smoothly and lights each surface by the way it faces (an attenuated light), as GZDoom recommends.", () => Lights.Recognise(t)?.Soft == true, on =>
        {
            var type = Lights.TypeOf(Lights.Kind(t.Type) ?? LightKind.Steady, on);
            _session.History.Record(on ? "Soften the light" : "Harden the light", e => e.Set(t, x => x.Type, (x, value) => x.Type = value, type));
        }));
    }

    /// <summary>For a locked door: place its key when the map has none of that colour, else select the key.</summary>
    /// <summary>The door's look: a strip of the game's own door pictures; the chosen one goes on its faces and sets its doorway's height.</summary>
    private void AddDoorLooks(Sector area)
    {
        if (_session.Resources is not { } resources) return;
        var looks = Doors.Looks(resources.Game.Engine, resources.Textures.Walls);
        if (looks.Count == 0) return;
        Door? Now() => Doors.Recognise(_session.Geometry, area);
        string Current() => Now()?.Own.FirstOrDefault()?.Front.Top ?? "";
        var choices = looks.Select(t => new PictureChoice(t.Name, $"{t.Name}, {t.Width} × {t.Height}: the door shows this picture, and its doorway becomes {t.Height} tall.",
            () => string.Equals(Current(), t.Name, StringComparison.OrdinalIgnoreCase),
            () =>
            {
                if (Now() is { } door) _session.History.Record("Change the door's look", e => Doors.Dress(e, _session.Geometry, door, t.Name, (t.Width, t.Height)));
            },
            () => resources.Textures.GetWall(t.Name), _session.OnUiThread)).ToList();
        Rows.Add(new PictureChoiceRow("Looks like", "The door's picture, one of the game's own doors. The doorway takes the picture's height: the wall shows above a short door, and a tall one fills the doorway. Right-click the door on the plan for the same choice.", choices));
    }

    private void AddDoorKey(Sector area)
    {
        if (Doors.Recognise(_session.Geometry, area) is not { Settings.Lock: not 0 } door || Keys.ColourOfLock(door.Settings.Lock, _session.Engine) is not { } colour) return;
        var keys = Keys.OfColour(_session.Geometry, colour, _session.Engine);
        if (keys.Count == 0) Rows.Add(new ActionRow($"Place the {colour} key…", $"Click the map next where the {colour} key goes: somewhere the player reaches before this door.", () => KeyFor?.Invoke(colour)));
        else Rows.Add(new ActionRow($"Select its key", $"Selects the {colour} key{(keys.Count > 1 ? "s" : "")} that open{(keys.Count > 1 ? "" : "s")} this door.", () => _session.Selection.Set([.. Keys.OfColour(_session.Geometry, colour, _session.Engine)])));
    }

    private void AddSwitch(Line wall)
    {
        Switch? Now() => Switches.Recognise(_session.Geometry, wall);
        Rows.Add(new ActionRow("Select what it works on", "Selects every area with the tag its action names; the map shows them joined to the switch by dashed lines.", () =>
        {
            if (Now() is { Targets.Count: > 0 } sw) _session.Selection.Set([.. sw.Targets]);
        }));
        Rows.Add(new FlagRow("Works again and again", "Every time it is used, not only the first (repeatspecial).", () => wall.Flag("repeatspecial"), on => Flag(wall, "repeatspecial", on, on ? "Make the switch work again and again" : "Make the switch work once")));
        Rows.Add(new ActionRow("Not a switch", "Takes the action away; the wall keeps its look and does nothing when used.", () => _session.History.Record("Take the switch away", e =>
        {
            e.Set(wall, x => x.Special, (x, v) => x.Special = v, 0);
            for (var i = 0; i < 5; i++)
            {
                var k = i;
                e.Set(wall, x => x.Args[k], (x, v) => x.Args[k] = v, 0);
            }

            e.Flag(wall, "playeruse", false);
            e.Flag(wall, "repeatspecial", false);
        })));
    }

    private string _mapSettingsKey = "";

    /// <summary>What the Map settings rows' choices are built from: the game's defaults and the values set, which can add a choice.</summary>
    private string MapSettingsKey() => $"{_session.Target}|{_session.Defaults?.Title}|{_session.Defaults?.Music}|{_session.Defaults?.Sky}|{_session.Defaults?.Next}|{_session.Defaults?.Slots.Count}|{_session.Defaults?.Switches.Count}|{_session.Defaults?.Foreign}|{_session.MapEntry.Music}|{_session.MapEntry.Sky}|{_session.MapEntry.Next}";

    /// <summary>
    /// The map's own settings, with nothing selected: its name, music, sky and what follows its exit, each the game's own
    /// for the slot unless changed; written into the project's zmapinfo.txt, which GZDoom reads with the map.
    /// </summary>
    private void AddMapSettings()
    {
        _mapSettingsKey = MapSettingsKey();
        var defaults = _session.Defaults;
        Rows.Add(new HeadingRow("MAP SETTINGS"));
        var targets = Targets.All;
        Rows.Add(new ChoiceRow("Plays in", "What this project plays in. GZDoom: everything WadSmith can make. Vanilla Doom: the 1993 engine's format and limits, as Chocolate Doom and the DOS game play it. Limit-removing: that format without the limits, as Crispy Doom plays it. With a classic target the map is kept in the classic format, and Problems lists anything that has no classic type.", [.. targets.Select(t => t.Title)],
            () => Math.Max(0, targets.ToList().FindIndex(t => t.Id == _session.Target)),
            i => _session.SetTarget(targets[i].Id)));
        if (defaults?.Foreign == true)
        {
            Rows.Add(new InfoRow("Settings file", "The project holds a zmapinfo.txt WadSmith did not write; WadSmith leaves it alone, so the map's name, music, sky and next map are set in that file.", () => "This project's own zmapinfo.txt sets the map's name, music, sky and next map."));
            return;
        }

        ProjectMap Entry() => _session.MapEntry;
        static string TheGames(string? value, string what) => value is { Length: > 0 } ? $"The game's ({value})" : $"The game's {what}";
        static List<string> Choices(IEnumerable<string> list, string? current, bool sorted)
        {
            var choices = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (sorted) choices.Sort(StringComparer.OrdinalIgnoreCase);
            if (current is { Length: > 0 } && !choices.Contains(current, StringComparer.OrdinalIgnoreCase)) choices.Add(current);
            return choices;
        }

        static int Index(List<string> choices, string? current) => current is null ? 0 : choices.FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase)) + 1;

        Rows.Add(new TextRow("Name", "What players see as the map's name, on the automap and between maps (levelname). Leave it empty for the game's own name for this slot.", () => Entry().Title,
            v => _session.ChangeMapSettings(m => m with { Title = v }), hint: () => defaults?.Title is { } t && Entry().Title.Length == 0 ? $"the game's: {t}" : "", texture: false));

        var music = Choices(_session.Resources?.Music ?? [], Entry().Music, sorted: true);
        Rows.Add(new ChoiceRow("Music", "The music played in this map (music): the game's own for this slot, or any of the game's tracks.", [TheGames(defaults?.Music, "music"), .. music],
            () => Index(music, Entry().Music), i => _session.ChangeMapSettings(m => m with { Music = i == 0 ? null : music[i - 1] })));

        var skies = Choices(_session.Resources?.Skies ?? [], Entry().Sky, sorted: true);
        Rows.Add(new ChoiceRow("Sky", "The sky seen over the map (sky1): the game's own for this slot, or any sky the game has.", [TheGames(defaults?.Sky, "sky"), .. skies],
            () => Index(skies, Entry().Sky), i => _session.ChangeMapSettings(m => m with { Sky = i == 0 ? null : skies[i - 1] })));
        Rows.Add(new ActionRow("Make a sky from a 360° picture…", "Turns a 360° picture (a panorama twice as wide as tall, or an HDRI photograph in .hdr) into a sky all round and overhead, and makes it this map's sky; GZDoom draws it as a skybox.", () => _ = MakeSkyAsync()));
        Rows.Add(new ActionRow("Bring in a Wolfenstein 3D level…", "Turns a level of your own Wolfenstein 3D (or Spear of Destiny) into rooms, doors and walls in this game's pictures, as a new map of this project: pick its MAPHEAD file, then the level.", () => _ = PickGridAsync()));
        if (_grid is { } grid)
        {
            Rows.Add(new ChoiceRow("Wolfenstein 3D level", "Which level of the file to bring in.", grid.Levels, () => _gridLevel, i => _gridLevel = i));
            Rows.Add(new ActionRow("Make this level a map of the project", "Brings the level in as the project's next map; open it from the map chooser at the top.", () =>
            {
                _session.ImportGridLevel(grid.Path, _gridLevel);
                _grid = null;
                Rebuild();
            }));
        }

        var ends = ProjectMapInfo.EndToTitle;
        var slots = Choices([.. defaults?.Slots ?? [], .. _session.Project.Maps().Select(m => m.Slot)], Entry().Next is { } n && n != ends ? n : null, sorted: false);
        Rows.Add(new ChoiceRow("After the exit", "Where the exit leads (next): the game's next map for this slot, another map, or the end of the game and the title screen.", [TheGames(defaults?.Next, "next map"), .. slots, "The game ends"],
            () => Entry().Next == ends ? slots.Count + 1 : Index(slots, Entry().Next), i => _session.ChangeMapSettings(m => m with { Next = i == 0 ? null : i == slots.Count + 1 ? ends : slots[i - 1] })));

        if (defaults is { Switches.Count: > 0 }) Rows.Add(new InfoRow("Plays like the original", "GZDoom turns a few behaviour switches on for the original map (compat_ settings); the remix asks for the same ones in its zmapinfo.txt, so it plays the same.", () => string.Join(", ", defaults.Switches)));
    }

    /// <summary>A room's platforms and water (GZDoom's 3D floors), each with its settings, and the ways to add one.</summary>
    private void AddPlatforms(Sector room)
    {
        var platforms = Platforms.Of(_session.Geometry, room);
        foreach (var p in platforms) AddPlatform(p, control: false);
        if (platforms.Count == 0) Rows.Add(new HeadingRow("PLATFORMS AND WATER"));
        Rows.Add(new ActionRow("Add a floating platform", string.Create(CultureInfo.InvariantCulture, $"A solid slab across the whole room that the player walks under and stands on (GZDoom's 3D floor, Sector_Set3DFloor). The room needs to be {EditorSession.PlatformRoomHeight:0} tall."), () => _session.AddPlatform(room, PlatformKind.Solid)));
        Rows.Add(new ActionRow("Fill it with water", "See-through water across the whole room that the player swims in, its surface 32 above the floor (GZDoom's 3D floor, swimmable).", () => _session.AddPlatform(room, PlatformKind.Water)));
    }

    /// <summary>
    /// A platform's settings (in the room it stands in, or on its control room): what it is, its bottom and top, how
    /// see-through it is, its pictures, and taking it away. Each is one undo step on the control room or its wall.
    /// </summary>
    private void AddPlatform(Platform p, bool control)
    {
        var (c, line) = (p.Control, p.ControlLine);
        Rows.Add(new HeadingRow(p.Kind == PlatformKind.Water ? "WATER" : "PLATFORM"));
        Rows.Add(new ChoiceRow("It is", "What the player does with it: stands on it and walks under it, swims in it, or passes through it (the 3D floor's type).", ["Solid", "Water", "Passable"],
            () => Math.Clamp((int)p.Kind - 1, 0, 2), i => _session.SetPlatformKind(p, (PlatformKind)(i + 1))));
        Rows.Add(new NumberRow("Bottom", "The height of its underside (its control room's floor).", () => (decimal)c.FloorHeight, v => Set(c, "Change the platform's bottom", e => e.FloorHeight, (e, x) => e.FloorHeight = x, (double)v), 8,
            hint: () => string.Create(CultureInfo.InvariantCulture, $"{c.FloorHeight - p.Room.FloorHeight:0} above the floor")));
        Rows.Add(new NumberRow("Top", "The height of its top, where the player stands (its control room's ceiling).", () => (decimal)c.CeilingHeight, v => Set(c, "Change the platform's top", e => e.CeilingHeight, (e, x) => e.CeilingHeight = x, (double)v), 8,
            hint: () => string.Create(CultureInfo.InvariantCulture, $"{c.CeilingHeight - c.FloorHeight:0} thick, {p.Room.CeilingHeight - c.CeilingHeight:0} under the ceiling")));
        Rows.Add(new NumberRow("See-through", "How much shows through it, in percent: 0 is solid-looking (the special's alpha).", () => Math.Round((255 - p.Alpha) * 100m / 255), v =>
            _session.History.Record("Change how see-through the platform is", e => Platforms.SetAlpha(e, p, (int)Math.Round(255 - (double)v * 255 / 100))), 10, 0, 100, () => "%"));
        Rows.Add(new TextRow("Top texture", "The picture on its top (its control room's ceiling picture, as GZDoom draws it).", () => c.CeilingTexture, v => Set(c, "Change the platform's top texture", e => e.CeilingTexture, (e, x) => e.CeilingTexture = x, v)));
        Rows.Add(new TextRow("Underside texture", "The picture on its underside (its control room's floor picture).", () => c.FloorTexture, v => Set(c, "Change the platform's underside texture", e => e.FloorTexture, (e, x) => e.FloorTexture = x, v)));
        Rows.Add(new TextRow("Side texture", "The picture on its sides, seen from the rooms beside it (its control wall's middle texture).", () => line.Front.Middle, v => Set(line.Front, "Change the platform's side texture", e => e.Middle, (e, x) => e.Middle = x, v)));
        if (control) Rows.Add(new ActionRow("Select the room it stands in", "Selects the room the platform spans.", () => _session.Selection.Set(p.Room)));
        else Rows.Add(new ActionRow("Select its control room", "Selects the little room outside the map the game reads the platform from.", () => _session.Selection.Set(c)));
        Rows.Add(new ActionRow(p.Kind == PlatformKind.Water ? "Drain the water" : "Remove the platform", "Takes it away with its control room; the room keeps its tag.", () => _session.RemovePlatform(p)));
    }

    /// <summary>
    /// The Ramp tool (§3.7): a room's floor or ceiling sloped from one of its walls, where it keeps the room's height, to
    /// a height at the far side; the plane fields GZDoom reads.
    /// </summary>
    private void AddSlope(Sector s, bool ceiling)
    {
        var walls = Slopes.Walls(_session.Geometry, s).ToList();
        if (walls.Count < 2) return;
        var part = ceiling ? "ceiling" : "floor";
        var field = ceiling ? "ceilingplane" : "floorplane";
        (Line Wall, double Far)? Now() => (ceiling ? SlopePlane.Ceiling(s) : SlopePlane.Floor(s)) is { } p ? Slopes.Recognise(_session.Geometry, s, p, ceiling) : null;
        double Base() => ceiling ? s.CeilingHeight : s.FloorHeight;
        void Slope(Line wall, double far) => _session.History.Record($"Slope the {part}", e => Slopes.Set(e, s, Slopes.Ramp(_session.Geometry, s, wall, Base(), far, ceiling), ceiling));
        Rows.Add(new ChoiceRow($"{char.ToUpperInvariant(part[0])}{part[1..]} slopes from", $"The wall the {part} stays level along, at the {part} height above; from there it slopes to the height below at the far side of the room ({field} fields, as GZDoom reads them).", ["Not sloped", .. walls.Select(w => w.Words)],
            () => Now() is { } n ? walls.FindIndex(w => ReferenceEquals(w.Wall, n.Wall)) + 1 : 0,
            i =>
            {
                if (i == 0) _session.History.Record($"Flatten the {part}", e => Slopes.Set(e, s, null, ceiling));
                else Slope(walls[i - 1].Wall, Now()?.Far ?? Base() + (ceiling ? -64 : 64));
            }));
        Rows.Add(new NumberRow($"{char.ToUpperInvariant(part[0])}{part[1..]} reaches", $"The {part}'s height at the far side of the room, away from the wall it slopes from.", () => (decimal)(Now()?.Far ?? Base()), v =>
        {
            if (Now() is { } n) Slope(n.Wall, (double)v);
        }, 8));
    }

    /// <summary>A room's colour field (lightcolor, fadecolor) as red, green and blue; <paramref name="otherwise"/> when it has none.</summary>
    private static (int R, int G, int B) RoomColour(Sector s, string key, int otherwise)
    {
        var value = s.Extra.TryGet(key, out var v) && v.Kind == Core.Maps.Udmf.UdmfValueKind.Integer ? (int)Math.Clamp(v.AsInt(), 0, 0xFFFFFF) : otherwise;
        return ((value >> 16) & 255, (value >> 8) & 255, value & 255);
    }

    /// <summary>Sets a room's colour field from "#RRGGBB" (the default value takes the field away), one undo step.</summary>
    private void SetRoomColour(Sector s, string key, string text, int otherwise, string label)
    {
        if (Lights.ParseHex(text) is not { } c)
        {
            _session.Tell("Write the colour as #RRGGBB, as a paint program shows it.");
            return;
        }

        var value = (c.R << 16) | (c.G << 8) | c.B;
        _session.History.Record(label, e => e.Field(s, key, value == otherwise ? (Core.Maps.Udmf.UdmfValue?)null : Core.Maps.Udmf.UdmfValue.Int(value)));
    }

    /// <summary>The map's groups, with nothing selected: each to select, lock or unlock (a locked one is unlocked here).</summary>
    private void AddGroups()
    {
        if (_groups.Count == 0) return;
        Rows.Add(new HeadingRow("GROUPS"));
        foreach (var g in _groups)
        {
            var parts = new List<string>();
            if (g.Rooms.Count > 0) parts.Add(g.Rooms.Count == 1 ? "1 room" : $"{g.Rooms.Count} rooms");
            if (g.Things.Count > 0) parts.Add(g.Things.Count == 1 ? "1 thing" : $"{g.Things.Count} things");
            Rows.Add(new InfoRow(g.Name, "A group of rooms and things you named.", () => string.Join(", ", parts) + (g.Locked ? " · locked" : "")));
            var group = g;
            if (!g.Locked) Rows.Add(new ActionRow($"Select {g.Name}", "Selects every room and thing in the group.", () => _session.SelectGroup(group)));
            Rows.Add(new FlagRow($"{g.Name} is locked", "A locked group cannot be selected or changed, and dragging its neighbours never moves its corners.", () => group.Locked, on => _session.LockGroup(group, on)));
            Rows.Add(new FlagRow($"{g.Name} is hidden", "Hides the group on the plan and in the 3D view while you work elsewhere (this session only: the map is not changed, and the game shows it).", () => _session.Hidden.Contains(group.Name), on => _session.Hide(group.Name, on)));
        }
    }

    /// <summary>A selection's group: its name to type (empty takes them out), and locking it.</summary>
    private void AddGrouping(IReadOnlyList<Element> items)
    {
        var members = Grouping.MembersOf(items);
        if (members.Count == 0) return;
        Rows.Add(new HeadingRow("GROUP"));
        Rows.Add(new TextRow("Group", "A name for these rooms and things together (\"Courtyard\"): with nothing selected, the Inspector lists the map's groups to select or lock. Leave it empty to take them out of their group.", () => members.Select(Grouping.GroupOf).Distinct().Count() == 1 ? Grouping.GroupOf(members[0]) ?? "" : "", name => _session.NameGroup(members, name), texture: false));
        Rows.Add(new ActionRow(members.Count == 1 ? "Lock it" : "Lock them", "Locks the selection against changes: it can no longer be selected or dragged, nor reshaped by its neighbours. Unlock it with nothing selected, under Groups.", () => _session.LockMembers(members)));
    }

    /// <summary>
    /// How a floor's or ceiling's texture lies on it (GZDoom's panning, rotation and scale fields): turned, shifted, sized.
    /// A value back at its default takes the field away.
    /// </summary>
    private void AddFlatPlacement(Sector s, bool ceiling)
    {
        var part = ceiling ? "ceiling" : "floor";
        var where = ceiling ? "Ceiling" : "Floor";
        FlatTransform Now() => FlatTransform.Of(s, ceiling);
        void Field(string name, double value, double plain) => _session.History.Record($"Change the {part} texture", e => e.Field(s, name + part, Math.Abs(value - plain) < 1e-9 ? (Core.Maps.Udmf.UdmfValue?)null : Core.Maps.Udmf.UdmfValue.Float(value)));
        Rows.Add(new HeadingRow(ceiling ? "CEILING TEXTURE" : "FLOOR TEXTURE"));
        Rows.Add(new NumberRow($"{where} texture turn (°)", $"Turns the {part}'s texture, clockwise as seen from above (rotation{part}).", () => (decimal)Now().Rotation, v => Field("rotation", (double)v, 0), 15, -360, 360));
        Rows.Add(new NumberRow($"{where} texture shift ↔", $"Moves the {part}'s texture west (more) or east (less) under the room, in texture pixels (xpanning{part}).", () => (decimal)Now().XPan, v => Field("xpanning", (double)v, 0), 8));
        Rows.Add(new NumberRow($"{where} texture shift ↕", $"Moves the {part}'s texture north (more) or south (less), in texture pixels (ypanning{part}).", () => (decimal)Now().YPan, v => Field("ypanning", (double)v, 0), 8));
        Rows.Add(new NumberRow($"{where} texture size (%)", $"How big the {part}'s texture is drawn: 200 is twice as big, 50 half (x/yscale{part}).", () => (decimal)Math.Round(100 / Now().XScale), v =>
        {
            if (v <= 0) return;
            var scale = 100 / (double)v;
            _session.History.Record($"Change the {part} texture", e =>
            {
                e.Field(s, "xscale" + part, Math.Abs(scale - 1) < 1e-9 ? (Core.Maps.Udmf.UdmfValue?)null : Core.Maps.Udmf.UdmfValue.Float(scale));
                e.Field(s, "yscale" + part, Math.Abs(scale - 1) < 1e-9 ? (Core.Maps.Udmf.UdmfValue?)null : Core.Maps.Udmf.UdmfValue.Float(scale));
            });
        }, 25, 1, 6400));
    }

    /// <summary>
    /// Heights of several rooms at once: each value as theirs when they share it (the lowest otherwise, with their range
    /// shown), set for all; and raising them all together by an amount (one undo step either way).
    /// </summary>
    private void AddHeights(List<Sector> rooms)
    {
        Rows.Add(new HeadingRow("HEIGHTS"));
        string Range(Func<Sector, double> get) => rooms.Select(get).Distinct().Count() == 1 ? "" : string.Create(CultureInfo.InvariantCulture, $"they differ: {rooms.Min(get):0} to {rooms.Max(get):0}");
        void All(string label, Action<StructureEdit, Sector> set) => _session.History.Record(label, e =>
        {
            foreach (var r in rooms.Where(r => r.OnMap)) set(e, r);
        });
        Rows.Add(new NumberRow("Floors at", "Sets every selected room's floor to this height.", () => (decimal)rooms.Min(r => r.FloorHeight), v => All("Change floor heights", (e, r) => e.Set(r, x => x.FloorHeight, (x, h) => x.FloorHeight = h, (double)v)), 8, hint: () => Range(r => r.FloorHeight)));
        Rows.Add(new NumberRow("Ceilings at", "Sets every selected room's ceiling to this height.", () => (decimal)rooms.Min(r => r.CeilingHeight), v => All("Change ceiling heights", (e, r) => e.Set(r, x => x.CeilingHeight, (x, h) => x.CeilingHeight = h, (double)v)), 8, hint: () => Range(r => r.CeilingHeight)));
        Rows.Add(new NumberRow("Room heights", "Sets every selected room's ceiling this far above its own floor.", () => (decimal)rooms.Min(r => r.CeilingHeight - r.FloorHeight), v => All("Change room heights", (e, r) => e.Set(r, x => x.CeilingHeight, (x, h) => x.CeilingHeight = h, r.FloorHeight + (double)v)), 8, 16, hint: () => Range(r => r.CeilingHeight - r.FloorHeight),
            presets: PresetChoice.For(Presets.RoomHeights, () => rooms.Select(r => r.CeilingHeight - r.FloorHeight).Distinct().Count() == 1 ? rooms[0].CeilingHeight - rooms[0].FloorHeight : double.NaN,
                h => All("Make the rooms " + Presets.Named(Presets.RoomHeights, h)!.Name.ToLowerInvariant(), (e, r) => e.Set(r, x => x.CeilingHeight, (x, c) => x.CeilingHeight = c, r.FloorHeight + h)), HeightTip, bars: true)));
        Rows.Add(new NumberRow("Brightness of all", "Sets every selected room's brightness.", () => rooms.Min(r => r.Light), v => All("Change brightness", (e, r) => e.Set(r, x => x.Light, (x, l) => x.Light = l, (int)v)), 16, 0, 255, () => Range(r => r.Light),
            presets: PresetChoice.For(Presets.Brightness, () => rooms.Select(r => r.Light).Distinct().Count() == 1 ? rooms[0].Light : double.NaN,
                l => All("Make the rooms " + Presets.Named(Presets.Brightness, l)!.Name.ToLowerInvariant(), (e, r) => e.Set(r, x => x.Light, (x, v) => x.Light = v, (int)l)), LightTip, swatches: true)));
        Rows.Add(new NumberRow("Raise them all by", "Moves every selected room's floor and ceiling up (or down) by this much together, keeping their differences.", () => 0, v => All("Raise rooms", (e, r) =>
        {
            e.Set(r, x => x.FloorHeight, (x, h) => x.FloorHeight = h, r.FloorHeight + (double)v);
            e.Set(r, x => x.CeilingHeight, (x, h) => x.CeilingHeight = h, r.CeilingHeight + (double)v);
        }), 8));
    }

    /// <summary>A room joining two floors too far apart to step between (D27): steps or a ramp from the low one to the high one.</summary>
    private void AddClimb(Sector s)
    {
        if (Climbs.Find(_session.Geometry, s) is not { } climb) return;
        var (low, high) = (climb.Low.FloorHeight, climb.High.FloorHeight);
        Rows.Add(new InfoRow("Joins floors", "The floors of the rooms on either side: more than a step (24) apart, the player cannot climb from one to the other.",
            () => string.Create(CultureInfo.InvariantCulture, $"{low:0} and {high:0}, {high - low:0} apart")));
        if (Climbs.StairsRun(_session.Geometry, climb) is not null)
        {
            Rows.Add(new ActionRow("Add steps", string.Create(CultureInfo.InvariantCulture, $"Cuts the room into stairs climbing from {low:0} to {high:0}, each step a comfortable rise, as the Stairs tool makes them. One undo step."), () => _session.AddSteps(s)));
        }

        if (climb.Rise / climb.Length <= Climbs.SteepestRamp)
        {
            Rows.Add(new ActionRow("Make a ramp", string.Create(CultureInfo.InvariantCulture, $"Slopes the floor from {low:0} at the low room to {high:0} at the high one, a ramp the player walks up (a sloped floor, as GZDoom reads it). One undo step."), () => _session.MakeRamp(s)));
        }
    }

    /// <summary>Matching a neighbour (§3.4): the floor or ceiling of one of the rooms beside it, chosen by its height.</summary>
    private void AddMatchNeighbour(Sector s)
    {
        if (_session.Geometry.WallBoundsAt(s.Slot) is not { } b) return;
        var beside = _session.Geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY)
            .Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, s) ^ ReferenceEquals(l.Back.Sector, s)))
            .Select(l => ReferenceEquals(l.Front.Sector, s) ? l.Back!.Sector : l.Front.Sector).Distinct().ToList();
        double[] floors = [.. beside.Select(r => r.FloorHeight).Where(h => h != s.FloorHeight).Distinct().Order()];
        double[] ceilings = [.. beside.Select(r => r.CeilingHeight).Where(h => h != s.CeilingHeight).Distinct().Order()];
        if (floors.Length > 0)
        {
            Rows.Add(new ChoiceRow("Floor level with", "Sets the floor to the height of a room beside it.", ["a room beside…", .. floors.Select(h => string.Create(CultureInfo.InvariantCulture, $"{h:0} (the room beside)"))], () => 0, i =>
            {
                if (i > 0) Set(s, "Match the floor", e => e.FloorHeight, (e, x) => e.FloorHeight = x, floors[i - 1]);
            }));
        }

        if (ceilings.Length > 0)
        {
            Rows.Add(new ChoiceRow("Ceiling level with", "Sets the ceiling to the height of a room beside it.", ["a room beside…", .. ceilings.Select(h => string.Create(CultureInfo.InvariantCulture, $"{h:0} (the room beside)"))], () => 0, i =>
            {
                if (i > 0) Set(s, "Match the ceiling", e => e.CeilingHeight, (e, x) => e.CeilingHeight = x, ceilings[i - 1]);
            }));
        }
    }

    private decimal _curveReach = 64;
    private decimal _curveSides = 6;

    /// <summary>Rounding off a corner where two walls meet (arc walls).</summary>
    private void AddRounding(Vertex corner)
    {
        Rows.Add(new HeadingRow("ROUND IT OFF"));
        _curveReach = (decimal)Rounding.DefaultReach(_session.Geometry, corner);
        Rows.Add(new NumberRow("Curve reach", "How far along each wall from the corner the curve starts and ends.", () => _curveReach, v => _curveReach = v, 8, 4, 4096));
        Rows.Add(new NumberRow("Curve sides", "How many walls the curve is made of: more is smoother, fewer is closer to the game's own maps.", () => _curveSides, v => _curveSides = v, 1, 2, 32));
        Rows.Add(new ActionRow("Round this corner", "Turns the corner into a curve of walls that meets both walls smoothly; the rooms either side follow it. One undo step.", () => _session.RoundCorner(corner, (double)_curveReach, (int)_curveSides)));
    }

    private int _gridColumns;
    private decimal _gridSpacing = 64;

    /// <summary>Lining up, spacing out and arranging the selected objects in a ring or a grid (rooms stay where they are).</summary>
    private void AddArranging(List<Thing> things)
    {
        void Do(string label, Action<StructureEdit> arrange) => _session.History.Record(label, arrange);
        Rows.Add(new HeadingRow("ARRANGE THE OBJECTS"));
        string[] lines = ["Choose a line…", "The leftmost", "A line down the middle", "The rightmost", "The lowest", "A line across the middle", "The highest"];
        Rows.Add(new ChoiceRow("Line them up on", "Moves the selected objects onto one line: through the one farthest left, right, down or up, or through the middle between them.", lines, () => 0, i =>
        {
            if (i > 0) Do("Line up objects", e => Arranging.Line(e, things, (LineUp)(i - 1)));
        }));
        if (things.Count >= 3)
        {
            Rows.Add(new ActionRow("Space them evenly, left to right", "Spreads them evenly between the two farthest apart, side to side, in the order they stand.", () => Do("Space objects evenly", e => Arranging.Spread(e, things, across: true))));
            Rows.Add(new ActionRow("Space them evenly, bottom to top", "Spreads them evenly between the two farthest apart, up and down the map, in the order they stand.", () => Do("Space objects evenly", e => Arranging.Spread(e, things, across: false))));
        }

        Rows.Add(new ActionRow("Arrange in a ring, facing in", "Puts them evenly round a ring about their middle, each facing the middle: eight imps closing in.", () => Do("Arrange in a ring", e => Arranging.Ring(e, things, Arranging.Spread(things).Centre, Arranging.RingReach(things), RingFacing.Inward))));
        Rows.Add(new ActionRow("Arrange in a ring, facing out", "Puts them evenly round a ring about their middle, each facing away from it.", () => Do("Arrange in a ring", e => Arranging.Ring(e, things, Arranging.Spread(things).Centre, Arranging.RingReach(things), RingFacing.Outward))));
        Rows.Add(new NumberRow("Ring reach", "How far the farthest is from their middle; a new reach rings them all round their middle that far out.", () => (decimal)Math.Round(Arranging.Spread(things).Radius), v => Do("Change the ring", e => Arranging.Ring(e, things, Arranging.Spread(things).Centre, (double)v, RingFacing.AsTheyWere)), 16, 0, 8192));

        _gridColumns = (int)Math.Ceiling(Math.Sqrt(things.Count));
        void Grid() => Do("Arrange in a grid", e => Arranging.Grid(e, things, _gridColumns, (double)_gridSpacing));
        Rows.Add(new ActionRow("Arrange in a grid", "Puts them in rows from the top left of where they are, in reading order.", Grid));
        Rows.Add(new NumberRow("Grid columns", "How many stand in each row of the grid.", () => _gridColumns, v =>
        {
            _gridColumns = (int)v;
            Grid();
        }, 1, 1, 64));
        Rows.Add(new NumberRow("Grid spacing", "How far apart they stand in the grid: the player is 32 across, most monsters 40 to 64.", () => _gridSpacing, v =>
        {
            _gridSpacing = v;
            Grid();
        }, 8, 16, 1024));
    }

    private void AddStairs(Sector step)
    {
        Staircase? Now() => StairsFinder.Recognise(_session.Geometry, step);
        Rows.Add(new ActionRow("Select all the steps", "Selects every step of this flight.", () =>
        {
            if (Now() is { } st) _session.Selection.Set([.. st.Steps]);
        }));
        Rows.Add(new NumberRow("Steps", "How many steps the flight has; a straight flight is cut again into that many, from the floor below to the floor above.", () => Now() is { } st ? st.Count : 0, v => _session.ReStep(step, (int)v), 1, 1, 64));
        Rows.Add(new NumberRow("Each step rises", "How much higher each step is than the one below, from the floor below the stairs; the player steps up 24 at most.", () => Now() is { } st ? (decimal)Math.Round(st.StepRise) : 0, v =>
        {
            if (Now() is { } st) _session.History.Record("Change how the stairs rise", e => StairsFinder.SetRise(e, st, (double)v));
        }, 2, 1, (decimal)Stairs.MaxRise));
        Rows.Add(new ActionRow("Even the steps out", "Makes every step rise the same, from the floor below the stairs to the floor above them.", () =>
        {
            if (Now() is { } st) _session.History.Record("Even the steps out", e => StairsFinder.SetRise(e, st, st.Rise));
        }));
    }

    private void AddSecret(Sector area)
    {
        Secret? Now() => Secrets.Recognise(_session.Geometry, area);
        Rows.Add(new FlagRow("Hidden on the in-game map", "Its entrances, with the walls that open a door into it, show as solid walls on the automap (secret).", () => Now()?.Hidden == true, on =>
        {
            if (Now() is { } s) _session.History.Record(on ? "Hide the secret's entrances" : "Show the secret's entrances", e => s.Entrances.ToList().ForEach(l => e.Flag(l, "secret", on)));
        }));
        foreach (var door in Now()?.Doors ?? [])
        {
            Rows.Add(new ActionRow("Make its door look like the wall", "The door into it takes the texture of the wall beside it, lined up with it, as Doom's secret doors do; it still opens when used.", () => _session.HideSecretDoor(area, door.Area)));
        }

        Rows.Add(new ActionRow("Not a secret", "It stops counting toward the secrets, and its entrances show on the in-game map again.", () => _session.MakeSecret(area, secret: false)));
    }

    private void AddWindow(Sector area)
    {
        WallWindow? Now() => WallWindows.Recognise(_session.Geometry, area);
        string Above() => Now() is { } w ? string.Create(CultureInfo.InvariantCulture, $"{area.FloorHeight - w.Rooms.Min(r => r.FloorHeight):0} above the lower floor") : "";
        string Tall() => string.Create(CultureInfo.InvariantCulture, $"the opening is {area.CeilingHeight - area.FloorHeight:0} tall");
        Rows.Add(new NumberRow("Sill height", "The bottom of the opening: the window area's floor (heightfloor). More than 24 above a floor, the player cannot climb in.", () => (decimal)area.FloorHeight, v => Set(area, "Change the sill height", e => e.FloorHeight, (e, x) => e.FloorHeight = x, (double)v), 8, hint: Above));
        Rows.Add(new NumberRow("Top height", "The top of the opening: the window area's ceiling (heightceiling).", () => (decimal)area.CeilingHeight, v => Set(area, "Change the window's top", e => e.CeilingHeight, (e, x) => e.CeilingHeight = x, (double)v), 8, hint: Tall));
        Rows.Add(new FlagRow("Nothing walks or flies through", "Its walls stop players and monsters, flying ones too; shots and sight still pass (blocking).", () => Now()?.Blocks == true, on =>
        {
            if (Now() is { } w) _session.History.Record(on ? "Block the window" : "Unblock the window", e => w.Faces.ToList().ForEach(l => e.Flag(l, "blocking", on)));
        }));
    }

    private static string Compass(int angle) => (((angle % 360) + 360) % 360) switch
    {
        < 23 or >= 338 => "east",
        < 68 => "north-east",
        < 113 => "north",
        < 158 => "north-west",
        < 203 => "west",
        < 248 => "south-west",
        < 293 => "south",
        _ => "south-east",
    };

    /// <summary>A teleporter's settings: where it goes, who it sends, and taking it away.</summary>
    private void AddTeleporter(Line l)
    {
        Rows.Add(new ActionRow("Select where it goes", "Selects the arrival spot this wall sends to.", () =>
        {
            if (Teleports.Recognise(_session.Geometry, l) is { Arrivals.Count: > 0 } t) _session.Selection.Set([.. t.Arrivals]);
        }));
        Rows.Add(new FlagRow("Monsters are sent too", "Monsters walking across it are teleported as well (monstercross).", () => l.Flag("monstercross") || l.Flag("monsteruse"),
            on => _session.History.Record(on ? "Let monsters teleport" : "Keep monsters from teleporting", e => e.Flag(l, l.Flag("playeruse") ? "monsteruse" : "monstercross", on))));
        Rows.Add(new FlagRow("Works again and again", "Every crossing teleports, not only the first (repeatspecial).", () => l.Flag("repeatspecial"),
            on => _session.History.Record(on ? "Make the teleporter repeat" : "Make the teleporter work once", e => e.Flag(l, "repeatspecial", on))));
        Rows.Add(new ActionRow("Not a teleporter", "Takes the teleport away from this wall; the arrival spot stays.", () => _session.History.Record("Take the teleporter away", e =>
        {
            e.Set(l, x => x.Special, (x, v) => x.Special = v, 0);
            foreach (var f in new[] { "playercross", "monstercross", "playeruse", "monsteruse", "repeatspecial" }) e.Flag(l, f, false);
        })));
    }

    /// <summary>An exit's settings: where it goes, and taking it away.</summary>
    private void AddExit(Line l)
    {
        Rows.Add(new ChoiceRow("Goes to", "The map that follows: the next one, or the secret one the map settings name (Exit_Normal or Exit_Secret).", ["The next map", "The secret map"],
            () => l.Special == Exits.Secret ? 1 : 0,
            v => _session.History.Record("Change where the exit goes", e => Exits.GoTo(e, l, v == 1))));
        Rows.Add(new ActionRow("Not an exit", "Takes the exit away; the wall keeps its look and does nothing when used or crossed.", () => _session.History.Record("Take the exit away", e => Exits.Clear(e, l))));
    }

    /// <summary>The door in one sentence: how it opens, what locks it, what it does after.</summary>
    /// <summary>The door's own settings, each one undoable change to every wall that opens it.</summary>
    private void AddDoor(Sector area)
    {
        Door? Now() => Doors.Recognise(_session.Geometry, area);
        void Change(Func<Door, (DoorSettings Settings, bool Monsters, bool StartsOpen)> to, string label)
        {
            if (Now() is not { } d) return;
            var (settings, monsters, startsOpen) = to(d);
            _session.History.Record(label, e => Doors.Change(e, d, settings, monsters, startsOpen));
        }

        Rows.Add(new ChoiceRow("Speed", "How fast it opens and closes: Doom's normal doors or its fast ones (door speed).", ["Normal", "Fast"],
            () => Now()?.Settings.Fast == true ? 1 : 0,
            v => Change(d => (d.Settings with { Fast = v == 1 }, d.Monsters, d.StartsOpen), "Change door speed")));
        Rows.Add(new ChoiceRow("After opening", "Whether it closes by itself after a few seconds or stays open (Door_Raise or Door_Open).", ["Closes again", "Stays open"],
            () => Now()?.Settings.StaysOpen == true ? 1 : 0,
            v => Change(d => (d.Settings with { StaysOpen = v == 1 }, d.Monsters, d.StartsOpen), "Change what the door does after opening")));
        if (_session.Look == Look.For("doom"))
        {
            AddDoorLooks(area);
            Rows.Add(new ChoiceRow("Locked with", "The key the player needs to open it; the card or the skull of that colour both work, as in Doom (lock). Dragging a key from Objects onto the door, or right-clicking the door, locks it too.", [.. Doors.DoomLocks.Select(k => k.Name)],
                () => Now() is { } d ? Math.Max(0, Doors.DoomLocks.ToList().FindIndex(k => k.Lock == d.Settings.Lock)) : 0,
                v => Change(d => (d.Settings with { Lock = Doors.DoomLocks[v].Lock }, d.Monsters, d.StartsOpen), "Change the door's lock")));
        }

        Rows.Add(new FlagRow("Monsters open it too", "Monsters chasing the player can open it (monsteruse). Locked doors never open for monsters.", () => Now()?.Monsters == true,
            on => Change(d => (d.Settings, on, d.StartsOpen), on ? "Let monsters open the door" : "Keep monsters from opening the door")));
        Rows.Add(new FlagRow("Starts open", "Open when the map starts, its ceiling already raised; it still closes and opens as set.", () => Now()?.StartsOpen == true,
            on => Change(d => (d.Settings, d.Monsters, on), on ? "Start the door open" : "Start the door closed")));
        Rows.Add(new InfoRow("Opens to", "How high its ceiling rises: 4 below the lowest ceiling beside it, as the game does it.", () => Now() is { } d ? string.Create(CultureInfo.InvariantCulture, $"{d.OpensTo:0}, {d.OpensTo - area.FloorHeight:0} tall") : "—"));
        Rows.Add(new InfoRow("Opened from", "Its own walls, used from the rooms beside it, and walls elsewhere that name its tag (a switch, a line walked over).", () =>
        {
            if (Now() is not { } d) return "—";
            var own = d.Own.Count();
            return string.Join(", ", (own == 0 ? [] : new[] { own == 1 ? "1 of its walls" : $"{own} of its walls" }).Concat(d.RemoteWays()));
        }));
    }

    private void AddLine(Line l)
    {
        Rows.Add(new InfoRow("Length", "How long the wall is, in map units.", () => l.Length.ToString("0.#", CultureInfo.InvariantCulture)));
        Rows.Add(new InfoRow("Action", "What happens when the wall is used, crossed or shot (line special).", () => l.Special == 0 ? "None" : $"{ActionCatalog.Get(l.Special)?.Name ?? "Unknown"} ({l.Special.ToString(CultureInfo.InvariantCulture)})"));
        Rows.Add(new FlagRow("Blocks movement", "Nothing crosses this wall, even if both sides are open (blocking).", () => l.Flag("blocking"), on => Flag(l, "blocking", on, "Change blocking")));
        Rows.Add(new FlagRow("Blocks monsters", "Players pass, monsters do not (blockmonsters).", () => l.Flag("blockmonsters"), on => Flag(l, "blockmonsters", on, "Change monster blocking")));
        Rows.Add(new FlagRow("Hidden on the map", "Not drawn on the in-game map (dontdraw).", () => l.Flag("dontdraw"), on => Flag(l, "dontdraw", on, "Change map visibility")));
        Rows.Add(new FlagRow("Counts as a secret", "Shown as a solid wall on the in-game map so it does not give a secret away (secret).", () => l.Flag("secret"), on => Flag(l, "secret", on, "Change secret")));
        Rows.Add(new FlagRow("Shown on the map from the start", "Drawn on the in-game map before the player has seen it (mapped).", () => l.Flag("mapped"), on => Flag(l, "mapped", on, "Change map visibility")));
        Rows.Add(new FlagRow("Blocks sound", "Muffles shots: a monster hears the player through one such wall but not through two (sound block, blocksound).", () => l.Flag("blocksound"), on => Flag(l, "blocksound", on, "Change sound blocking")));
        if (!_session.Project.ClassicFormat) Rows.Add(new FlagRow("Blocks players", "Monsters pass, players do not (blockplayers).", () => l.Flag("blockplayers"), on => Flag(l, "blockplayers", on, "Change player blocking")));
        if (l.Back is null)
        {
            Rows.Add(new FlagRow("Texture sits on the floor", "The texture's bottom edge rests on the floor, so it stays put when the ceiling moves, as door tracks need (lower unpegged, dontpegbottom).", () => l.Flag("dontpegbottom"), on => Flag(l, "dontpegbottom", on, "Change texture anchoring")));
        }
        else
        {
            if (l.Special == 0)
            {
                // What a first-time mapper came here for: the opening is where a door or a window goes.
                Rows.Add(new ActionRow("Make it a door", "Builds a door into this opening with the game's door and track textures, with wall either side when the opening is wider than a door; it opens when the player uses it and closes again. One undo step.", () => _session.MakeDoor(l)));
                Rows.Add(new ActionRow("Make it a window", "Turns the middle of this opening into a window: a sill and a top the player sees over but cannot pass. One undo step.", () =>
                {
                    var (from, to) = Doors.Centred(l, Math.Min(64, l.Length));
                    _session.MakeWindow(l, from, to);
                }));
                Rows.Add(new ActionRow("Wall it off", "Closes the opening with a wall, so the rooms are apart: a slab with the room's wall texture on it. One undo step.", () => _session.WallOff(l)));
            }

            Rows.Add(new FlagRow("Upper texture hangs from the ceiling", "The upper texture starts at the higher ceiling and runs down, instead of rising from the opening below it (upper unpegged, dontpegtop).", () => l.Flag("dontpegtop"), on => Flag(l, "dontpegtop", on, "Change texture anchoring")));
            Rows.Add(new FlagRow("Lower texture continues the wall above", "The lower texture is placed as if the wall ran down from the ceiling, so it carries on the pattern above it (lower unpegged, dontpegbottom).", () => l.Flag("dontpegbottom"), on => Flag(l, "dontpegbottom", on, "Change texture anchoring")));
        }

        Rows.Add(new ActionRow("Split it in half", "Adds a corner in the middle of the wall, making two walls of it (Eureka's split in half).", _session.SplitWalls));
    }

    /// <summary>One face on its own: only the parts it can show, with whether each is seen, and its offsets.</summary>
    private void AddFace(Side side)
    {
        var wall = side.Line;
        if (wall?.Opposite(side) is not { } other)
        {
            Rows.Add(new TextRow("Texture", "The picture on this wall (texturemiddle).", () => side.Middle, v => Set(side, "Change wall texture", e => e.Middle, (e, x) => e.Middle = x, v)));
        }
        else
        {
            string Upper()
            {
                var (here, there) = (side.Sector, other.Sector);
                if (MapScene.IsSky(here.CeilingTexture) && MapScene.IsSky(there.CeilingTexture)) return "Not seen: both sides are open sky.";
                return here.CeilingHeight > there.CeilingHeight
                    ? string.Create(CultureInfo.InvariantCulture, $"Seen above the opening, {here.CeilingHeight - there.CeilingHeight:0} tall.")
                    : "Not seen now: the ceiling beyond is not lower.";
            }

            string Lower() => other.Sector.FloorHeight > side.Sector.FloorHeight
                ? string.Create(CultureInfo.InvariantCulture, $"Seen below the opening, a step of {other.Sector.FloorHeight - side.Sector.FloorHeight:0}.")
                : "Not seen now: the floor beyond is not higher.";

            string Middle() => side.Middle is "-" or "" ? "None: the opening is clear." : "Hangs once in the opening, like a grate or a fence.";

            Rows.Add(new TextRow("Upper texture", "Shown where the ceiling on the other side is lower (texturetop).", () => side.Top, v => Set(side, "Change upper texture", e => e.Top, (e, x) => e.Top = x, v), Upper));
            Rows.Add(new TextRow("Middle texture", "A grate or fence across the opening; - for none (texturemiddle).", () => side.Middle, v => Set(side, "Change middle texture", e => e.Middle, (e, x) => e.Middle = x, v), Middle));
            Rows.Add(new TextRow("Lower texture", "Shown where the floor on the other side is higher (texturebottom).", () => side.Bottom, v => Set(side, "Change lower texture", e => e.Bottom, (e, x) => e.Bottom = x, v), Lower));
        }

        Rows.Add(new NumberRow("Texture shift across", "Moves this face's textures sideways, in pixels; above zero moves them left (x offset, offsetx).", () => (decimal)side.OffsetX, v => Set(side, "Shift texture across", e => e.OffsetX, (e, x) => e.OffsetX = x, (double)v), 1));
        Rows.Add(new NumberRow("Texture shift up", "Moves this face's textures up, in pixels; below zero moves them down (y offset, offsety).", () => (decimal)side.OffsetY, v => Set(side, "Shift texture up", e => e.OffsetY, (e, x) => e.OffsetY = x, (double)v), 1));
        Rows.Add(new ActionRow("Line up the walls along it", "The same texture on the walls before and after this one runs on from this face, across the corners, as if they were one wall (auto-align; in the 3D view, Ctrl+A on a face).", () => _session.AlignTextures(side, Aligning.PartOf(side))));
    }

    private void AddSide(string which, Side side)
    {
        Rows.Add(new TextRow($"{which}: upper texture", "Shown where the ceiling on the other side is lower (texturetop).", () => side.Top, v => Set(side, "Change upper texture", e => e.Top, (e, x) => e.Top = x, v)));
        Rows.Add(new TextRow($"{which}: middle texture", "The wall you see; on an open wall, a grate or fence (texturemiddle).", () => side.Middle, v => Set(side, "Change middle texture", e => e.Middle, (e, x) => e.Middle = x, v)));
        Rows.Add(new TextRow($"{which}: lower texture", "Shown where the floor on the other side is higher (texturebottom).", () => side.Bottom, v => Set(side, "Change lower texture", e => e.Bottom, (e, x) => e.Bottom = x, v)));
    }

    /// <summary>
    /// The rows that reach from the selection across the map: select the rooms, walls or objects like this one (Eureka's
    /// select-alike), copy its properties, and paste copied ones onto it. <paramref name="one"/> is the one element
    /// selected; null for several, which get only the paste.
    /// </summary>
    private void AddLikeIt(Element? one)
    {
        var held = Properties.Held;
        var alike = one is null ? [] : EditorViewModel.AlikeChoices.Where(a => Selecting.Fits(one, a.By)).ToList();
        var copy = one is not null and not Vertex;
        var paste = held is not null && _session.Selection.Items.Any(e => Properties.Fits(held, e));
        if (alike.Count == 0 && !copy && !paste) return;
        Rows.Add(new HeadingRow("LIKE THIS ONE"));
        foreach (var a in alike) Rows.Add(new ActionRow(a.Label, $"{a.Command}, all over the map.", () => _session.SelectAlike(a.By)));
        if (copy) Rows.Add(new ActionRow("Copy its properties", "Remembers how it looks and what it does (not where it is, nor its tag or id), to give the same to others: select them and choose Paste (Ctrl+Shift+C).", _session.CopyProperties));
        if (paste) Rows.Add(new ActionRow(one is null ? "Paste the copied properties onto them" : "Paste the copied properties onto it", $"Gives the selection {Properties.Of(held!)} looks and settings, as one undo step (Ctrl+Shift+V).", _session.PasteProperties));
    }

    private void AddThing(Thing t)
    {
        // A prop from a 3D model (Phase 11): which way the model faces, for models made facing another way than Doom's forward.
        if (ModelProps.Of(_session.Project, t.Type) is { } model)
        {
            double[] turns = [0, 90, 180, 270];
            Rows.Add(new ChoiceRow("Model faces", "Which way the model is turned, for one made facing another way than Doom's forward (Blender's usual front is a quarter turn off). Every one placed turns with it, in the editor and in GZDoom (MODELDEF AngleOffset).",
                ["As it was made", "A quarter turn left", "Turned around", "A quarter turn right"],
                () => Math.Max(0, Array.IndexOf(turns, ModelProps.TurnOf(_session.Project.CopiedNow(), model))),
                i => _session.TurnModel(t, turns[i])));
        }

        // A Duke Nukem 3D enemy (D55): which Doom monster's behaviour it has.
        if (DukeBorrowing.Of(_session.Project, t.Type) is { } duke)
        {
            var behaviours = WadSmith.Core.Build.DukeEnemies.Behaviours;
            Rows.Add(new ChoiceRow("Behaves like", "The Doom monster whose mind, attacks, health and sounds it has; it keeps Duke's look. Duke's own behaviour lives in scripts GZDoom cannot run.", [.. behaviours.Select(b => b.Name)],
                () => Math.Max(0, behaviours.ToList().FindIndex(b => b.Class == (DukeBorrowing.Of(_session.Project, t.Type)?.Behaviour ?? duke.Behaviour))),
                i => _session.ChangeDukeBehaviour(t, behaviours[i].Class)));
        }

        Rows.Add(new NumberRow("Type", "Which monster, item or decoration this is (editor number, type).", () => t.Type, v => Set(t, "Change thing type", e => e.Type, (e, x) => e.Type = x, (int)v), 1, 0, 65535));
        Rows.Add(new NumberRow("Facing", "The direction it faces, in degrees: 0 east, 90 north (angle).", () => t.Angle, v => Set(t, "Turn thing", e => e.Angle, (e, x) => e.Angle = x, (int)v), 45, -360, 360));
        Rows.Add(new NumberRow("Height above floor", "For things that float or hang (height).", () => (decimal)t.Height, v => Set(t, "Change thing height", e => e.Height, (e, x) => e.Height = x, (double)v), 8));
        Rows.Add(new FlagRow("Appears on Easy", "Included on the two easiest difficulties (skill1, skill2).", () => t.Flag("skill1") || t.Flag("skill2"), on => Flags(t, ["skill1", "skill2"], on)));
        Rows.Add(new FlagRow("Appears on Normal", "Included on the middle difficulty (skill3).", () => t.Flag("skill3"), on => Flags(t, ["skill3"], on)));
        Rows.Add(new FlagRow("Appears on Hard", "Included on the two hardest difficulties (skill4, skill5).", () => t.Flag("skill4") || t.Flag("skill5"), on => Flags(t, ["skill4", "skill5"], on)));
        Rows.Add(new FlagRow("Deaf", "Waits until it sees the player instead of coming when it hears shots (ambush).", () => t.Flag("ambush"), on => Flags(t, ["ambush"], on)));
        Rows.Add(new FlagRow("In single player", "Included when one person plays (single; off = multiplayer only).", () => t.Flag("single"), on => Flags(t, ["single"], on)));
        Rows.Add(new FlagRow("In co-op", "Included when friends play the map together (coop).", () => t.Flag("coop"), on => Flags(t, ["coop"], on)));
        Rows.Add(new FlagRow("In deathmatch", "Included when players fight each other on the map (dm).", () => t.Flag("dm"), on => Flags(t, ["dm"], on)));
        if (_session.Project.ClassicFormat) return;
        Rows.Add(new FlagRow("Friendly", "A monster that fights for the player instead of against them (friend).", () => t.Flag("friend"), on => Flags(t, ["friend"], on)));
        Rows.Add(new FlagRow("Asleep until woken", "Does nothing at all until a script or a wall's action wakes it with its id (dormant).", () => t.Flag("dormant"), on => Flags(t, ["dormant"], on)));
        Rows.Add(new NumberRow("Id", "A number a wall's action or a script finds this object by, to wake it, move it or send something to it (thing id, TID).", () => t.Tid, v => Set(t, "Change the object's id", e => e.Tid, (e, x) => e.Tid = x, (int)v), 1, 0, 32767));
    }

    private void Flag(Element e, string name, bool on, string label) => _session.History.Run(new SetProperty<Element, bool>(e, label, x => x.Flag(name), (x, v) => x.SetFlag(name, v), on));

    private void Flags(Thing t, string[] names, bool on) =>
        _session.History.Run(new Batch(on ? "Show on a difficulty" : "Hide on a difficulty", names.Select(n => (IEdit)new SetProperty<Element, bool>(t, n, x => x.Flag(n), (x, v) => x.SetFlag(n, v), on)).ToList()));
}
