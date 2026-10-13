using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>What "Select alike" matches the chosen element by.</summary>
public enum Alike
{
    /// <summary>Rooms with the same floor picture.</summary>
    FloorPicture,

    /// <summary>Rooms with the same ceiling picture.</summary>
    CeilingPicture,

    /// <summary>Rooms with the floor at the same height.</summary>
    FloorHeight,

    /// <summary>Rooms with the ceiling at the same height.</summary>
    CeilingHeight,

    /// <summary>Rooms with the same brightness.</summary>
    Brightness,

    /// <summary>Rooms joined to it, room to room, with the floor at the same height (Eureka's "select same h").</summary>
    JoinedFloorHeight,

    /// <summary>Walls with the chosen wall's picture on any part of either face.</summary>
    WallPicture,

    /// <summary>The walls the chosen wall runs on into, corner by corner, up to where the way forks (Eureka's "select path").</summary>
    WallsAlong,

    /// <summary>Objects of the same kind.</summary>
    ObjectKind,
}

/// <summary>
/// Selections made from the whole map (Eureka's select all, invert and select-alike): each reads only a geometry's frame,
/// so it runs on a worker; the session sets what it returns.
/// </summary>
public static class Selecting
{
    /// <summary>Everything of the kind <paramref name="only"/> picks: with anything, the rooms (which carry their walls), the walls no room holds, the lone corners and the objects.</summary>
    public static IReadOnlyList<Element> All(MapGeometry geometry, PickOnly only)
    {
        var (minX, minY, maxX, maxY) = geometry.Extent();
        return BoxSelection.Pick(geometry, minX - 1, minY - 1, maxX + 1, maxY + 1, only);
    }

    /// <summary>
    /// Everything not in <paramref name="selected"/>, of the kinds it holds (rooms for rooms, walls for walls, faces as
    /// their walls, corners for corners, objects for objects); of the kind <paramref name="only"/> picks when it picks
    /// one; everything when nothing is selected.
    /// </summary>
    public static IReadOnlyList<Element> Invert(MapGeometry geometry, IReadOnlyCollection<Element> selected, PickOnly only)
    {
        var kinds = new HashSet<PickOnly>();
        if (only != PickOnly.Anything) kinds.Add(only);
        else
        {
            foreach (var e in selected)
            {
                kinds.Add(e switch
                {
                    Sector => PickOnly.Rooms,
                    Line or Side => PickOnly.Walls,
                    Vertex => PickOnly.Corners,
                    _ => PickOnly.Objects,
                });
            }
        }

        if (kinds.Count == 0) return All(geometry, only);
        var had = new HashSet<Element>(selected.Select(e => e is Side { Line: { } l } ? l : e), ReferenceEqualityComparer.Instance);
        var f = geometry.Frame;
        var picked = new List<Element>();
        if (kinds.Contains(PickOnly.Rooms)) Add(f.Sectors.Select(s => (Element?)s.Element));
        if (kinds.Contains(PickOnly.Walls)) Add(f.Lines.Select(l => (Element?)l.Element));
        if (kinds.Contains(PickOnly.Corners)) Add(f.Vertices.Select(v => (Element?)v.Element));
        if (kinds.Contains(PickOnly.Objects)) Add(f.Things.Select(t => (Element?)t.Element));
        return picked;

        void Add(IEnumerable<Element?> all)
        {
            foreach (var e in all)
            {
                if (e is { OnMap: true } && !had.Contains(e)) picked.Add(e);
            }
        }
    }

    /// <summary>Whether <paramref name="by"/> applies to <paramref name="seed"/> (a room's measure to a room, a wall's to a wall or a face, a kind to an object).</summary>
    public static bool Fits(Element seed, Alike by) => by switch
    {
        Alike.WallPicture => seed is Line or Side,
        Alike.WallsAlong => seed is Line or Side,
        Alike.ObjectKind => seed is Thing,
        _ => seed is Sector,
    };

    /// <summary>
    /// The elements like <paramref name="seed"/> by <paramref name="by"/>, the seed first; just the seed when nothing else
    /// matches, and nothing when the measure does not apply to it (see <see cref="Fits"/>).
    /// </summary>
    public static IReadOnlyList<Element> Like(MapGeometry geometry, Element seed, Alike by)
    {
        if (!Fits(seed, by) || !seed.OnMap) return [];
        var f = geometry.Frame;
        switch (by)
        {
            case Alike.ObjectKind:
            {
                var type = f.Things[seed.Slot].Type;
                return Ordered(seed, f.Things.Where(t => t.Element is { OnMap: true } && t.Type == type).Select(t => (Element)t.Element!));
            }

            case Alike.WallPicture:
            {
                if (WallPicture(f, seed) is not { } picture) return [Wall(seed)];
                return Ordered(Wall(seed), f.Lines.Where(l => l.Element is { OnMap: true } && Wears(f, l, picture)).Select(l => (Element)l.Element!));
            }

            case Alike.WallsAlong:
                return Along(f, (Line)Wall(seed));

            case Alike.JoinedFloorHeight:
                return Joined(f, seed.Slot);

            default:
            {
                var s = f.Sectors[seed.Slot];
                Func<SectorFrame, bool> same = by switch
                {
                    Alike.FloorPicture => o => string.Equals(o.FloorTexture, s.FloorTexture, StringComparison.OrdinalIgnoreCase),
                    Alike.CeilingPicture => o => string.Equals(o.CeilingTexture, s.CeilingTexture, StringComparison.OrdinalIgnoreCase),
                    Alike.FloorHeight => o => o.FloorHeight == s.FloorHeight,
                    Alike.CeilingHeight => o => o.CeilingHeight == s.CeilingHeight,
                    _ => o => o.Light == s.Light,
                };
                return Ordered(seed, f.Sectors.Where(o => o.Element is { OnMap: true } && same(o)).Select(o => (Element)o.Element!));
            }
        }
    }

