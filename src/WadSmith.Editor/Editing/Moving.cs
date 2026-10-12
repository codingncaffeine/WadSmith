using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// What moves when a selection is dragged: the corners (a wall's two ends, every corner of a room,
/// a face's wall) and the objects (those selected and those standing in a selected room), and the
/// walls the move carries whole or stretches (one end moving). Gathered from a geometry's frame, so a
/// worker can gather a selection of any size; only the starting places are read from the map itself,
/// by <see cref="MoveEdit"/> on the UI thread.
/// </summary>
public sealed class MoveSet
{
    private MoveSet(Vertex[] vertices, Thing[] things, (double X1, double Y1, double X2, double Y2)[] carried, (double MX, double MY, double FX, double FY)[] stretched, (double X, double Y)[] thingSpots)
    {
        Vertices = vertices;
        Things = things;
        Carried = carried;
        Stretched = stretched;
        ThingSpots = thingSpots;
    }

    public IReadOnlyList<Vertex> Vertices { get; }

    public IReadOnlyList<Thing> Things { get; }

    /// <summary>Walls with both ends moving, as they were (they move whole).</summary>
    public IReadOnlyList<(double X1, double Y1, double X2, double Y2)> Carried { get; }

    /// <summary>Walls with one end moving: that end (M) as it was, and the end that stays (F).</summary>
    public IReadOnlyList<(double MX, double MY, double FX, double FY)> Stretched { get; }

    /// <summary>Where the moving objects stood.</summary>
    public IReadOnlyList<(double X, double Y)> ThingSpots { get; }

    public int Count => Vertices.Count + Things.Count;

    public bool IsEmpty => Count == 0;

    /// <summary>The places (slots) of what is selected, taken on the UI thread, for <see cref="Gather"/>.</summary>
    public readonly record struct Seeds(int[] Lines, int[] Sectors, int[] Vertices, int[] Things)
    {
        /// <summary>The selection's places: walls, the walls of selected faces, rooms, corners and objects.</summary>
        public static Seeds Of(IEnumerable<Element> selection)
        {
            var (lines, sectors, vertices, things) = (new List<int>(), new List<int>(), new List<int>(), new List<int>());
            foreach (var e in selection)
            {
                switch (e)
                {
                    case Line l when l.OnMap:
                        lines.Add(l.Slot);
                        break;
                    case Side { Line: { OnMap: true } l }:
                        lines.Add(l.Slot);
                        break;
                    case Sector s when s.OnMap:
                        sectors.Add(s.Slot);
                        break;
                    case Vertex v when v.OnMap:
                        vertices.Add(v.Slot);
                        break;
                    case Thing t when t.OnMap:
                        things.Add(t.Slot);
                        break;
                }
            }

            return new Seeds([.. lines], [.. sectors], [.. vertices], [.. things]);
        }
    }

