using System.Globalization;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Rounded corners (arc walls): a corner where two walls meet becomes a curve of a few walls that leaves each wall
/// <c>reach</c> from the corner and meets both smoothly (tangent), on whole units. The rooms either side keep their shape
/// otherwise; textures run on along the curve as along the walls it came from.
/// </summary>
public static class Rounding
{
    /// <summary>The walls meeting at a corner, from the frame's grid (any thread that may read elements).</summary>
    public static IReadOnlyList<Line> WallsAt(MapGeometry geometry, Vertex corner) =>
        [.. geometry.LinesIn(corner.X - 0.5, corner.Y - 0.5, corner.X + 0.5, corner.Y + 0.5).Where(l => l.OnMap && (ReferenceEquals(l.Start, corner) || ReferenceEquals(l.End, corner)))];

    /// <summary>The reach a curve gets unless asked: 64, or less on short walls (two fifths of the shorter).</summary>
    public static double DefaultReach(MapGeometry geometry, Vertex corner) =>
        WallsAt(geometry, corner) is { Count: 2 } w ? Math.Max(8, Math.Min(64, Math.Floor(0.4 * Math.Min(w[0].Length, w[1].Length)))) : 64;

    /// <summary>
    /// Rounds off <paramref name="corner"/> through <paramref name="edit"/> into a curve of <paramref name="sides"/> walls (2 to
    /// 32) starting <paramref name="reach"/> along each wall. Returns why it cannot, or null.
    /// </summary>
    public static string? Round(StructureEdit edit, MapGeometry geometry, Vertex corner, double reach, int sides)
    {
        var walls = WallsAt(geometry, corner);
        if (walls.Count != 2) return "Only a corner where two walls meet can be rounded.";
        sides = Math.Clamp(sides, 2, 32);
        var (a, b) = (walls[0], walls[1]);
        var (p, q) = (ReferenceEquals(a.Start, corner) ? a.End : a.Start, ReferenceEquals(b.Start, corner) ? b.End : b.Start);
        var (ux, uy) = Unit(p.X - corner.X, p.Y - corner.Y);
        var (vx, vy) = Unit(q.X - corner.X, q.Y - corner.Y);
        var turn = Math.Acos(Math.Clamp(ux * vx + uy * vy, -1, 1));
        if (turn > Math.PI - 0.01) return "This corner is already straight.";
        if (turn < 0.05) return "The walls here fold back on each other; there is no corner to round.";
        if (reach >= Math.Min(a.Length, b.Length) - 1) return string.Create(CultureInfo.InvariantCulture, $"The curve cannot reach {reach:0} along the walls: the shorter is only {Math.Min(a.Length, b.Length):0} long.");

        // The circle touching both walls reach from the corner: its middle lies on the line halving the corner.
        var (t1, t2) = (new MapPoint(corner.X + ux * reach, corner.Y + uy * reach), new MapPoint(corner.X + vx * reach, corner.Y + vy * reach));
        var (wx, wy) = Unit(ux + vx, uy + vy);
        var away = reach / Math.Cos(turn / 2);
        var centre = new MapPoint(corner.X + wx * away, corner.Y + wy * away);
        var (s0, s1) = (Math.Atan2(t1.Y - centre.Y, t1.X - centre.X), Math.Atan2(t2.Y - centre.Y, t2.X - centre.X));
        var sweep = s1 - s0;
        while (sweep > Math.PI) sweep -= 2 * Math.PI;
        while (sweep < -Math.PI) sweep += 2 * Math.PI;
        var radius = centre.DistanceTo(t1);
        var points = new List<MapPoint>();
        for (var i = 0; i <= sides; i++)
        {
            var angle = s0 + sweep * i / sides;
            var point = new MapPoint(Math.Round(centre.X + radius * Math.Cos(angle)), Math.Round(centre.Y + radius * Math.Sin(angle)));
            if (points.Count == 0 || points[^1] != point) points.Add(point);
        }

        if (points.Count < 3) return "The curve is too small to round at this size; try a longer reach.";

        // Nothing else may lie in the corner the curve cuts off.
        MapPoint c = new(corner.X, corner.Y);
        foreach (var l in geometry.LinesIn(Math.Min(c.X, Math.Min(t1.X, t2.X)), Math.Min(c.Y, Math.Min(t1.Y, t2.Y)), Math.Max(c.X, Math.Max(t1.X, t2.X)), Math.Max(c.Y, Math.Max(t1.Y, t2.Y))))
        {
            if (!l.OnMap || ReferenceEquals(l, a) || ReferenceEquals(l, b)) continue;
            if (CutsInto(new MapPoint(l.Start.X, l.Start.Y), new MapPoint(l.End.X, l.End.Y), c, t1, t2)) return "Another wall lies in the corner the curve would cut off.";
        }

        // Split each wall reach from the corner; the corner then becomes the curve's first point inside, and the piece from it
        // to the far end is split again and again for the rest.
        var first = edit.Add(new Vertex { X = points[0].X, Y = points[0].Y });
        var last = edit.Add(new Vertex { X = points[^1].X, Y = points[^1].Y });
        // A split keeps the wall's first piece and returns the rest, so the piece joining the corner to the curve's end is known
        // without looking through the map.
        Walls.Split(edit, a, first);
        var rest = Walls.Split(edit, b, last);
        var piece = ReferenceEquals(b.Start, corner) ? b : rest;
        Move(edit, corner, points[1]);
        var at = corner;
        for (var i = 2; i < points.Count - 1; i++)
        {
            var next = edit.Add(new Vertex { X = (at.X + last.X) / 2, Y = (at.Y + last.Y) / 2 });
            var second = Walls.Split(edit, piece, next);
            piece = ReferenceEquals(second.Start, last) || ReferenceEquals(second.End, last) ? second : piece;
            Move(edit, next, points[i]);
            at = next;
        }

        return null;
    }

    private static void Move(StructureEdit edit, Vertex v, MapPoint to)
    {
        edit.Set(v, x => x.X, (x, value) => x.X = value, to.X);
        edit.Set(v, x => x.Y, (x, value) => x.Y = value, to.Y);
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return (x / length, y / length);
    }

    /// <summary>Whether a wall reaches inside the triangle: an end inside it, or crossing one of its sides (touching does not count).</summary>
    private static bool CutsInto(MapPoint a, MapPoint b, MapPoint p0, MapPoint p1, MapPoint p2)
    {
        static double Cross(MapPoint o, MapPoint s, MapPoint t) => (s.X - o.X) * (t.Y - o.Y) - (s.Y - o.Y) * (t.X - o.X);
        bool Inside(MapPoint p)
        {
            var (d1, d2, d3) = (Cross(p0, p1, p), Cross(p1, p2, p), Cross(p2, p0, p));
            return (d1 > 1e-9 && d2 > 1e-9 && d3 > 1e-9) || (d1 < -1e-9 && d2 < -1e-9 && d3 < -1e-9);
        }

        static bool Crosses(MapPoint a, MapPoint b, MapPoint c, MapPoint d)
        {
            var (d1, d2, d3, d4) = (Cross(c, d, a), Cross(c, d, b), Cross(a, b, c), Cross(a, b, d));
            return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
        }

        return Inside(a) || Inside(b) || Crosses(a, b, p0, p1) || Crosses(a, b, p1, p2) || Crosses(a, b, p2, p0);
    }
}
