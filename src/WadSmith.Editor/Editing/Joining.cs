using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// What a move does when it is let go (Doom Builder's joining of dragged geometry): a moved corner on
/// a corner that stayed becomes that corner; a corner in the middle of a wall splits it; and two
/// walls lying on each other, each with a room on one side, become one wall between the two rooms.
/// Walls left crossing are reported, not changed.
/// </summary>
public static class Joining
{
    private const double Eps = 1e-6;

    /// <summary>
    /// Joins the moved part of the map to the rest through <paramref name="edit"/>; returns how many
    /// times a moved wall now crosses a wall that stayed (0 when nothing crosses). Works near the
    /// moved corners only.
    /// </summary>
    public static int AfterMove(StructureEdit edit, MoveSet set, Look look)
    {
        if (set.Vertices.Count == 0) return 0;
        var geometry = set.Geometry;
        var moved = new HashSet<Vertex>(set.Vertices, ReferenceEqualityComparer.Instance);
        var movedLines = set.Lines.Where(l => l.OnMap).ToList();
        var (x0, y0, x1, y1) = (set.Vertices.Min(v => v.X) - 1, set.Vertices.Min(v => v.Y) - 1, set.Vertices.Max(v => v.X) + 1, set.Vertices.Max(v => v.Y) + 1);

        // What stayed near where the moved part landed; it did not move, so its places in the shapes are current.
        var stillVertices = geometry.VerticesIn(x0, y0, x1, y1).Where(v => v.OnMap && !moved.Contains(v)).ToList();
        var movedSet = new HashSet<Line>(movedLines, ReferenceEqualityComparer.Instance);
        var stillLines = geometry.LinesIn(x0, y0, x1, y1).Where(l => l.OnMap && !movedSet.Contains(l)).ToList();

        // 1. A moved corner on a corner that stayed becomes that corner.
        foreach (var mv in set.Vertices)
        {
            if (stillVertices.FirstOrDefault(v => Math.Abs(v.X - mv.X) <= Eps && Math.Abs(v.Y - mv.Y) <= Eps) is not { } sv) continue;
            foreach (var l in movedLines)
            {
                if (l.Start == mv) edit.Set(l, x => x.Start, (x, v) => x.Start = v, sv);
                if (l.End == mv) edit.Set(l, x => x.End, (x, v) => x.End = v, sv);
            }

            edit.Remove(mv);
        }

        // A wall whose two corners became one has gone to nothing: it goes, with its faces.
        foreach (var l in movedLines.Where(l => l.Start == l.End).ToList())
        {
            RemoveWall(edit, l);
            movedLines.Remove(l);
        }

        // 2. A corner in the middle of a wall splits it, both ways round.
        var corners = movedLines.SelectMany(l => new[] { l.Start, l.End }).Distinct().ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var v in corners.Where(c => c.OnMap))
            {
                if (stillLines.FirstOrDefault(l => Inside(v, l)) is { } wall)
                {
                    stillLines.Add(Walls.Split(edit, wall, v));
                    changed = true;
                }
            }

            foreach (var v in stillVertices.Where(c => c.OnMap))
            {
                if (movedLines.FirstOrDefault(l => Inside(v, l)) is { } wall)
                {
                    movedLines.Add(Walls.Split(edit, wall, v));
                    changed = true;
                }
            }
        }

        // 3. Two walls on each other with their rooms on opposite sides become one wall between the rooms.
        foreach (var l in movedLines.ToList())
        {
            if (l.Back is not null) continue;
            var other = stillLines.FirstOrDefault(s => s.OnMap && s.Back is null && s.Start == l.End && s.End == l.Start);
            if (other is null) continue;
            Walls.OpenUp(edit, other, l.Front.Sector, look.Wall, like: l.Front);
            RemoveWall(edit, l);
            movedLines.Remove(l);
        }

        // Crossings are left for the user to see; count those the move made.
        var crossing = 0;
        foreach (var l in movedLines)
        {
            foreach (var s in stillLines)
            {
                if (s.OnMap && l.OnMap && Cross(l, s)) crossing++;
            }
        }

        return crossing;
    }

    private static void RemoveWall(StructureEdit edit, Line l)
    {
        edit.Remove(l);
        edit.Remove(l.Front);
        if (l.Back is { } b) edit.Remove(b);
    }

    /// <summary>Whether a corner lies on a wall strictly between its ends.</summary>
    private static bool Inside(Vertex v, Line l)
    {
        if (v == l.Start || v == l.End) return false;
        var (ax, ay, bx, by) = (l.Start.X, l.Start.Y, l.End.X, l.End.Y);
        var (dx, dy) = (bx - ax, by - ay);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= Eps) return false;
        var t = ((v.X - ax) * dx + (v.Y - ay) * dy) / (length * length);
        if (t * length <= Eps || (1 - t) * length <= Eps) return false;
        return Math.Abs((v.X - ax) * dy - (v.Y - ay) * dx) / length <= Eps;
    }

    private static bool Cross(Line p, Line q)
    {
        static double Side(Vertex a, Vertex b, Vertex c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (p.Start == q.Start || p.Start == q.End || p.End == q.Start || p.End == q.End) return false;
        var (d1, d2) = (Side(q.Start, q.End, p.Start), Side(q.Start, q.End, p.End));
        var (d3, d4) = (Side(p.Start, p.End, q.Start), Side(p.Start, p.End, q.End));
        return ((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps)) && ((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps));
    }
}
