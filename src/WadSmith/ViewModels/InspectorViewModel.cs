using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core.Games;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.App.ViewModels;

/// <summary>One line of the Inspector: a friendly label, its classic name for the tooltip, and a value.</summary>
internal abstract partial class InspectorRow(string label, string tooltip) : ObservableObject
{
    public string Label { get; } = label;

    public string Tooltip { get; } = tooltip;

    /// <summary>Re-reads the value after any change to the map (an undo, an edit elsewhere).</summary>
    public abstract void Refresh();
}

/// <summary>A number the user can type or step; every change is one undoable edit.</summary>
internal sealed partial class NumberRow : InspectorRow
{
    private readonly Func<decimal> _get;
    private readonly Action<decimal> _set;
    private readonly Func<string>? _hint;
    private bool _refreshing;

    public NumberRow(string label, string tooltip, Func<decimal> get, Action<decimal> set, decimal step, decimal min = -32768, decimal max = 32767, Func<string>? hint = null)
        : base(label, tooltip)
    {
        _get = get;
        _set = set;
        _hint = hint;
        Step = step;
        Minimum = min;
        Maximum = max;
        Refresh();
    }

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
        _refreshing = false;
    }
}

/// <summary>A name (a texture) the user can type; committed when the field loses focus or Enter is pressed.</summary>
internal sealed partial class TextRow : InspectorRow
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Func<string>? _hint;

    public TextRow(string label, string tooltip, Func<string> get, Action<string> set, Func<string>? hint = null)
        : base(label, tooltip)
    {
        _get = get;
        _set = set;
        _hint = hint;
        Refresh();
    }

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _hintText = "";

    /// <summary>Called by the view when editing ends.</summary>
    public void Commit()
    {
        var v = Value.Trim().ToUpperInvariant();
        if (v.Length == 0) v = "-";
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
    private readonly EditorSession _session;
    private Func<string>? _summaryOf;
    private string _kind = "";

    /// <summary>Raised when the user asks to make a room a teleport pad: the editor then waits for the arrival spot.</summary>
    public event Action<Sector>? TeleportFrom;

    /// <summary>Raised when the user asks for a switch that works a door or a lift: the editor then waits for the wall.</summary>
    public event Action<Sector>? SwitchFor;

    /// <summary>Raised when the user asks to place the key for a locked door (its colour): the editor then waits for the spot.</summary>
    public event Action<string>? KeyFor;

    /// <summary>What the selection is, as far as the Inspector's layout goes: a door, an exit, a wall, a room…</summary>
    private string KindOf() => _session.Selection.Items switch
    {
        [Sector s] => Doors.Recognise(_session.Geometry, s) is { } door ? $"door {door.Settings.Lock}" : Lifts.Recognise(_session.Geometry, s) is not null ? "lift" : WallWindows.Recognise(_session.Geometry, s) is not null ? "window" : Secrets.IsSecret(s) ? "secret" : Rooms.Island(_session.Geometry, s) is not null ? "island" : "room",
        [Line l] => Exits.IsExit(l) ? "exit" : Teleports.IsTeleport(l) ? "teleporter" : Switches.Recognise(_session.Geometry, l) is not null ? "switch" : l.Special == 0 ? "wall" : "wall with an action",
        [Thing t] => Teleports.IsArrival(t) ? "arrival" : "thing",
        [Side { Line: { } l }] => Exits.IsExit(l) ? "exit face" : "face",
        [var one] => one.GetType().Name,
        var many => many.Count.ToString(CultureInfo.InvariantCulture),
    };

    public InspectorViewModel(EditorSession session)
    {
        _session = session;
        session.Selection.Changed += Rebuild;

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

    public ObservableCollection<InspectorRow> Rows { get; } = [];

    [ObservableProperty]
    private string _heading = "";

    [ObservableProperty]
    private string _summary = "";

    private void Rebuild()
    {
        Rows.Clear();
        _summaryOf = null;
        _kind = KindOf();
        var items = _session.Selection.Items;
        if (items.Count == 0)
        {
            Heading = "Nothing selected";
            Summary = "Click a room, a wall, a corner or a thing to see what it is and change it.";
            return;
        }

        if (items.Count > 1)
        {
            var kinds = items.Select(Name).Distinct().ToList();
            Heading = kinds.Count == 1 ? $"{items.Count} {kinds[0].ToLowerInvariant()}s selected" : $"{items.Count} things selected";
            Summary = string.Join(", ", items.GroupBy(i => Name(i)).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}{(g.Count() > 1 ? "s" : "")}"));
            if (items.All(i => i is Sector)) Rows.Add(new ActionRow("Join them into one room", "The rooms become the first one you selected: the walls between them go, and its floor, ceiling and light hold for all of it (J).", _session.JoinRooms));
            AddShaping();
            Rows.Add(new ActionRow("Delete them", "Deletes everything selected so the map stays whole (Delete).", _session.DeleteSelection));
            return;
        }

        switch (items[0])
        {
            case Sector s when Doors.Recognise(_session.Geometry, s) is not null:
                Heading = "Door";
                _summaryOf = () => Doors.Recognise(_session.Geometry, s) is { } d ? Describe(d) : "No longer a door.";
                Summary = _summaryOf();
                AddDoor(s);
                AddDoorKey(s);
                Rows.Add(new ActionRow("Add a switch that opens it", "Click a solid wall next: a piece of it becomes a switch the player uses to open this door from there (the door gets a tag; its own walls still open it).", () => SwitchFor?.Invoke(s)));
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s when Lifts.Recognise(_session.Geometry, s) is not null:
                Heading = "Lift";
                _summaryOf = () => Lifts.Recognise(_session.Geometry, s) is { } lift ? Describe(lift) : "No longer a lift.";
                Summary = _summaryOf();
                AddLift(s);
                Rows.Add(new ActionRow("Add a switch that lowers it", "Click a solid wall next: a piece of it becomes a switch the player uses to send this lift down from there.", () => SwitchFor?.Invoke(s)));
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s when WallWindows.Recognise(_session.Geometry, s) is not null:
                Heading = "Window";
                _summaryOf = () => WallWindows.Recognise(_session.Geometry, s) is { } window ? Describe(window) : "No longer a window: the player can climb into it, or it is closed.";
                Summary = _summaryOf();
                AddWindow(s);
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s when Secrets.IsSecret(s):
                Heading = "Secret area";
                _summaryOf = () => Secrets.Recognise(_session.Geometry, s) is { } secret ? Describe(secret) : "No longer a secret.";
                Summary = _summaryOf();
                AddSecret(s);
                Rows.Add(new HeadingRow("ITS AREA"));
                AddSector(s);
                break;
            case Sector s:
                Heading = "Room area";
                Summary = "Its floor, ceiling and light.";
                AddSector(s);
                Rows.Add(new ActionRow("Make it a secret", "Finding it counts toward the secrets at the end of the map, and the walls into it show as solid walls on the in-game map so they do not give it away.", () => _session.MakeSecret(s)));
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
            case Line l when Switches.Recognise(_session.Geometry, l) is not null:
                Heading = "Switch";
                _summaryOf = () => Switches.Recognise(_session.Geometry, l) is { } sw ? Describe(sw) : "No longer a switch.";
                Summary = _summaryOf();
                AddSwitch(l);
                Rows.Add(new HeadingRow("THE WALL"));
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } switchBack) AddSide("Back", switchBack);
                break;
            case Line l when Teleports.IsTeleport(l):
                Heading = "Teleporter";
                _summaryOf = () => Teleports.Recognise(_session.Geometry, l) is { } t ? Describe(t) : "No longer a teleporter.";
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
                Heading = l.Back is null ? "Solid wall" : "Wall between two areas";
                Summary = l.Back is null ? "Nothing can pass through it." : "Players can walk through unless it blocks them.";
                AddLine(l);
                AddSide("Front", l.Front);
                if (l.Back is { } back) AddSide("Back", back);
                Rows.Add(new ActionRow(l.Back is null ? "Select its face" : "Select the front face", "Shows only the side you see from the front: its textures and how they line up.", () => _session.Selection.Set(l.Front)));
                if (l.Back is { } behind) Rows.Add(new ActionRow("Select the back face", "Shows only the side you see from the back: its textures and how they line up.", () => _session.Selection.Set(behind)));
                if (l.Special == 0)
                {
                    Rows.Add(l.Back is null
                        ? new ActionRow("Make it the exit switch", "The player uses this wall to end the map; it gets the game's exit switch texture (Exit_Normal).", () => _session.History.Record("Make the exit", e => Exits.Make(e, l, false, _session.Look)))
                        : new ActionRow("End the map when walked across", "Crossing this wall ends the map and goes on to the next one (Exit_Normal).", () => _session.History.Record("Make the exit", e => Exits.Make(e, l, false, _session.Look))));
                }

                break;
            case Side side:
                var solid = side.Line?.Back is null;
                Heading = "Wall face";
                Summary = solid ? "The side of a solid wall you see from its room: its texture and how it lines up." : "One side of a wall between two areas, as you see it from this side: its textures and how they line up.";
                AddFace(side);
                if (side.Line is { } wall)
                {
                    Rows.Add(new HeadingRow("THE WHOLE WALL"));
                    AddLine(wall);
                    Rows.Add(new ActionRow("Select the whole wall", "Selects the wall with both its faces, as clicking it on the map does.", () => _session.Selection.Set(wall)));
                    if (wall.Opposite(side) is { } other) Rows.Add(new ActionRow("Select the other face", "Shows the side of this wall you see from the other area.", () => _session.Selection.Set(other)));
                }

                break;
            case Thing t when Keys.ColourOf(t.Type) is { } colour:
                Heading = _session.Resources?.Things.Find(t.Type)?.Name ?? $"The {colour} key";
                _summaryOf = () => Keys.DoorsOpenedBy(_session.Geometry, t.Type).Select(l => l.Args[0] != 0 ? (object)l.Args[0] : l.Back?.Sector ?? (object)l).Distinct().Count() switch
                {
                    0 => $"No door is locked with the {colour} key yet: lock one in its Inspector (Locked with).",
                    1 => $"Opens the door locked with the {colour} key.",
                    var n => $"Opens the {n} doors locked with the {colour} key.",
                };
                Summary = _summaryOf();
                Rows.Add(new ActionRow("Select the doors it opens", "Selects every wall whose door this key opens.", () =>
                {
                    if (Keys.DoorsOpenedBy(_session.Geometry, t.Type) is { Count: > 0 } doors) _session.Selection.Set([.. doors]);
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
                break;
        }

        if (items[0] is Sector) AddShaping();

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

    private void AddSector(Sector s)
    {
        string Players() => string.Create(CultureInfo.InvariantCulture, $"room is {s.CeilingHeight - s.FloorHeight:0} tall ≈ {(s.CeilingHeight - s.FloorHeight) / PlayerHeight:0.#} players");
        Rows.Add(new NumberRow("Floor height", "How high the floor is, in map units (heightfloor). The player steps up 24 at most.", () => (decimal)s.FloorHeight, v => Set(s, "Change floor height", e => e.FloorHeight, (e, x) => e.FloorHeight = x, (double)v), 8));
        Rows.Add(new NumberRow("Ceiling height", "How high the ceiling is (heightceiling). The player is 56 tall.", () => (decimal)s.CeilingHeight, v => Set(s, "Change ceiling height", e => e.CeilingHeight, (e, x) => e.CeilingHeight = x, (double)v), 8, hint: Players));
        Rows.Add(new NumberRow("Brightness", "0 is dark, 255 full light (lightlevel). Doom's rooms are usually around 160.", () => s.Light, v => Set(s, "Change brightness", e => e.Light, (e, x) => e.Light = x, (int)v), 16, 0, 255));
        Rows.Add(new TextRow("Floor texture", "The picture on the floor (texturefloor).", () => s.FloorTexture, v => Set(s, "Change floor texture", e => e.FloorTexture, (e, x) => e.FloorTexture = x, v)));
        Rows.Add(new TextRow("Ceiling texture", "The picture on the ceiling (textureceiling).", () => s.CeilingTexture, v => Set(s, "Change ceiling texture", e => e.CeilingTexture, (e, x) => e.CeilingTexture = x, v)));
        Rows.Add(new FlagRow("Counts as a secret", "Finding this area counts toward the secrets at the end of the map (sector type 1024).", () => (s.Special & 1024) != 0, on => Set(s, on ? "Make a secret" : "Unmake a secret", e => e.Special, (e, x) => e.Special = x, on ? s.Special | 1024 : s.Special & ~1024)));
        Rows.Add(new InfoRow("Effect", "What the area does to the player or its light (sector type).", () => (s.Special & ~1024) == 0 ? "None" : (s.Special & ~1024).ToString(CultureInfo.InvariantCulture)));
        Rows.Add(new NumberRow("Tag", "A number that links a switch or trigger to this area (sector tag, id).", () => s.Tag, v => Set(s, "Change tag", e => e.Tag, (e, x) => e.Tag = x, (int)v), 1, 0, 65535));
    }

    private static string Describe(Lift lift)
    {
        var ways = string.Join(", ", Triggers.Ways(lift.Triggers));
        var wait = string.Create(CultureInfo.InvariantCulture, $"{lift.Settings.Delay / 35.0:0.#} s");
        return string.Create(CultureInfo.InvariantCulture, $"Goes down to {lift.GoesDownTo:0} when set off from {ways}{(lift.Monsters ? " (monsters too)" : "")}, waits {wait} and comes back up.");
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

    /// <summary>For a locked door: place its key when the map has none of that colour, else select the key.</summary>
    private void AddDoorKey(Sector area)
    {
        if (_session.Look != Look.For("doom") || Doors.Recognise(_session.Geometry, area) is not { Settings.Lock: not 0 } door || Keys.ColourOfLock(door.Settings.Lock) is not { } colour) return;
        var keys = Keys.OfColour(_session.Geometry, colour);
        if (keys.Count == 0) Rows.Add(new ActionRow($"Place the {colour} key…", $"Click the map next where the {colour} keycard goes: somewhere the player reaches before this door.", () => KeyFor?.Invoke(colour)));
        else Rows.Add(new ActionRow($"Select its key", $"Selects the {colour} key{(keys.Count > 1 ? "s" : "")} that open{(keys.Count > 1 ? "" : "s")} this door.", () => _session.Selection.Set([.. Keys.OfColour(_session.Geometry, colour)])));
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

    private static string Describe(Switch s)
    {
        var what = s.Targets.Count switch
        {
            0 => "",
            1 => string.Create(CultureInfo.InvariantCulture, $" (the area with tag {s.Wall.Args[0]})"),
            var n => string.Create(CultureInfo.InvariantCulture, $" ({n} areas with tag {s.Wall.Args[0]})"),
        };
        var none = s.Targets.Count == 0 && Switches.NamesAreas(s.Wall.Special) ? string.Create(CultureInfo.InvariantCulture, $" No area has tag {s.Wall.Args[0]}, so it does nothing.") : "";
        return $"{char.ToUpperInvariant(s.Does[0])}{s.Does[1..]}{what} when the player uses it{(s.Repeats ? ", again and again" : ", once")}.{none}";
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

    private static string Describe(Secret s)
    {
        var doors = s.Doors.Count switch { 0 => "", 1 => " A door leads into it.", var n => $" {n} doors lead into it." };
        return $"Counts toward the map's secrets when the player steps in.{doors}{(s.Hidden ? " Its way in shows as a solid wall on the in-game map." : " Its way in shows on the in-game map.")}";
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

    private static string Describe(WallWindow w)
    {
        var sill = w.Sill - w.Rooms.Min(r => r.FloorHeight);
        return string.Create(CultureInfo.InvariantCulture, $"An opening {w.Top - w.Sill:0} tall between two rooms, its sill {sill:0} above the lower floor{(w.Blocks ? "; nothing walks or flies through it" : "")}. The player sees through it and cannot climb in.");
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

    private static string Describe(Teleporter t)
    {
        var who = t.Monsters ? "the player or a monster" : "the player";
        var how = t.ByUse ? "uses it" : "walks across it from the front";
        var where = t.Arrivals.Count switch
        {
            0 => "nowhere: no arrival spot has its id or stands in an area with its tag",
            1 => string.Create(CultureInfo.InvariantCulture, $"the arrival spot at {t.Arrivals[0].X:0}, {t.Arrivals[0].Y:0}"),
            var n => $"the first of {n} arrival spots",
        };
        return $"Sends {who} who {how} to {where}{(t.Repeats ? "" : ", once")}.";
    }

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
    private static string Describe(Door d)
    {
        var remote = string.Join(", ", d.RemoteWays());
        var own = d.Own.Any() ? d.Monsters ? "when the player or a monster uses it" : "when the player uses it" : "";
        var who = remote.Length == 0 ? own : own.Length == 0 ? $"from {remote}" : $"{own}, or from {remote}";
        var key = Doors.DoomLocks.FirstOrDefault(k => k.Lock == d.Settings.Lock && k.Lock != 0).Name is { } name ? $", with the {name.ToLowerInvariant()}" : d.Settings.Lock != 0 ? $", with lock {d.Settings.Lock}" : "";
        var after = d.Settings.StaysOpen ? "stays open" : string.Create(CultureInfo.InvariantCulture, $"closes again after {d.Settings.Delay / 35.0:0.#} s");
        return $"Opens {who}{key}; {after}.";
    }

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
            Rows.Add(new ChoiceRow("Locked with", "The key the player needs to open it; the card or the skull of that colour both work, as in Doom (lock).", [.. Doors.DoomLocks.Select(k => k.Name)],
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
        if (l.Back is null)
        {
            Rows.Add(new FlagRow("Texture sits on the floor", "The texture's bottom edge rests on the floor, so it stays put when the ceiling moves, as door tracks need (lower unpegged, dontpegbottom).", () => l.Flag("dontpegbottom"), on => Flag(l, "dontpegbottom", on, "Change texture anchoring")));
        }
        else
        {
            Rows.Add(new FlagRow("Upper texture hangs from the ceiling", "The upper texture starts at the higher ceiling and runs down, instead of rising from the opening below it (upper unpegged, dontpegtop).", () => l.Flag("dontpegtop"), on => Flag(l, "dontpegtop", on, "Change texture anchoring")));
            Rows.Add(new FlagRow("Lower texture continues the wall above", "The lower texture is placed as if the wall ran down from the ceiling, so it carries on the pattern above it (lower unpegged, dontpegbottom).", () => l.Flag("dontpegbottom"), on => Flag(l, "dontpegbottom", on, "Change texture anchoring")));
        }
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
    }

    private void AddSide(string which, Side side)
    {
        Rows.Add(new TextRow($"{which}: upper texture", "Shown where the ceiling on the other side is lower (texturetop).", () => side.Top, v => Set(side, "Change upper texture", e => e.Top, (e, x) => e.Top = x, v)));
        Rows.Add(new TextRow($"{which}: middle texture", "The wall you see; on an open wall, a grate or fence (texturemiddle).", () => side.Middle, v => Set(side, "Change middle texture", e => e.Middle, (e, x) => e.Middle = x, v)));
        Rows.Add(new TextRow($"{which}: lower texture", "Shown where the floor on the other side is higher (texturebottom).", () => side.Bottom, v => Set(side, "Change lower texture", e => e.Bottom, (e, x) => e.Bottom = x, v)));
    }

    private void AddThing(Thing t)
    {
        Rows.Add(new NumberRow("Type", "Which monster, item or decoration this is (editor number, type).", () => t.Type, v => Set(t, "Change thing type", e => e.Type, (e, x) => e.Type = x, (int)v), 1, 0, 65535));
        Rows.Add(new NumberRow("Facing", "The direction it faces, in degrees: 0 east, 90 north (angle).", () => t.Angle, v => Set(t, "Turn thing", e => e.Angle, (e, x) => e.Angle = x, (int)v), 45, -360, 360));
        Rows.Add(new NumberRow("Height above floor", "For things that float or hang (height).", () => (decimal)t.Height, v => Set(t, "Change thing height", e => e.Height, (e, x) => e.Height = x, (double)v), 8));
        Rows.Add(new FlagRow("Appears on Easy", "Included on the two easiest difficulties (skill1, skill2).", () => t.Flag("skill1") || t.Flag("skill2"), on => Flags(t, ["skill1", "skill2"], on)));
        Rows.Add(new FlagRow("Appears on Normal", "Included on the middle difficulty (skill3).", () => t.Flag("skill3"), on => Flags(t, ["skill3"], on)));
        Rows.Add(new FlagRow("Appears on Hard", "Included on the two hardest difficulties (skill4, skill5).", () => t.Flag("skill4") || t.Flag("skill5"), on => Flags(t, ["skill4", "skill5"], on)));
        Rows.Add(new FlagRow("Deaf", "Waits until it sees the player instead of coming when it hears shots (ambush).", () => t.Flag("ambush"), on => Flags(t, ["ambush"], on)));
    }

    private void Flag(Element e, string name, bool on, string label) => _session.History.Run(new SetProperty<Element, bool>(e, label, x => x.Flag(name), (x, v) => x.SetFlag(name, v), on));

    private void Flags(Thing t, string[] names, bool on) =>
        _session.History.Run(new Batch(on ? "Show on a difficulty" : "Hide on a difficulty", names.Select(n => (IEdit)new SetProperty<Element, bool>(t, n, x => x.Flag(n), (x, v) => x.SetFlag(n, v), on)).ToList()));
}
