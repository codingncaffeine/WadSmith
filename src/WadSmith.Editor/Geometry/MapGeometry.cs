using WadSmith.Editor.Model;

namespace WadSmith.Editor.Geometry;

/// <summary>A point on the map, in map units (Y grows north).</summary>
public readonly record struct MapPoint(double X, double Y)
{
    public static MapPoint Of(Vertex v) => new(v.X, v.Y);

    public double DistanceTo(MapPoint p) => Math.Sqrt((X - p.X) * (X - p.X) + (Y - p.Y) * (Y - p.Y));
}

/// <summary>
/// Shapes and hit tests over an <see cref="EditorMap"/>: each area's outline as closed loops, which
/// area a point is in, and the nearest wall, corner or thing to a point. Results for areas are cached
/// per map version.
/// </summary>
public sealed class MapGeometry(EditorMap map)
{
    private long _cachedVersion = -1;
    private Dictionary<Sector, List<MapPoint[]>> _loops = new(ReferenceEqualityComparer.Instance);
    private Dictionary<Sector, (double MinX, double MinY, double MaxX, double MaxY)> _bounds = new(ReferenceEqualityComparer.Instance);

    public EditorMap Map { get; } = map;

    /// <summary>
    /// The closed loops around an area: its walls, each turned so the area is on its right, chained
    /// end to start. An area that is not closed gives its open chains as well; the checks report it.
    /// </summary>
    public IReadOnlyList<MapPoint[]> Loops(Sector sector)
    {
        Refresh();
        return _loops.TryGetValue(sector, out var l) ? l : [];
    }

    /// <summary>The area containing the point (even-odd over its loops), or null for the void.</summary>
    public Sector? SectorAt(MapPoint p)
    {
        Refresh();
        Sector? best = null;
        var bestArea = double.MaxValue;
        foreach (var (sector, loops) in _loops)
        {
            var b = _bounds[sector];
            if (p.X < b.MinX || p.X > b.MaxX || p.Y < b.MinY || p.Y > b.MaxY) continue;
            if (!Inside(loops, p)) continue;

            // Nested areas (a pillar's area inside a room): the smallest one containing the point wins.
            var area = (b.MaxX - b.MinX) * (b.MaxY - b.MinY);
            if (area < bestArea)
            {
                best = sector;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>The wall nearest the point within <paramref name="radius"/> map units.</summary>
    public Line? LineNear(MapPoint p, double radius)
    {
        Line? best = null;
        var bestD = radius;
        foreach (var l in Map.Lines)
        {
            var d = DistanceToSegment(p, MapPoint.Of(l.Start), MapPoint.Of(l.End));
            if (d <= bestD)
            {
                bestD = d;
                best = l;
            }
        }

        return best;
    }

    public Vertex? VertexNear(MapPoint p, double radius)
    {
        Vertex? best = null;
        var bestD = radius;
        foreach (var v in Map.Vertices)
        {
            var d = p.DistanceTo(MapPoint.Of(v));
            if (d <= bestD)
            {
                bestD = d;
                best = v;
            }
        }

        return best;
    }

    public Thing? ThingNear(MapPoint p, double radius)
    {
        Thing? best = null;
        var bestD = radius;
        foreach (var t in Map.Things)
        {
            var d = p.DistanceTo(new MapPoint(t.X, t.Y));
            if (d <= bestD)
            {
                bestD = d;
                best = t;
            }
        }

        return best;
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
    public (double MinX, double MinY, double MaxX, double MaxY) Extent()
    {
        if (Map.Vertices.Count == 0) return (-512, -512, 512, 512);
        return (Map.Vertices.Min(v => v.X), Map.Vertices.Min(v => v.Y), Map.Vertices.Max(v => v.X), Map.Vertices.Max(v => v.Y));
    }

    private static bool Inside(List<MapPoint[]> loops, MapPoint p)
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

    private void Refresh()
    {
        if (_cachedVersion == Map.Version) return;
        _cachedVersion = Map.Version;

        // Each side contributes its wall, turned so its area is on the right (front: start to end).
        var edges = new Dictionary<Sector, List<(Vertex From, Vertex To)>>(ReferenceEqualityComparer.Instance);
        foreach (var l in Map.Lines)
        {
            Add(l.Front.Sector, l.Start, l.End);
            if (l.Back is { } back) Add(back.Sector, l.End, l.Start);
        }

        void Add(Sector s, Vertex a, Vertex b)
        {
            if (!edges.TryGetValue(s, out var list)) edges[s] = list = [];
            list.Add((a, b));
        }

        _loops = new(ReferenceEqualityComparer.Instance);
        _bounds = new(ReferenceEqualityComparer.Instance);
        foreach (var (sector, list) in edges)
        {
            // A self-referencing wall (the same area on both sides) adds nothing to the outline.
            var pairs = list.Where(e => !list.Contains((e.To, e.From))).ToList();
            var byStart = new Dictionary<Vertex, List<int>>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < pairs.Count; i++)
            {
                if (!byStart.TryGetValue(pairs[i].From, out var starts)) byStart[pairs[i].From] = starts = [];
                starts.Add(i);
            }

            var used = new bool[pairs.Count];
            var loops = new List<MapPoint[]>();
            for (var i = 0; i < pairs.Count; i++)
            {
                if (used[i]) continue;
                var loop = new List<MapPoint>();
                var at = i;
                while (at >= 0 && !used[at])
                {
                    used[at] = true;
                    loop.Add(MapPoint.Of(pairs[at].From));
                    var next = -1;
                    if (byStart.TryGetValue(pairs[at].To, out var candidates))
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

                    at = next;
                }

                if (loop.Count >= 3) loops.Add([.. loop]);
            }

            if (loops.Count == 0) continue;
            _loops[sector] = loops;
            var all = loops.SelectMany(x => x).ToList();
            _bounds[sector] = (all.Min(q => q.X), all.Min(q => q.Y), all.Max(q => q.X), all.Max(q => q.Y));
        }
    }
}
