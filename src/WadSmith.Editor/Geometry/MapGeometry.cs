using WadSmith.Editor.Model;

namespace WadSmith.Editor.Geometry;

/// <summary>A point on the map, in map units (Y grows north).</summary>
public readonly record struct MapPoint(double X, double Y)
{
    public static MapPoint Of(Vertex v) => new(v.X, v.Y);

    public double DistanceTo(MapPoint p) => Math.Sqrt((X - p.X) * (X - p.X) + (Y - p.Y) * (Y - p.Y));
}

/// <summary>
/// Shapes and hit tests for one <see cref="MapFrame"/>: each area's outline as closed loops, which
/// area a point is in, and the nearest wall, corner or thing to a point. Built once per frame on a
/// worker (whole-map work never runs on the UI thread) and read-only afterwards; the queries use
/// spatial grids, so a hover on a 100,000-wall map costs microseconds.
/// </summary>
public sealed class MapGeometry
{
    private static readonly MapPoint[][] NoLoops = [];

    private readonly MapPoint[][][] _loops;
    private readonly (double MinX, double MinY, double MaxX, double MaxY)[] _bounds;
    private readonly (double MinX, double MinY, double MaxX, double MaxY)?[] _wallBounds;
    private readonly (double MinX, double MinY, double MaxX, double MaxY) _extent;
    private readonly SpatialGrid _lines;
    private readonly SpatialGrid _vertices;
    private readonly SpatialGrid _things;
    private readonly SpatialGrid _sectors;
    private readonly Dictionary<int, List<int>> _byFirstArg = [];

    private MapGeometry(MapFrame frame)
    {
        Frame = frame;
        (_loops, _bounds, _wallBounds) = TraceOutlines(frame);
        _extent = ExtentOf(frame);
        foreach (var sector in frame.Sectors)
        {
            if (sector.Element is null) continue;
            FloorRange = FloorRange is { } r ? (Math.Min(r.Min, sector.FloorHeight), Math.Max(r.Max, sector.FloorHeight)) : (sector.FloorHeight, sector.FloorHeight);
        }

        _lines = SpatialGrid.Build(_extent, frame.Lines.Count, i =>
        {
            var l = frame.Lines[i];
            if (l.Element is null) return null;
            var a = frame.Vertices[l.V1];
            var b = frame.Vertices[l.V2];
            return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        });
        _vertices = SpatialGrid.Build(_extent, frame.Vertices.Count, i => frame.Vertices[i] is { Element: not null } v ? (v.X, v.Y, v.X, v.Y) : null);
        _things = SpatialGrid.Build(_extent, frame.Things.Count, i => frame.Things[i] is { Element: not null } t ? (t.X, t.Y, t.X, t.Y) : null);
        _sectors = SpatialGrid.Build(_extent, frame.Sectors.Count, i => _loops[i].Length > 0 ? _bounds[i] : null);

        // Walls with an action, by its first argument: for most actions the tag of what they work on.
        for (var i = 0; i < frame.Lines.Count; i++)
        {
            var l = frame.Lines[i];
            if (l.Element is null || l.Special == 0 || l.Args[0] == 0) continue;
            if (!_byFirstArg.TryGetValue(l.Args[0], out var list)) _byFirstArg[l.Args[0]] = list = [];
            list.Add(i);
        }
    }

    /// <summary>The places of the walls whose action's first argument is <paramref name="tag"/> (the walls that work on that tag, for most actions).</summary>
    public IReadOnlyList<int> LinesNaming(int tag) => tag != 0 && _byFirstArg.TryGetValue(tag, out var list) ? list : [];

    /// <summary>The frame these shapes were made from.</summary>
    public MapFrame Frame { get; }

    public long Version => Frame.Version;

    /// <summary>The lowest and highest floor of any area; null for a map without areas.</summary>
    public (double Min, double Max)? FloorRange { get; }

    /// <summary>Traces outlines and indexes everything; whole-map work, so call it on a worker.</summary>
    public static MapGeometry Build(MapFrame frame) => new(frame);

    /// <summary>
    /// The closed loops around an area: its walls, each turned so the area is on its right, chained
    /// end to start. An area that is not closed gives its open chains as well; the checks report it.
    /// </summary>
    public IReadOnlyList<MapPoint[]> Loops(Sector sector) => LoopsAt(sector.Slot);

