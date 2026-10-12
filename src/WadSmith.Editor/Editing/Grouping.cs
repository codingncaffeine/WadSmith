using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>A named group of rooms and objects (§3.4), and whether it is locked.</summary>
/// <param name="Name">What the user called it ("Courtyard").</param>
/// <param name="Rooms">Its rooms.</param>
/// <param name="Things">Its objects.</param>
/// <param name="Locked">Whether any of it is locked against changes.</param>
public sealed record MapGroup(string Name, IReadOnlyList<Sector> Rooms, IReadOnlyList<Thing> Things, bool Locked)
{
    public IEnumerable<Element> Members => Rooms.Cast<Element>().Concat(Things);
}

/// <summary>
/// Groups and locks (§3.4): a group is a name on each of its rooms and objects, kept in the map as a field GZDoom leaves
/// alone (<c>user_wadsmith_group</c>), so it goes with the map and with copies; a lock is a field too, and a locked room
/// or object cannot be picked or changed by dragging its neighbours, until it is unlocked.
/// </summary>
public static class Grouping
{
    public const string GroupField = "user_wadsmith_group";
    public const string LockField = "user_wadsmith_locked";

    /// <summary>The name of the group an element is in, or null.</summary>
    public static string? GroupOf(Element e) => e.Extra.TryGet(GroupField, out var v) && v.Kind == UdmfValueKind.String && v.AsString().Length > 0 ? v.AsString() : null;

    public static bool IsLocked(Element e) => e.Flag(LockField);

    /// <summary>What a selection puts in a group: its rooms and objects (walls and corners belong to their rooms).</summary>
    public static IReadOnlyList<Element> MembersOf(IEnumerable<Element> selection) => [.. selection.Where(e => e is Sector or Thing && e.OnMap)];

    /// <summary>Puts the members in the group <paramref name="name"/> (null or empty: in none, and unlocked).</summary>
    public static void Name(StructureEdit edit, IEnumerable<Element> members, string? name)
    {
        var clean = name?.Trim();
        foreach (var m in members)
        {
            edit.Field(m, GroupField, string.IsNullOrEmpty(clean) ? (UdmfValue?)null : UdmfValue.Str(clean));
            if (string.IsNullOrEmpty(clean)) edit.Field(m, LockField, null);
        }
    }

    /// <summary>Locks or unlocks the members.</summary>
    public static void Lock(StructureEdit edit, IEnumerable<Element> members, bool locked)
    {
        foreach (var m in members) edit.Field(m, LockField, locked ? UdmfValue.Bool(true) : (UdmfValue?)null);
    }

    /// <summary>The map's groups, from a frame (any thread), by name.</summary>
    public static IReadOnlyList<MapGroup> In(MapFrame f)
    {
        var rooms = new Dictionary<string, List<Sector>>();
        var things = new Dictionary<string, List<Thing>>();
        var locked = new HashSet<string>();
        foreach (var s in f.Sectors)
        {
            if (s.Element is null || GroupIn(s.Extra) is not { } n) continue;
            if (!rooms.TryGetValue(n, out var list)) rooms[n] = list = [];
            list.Add(s.Element);
            if (Locked(s.Extra)) locked.Add(n);
        }

        foreach (var t in f.Things)
        {
            if (t.Element is null || GroupIn(t.Extra) is not { } n) continue;
            if (!things.TryGetValue(n, out var list)) things[n] = list = [];
            list.Add(t.Element);
            if (Locked(t.Extra)) locked.Add(n);
        }

        return [.. rooms.Keys.Union(things.Keys).Order(StringComparer.CurrentCultureIgnoreCase).Select(n => new MapGroup(n, rooms.GetValueOrDefault(n) ?? [], things.GetValueOrDefault(n) ?? [], locked.Contains(n)))];
    }

    /// <summary>The group named in a frame's fields (any thread), or null.</summary>
    public static string? GroupIn(KeyValuePair<string, UdmfValue>[] extra) => extra.FirstOrDefault(kv => kv.Key == GroupField) is { Key: not null } kv && kv.Value.Kind == UdmfValueKind.String && kv.Value.AsString().Length > 0 ? kv.Value.AsString() : null;

    private static bool Locked(KeyValuePair<string, UdmfValue>[] extra) => extra.Any(kv => kv.Key == LockField && kv.Value.Kind == UdmfValueKind.Boolean && kv.Value.AsBool());

    /// <summary>
    /// The locked group (or "a locked room") whose room moving these corners would change, or null. Reads the frame only:
    /// any thread, as a drag's worker gathers what moves.
    /// </summary>
    public static string? HeldBy(MapGeometry g, IEnumerable<Vertex> corners)
    {
        var f = g.Frame;
        foreach (var v in corners)
        {
            var slot = v.Slot;
            if (slot < 0 || slot >= f.Vertices.Count) continue;
            var (x, y) = (f.Vertices[slot].X, f.Vertices[slot].Y);
            foreach (var i in g.LineSlotsIn(x - 0.5, y - 0.5, x + 0.5, y + 0.5))
            {
                var l = f.Lines[i];
                if (l.Element is null || (l.V1 != slot && l.V2 != slot)) continue;
                foreach (var side in (int[])[l.Front, l.Back])
                {
                    if (side < 0) continue;
                    var sector = f.Sides[side].Sector;
                    if (sector < 0) continue;
                    if (Locked(f.Sectors[sector].Extra)) return GroupIn(f.Sectors[sector].Extra) ?? "a locked room";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an element is hidden with its group (this session only): a room or object of a hidden group; a wall or face
    /// with every room it has hidden; a corner whose walls are all hidden.
    /// </summary>
    public static bool Hides(MapGeometry geometry, Element e, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0) return false;
        bool Room(Sector s) => GroupOf(s) is { } n && hidden.Contains(n);
        bool Wall(Line l) => Room(l.Front.Sector) && (l.Back is null || Room(l.Back.Sector));
        return e switch
        {
            Sector s => Room(s),
            Thing t => GroupOf(t) is { } n && hidden.Contains(n),
            Line l => Wall(l),
            Side { Line: { } l } => Wall(l),
            Vertex v => geometry.LinesIn(v.X - 0.5, v.Y - 0.5, v.X + 0.5, v.Y + 0.5).Where(l => l.OnMap && (ReferenceEquals(l.Start, v) || ReferenceEquals(l.End, v))).All(Wall),
            _ => false,
        };
    }

    /// <summary>
    /// Whether an element is held by a lock: a locked room or object; a wall or face whose rooms are all locked; a corner
    /// touching a locked room (moving it would change that room).
    /// </summary>
    public static bool Holds(MapGeometry geometry, Element e) => e switch
    {
        Sector s => IsLocked(s),
        Thing t => IsLocked(t),
        Line l => IsLocked(l.Front.Sector) && (l.Back is null || IsLocked(l.Back.Sector)),
        Side { Line: { } l } => Holds(geometry, l),
        Vertex v => geometry.LinesIn(v.X - 0.5, v.Y - 0.5, v.X + 0.5, v.Y + 0.5).Any(l => l.OnMap && (ReferenceEquals(l.Start, v) || ReferenceEquals(l.End, v)) && (IsLocked(l.Front.Sector) || (l.Back is { } b && IsLocked(b.Sector)))),
        _ => false,
    };
}
