using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Draws a closed shape into the map the way Doom map editors always have: the shape's corners join
/// the corners they land on, walls are split where the shape crosses or touches them, and every region
/// inside the shape that reaches its outline becomes an area of its own. Walls already there are never
/// removed; a region that already is one whole area (not carrying on outside the outline), or that
/// lies within the shape without reaching its outline (a room drawn around, a pillar's empty inside),
/// stays as it was. A new area looks like
/// the area it was drawn in (heights, textures, light; not its effect or tag), or like the game's Look
/// when drawn in the void, with the heights of the room it is drawn against (D27). Walls between two areas lose their blocking and move a solid wall's texture
/// to the upper and lower parts, as a wall that gains a second side does in every editor.
/// </summary>
/// <remarks>
/// Works only near the shape (walls and corners found through the shapes' grids), so it costs the same
/// on any size of map. <c>before</c> must show the map as it is (its version equals the map's).
/// </remarks>
public static class ShapeDrawing
{
    /// <summary>Points closer than this are the same point, in map units.</summary>
    private const double Eps = 1e-6;

    /// <summary>How far off a wall a point is taken to find out what lies on that side of it.</summary>
    private const double Probe = 1.0 / 64;

    /// <summary>A wall in the drawing's working set: an existing line, or a piece of the shape that has no line yet.</summary>
    private sealed class Edge(Vertex a, Vertex b, Line? line)
    {
        public Vertex A { get; set; } = a;

        public Vertex B { get; set; } = b;

        public Line? Line { get; } = line;

        /// <summary>On the shape's outline.</summary>
        public bool Boundary { get; set; }

        /// <summary>The shape runs from A to B along it, so the inside is on the right of A to B.</summary>
        public bool BoundaryForward { get; set; }

        /// <summary>A divider drawn inside the shape (a cut), not part of its outline.</summary>
        public bool Cut { get; set; }

        /// <summary>For a new piece: the area found on its right (A to B), and on its left (a cut's other side).</summary>
        public Sector? Right { get; set; }

        public Sector? Left { get; set; }
    }

    /// <summary>One direction of an edge, with the region it bounds on its right.</summary>
    private readonly record struct Half(Edge Edge, bool Forward)
    {
        public Vertex Origin => Forward ? Edge.A : Edge.B;

        public Vertex Target => Forward ? Edge.B : Edge.A;

        public Side? Side => Edge.Line is { } l ? Forward ? l.Front : l.Back : null;
    }