    /// <summary>The loops of the area in <paramref name="slot"/>.</summary>
    public IReadOnlyList<MapPoint[]> LoopsAt(int slot) => slot >= 0 && slot < _loops.Length ? _loops[slot] : NoLoops;

    /// <summary>The area containing the point (even-odd over its loops), or null for the void.</summary>
    public Sector? SectorAt(MapPoint p)
    {
        Sector? best = null;
        var bestSlot = -1;
        var bestArea = double.MaxValue;
        foreach (var s in _sectors.Near(p.X, p.Y, p.X, p.Y))
        {
            var b = _bounds[s];
            if (p.X < b.MinX || p.X > b.MaxX || p.Y < b.MinY || p.Y > b.MaxY) continue;
            if (!Inside(_loops[s], p)) continue;

            // Nested areas (a pillar's area inside a room): the smallest one containing the point wins.
            var area = (b.MaxX - b.MinX) * (b.MaxY - b.MinY);
            if (area < bestArea || (area == bestArea && s < bestSlot))
            {
                best = Frame.Sectors[s].Element;
                bestSlot = s;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>The bounds of every wall with a face in the area in <paramref name="slot"/>, closed outline or not; null for an area without walls.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? WallBoundsAt(int slot) => slot >= 0 && slot < _wallBounds.Length ? _wallBounds[slot] : null;

    /// <summary>Every wall whose box meets the box given, each once.</summary>
    public IReadOnlyList<Line> LinesIn(double minX, double minY, double maxX, double maxY) => [.. LineSlotsIn(minX, minY, maxX, maxY).Select(i => Frame.Lines[i].Element!)];

    /// <summary>The places (slots in the frame) of every wall whose box meets the box given, each once.</summary>
    public IReadOnlyList<int> LineSlotsIn(double minX, double minY, double maxX, double maxY)
    {
        var found = new List<int>();
        var seen = new HashSet<int>();
        foreach (var i in _lines.Near(minX, minY, maxX, maxY))
        {
            var l = Frame.Lines[i];
            if (l.Element is null || !seen.Add(i)) continue;
            var a = Frame.Vertices[l.V1];
            var b = Frame.Vertices[l.V2];
            if (Math.Max(a.X, b.X) < minX || Math.Min(a.X, b.X) > maxX || Math.Max(a.Y, b.Y) < minY || Math.Min(a.Y, b.Y) > maxY) continue;
            found.Add(i);
        }

        return found;
    }

    /// <summary>The places of every area with an outline whose bounds meet the box given, each once.</summary>
    public IReadOnlyList<int> SectorSlotsIn(double minX, double minY, double maxX, double maxY)
    {
        var found = new List<int>();
        var seen = new HashSet<int>();
        foreach (var i in _sectors.Near(minX, minY, maxX, maxY))
        {
            if (!seen.Add(i) || _loops[i].Length == 0) continue;
            var b = _bounds[i];
            if (b.MaxX < minX || b.MinX > maxX || b.MaxY < minY || b.MinY > maxY) continue;
            found.Add(i);
        }

        return found;
    }

    /// <summary>The places of every object inside the box given, each once.</summary>
    public IReadOnlyList<int> ThingSlotsIn(double minX, double minY, double maxX, double maxY)
    {
        var found = new List<int>();
        var seen = new HashSet<int>();
        foreach (var i in _things.Near(minX, minY, maxX, maxY))
        {
            var t = Frame.Things[i];
            if (t.Element is null || !seen.Add(i) || t.X < minX || t.X > maxX || t.Y < minY || t.Y > maxY) continue;
            found.Add(i);
        }

        return found;
    }

    /// <summary>Every corner inside the box given, each once.</summary>
    public IReadOnlyList<Vertex> VerticesIn(double minX, double minY, double maxX, double maxY) => [.. VertexSlotsIn(minX, minY, maxX, maxY).Select(i => Frame.Vertices[i].Element!)];

    /// <summary>The places of every corner inside the box given, each once.</summary>
    public IReadOnlyList<int> VertexSlotsIn(double minX, double minY, double maxX, double maxY)
    {
        var found = new List<int>();
        var seen = new HashSet<int>();
        foreach (var i in _vertices.Near(minX, minY, maxX, maxY))
        {
            var v = Frame.Vertices[i];
            if (v.Element is null || !seen.Add(i) || v.X < minX || v.X > maxX || v.Y < minY || v.Y > maxY) continue;
            found.Add(i);
        }

        return found;
    }

    /// <summary>The wall nearest the point within <paramref name="radius"/> map units.</summary>
    public Line? LineNear(MapPoint p, double radius)
    {
        Line? best = null;
        var bestD = radius;
        foreach (var i in _lines.Near(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius))
        {
            var l = Frame.Lines[i];
            var a = Frame.Vertices[l.V1];
            var b = Frame.Vertices[l.V2];
            var d = DistanceToSegment(p, new MapPoint(a.X, a.Y), new MapPoint(b.X, b.Y));
            // Ties go to the later wall, as a scan in map order would pick.
            if (d < bestD || (d == bestD && (best is null || i > best.Slot)))
            {
                bestD = d;
                best = l.Element;
            }
        }

        return best;
    }

    public Vertex? VertexNear(MapPoint p, double radius)
    {
        Vertex? best = null;
        var bestD = radius;
        foreach (var i in _vertices.Near(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius))
        {
            var v = Frame.Vertices[i];
            var d = p.DistanceTo(new MapPoint(v.X, v.Y));
            if (d < bestD || (d == bestD && (best is null || i > best.Slot)))
            {
                bestD = d;
                best = v.Element;
            }
        }

        return best;
    }

    public Thing? ThingNear(MapPoint p, double radius)
    {
        Thing? best = null;
        var bestD = radius;
        foreach (var i in _things.Near(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius))
        {
            var t = Frame.Things[i];
            var d = p.DistanceTo(new MapPoint(t.X, t.Y));
            if (d < bestD || (d == bestD && (best is null || i > best.Slot)))
            {
                bestD = d;
                best = t.Element;
            }
        }

        return best;
    }

    /// <summary>The walls the segment from <paramref name="a"/> to <paramref name="b"/> crosses (touching a wall's end counts).</summary>
    public IEnumerable<LineFrame> WallsCrossing(MapPoint a, MapPoint b)
    {
        var seen = new HashSet<int>();
        foreach (var i in _lines.Near(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)))
        {
            if (!seen.Add(i)) continue;
            var l = Frame.Lines[i];
            if (l.Element is null) continue;
            var c = Frame.Vertices[l.V1];
            var d = Frame.Vertices[l.V2];
            if (SegmentsCross(a, b, new MapPoint(c.X, c.Y), new MapPoint(d.X, d.Y))) yield return l;
        }
    }

    /// <summary>The area at a point and its floor and ceiling heights, or null over the void.</summary>
    public SectorFrame? AreaAt(MapPoint p) => SectorAt(p) is { } s && s.Slot < Frame.Sectors.Count ? Frame.Sectors[s.Slot] : null;

    private static bool SegmentsCross(MapPoint a, MapPoint b, MapPoint c, MapPoint d)
    {
        static double Cross(MapPoint o, MapPoint p, MapPoint q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        var d1 = Cross(c, d, a);
        var d2 = Cross(c, d, b);
        var d3 = Cross(a, b, c);
        var d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 <= 0) || (d1 < 0 && d2 >= 0) || (d1 == 0 && d2 != 0)) && ((d3 >= 0 && d4 <= 0) || (d3 <= 0 && d4 >= 0));
    }

    public static double DistanceToSegment(MapPoint p, MapPoint a, MapPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 == 0) return p.DistanceTo(a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return p.DistanceTo(new MapPoint(a.X + t * dx, a.Y + t * dy));
    }

    /// <summary>The map's extent, for fitting it to a view.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Extent() => _extent;

    private static (double MinX, double MinY, double MaxX, double MaxY) ExtentOf(MapFrame frame)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var v in frame.Vertices)
        {
            if (v.Element is null) continue;
            x0 = Math.Min(x0, v.X);
            y0 = Math.Min(y0, v.Y);
            x1 = Math.Max(x1, v.X);
            y1 = Math.Max(y1, v.Y);
        }