    /// <summary>The picture a wall is known by: its face's middle when it is solid, else the first of the face's lower, upper and middle parts that has one; the front face's unless a face was chosen.</summary>
    public static string? WallPicture(MapFrame f, Element seed)
    {
        var side = seed switch
        {
            Side s => f.Sides[s.Slot],
            Line l => f.Lines[l.Slot].Front >= 0 ? f.Sides[f.Lines[l.Slot].Front] : (SideFrame?)null,
            _ => null,
        };
        if (side is not { } face) return null;
        var line = f.Lines[seed is Side sd ? sd.Line!.Slot : seed.Slot];
        string[] parts = line.Back < 0 ? [face.Middle] : [face.Bottom, face.Top, face.Middle];
        return parts.FirstOrDefault(p => p != "-" && p.Length > 0);
    }

    private static Element Wall(Element seed) => seed is Side { Line: { } l } ? l : seed;

    private static bool Wears(MapFrame f, LineFrame l, string picture)
    {
        foreach (var side in new[] { l.Front, l.Back })
        {
            if (side < 0) continue;
            var s = f.Sides[side];
            if (string.Equals(s.Top, picture, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Middle, picture, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Bottom, picture, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static List<Element> Ordered(Element seed, IEnumerable<Element> found)
    {
        var list = new List<Element> { seed };
        list.AddRange(found.Where(e => !ReferenceEquals(e, seed)));
        return list;
    }

    /// <summary>
    /// The walls joined end to end with <paramref name="seed"/>: from each end, on through the corner while exactly one
    /// other wall of the same sort meets it (solid walls follow solid walls, open ones open ones), stopping where the way
    /// forks, ends, or comes back round.
    /// </summary>
    private static List<Element> Along(MapFrame f, Line seed)
    {
        var solid = f.Lines[seed.Slot].Back < 0;
        var at = new Dictionary<int, List<int>>();
        for (var i = 0; i < f.Lines.Count; i++)
        {
            var l = f.Lines[i];
            if (l.Element is not { OnMap: true } || (l.Back < 0) != solid) continue;
            Add(l.V1, i);
            Add(l.V2, i);
        }

        var found = new List<Element> { seed };
        var seen = new HashSet<int> { seed.Slot };
        var start = f.Lines[seed.Slot];
        foreach (var end in new[] { start.V1, start.V2 })
        {
            var (corner, from) = (end, seed.Slot);
            while (at.TryGetValue(corner, out var meeting) && meeting.Count == 2)
            {
                var next = meeting[0] == from ? meeting[1] : meeting[0];
                if (!seen.Add(next)) break;
                found.Add(f.Lines[next].Element!);
                var n = f.Lines[next];
                corner = n.V1 == corner ? n.V2 : n.V1;
                from = next;
            }
        }

        return found;

        void Add(int v, int line)
        {
            if (!at.TryGetValue(v, out var list)) at[v] = list = [];
            list.Add(line);
        }
    }

    /// <summary>The rooms reached from <paramref name="seed"/> across open walls, room to room, keeping to rooms whose floor is at the seed's height.</summary>
    private static List<Element> Joined(MapFrame f, int seed)
    {
        var height = f.Sectors[seed].FloorHeight;
        var next = new Dictionary<int, List<int>>();
        foreach (var l in f.Lines)
        {
            if (l.Element is not { OnMap: true } || l.Front < 0 || l.Back < 0) continue;
            var (a, b) = (f.Sides[l.Front].Sector, f.Sides[l.Back].Sector);
            if (a == b) continue;
            if (!next.TryGetValue(a, out var la)) next[a] = la = [];
            if (!next.TryGetValue(b, out var lb)) next[b] = lb = [];
            la.Add(b);
            lb.Add(a);
        }

        var found = new List<Element>();
        var seen = new HashSet<int> { seed };
        var queue = new Queue<int>([seed]);
        while (queue.TryDequeue(out var s))
        {
            found.Add(f.Sectors[s].Element!);
            if (!next.TryGetValue(s, out var around)) continue;
            foreach (var o in around)
            {
                if (f.Sectors[o].Element is { OnMap: true } && f.Sectors[o].FloorHeight == height && seen.Add(o)) queue.Enqueue(o);
            }
        }

        return found;
    }
}