    /// <summary>
    /// Draws <paramref name="shape"/> (three or more corners, either way round, not crossing itself)
    /// into the map through <paramref name="edit"/>, and returns the areas made inside it.
    /// <paramref name="cuts"/> are walls drawn across the inside as well (each from one point on the
    /// outline or inside to another), so the regions between them become areas of their own sharing
    /// those walls; their lower parts show <paramref name="cutLower"/> (a staircase's risers), else the
    /// Look's wall.
    /// </summary>
    public static IReadOnlyList<Sector> Draw(StructureEdit edit, MapGeometry before, IReadOnlyList<MapPoint> shape, Look look, IReadOnlyList<(MapPoint A, MapPoint B)>? cuts = null, string? cutLower = null)
    {
        if (before.Version != edit.Map.Version) throw new InvalidOperationException("The map's shapes are behind the map; draw once they have caught up.");
        var corners = Clean(shape);
        var map = edit.Map;

        // The walls and corners near the shape: everything the shape can touch.
        var (x0, y0, x1, y1) = (corners.Min(p => p.X) - 1, corners.Min(p => p.Y) - 1, corners.Max(p => p.X) + 1, corners.Max(p => p.Y) + 1);
        var edges = before.LinesIn(x0, y0, x1, y1).Select(l => new Edge(l.Start, l.End, l)).ToList();
        var vertices = new List<Vertex>(before.VerticesIn(x0, y0, x1, y1));
        foreach (var e in edges)
        {
            if (!vertices.Contains(e.A)) vertices.Add(e.A);
            if (!vertices.Contains(e.B)) vertices.Add(e.B);
        }

        // The shape's corners: existing corners where they land on one, a split wall where they land on a wall.
        var ring = corners.Select(p => CornerAt(p)).ToArray();

        // Each side of the shape: split by the corners on it and the walls crossing it, then one edge per piece.
        for (var i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            var stops = new List<(double T, Vertex V)> { (0, a), (1, b) };
            foreach (var v in vertices)
            {
                if (v != a && v != b && Along(Point(v), Point(a), Point(b)) is { } t) stops.Add((t, v));
            }

            foreach (var e in edges.ToList())
            {
                if (Crossing(Point(a), Point(b), Point(e.A), Point(e.B)) is not { } hit) continue;
                var m = edit.Add(new Vertex { X = hit.X, Y = hit.Y });
                vertices.Add(m);
                Split(e, m);
                stops.Add((hit.T, m));
            }

            stops.Sort((p, q) => p.T.CompareTo(q.T));
            for (var k = 0; k + 1 < stops.Count; k++)
            {
                var (u, w) = (stops[k].V, stops[k + 1].V);
                if (u == w) continue;
                var existing = edges.FirstOrDefault(e => (e.A == u && e.B == w) || (e.A == w && e.B == u));
                if (existing is null)
                {
                    existing = new Edge(u, w, null);
                    edges.Add(existing);
                }

                existing.Boundary = true;
                existing.BoundaryForward = existing.A == u;
            }
        }

        // The cuts: split where they touch or cross what is there, one new edge per piece not already a wall.
        foreach (var (pa, pb) in cuts ?? [])
        {
            var a = CornerAt(pa);
            var b = CornerAt(pb);
            if (a == b) continue;
            var stops = new List<(double T, Vertex V)> { (0, a), (1, b) };
            foreach (var v in vertices)
            {
                if (v != a && v != b && Along(Point(v), Point(a), Point(b)) is { } t) stops.Add((t, v));
            }

            foreach (var e in edges.ToList())
            {
                if (Crossing(Point(a), Point(b), Point(e.A), Point(e.B)) is not { } hit) continue;
                var m = edit.Add(new Vertex { X = hit.X, Y = hit.Y });
                vertices.Add(m);
                Split(e, m);
                stops.Add((hit.T, m));
            }

            stops.Sort((p, q) => p.T.CompareTo(q.T));
            for (var k = 0; k + 1 < stops.Count; k++)
            {
                var (u, w) = (stops[k].V, stops[k + 1].V);
                if (u != w && !edges.Any(e => (e.A == u && e.B == w) || (e.A == w && e.B == u))) edges.Add(new Edge(u, w, null) { Cut = true });
            }
        }

        // The region inside the shape: its outline seen from inside, and both sides of every wall within it.
        var outline = ring.Select(Point).ToArray();
        var halves = new List<Half>();
        foreach (var e in edges)
        {
            if (e.Boundary)
            {
                halves.Add(new Half(e, e.BoundaryForward));
            }
            else if (Contains(outline, Mid(e)))
            {
                halves.Add(new Half(e, true));
                halves.Add(new Half(e, false));
            }
        }

        var (outer, holes) = Trace(halves);

        // Each hole (an island's outside, a loose wall) belongs to the smallest region around it.
        var faces = outer.Select(c => (Cycle: c, Area: Math.Abs(Area(c)), Holes: new List<List<Half>>())).ToList();
        foreach (var hole in holes)
        {
            var probe = RightOf(hole[0]);
            var home = faces.Where(f => Contains(f.Cycle.Select(h => Point(h.Origin)).ToArray(), probe)).OrderBy(f => f.Area).FirstOrDefault();
            home.Holes?.Add(hole);
        }

        // A region that touches the shape's outline becomes a new area, unless it already is one whole area
        // (one area all round, which does not carry on outside the outline). Regions inside the shape that
        // do not reach its outline (a room drawn around, a pillar's empty inside) stay as they were.
        var made = new List<Sector>();
        var emptied = new HashSet<Sector>(ReferenceEqualityComparer.Instance);
        foreach (var (cycle, _, faceHoles) in faces)
        {
            var all = cycle.Concat(faceHoles.SelectMany(h => h)).ToList();
            if (!all.Any(h => h.Edge.Boundary)) continue;
            var first = all[0].Side?.Sector;
            if (first is not null && all.All(h => ReferenceEquals(h.Side?.Sector, first)) && !all.Any(h => h.Edge.Boundary && ReferenceEquals(new Half(h.Edge, !h.Forward).Side?.Sector, first))) continue;

            // In the void, against other rooms: the heights of the one it shares the most wall with, so no step appears by
            // accident (D27).
            var around = before.SectorAt(RightOf(cycle[0]));
            var neighbour = around is not null ? null : cycle.Select(h => (Sector: new Half(h.Edge, !h.Forward).Side?.Sector, Length: Point(h.Edge.A).DistanceTo(Point(h.Edge.B))))
                .Where(x => x.Sector is not null).GroupBy(x => x.Sector!, ReferenceEqualityComparer.Instance).OrderByDescending(g => g.Sum(x => x.Length)).Select(g => (Sector?)g.Key).FirstOrDefault();
            var sector = edit.Add(NewSector(around, look, neighbour));
            made.Add(sector);
            foreach (var h in all)
            {
                if (h.Edge.Line is not { } line)
                {
                    if (h.Forward) h.Edge.Right = sector;
                    else h.Edge.Left = sector;
                }
                else if (h.Side is { } side)
                {
                    emptied.Add(side.Sector);
                    edit.Set(side, s => s.Sector, (s, v) => s.Sector = v, sector);
                }
                else
                {
                    GiveBack(line, sector);
                }
            }
        }

        // The cuts' walls: between the areas on their two sides.
        foreach (var e in edges)
        {
            if (e.Line is not null || !e.Cut || e.Right is not { } right || e.Left is not { } left) continue;
            var lower = cutLower ?? look.Wall;
            var line = new Line(e.A, e.B, edit.Add(new Side(right) { Top = look.Wall, Bottom = lower }))
            {
                Back = edit.Add(new Side(left) { Top = look.Wall, Bottom = lower }),
            };
            line.SetFlag("twosided", true);
            edit.Add(line);
        }

        // The shape's new walls: the new area inside, and outside whatever was there before (if anything).
        foreach (var e in edges)
        {
            if (e.Line is not null || e.Cut || e.Right is not { } inside) continue;
            var outside = before.SectorAt(LeftOf(e));
            var front = edit.Add(new Side(inside));
            var line = new Line(e.A, e.B, front);
            if (outside is null)
            {
                front.Middle = look.Wall;
                line.SetFlag("blocking", true);
            }
            else
            {
                front.Top = front.Bottom = look.Wall;
                var back = edit.Add(new Side(outside) { Top = look.Wall, Bottom = look.Wall });
                line.Back = back;
                line.SetFlag("twosided", true);
            }

            edit.Add(line);
        }

        // An area left with no walls at all (drawn over completely) is taken off the map.
        foreach (var old in emptied)
        {
            if (made.Contains(old) || before.WallBoundsAt(old.Slot) is not { } b || b.MinX < x0 || b.MinY < y0 || b.MaxX > x1 || b.MaxY > y1) continue;
            if (!edges.Any(e => e.Line is { } l && (ReferenceEquals(l.Front.Sector, old) || ReferenceEquals(l.Back?.Sector, old)))) edit.Remove(old);
        }

        return made;

        Vertex CornerAt(MapPoint p)
        {
            foreach (var v in vertices)
            {
                if (Math.Abs(v.X - p.X) <= Eps && Math.Abs(v.Y - p.Y) <= Eps) return v;
            }

            var corner = edit.Add(new Vertex { X = p.X, Y = p.Y });
            vertices.Add(corner);
            foreach (var e in edges)
            {
                if (Along(p, Point(e.A), Point(e.B)) is null) continue;
                Split(e, corner);
                break;
            }

            return corner;
        }

        // Splits a wall at a corner on it (Walls.Split, or a piece of the shape not yet a wall) and keeps the working set in step.
        void Split(Edge e, Vertex m)
        {
            if (e.Line is null)
            {
                var to = e.B;
                e.B = m;
                edges.Add(new Edge(m, to, null) { Boundary = e.Boundary, BoundaryForward = e.BoundaryForward, Cut = e.Cut });
                return;
            }

            var line = e.Line;
            var end = line.End;
            var piece = Walls.Split(edit, line, m);
            e.B = m;

            // A wall already on the outline (or a cut) stays so in both its pieces.
            edges.Add(new Edge(m, end, piece) { Boundary = e.Boundary, BoundaryForward = e.BoundaryForward, Cut = e.Cut });
        }

        void GiveBack(Line line, Sector sector) => Walls.OpenUp(edit, line, sector, look.Wall);
    }