        return x0 > x1 ? (-512, -512, 512, 512) : (x0, y0, x1, y1);
    }

    private static bool Inside(MapPoint[][] loops, MapPoint p)
    {
        var inside = false;
        foreach (var loop in loops)
        {
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Each area's loops and bounds, by area slot.</summary>
    private static (MapPoint[][][] Loops, (double, double, double, double)[] Bounds, (double, double, double, double)?[] WallBounds) TraceOutlines(MapFrame frame)
    {
        var sectorCount = frame.Sectors.Count;

        // Each side contributes its wall, turned so its area is on the right (front: start to end).
        // Edges are grouped by area in flat arrays: no allocation per area for the common small ones.
        var start = new int[sectorCount + 1];
        int SectorOf(int side) => side >= 0 ? frame.Sides[side].Sector : -1;
        foreach (var l in frame.Lines)
        {
            if (l.Element is null) continue;
            if (SectorOf(l.Front) is var f and >= 0) start[f + 1]++;
            if (SectorOf(l.Back) is var b and >= 0) start[b + 1]++;
        }

        for (var i = 0; i < sectorCount; i++) start[i + 1] += start[i];
        var from = new int[start[sectorCount]];
        var to = new int[start[sectorCount]];
        var fill = (int[])start.Clone();
        foreach (var l in frame.Lines)
        {
            if (l.Element is null) continue;
            if (SectorOf(l.Front) is var f and >= 0)
            {
                from[fill[f]] = l.V1;
                to[fill[f]++] = l.V2;
            }

            if (SectorOf(l.Back) is var b and >= 0)
            {
                from[fill[b]] = l.V2;
                to[fill[b]++] = l.V1;
            }
        }

        var loops = new MapPoint[sectorCount][][];
        var bounds = new (double, double, double, double)[sectorCount];
        var wallBounds = new (double, double, double, double)?[sectorCount];
        var keep = new List<int>();
        var chain = new List<MapPoint>();
        for (var s = 0; s < sectorCount; s++)
        {
            int s0 = start[s], s1 = start[s + 1];
            loops[s] = NoLoops;
            if (s1 == s0) continue;
            double wx0 = double.MaxValue, wy0 = double.MaxValue, wx1 = double.MinValue, wy1 = double.MinValue;
            for (var e = s0; e < s1; e++)
            {
                var v = frame.Vertices[from[e]];
                var w = frame.Vertices[to[e]];
                (wx0, wy0, wx1, wy1) = (Math.Min(wx0, Math.Min(v.X, w.X)), Math.Min(wy0, Math.Min(v.Y, w.Y)), Math.Max(wx1, Math.Max(v.X, w.X)), Math.Max(wy1, Math.Max(v.Y, w.Y)));
            }

            wallBounds[s] = (wx0, wy0, wx1, wy1);

            // A self-referencing wall (the same area on both sides) adds nothing to the outline.
            keep.Clear();
            var big = s1 - s0 > 32;
            HashSet<long>? pairs = null;
            if (big)
            {
                pairs = new HashSet<long>(s1 - s0);
                for (var e = s0; e < s1; e++) pairs.Add(((long)from[e] << 32) | (uint)to[e]);
            }

            for (var e = s0; e < s1; e++)
            {
                bool reversed;
                if (pairs is not null)
                {
                    reversed = pairs.Contains(((long)to[e] << 32) | (uint)from[e]);
                }
                else
                {
                    reversed = false;
                    for (var o = s0; o < s1 && !reversed; o++) reversed = from[o] == to[e] && to[o] == from[e];
                }

                if (!reversed) keep.Add(e);
            }

            // Chain the walls end to start, taking the first unused wall that starts where the last ended.
            Dictionary<int, List<int>>? byStart = null;
            if (big)
            {
                byStart = [];
                for (var k = 0; k < keep.Count; k++)
                {
                    if (!byStart.TryGetValue(from[keep[k]], out var list)) byStart[from[keep[k]]] = list = [];
                    list.Add(k);
                }
            }

            var used = new bool[keep.Count];
            var found = new List<MapPoint[]>();
            for (var i = 0; i < keep.Count; i++)
            {
                if (used[i]) continue;
                chain.Clear();
                var at = i;
                while (at >= 0 && !used[at])
                {
                    used[at] = true;
                    var v = frame.Vertices[from[keep[at]]];
                    chain.Add(new MapPoint(v.X, v.Y));
                    var end = to[keep[at]];
                    var next = -1;
                    if (byStart is not null)
                    {
                        if (byStart.TryGetValue(end, out var candidates))
                        {
                            foreach (var c in candidates)
                            {
                                if (!used[c])
                                {
                                    next = c;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        for (var c = 0; c < keep.Count; c++)
                        {
                            if (!used[c] && from[keep[c]] == end)
                            {
                                next = c;
                                break;
                            }
                        }
                    }

                    at = next;
                }

                if (chain.Count >= 3) found.Add([.. chain]);
            }

            if (found.Count == 0) continue;
            loops[s] = [.. found];
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var loop in found)
            {
                foreach (var q in loop)
                {
                    x0 = Math.Min(x0, q.X);
                    y0 = Math.Min(y0, q.Y);
                    x1 = Math.Max(x1, q.X);
                    y1 = Math.Max(y1, q.Y);
                }
            }

            bounds[s] = (x0, y0, x1, y1);
        }

        return (loops, bounds, wallBounds);
    }
}

/// <summary>
/// A uniform grid over the map for finding items near a point. Each item is filed in every cell its
/// bounds cross; an item that would cross a great many cells (a very long diagonal wall, a huge area)
/// goes on a short list that every query checks.
/// </summary>
internal sealed class SpatialGrid
{
    private const int MaxCellsPerItem = 256;

    private readonly double _x0;
    private readonly double _y0;
    private readonly double _cell;
    private readonly int _cols;
    private readonly int _rows;
    private readonly int[] _start;
    private readonly int[] _items;
    private readonly int[] _large;

    private SpatialGrid(double x0, double y0, double cell, int cols, int rows, int[] start, int[] items, int[] large)
    {
        (_x0, _y0, _cell, _cols, _rows, _start, _items, _large) = (x0, y0, cell, cols, rows, start, items, large);
    }

    /// <summary>About two items per cell, at most four million cells.</summary>
    public static SpatialGrid Build((double MinX, double MinY, double MaxX, double MaxY) extent, int count, Func<int, (double MinX, double MinY, double MaxX, double MaxY)?> boundsOf)
    {
        var w = Math.Max(extent.MaxX - extent.MinX, 1);
        var h = Math.Max(extent.MaxY - extent.MinY, 1);
        var cell = Math.Max(16, Math.Sqrt(w * h / Math.Max(count / 2.0, 1)));
        while ((w / cell + 1) * (h / cell + 1) > 4_000_000) cell *= 2;
        var cols = (int)(w / cell) + 1;
        var rows = (int)(h / cell) + 1;

        var spans = new (int C0, int R0, int C1, int R1)[count];
        var start = new int[cols * rows + 1];
        var large = new List<int>();
        for (var i = 0; i < count; i++)
        {
            spans[i] = (-1, -1, -1, -1);
            if (boundsOf(i) is not { } b) continue;
            int c0 = Col(b.MinX), r0 = Row(b.MinY), c1 = Col(b.MaxX), r1 = Row(b.MaxY);
            if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > MaxCellsPerItem)
            {
                large.Add(i);
                continue;
            }

            spans[i] = (c0, r0, c1, r1);
            for (var r = r0; r <= r1; r++)
            {
                for (var c = c0; c <= c1; c++) start[r * cols + c + 1]++;
            }
        }

        for (var i = 0; i < cols * rows; i++) start[i + 1] += start[i];
        var items = new int[start[cols * rows]];
        var fill = (int[])start.Clone();
        for (var i = 0; i < count; i++)
        {
            var (c0, r0, c1, r1) = spans[i];
            if (c0 < 0) continue;
            for (var r = r0; r <= r1; r++)
            {
                for (var c = c0; c <= c1; c++) items[fill[r * cols + c]++] = i;
            }
        }

        return new SpatialGrid(extent.MinX, extent.MinY, cell, cols, rows, start, items, [.. large]);

        int Col(double x) => Math.Clamp((int)((x - extent.MinX) / cell), 0, cols - 1);
        int Row(double y) => Math.Clamp((int)((y - extent.MinY) / cell), 0, rows - 1);
    }

    /// <summary>Items filed in the cells the box crosses, plus the large ones; an item may come more than once.</summary>
    public IEnumerable<int> Near(double minX, double minY, double maxX, double maxY)
    {
        foreach (var i in _large) yield return i;
        int c0 = Col(minX), r0 = Row(minY), c1 = Col(maxX), r1 = Row(maxY);
        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                var cellIndex = r * _cols + c;
                for (var k = _start[cellIndex]; k < _start[cellIndex + 1]; k++) yield return _items[k];
            }
        }
    }

    private int Col(double x) => Math.Clamp((int)Math.Floor((x - _x0) / _cell), 0, _cols - 1);

    private int Row(double y) => Math.Clamp((int)Math.Floor((y - _y0) / _cell), 0, _rows - 1);
}