    /// <summary>
    /// Gathers what moves for a selection: the corners of its walls, faces' walls and rooms, its
    /// corners, its objects and the objects standing in its rooms. Reads only the geometry's frame,
    /// so a worker can gather a selection of any size.
    /// </summary>
    public static MoveSet Gather(MapGeometry geometry, Seeds seeds)
    {
        var f = geometry.Frame;
        var moving = new HashSet<int>();
        var thingSlots = new HashSet<int>();
        foreach (var v in seeds.Vertices)
        {
            if (v < f.Vertices.Count && f.Vertices[v].Element is not null) moving.Add(v);
        }

        foreach (var l in seeds.Lines)
        {
            if (l >= f.Lines.Count || f.Lines[l] is not { Element: not null } lf) continue;
            moving.Add(lf.V1);
            moving.Add(lf.V2);
        }

        // A room moves with every corner of its walls and every object standing in it.
        foreach (var s in seeds.Sectors)
        {
            if (s >= f.Sectors.Count || f.Sectors[s].Element is not { } room || geometry.WallBoundsAt(s) is not { } b) continue;
            foreach (var slot in geometry.LineSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                var lf = f.Lines[slot];
                if (SectorOf(lf.Front) == s || SectorOf(lf.Back) == s)
                {
                    moving.Add(lf.V1);
                    moving.Add(lf.V2);
                }
            }

            foreach (var t in geometry.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                if (ReferenceEquals(geometry.SectorAt(new MapPoint(f.Things[t].X, f.Things[t].Y)), room)) thingSlots.Add(t);
            }
        }

        foreach (var t in seeds.Things)
        {
            if (t < f.Things.Count && f.Things[t].Element is not null) thingSlots.Add(t);
        }

        // The walls the move touches: found round each moving corner, carried when both ends move.
        var carried = new List<(double, double, double, double)>();
        var stretched = new List<(double, double, double, double)>();
        var seen = new HashSet<int>();
        foreach (var slot in moving)
        {
            var v = f.Vertices[slot];
            foreach (var l in geometry.LineSlotsIn(v.X, v.Y, v.X, v.Y))
            {
                if (!seen.Add(l)) continue;
                var lf = f.Lines[l];
                if (lf.V1 != slot && lf.V2 != slot) continue;
                var (a, b) = (f.Vertices[lf.V1], f.Vertices[lf.V2]);
                var (ma, mb) = (moving.Contains(lf.V1), moving.Contains(lf.V2));
                if (ma && mb) carried.Add((a.X, a.Y, b.X, b.Y));
                else if (ma) stretched.Add((a.X, a.Y, b.X, b.Y));
                else stretched.Add((b.X, b.Y, a.X, a.Y));
            }
        }

        var thingList = thingSlots.Order().Select(i => f.Things[i]).ToList();
        return new MoveSet(
            [.. moving.Order().Select(i => f.Vertices[i].Element!)],
            [.. thingList.Select(t => t.Element!)],
            [.. carried],
            [.. stretched],
            [.. thingList.Select(t => (t.X, t.Y))]);

        int SectorOf(int side) => side >= 0 && side < f.Sides.Count ? f.Sides[side].Sector : -1;
    }
}

/// <summary>
/// Moves corners and objects by one offset from where they started. Every step of one drag merges
/// into the first, so the drag undoes in one step; undo puts everything back exactly where it was.
/// </summary>
public sealed class MoveEdit : IEdit
{
    private readonly MoveSet _set;
    private readonly (double X, double Y)[] _vertexStart;
    private readonly (double X, double Y)[] _thingStart;

    /// <summary>Reads where everything starts from the map: make it on the UI thread.</summary>
    public MoveEdit(MoveSet set, double dx, double dy)
    {
        _set = set;
        _vertexStart = [.. set.Vertices.Select(v => (v.X, v.Y))];
        _thingStart = [.. set.Things.Select(t => (t.X, t.Y))];
        (Dx, Dy) = (dx, dy);
    }

    private MoveEdit(MoveEdit first, double dx, double dy)
    {
        _set = first._set;
        _vertexStart = first._vertexStart;
        _thingStart = first._thingStart;
        (Dx, Dy) = (dx, dy);
    }

    public double Dx { get; private set; }

    public double Dy { get; private set; }

    public string Label => (_set.Vertices.Count, _set.Things.Count) switch
    {
        (0, 1) => "Move a thing",
        (0, _) => "Move things",
        (1, 0) => "Move a corner",
        _ => "Move",
    };

    public IEnumerable<Element> Touched => _set.Vertices.Cast<Element>().Concat(_set.Things);

    /// <summary>The same move with a new offset from the same start (the next step of a drag).</summary>
    public MoveEdit To(double dx, double dy) => new(this, dx, dy);

    public void Do(EditorMap map) => Place(Dx, Dy);

    public void Undo(EditorMap map) => Place(0, 0);

    public bool TryMerge(IEdit next)
    {
        if (next is not MoveEdit m || !ReferenceEquals(m._set, _set)) return false;
        (Dx, Dy) = (m.Dx, m.Dy);
        return true;
    }

    private void Place(double dx, double dy)
    {
        for (var i = 0; i < _vertexStart.Length; i++) (_set.Vertices[i].X, _set.Vertices[i].Y) = (_vertexStart[i].X + dx, _vertexStart[i].Y + dy);
        for (var i = 0; i < _thingStart.Length; i++) (_set.Things[i].X, _set.Things[i].Y) = (_thingStart[i].X + dx, _thingStart[i].Y + dy);
    }
}