    /// <summary>The shape's corners without repeats, turned clockwise (so the inside is on the right of each side).</summary>
    private static MapPoint[] Clean(IReadOnlyList<MapPoint> shape)
    {
        var list = new List<MapPoint>();
        foreach (var p in shape)
        {
            if (list.Count == 0 || list[^1].DistanceTo(p) > Eps) list.Add(p);
        }

        if (list.Count > 1 && list[0].DistanceTo(list[^1]) <= Eps) list.RemoveAt(list.Count - 1);
        if (list.Count < 3) throw new ArgumentException("A shape needs at least three corners.", nameof(shape));
        var area = 0.0;
        for (var i = 0; i < list.Count; i++)
        {
            var (a, b) = (list[i], list[(i + 1) % list.Count]);
            area += a.X * b.Y - b.X * a.Y;
        }

        if (Math.Abs(area) <= Eps) throw new ArgumentException("A shape needs an inside.", nameof(shape));
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                if (Crossing(list[i], list[(i + 1) % list.Count], list[j], list[(j + 1) % list.Count]) is not null) throw new ArgumentException("A shape must not cross itself.", nameof(shape));
            }
        }

        if (area > 0) list.Reverse();
        return [.. list];
    }

    /// <summary>
    /// Walks every half round its region, always taking the sharpest right turn, so the region stays on
    /// the right: clockwise walks go round a region's outline, the others round a hole in one.
    /// </summary>
    private static (List<List<Half>> Outer, List<List<Half>> Holes) Trace(List<Half> halves)
    {
        var leaving = new Dictionary<Vertex, List<Half>>(ReferenceEqualityComparer.Instance);
        foreach (var h in halves)
        {
            if (!leaving.TryGetValue(h.Origin, out var list)) leaving[h.Origin] = list = [];
            list.Add(h);
        }

        var outer = new List<List<Half>>();
        var holes = new List<List<Half>>();
        var done = new HashSet<Half>();
        foreach (var start in halves)
        {
            if (done.Contains(start)) continue;
            var cycle = new List<Half>();
            var at = start;
            do
            {
                if (!done.Add(at) || cycle.Count > halves.Count) throw new InvalidOperationException("The walls there overlap in a way WadSmith cannot follow yet.");
                cycle.Add(at);
                at = Next(at);
            }
            while (at != start);

            (Area(cycle) < -Eps ? outer : holes).Add(cycle);
        }

        return (outer, holes);

        Half Next(Half h)
        {
            var o = Point(h.Origin);
            var t = Point(h.Target);
            var back = Math.Atan2(o.Y - t.Y, o.X - t.X);
            Half? best = null;
            var bestTurn = double.MaxValue;
            foreach (var c in leaving.TryGetValue(h.Target, out var list) ? list : [])
            {
                var d = Point(c.Target);
                var turn = Math.Atan2(d.Y - t.Y, d.X - t.X) - back;
                while (turn <= 1e-12) turn += 2 * Math.PI;
                while (turn > 2 * Math.PI + 1e-12) turn -= 2 * Math.PI;

                // Going straight back along the same wall is the last resort (the end of a loose wall).
                if (c.Edge == h.Edge) turn = 2 * Math.PI + 1;
                if (turn < bestTurn)
                {
                    bestTurn = turn;
                    best = c;
                }
            }

            return best ?? throw new InvalidOperationException("The walls there overlap in a way WadSmith cannot follow yet.");
        }
    }

    private static Sector NewSector(Sector? around, Look look, Sector? neighbour = null)
    {
        if (around is null) return new Sector { FloorHeight = neighbour?.FloorHeight ?? look.FloorHeight, CeilingHeight = neighbour?.CeilingHeight ?? look.CeilingHeight, FloorTexture = look.Floor, CeilingTexture = look.Ceiling, Light = look.Light };
        var s = new Sector { FloorHeight = around.FloorHeight, CeilingHeight = around.CeilingHeight, FloorTexture = around.FloorTexture, CeilingTexture = around.CeilingTexture, Light = around.Light };
        foreach (var (k, v) in around.Extra) s.Extra[k] = v;
        return s;
    }

    private static MapPoint Point(Vertex v) => new(v.X, v.Y);

    private static MapPoint Mid(Edge e) => new((e.A.X + e.B.X) / 2, (e.A.Y + e.B.Y) / 2);

    /// <summary>A point just right of a half's middle: in the region it bounds.</summary>
    private static MapPoint RightOf(Half h)
    {
        var (o, t) = (Point(h.Origin), Point(h.Target));
        var length = o.DistanceTo(t);
        return new MapPoint((o.X + t.X) / 2 + (t.Y - o.Y) / length * Probe, (o.Y + t.Y) / 2 - (t.X - o.X) / length * Probe);
    }

    /// <summary>A point just left of a new edge's middle (outside the shape).</summary>
    private static MapPoint LeftOf(Edge e) => RightOf(new Half(e, !e.BoundaryForward));

    /// <summary>Twice the signed area of a walk (negative when clockwise).</summary>
    private static double Area(List<Half> cycle)
    {
        var area = 0.0;
        foreach (var h in cycle) area += h.Origin.X * h.Target.Y - h.Target.X * h.Origin.Y;
        return area;
    }

    /// <summary>Whether p lies on the segment a–b strictly between its ends; the position along it (0–1) if so.</summary>
    private static double? Along(MapPoint p, MapPoint a, MapPoint b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length2 = dx * dx + dy * dy;
        if (length2 <= Eps * Eps) return null;
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2;
        if (t * Math.Sqrt(length2) <= Eps || (1 - t) * Math.Sqrt(length2) <= Eps) return null;
        var cross = (p.X - a.X) * dy - (p.Y - a.Y) * dx;
        return Math.Abs(cross) / Math.Sqrt(length2) <= Eps ? t : null;
    }

    /// <summary>Where two segments cross in both their middles (not at an end, not lying along each other): the point, and how far along a–b.</summary>
    private static (double X, double Y, double T)? Crossing(MapPoint a, MapPoint b, MapPoint c, MapPoint d)
    {
        double Side(MapPoint p, MapPoint q, MapPoint r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        var lab = a.DistanceTo(b);
        var lcd = c.DistanceTo(d);
        if (lab <= Eps || lcd <= Eps) return null;
        var d1 = Side(c, d, a) / lcd;
        var d2 = Side(c, d, b) / lcd;
        var d3 = Side(a, b, c) / lab;
        var d4 = Side(a, b, d) / lab;
        if (!((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps))) return null;
        if (!((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps))) return null;
        var t = d1 / (d1 - d2);
        return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, t);
    }

    /// <summary>Even-odd point in polygon.</summary>
    private static bool Contains(MapPoint[] polygon, MapPoint p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }

        return inside;
    }
}
