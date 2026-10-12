using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Splitting a room along a line drawn across it, from one of its walls to another (as Doom Builder does
/// it): the line becomes walls with the room on both sides, and the smaller part becomes a room of its own
/// with every setting of the first.
/// </summary>
public static class Splitting
{
    private const double Near = 1e-6;

    /// <summary>
    /// Whether <paramref name="path"/> (two or more points) runs across one room from one of its walls to
    /// another, inside it all the way: the room, or null.
    /// </summary>
    public static Sector? Across(MapGeometry before, IReadOnlyList<MapPoint> path)
    {
        if (path.Count < 2) return null;
        var room = before.SectorAt(Mid(path[0], path[1]));
        if (room is null) return null;

        // Both ends on the room's walls; every bend inside it; no wall crossed or touched on the way.
        if (WallUnder(before, path[0], room) is null || WallUnder(before, path[^1], room) is null) return null;
        for (var i = 1; i < path.Count - 1; i++)
        {
            if (!ReferenceEquals(before.SectorAt(path[i]), room) || before.LineNear(path[i], 0.01) is not null) return null;
        }

        for (var i = 0; i + 1 < path.Count; i++)
        {
            var (a, b) = (path[i], path[i + 1]);
            if (a.DistanceTo(b) < 1 || !ReferenceEquals(before.SectorAt(Mid(a, b)), room)) return null;
            foreach (var l in before.LinesIn(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)))
            {
                if (l.OnMap && Crosses(a, b, new MapPoint(l.Start.X, l.Start.Y), new MapPoint(l.End.X, l.End.Y), i == 0, i + 2 == path.Count)) return null;
            }
        }

        return room;
    }

    /// <summary>
    /// Splits the room <see cref="Across"/> finds along <paramref name="path"/> through <paramref name="edit"/>.
    /// Returns the two parts (the room first, then the new one), or none and why.
    /// </summary>
    public static (IReadOnlyList<Sector> Parts, string? Why) Split(StructureEdit edit, MapGeometry before, IReadOnlyList<MapPoint> path)
    {
        if (Across(before, path) is not { } room) return ([], "To split a room, draw a line from one of its walls to another, inside it all the way.");

        // The room's outline the line runs between: both ends on the same loop, else the line goes round a pillar.
        var loops = before.Loops(room);
        var (start, end) = (path[0], path[^1]);
        MapPoint[]? loop = null;
        (int Edge, double T) a = default, b = default;
        foreach (var l in loops)
        {
            if (OnLoop(l, start) is { } sa && OnLoop(l, end) is { } sb)
            {
                (loop, a, b) = (l, sa, sb);
                break;
            }
        }

        if (loop is null) return ([], "This line runs from a wall to a pillar or another part of the room, so it does not cut it in two. Draw it from one wall to another.");

        // The two parts: from one end round the outline to the other, then back along the line.
        var inner = path.Skip(1).Take(path.Count - 2).ToList();
        var first = Round(loop, a, b, start, end);
        first.AddRange(Enumerable.Reverse(inner));
        var second = Round(loop, b, a, end, start);
        second.AddRange(inner);
        var piece = Math.Abs(Area(first)) <= Math.Abs(Area(second)) ? first : second;
        if (Math.Abs(Area(first)) < 1 || Math.Abs(Area(second)) < 1) return ([], "That line runs along the room's wall: it does not cut anything off.");

        // The line's corners: on a corner already there, or splitting the wall under them (as it is now: the
        // other end may have split the same wall already).
        var roomWalls = Deleting.WallsOf(before, room);
        Vertex Corner(MapPoint p)
        {
            if (before.VertexNear(p, 0.01) is { OnMap: true } v) return v;
            var corner = edit.Add(new Vertex { X = p.X, Y = p.Y });
            var now = roomWalls.Concat(edit.Touched.OfType<Line>()).Distinct().First(l => l.OnMap && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back?.Sector, room)) && Distance(p, l) < 0.01);
            Walls.Split(edit, now, corner);
            return corner;
        }

        var corners = new List<Vertex> { Corner(start) };
        corners.AddRange(inner.Select(p => edit.Add(new Vertex { X = p.X, Y = p.Y })));
        corners.Add(Corner(end));
        var made = new List<Line>();
        for (var i = 0; i + 1 < corners.Count; i++)
        {
            var line = new Line(corners[i], corners[i + 1], edit.Add(new Side(room))) { Back = edit.Add(new Side(room)) };
            line.SetFlag("twosided", true);
            made.Add(edit.Add(line));
        }

        // The new room: the smaller part, with every setting of the first; each face in it moves over.
        var part = new Sector { FloorHeight = room.FloorHeight, CeilingHeight = room.CeilingHeight, FloorTexture = room.FloorTexture, CeilingTexture = room.CeilingTexture, Light = room.Light, Special = room.Special, Tag = room.Tag };
        foreach (var (k, value) in room.Extra) part.Extra[k] = value;
        edit.Add(part);
        var walls = roomWalls.Concat(edit.Touched.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>()).Concat(made).Distinct().Where(l => l.OnMap).ToList();
        foreach (var l in walls)
        {
            var (dx, dy) = ((l.End.X - l.Start.X) / l.Length, (l.End.Y - l.Start.Y) / l.Length);
            var (mx, my) = ((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);
            if (ReferenceEquals(l.Front.Sector, room) && Inside(piece, new MapPoint(mx + dy / 8, my - dx / 8))) edit.Set(l.Front, s => s.Sector, (s, v) => s.Sector = v, part);
            if (l.Back is { } back && ReferenceEquals(back.Sector, room) && Inside(piece, new MapPoint(mx - dy / 8, my + dx / 8))) edit.Set(back, s => s.Sector, (s, v) => s.Sector = v, part);
        }

        return ([room, part], null);
    }

    /// <summary>The wall of <paramref name="room"/> the point lies on (or the end of), or null.</summary>
    private static Line? WallUnder(MapGeometry g, MapPoint p, Sector room) =>
        g.LinesIn(p.X - 0.01, p.Y - 0.01, p.X + 0.01, p.Y + 0.01).FirstOrDefault(l => l.OnMap && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back?.Sector, room)) && Distance(p, l) < 0.01);

    /// <summary>Where on the loop the point lies: the edge from corner i to i + 1, and how far along (0–1); null when off it.</summary>
    private static (int Edge, double T)? OnLoop(MapPoint[] loop, MapPoint p)
    {
        for (var i = 0; i < loop.Length; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Length]);
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var length2 = dx * dx + dy * dy;
            if (length2 <= 0) continue;
            var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2;
            if (t < -Near || t >= 1 - Near) continue;
            var (cx, cy) = (a.X + dx * t, a.Y + dy * t);
            if ((cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y) < 1e-4) return (i, Math.Max(0, t));
        }

        return null;
    }

    /// <summary>The outline from <paramref name="from"/> forward round the loop to <paramref name="to"/>, both ends included.</summary>
    private static List<MapPoint> Round(MapPoint[] loop, (int Edge, double T) a, (int Edge, double T) b, MapPoint from, MapPoint to)
    {
        var points = new List<MapPoint> { from };
        if (a.Edge == b.Edge && b.T > a.T)
        {
            points.Add(to);
            return points;
        }

        for (var i = (a.Edge + 1) % loop.Length; ; i = (i + 1) % loop.Length)
        {
            if (loop[i].DistanceTo(from) > Near) points.Add(loop[i]);
            if (i == b.Edge) break;
        }

        if (points[^1].DistanceTo(to) > Near) points.Add(to);
        return points;
    }

    private static double Area(List<MapPoint> p)
    {
        var sum = 0.0;
        for (var i = 0; i < p.Count; i++) sum += p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y;
        return sum / 2;
    }

    private static bool Inside(List<MapPoint> polygon, MapPoint p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }

        return inside;
    }

    /// <summary>Whether a piece of the line crosses or touches a wall anywhere but where the line starts (on its first piece) or ends (on its last).</summary>
    private static bool Crosses(MapPoint a, MapPoint b, MapPoint c, MapPoint d, bool first, bool last)
    {
        static double Cross(MapPoint o, MapPoint p, MapPoint q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        var (d1, d2, d3, d4) = (Cross(c, d, a), Cross(c, d, b), Cross(a, b, c), Cross(a, b, d));
        var scale = Math.Max(1, a.DistanceTo(b) * c.DistanceTo(d));
        bool Zero(double v) => Math.Abs(v) <= 1e-9 * scale;

        // Touching at the line's own start or end is how it meets its walls.
        bool AtEnds(MapPoint p) => (first && p.DistanceTo(a) < 0.01) || (last && p.DistanceTo(b) < 0.01);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        bool OnSegment(MapPoint p, MapPoint q, MapPoint r) => Math.Min(q.X, r.X) - 1e-9 <= p.X && p.X <= Math.Max(q.X, r.X) + 1e-9 && Math.Min(q.Y, r.Y) - 1e-9 <= p.Y && p.Y <= Math.Max(q.Y, r.Y) + 1e-9;
        if (Zero(d1) && OnSegment(a, c, d) && !AtEnds(a)) return true;
        if (Zero(d2) && OnSegment(b, c, d) && !AtEnds(b)) return true;
        if (Zero(d3) && OnSegment(c, a, b) && !AtEnds(c)) return true;
        if (Zero(d4) && OnSegment(d, a, b) && !AtEnds(d)) return true;
        return false;
    }

    private static double Distance(MapPoint p, Line l)
    {
        var (ax, ay, bx, by) = (l.Start.X, l.Start.Y, l.End.X, l.End.Y);
        var (dx, dy) = (bx - ax, by - ay);
        var t = Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / Math.Max(dx * dx + dy * dy, 1e-12), 0, 1);
        var (cx, cy) = (ax + dx * t, ay + dy * t);
        return Math.Sqrt((cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y));
    }

    private static MapPoint Mid(MapPoint a, MapPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
