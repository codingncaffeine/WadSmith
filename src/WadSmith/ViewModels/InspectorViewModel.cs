using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WadSmith.Core.Games;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

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

    public TextRow(string label, string tooltip, Func<string> get, Action<string> set)
        : base(label, tooltip)
    {
        _get = get;
        _set = set;
        Refresh();
    }

    [ObservableProperty]
    private string _value = "";

    /// <summary>Called by the view when editing ends.</summary>
    public void Commit()
    {
        var v = Value.Trim().ToUpperInvariant();
        if (v.Length == 0) v = "-";
        if (v != _get()) _set(v);
        Value = _get();
    }

    public override void Refresh() => Value = _get();
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

    public InspectorViewModel(EditorSession session)
    {
        _session = session;
        session.Selection.Changed += Rebuild;
        session.Map.Changed += _ =>
        {
            foreach (var r in Rows) r.Refresh();
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
        var items = _session.Selection.Items;
        if (items.Count == 0)
        {
            Heading = "Nothing selected";
            Summary = "Click a room, a wall, a corner or a thing to see what it is and change it.";
            return;
        }

        if (items.Count > 1)
        {
            Heading = $"{items.Count} things selected";
            Summary = string.Join(", ", items.GroupBy(i => Name(i)).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}{(g.Count() > 1 ? "s" : "")}"));
            return;
        }

        switch (items[0])
        {
            case Sector s:
                Heading = "Room area";
                Summary = "Its floor, ceiling and light.";
                AddSector(s);
                break;
            case Line l:
                Heading = l.Back is null ? "Solid wall" : "Wall between two areas";
                Summary = l.Back is null ? "Nothing can pass through it." : "Players can walk through unless it blocks them.";
                AddLine(l);
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
    }

    private static string Name(Element e) => e switch
    {
        Sector => "Area",
        Line => "Wall",
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

    private void AddLine(Line l)
    {
        Rows.Add(new InfoRow("Length", "How long the wall is, in map units.", () => l.Length.ToString("0.#", CultureInfo.InvariantCulture)));
        Rows.Add(new InfoRow("Action", "What happens when the wall is used, crossed or shot (line special).", () => l.Special == 0 ? "None" : $"{ActionCatalog.Get(l.Special)?.Name ?? "Unknown"} ({l.Special.ToString(CultureInfo.InvariantCulture)})"));
        Rows.Add(new FlagRow("Blocks movement", "Nothing crosses this wall, even if both sides are open (blocking).", () => l.Flag("blocking"), on => Flag(l, "blocking", on, "Change blocking")));
        Rows.Add(new FlagRow("Blocks monsters", "Players pass, monsters do not (blockmonsters).", () => l.Flag("blockmonsters"), on => Flag(l, "blockmonsters", on, "Change monster blocking")));
        Rows.Add(new FlagRow("Hidden on the map", "Not drawn on the in-game map (dontdraw).", () => l.Flag("dontdraw"), on => Flag(l, "dontdraw", on, "Change map visibility")));
        Rows.Add(new FlagRow("Counts as a secret", "Shown as a solid wall on the in-game map so it does not give a secret away (secret).", () => l.Flag("secret"), on => Flag(l, "secret", on, "Change secret")));
        AddSide("Front", l.Front);
        if (l.Back is { } back) AddSide("Back", back);
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
