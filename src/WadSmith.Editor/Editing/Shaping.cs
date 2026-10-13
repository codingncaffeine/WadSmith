using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Moving corners to where a shape wants them (Eureka's quantize, shape line and shape circle): onto the grid, evenly
/// along a straight line, or evenly round a circle. Each returns the new spot of every corner it moves; the session
/// moves them as one step and joins them to the map as a drag does.
/// </summary>
public static class Shaping
{
    /// <summary>The most corners a line or a circle is made of: shapes are for a handful, not a map.</summary>
    public const int MostCorners = 2000;

    /// <summary>The corners the selection stands on: corners as they are, a wall's two ends, a room's every corner.</summary>
    public static IReadOnlyList<Vertex> CornersOf(MapGeometry geometry, IEnumerable<Element> selection)
    {
        var corners = new List<Vertex>();
        var seen = new HashSet<Vertex>(ReferenceEqualityComparer.Instance);
        void Add(Vertex v)
        {
            if (v.OnMap && seen.Add(v)) corners.Add(v);
        }

        foreach (var e in selection)
        {
            switch (e)
            {
                case Vertex v:
                    Add(v);
                    break;
                case Line l:
                    Add(l.Start);
                    Add(l.End);
                    break;
                case Side { Line: { } l }:
                    Add(l.Start);
                    Add(l.End);
                    break;
                case Sector s when s.OnMap && geometry.WallBoundsAt(s.Slot) is { } b:
                    foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.Front.Sector == s || l.Back?.Sector == s))
                    {
                        Add(l.Start);
                        Add(l.End);
                    }

                    break;
            }
        }

        return corners;
    }

    /// <summary>
    /// The nearest grid point to each corner and object, <paramref name="step"/> apart. A corner whose grid point another
    /// corner holds (one staying, or one already moving there) stays where it is and is counted: two corners made one
    /// would fold a wall to nothing (Eureka refuses them too).
    /// </summary>
    public static (Dictionary<Vertex, MapPoint> Corners, Dictionary<Thing, MapPoint> Things, int Refused) Snap(MapGeometry geometry, IReadOnlyList<Vertex> corners, IEnumerable<Thing> things, double step)
    {
        var moving = new HashSet<Vertex>(corners, ReferenceEqualityComparer.Instance);
        var taken = new HashSet<(double, double)>();
        var moves = new Dictionary<Vertex, MapPoint>(ReferenceEqualityComparer.Instance);
        var refused = 0;
        MapPoint On(double x, double y) => new(Math.Round(x / step, MidpointRounding.AwayFromZero) * step, Math.Round(y / step, MidpointRounding.AwayFromZero) * step);

        // Corners already on the grid hold their spots first.
        foreach (var v in corners.Where(v => On(v.X, v.Y) == new MapPoint(v.X, v.Y))) taken.Add((v.X, v.Y));
        foreach (var v in corners)
        {
            var to = On(v.X, v.Y);
            if (to == new MapPoint(v.X, v.Y)) continue;
            var staying = geometry.VerticesIn(to.X - 0.5, to.Y - 0.5, to.X + 0.5, to.Y + 0.5).Any(o => o.OnMap && !moving.Contains(o) && o.X == to.X && o.Y == to.Y);
            if (staying || !taken.Add((to.X, to.Y)))
            {
                refused++;
                continue;
            }

            moves[v] = to;
        }

        // A wall the moves would fold to nothing or turn round (its ends passing each other) keeps one end where it was.
        var walls = corners.SelectMany(v => geometry.LinesIn(v.X - 0.5, v.Y - 0.5, v.X + 0.5, v.Y + 0.5).Where(l => l.OnMap && (l.Start == v || l.End == v))).Distinct().ToList();
        MapPoint Now(Vertex v) => moves.TryGetValue(v, out var p) ? p : new MapPoint(v.X, v.Y);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var l in walls)
            {
                var (a, b) = (Now(l.Start), Now(l.End));
                var dot = (b.X - a.X) * (l.End.X - l.Start.X) + (b.Y - a.Y) * (l.End.Y - l.Start.Y);
                if (dot > 0) continue;
                var back = moves.ContainsKey(l.End) ? l.End : l.Start;
                if (!moves.Remove(back)) continue;
                refused++;
                changed = true;
            }
        }

        var placed = new Dictionary<Thing, MapPoint>(ReferenceEqualityComparer.Instance);
        foreach (var t in things.Where(t => t.OnMap))
        {
            var to = On(t.X, t.Y);
            if (to != new MapPoint(t.X, t.Y)) placed[t] = to;
        }

        return (moves, placed, refused);
    }

    /// <summary>
    /// The corners evenly along the straight line between the two furthest apart, which stay; the rest keep their order
    /// along it. Null for fewer than three corners or too many.
    /// </summary>
    public static Dictionary<Vertex, MapPoint>? Line(IReadOnlyList<Vertex> corners, bool whole)
    {
        if (corners.Count is < 3 or > MostCorners) return null;
        var (a, b, far) = (corners[0], corners[1], -1.0);
        for (var i = 0; i < corners.Count; i++)
        {
            for (var j = i + 1; j < corners.Count; j++)
            {
                var d = Distance(corners[i], corners[j]);
                if (d > far) (a, b, far) = (corners[i], corners[j], d);
            }
        }

        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var order = corners.OrderBy(v => (v.X - a.X) * dx + (v.Y - a.Y) * dy).ToList();
        var moves = new Dictionary<Vertex, MapPoint>(ReferenceEqualityComparer.Instance);
        for (var i = 1; i < order.Count - 1; i++)
        {
            var t = i / (double)(order.Count - 1);
            moves[order[i]] = Round(new MapPoint(a.X + dx * t, a.Y + dy * t), whole);
        }

        return moves;
    }

    /// <summary>
    /// The corners evenly round the circle through their middle at their average distance from it, in the order they
    /// go round it, the first staying on its own bearing. Null for fewer than three corners or too many.
    /// </summary>
    public static Dictionary<Vertex, MapPoint>? Circle(IReadOnlyList<Vertex> corners, bool whole)
    {
        if (corners.Count is < 3 or > MostCorners) return null;
        var (cx, cy) = (corners.Average(v => v.X), corners.Average(v => v.Y));
        var radius = corners.Average(v => Math.Sqrt((v.X - cx) * (v.X - cx) + (v.Y - cy) * (v.Y - cy)));
        if (radius < 1) return null;
        var order = corners.OrderBy(v => Math.Atan2(v.Y - cy, v.X - cx)).ToList();
        var start = Math.Atan2(order[0].Y - cy, order[0].X - cx);
        var moves = new Dictionary<Vertex, MapPoint>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < order.Count; i++)
        {
            var angle = start + 2 * Math.PI * i / order.Count;
            moves[order[i]] = Round(new MapPoint(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)), whole);
        }

        return moves;
    }

    private static double Distance(Vertex a, Vertex b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // Whole units for a classic map, whose format holds no fractions; otherwise to a hundredth, as the map files keep them.
    private static MapPoint Round(MapPoint p, bool whole) => whole ? new(Math.Round(p.X), Math.Round(p.Y)) : new(Math.Round(p.X, 2), Math.Round(p.Y, 2));
}
