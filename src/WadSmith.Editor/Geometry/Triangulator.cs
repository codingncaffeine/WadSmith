namespace WadSmith.Editor.Geometry;

/// <summary>
/// Cuts an area's outline into triangles for the 3D view: each outer loop with the holes inside it is
/// joined into one polygon by a cut from each hole to a corner of the outline it can see, then ears
/// are clipped off one by one. Areas in maps are traced with the area on their right, so outer loops
/// run clockwise and holes counter-clockwise; the orientation, not the order, decides which is which.
/// Odd shapes (crossing walls, open chains) still give triangles, never an endless loop.
/// </summary>
public static class Triangulator
{
    /// <summary>The triangles covering the area, three points each, counter-clockwise.</summary>
    public static List<MapPoint> Triangulate(IReadOnlyList<MapPoint[]> loops)
    {
        var result = new List<MapPoint>();
        var outers = new List<List<MapPoint>>();
        var holes = new List<List<MapPoint>>();
        foreach (var loop in loops)
        {
            var clean = Clean(loop);
            if (clean.Count < 3) continue;
            var area = SignedArea(clean);
            if (area == 0) continue;

            // Clockwise loops (area on the right) are outlines; make them counter-clockwise. Holes the other way round.
            if (area < 0)
            {
                clean.Reverse();
                outers.Add(clean);
            }
            else
            {
                clean.Reverse();
                holes.Add(clean);
            }
        }

        // An area traced the other way round (or a lone hole) still deserves a floor.
        if (outers.Count == 0 && holes.Count > 0)
        {
            foreach (var h in holes)
            {
                h.Reverse();
                outers.Add(h);
            }

            holes.Clear();
        }

        // Each hole goes to the smallest outline that holds it.
        var owned = outers.Select(_ => new List<List<MapPoint>>()).ToList();
        foreach (var hole in holes)
        {
            var best = -1;
            var bestArea = double.MaxValue;
            for (var i = 0; i < outers.Count; i++)
            {
                if (!Inside(outers[i], hole[0])) continue;
                var a = -SignedArea(outers[i]) * -1;
                if (Math.Abs(a) < bestArea)
                {
                    bestArea = Math.Abs(a);
                    best = i;
                }
            }

            if (best >= 0) owned[best].Add(hole);
        }

        for (var i = 0; i < outers.Count; i++)
        {
            var polygon = owned[i].Count == 0 ? outers[i] : Bridge(outers[i], owned[i]);
            ClipEars(polygon, result);
        }

        return result;
    }

    /// <summary>Twice nothing: the shoelace area, positive when counter-clockwise (y up).</summary>
    public static double SignedArea(IReadOnlyList<MapPoint> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X - p[i].X) * (p[j].Y + p[i].Y);
        return a / 2;
    }

    private static List<MapPoint> Clean(MapPoint[] loop)
    {
        // Drop repeated corners (zero-length walls) so the clipper never sees a degenerate edge.
        var list = new List<MapPoint>(loop.Length);
        foreach (var p in loop)
        {
            if (list.Count == 0 || list[^1] != p) list.Add(p);
        }

        while (list.Count > 1 && list[0] == list[^1]) list.RemoveAt(list.Count - 1);
        return list;
    }

    /// <summary>Joins the holes to the outline: rightmost hole first, each by a cut to a corner it can see.</summary>
    private static List<MapPoint> Bridge(List<MapPoint> outer, List<List<MapPoint>> holes)
    {
        var polygon = new List<MapPoint>(outer);
        foreach (var hole in holes.OrderByDescending(h => h.Max(p => p.X)))
        {
            // The hole's rightmost corner, and the outline's corner nearest to its right that the cut can reach.
            var hi = 0;
            for (var i = 1; i < hole.Count; i++)
            {
                if (hole[i].X > hole[hi].X) hi = i;
            }

            var m = hole[hi];
            var join = FindBridge(polygon, m);
            if (join < 0) continue;

            // The hole touches the outline partway along an edge: put a corner there and join at it.
            if (polygon[join] != m && TouchingEdge(polygon, m) is { } touched)
            {
                polygon.Insert(touched + 1, m);
                join = touched + 1;
            }

            // outline … join, hole from m round to m, back to join, … outline. Where the hole touches the
            // outline (join and m are the same point) it is spliced in at that corner, with no zero-length cut.
            var merged = new List<MapPoint>(polygon.Count + hole.Count + 2);
            merged.AddRange(polygon.Take(join + 1));
            if (polygon[join] == m)
            {
                for (var k = 1; k <= hole.Count; k++) merged.Add(hole[(hi + k) % hole.Count]);
            }
            else
            {
                for (var k = 0; k <= hole.Count; k++) merged.Add(hole[(hi + k) % hole.Count]);
                merged.Add(polygon[join]);
            }

            merged.AddRange(polygon.Skip(join + 1));
            polygon = merged;
        }

        return polygon;
    }

    /// <summary>
    /// The corner of the polygon to join a hole's rightmost point <paramref name="m"/> to: cast a ray to
    /// the right, take the nearest edge it hits, then the corner of that edge (or, if another corner sits
    /// inside the triangle the cut would sweep, the one at the smallest angle) — Eberly's method.
    /// </summary>
    private static int FindBridge(List<MapPoint> polygon, MapPoint m)
    {
        var bestX = double.MaxValue;
        var edge = -1;
        for (int i = 0, n = polygon.Count; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];
            if ((a.Y > m.Y) == (b.Y > m.Y) && !(a.Y == m.Y || b.Y == m.Y)) continue;
            if (a.Y == b.Y) continue;
            var t = (m.Y - a.Y) / (b.Y - a.Y);
            if (t < 0 || t > 1) continue;
            var x = a.X + t * (b.X - a.X);
            if (x < m.X || x >= bestX) continue;
            bestX = x;
            edge = i;
        }

        if (edge < 0) return -1;
        var p0 = polygon[edge];
        var p1 = polygon[(edge + 1) % polygon.Count];
        var candidate = p0.X > p1.X ? edge : (edge + 1) % polygon.Count;
        var hit = new MapPoint(bestX, m.Y);
        var c = polygon[candidate];
        var best = candidate;

        // The ray met a corner exactly: that corner is in plain sight.
        if (p0 == hit) best = edge;
        else if (p1 == hit) best = (edge + 1) % polygon.Count;
        else
        {
            // Any corner inside the triangle m, hit, c would block the cut; take the one closest in
            // angle to the ray, and the nearer of two at the same angle.
            var bestAngle = double.MaxValue;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                var p = polygon[i];
                if (i == candidate || p.X < m.X) continue;
                if (!InTriangle(m, hit, c, p) && !InTriangle(m, c, hit, p)) continue;
                var angle = Math.Abs(Math.Atan2(p.Y - m.Y, p.X - m.X));
                var distance = p.DistanceTo(m);
                if (angle < bestAngle || (angle == bestAngle && distance < bestDistance))
                {
                    bestAngle = angle;
                    bestDistance = distance;
                    best = i;
                }
            }
        }

        // A corner earlier holes were joined to appears more than once; join to the copy whose inside
        // angle faces the hole, or the new cut would cross an earlier one.
        var corner = polygon[best];
        for (var i = 0; i < polygon.Count; i++)
        {
            if (polygon[i] != corner || !FacesInside(polygon, i, m)) continue;
            return i;
        }

        return best;
    }

    /// <summary>The edge (by its first corner) that passes through <paramref name="m"/> between its corners, if any.</summary>
    private static int? TouchingEdge(List<MapPoint> polygon, MapPoint m)
    {
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (a == m || b == m || Cross(a, b, m) != 0) continue;
            if (OnSegment(a, b, m)) return i;
        }

        return null;
    }

    /// <summary>Whether the point lies within the inside angle at corner <paramref name="i"/> of a counter-clockwise polygon.</summary>
    private static bool FacesInside(List<MapPoint> polygon, int i, MapPoint m)
    {
        var prev = polygon[(i - 1 + polygon.Count) % polygon.Count];
        var v = polygon[i];
        var next = polygon[(i + 1) % polygon.Count];
        return Cross(prev, v, next) >= 0
            ? Cross(v, next, m) >= 0 && Cross(prev, v, m) >= 0
            : Cross(v, next, m) >= 0 || Cross(prev, v, m) >= 0;
    }

    private static void ClipEars(List<MapPoint> polygon, List<MapPoint> output)
    {
        var n = polygon.Count;
        var next = new int[n];
        var prev = new int[n];
        for (var i = 0; i < n; i++)
        {
            next[i] = (i + 1) % n;
            prev[i] = (i - 1 + n) % n;
        }

        var remaining = n;
        var at = 0;
        while (remaining > 3)
        {
            // Look for an ear going round once; if none is clean (odd shapes), accept one with corners only
            // on its edges; failing that, cut the convex corner with the smallest ear so the loop always ends.
            var ear = FindEar(polygon, at, next, prev, remaining, strict: true);
            if (ear < 0) ear = FindEar(polygon, at, next, prev, remaining, strict: false);
            if (ear < 0) ear = SmallestConvex(polygon, at, next, prev, remaining);
            if (ear < 0) return;

            var a = prev[ear];
            var c = next[ear];
            if (Cross(polygon[a], polygon[ear], polygon[c]) > 0)
            {
                output.Add(polygon[a]);
                output.Add(polygon[ear]);
                output.Add(polygon[c]);
            }

            next[a] = c;
            prev[c] = a;
            remaining--;
            at = c;
        }

        var x = prev[at];
        var z = next[at];
        if (Cross(polygon[x], polygon[at], polygon[z]) > 0)
        {
            output.Add(polygon[x]);
            output.Add(polygon[at]);
            output.Add(polygon[z]);
        }
    }

    private static int FindEar(List<MapPoint> p, int start, int[] next, int[] prev, int remaining, bool strict)
    {
        var b = start;
        for (var k = 0; k < remaining; k++, b = next[b])
        {
            if (IsEar(p, prev[b], b, next[b], next, remaining, strict)) return b;
        }

        return -1;
    }

    private static int SmallestConvex(List<MapPoint> p, int start, int[] next, int[] prev, int remaining)
    {
        var best = -1;
        var bestArea = double.MaxValue;
        var b = start;
        for (var k = 0; k < remaining; k++, b = next[b])
        {
            var area = Cross(p[prev[b]], p[b], p[next[b]]);
            if (area <= 0 || area >= bestArea) continue;
            bestArea = area;
            best = b;
        }

        // Nothing convex left (a tangle): drop any corner.
        return best >= 0 ? best : start;
    }

    /// <summary>
    /// Whether the corner b with its neighbours a and c is an ear: convex, and no other remaining corner
    /// inside it or on its new edge a–c. Copies of a, b and c (where a hole joins) do not count.
    /// Relaxed (<paramref name="strict"/> false), only corners strictly inside count.
    /// </summary>
    private static bool IsEar(List<MapPoint> p, int a, int b, int c, int[] next, int remaining, bool strict)
    {
        var pa = p[a];
        var pb = p[b];
        var pc = p[c];
        if (Cross(pa, pb, pc) <= 0) return false;
        var i = next[c];
        for (var k = 0; k < remaining - 3; k++, i = next[i])
        {
            var q = p[i];
            if (q == pa || q == pb || q == pc) continue;
            var ab = Cross(pa, pb, q);
            var bc = Cross(pb, pc, q);
            var ca = Cross(pc, pa, q);
            if (ab > 0 && bc > 0 && ca > 0) return false;
            if (strict && ab >= 0 && bc >= 0 && ca == 0 && OnSegment(pc, pa, q)) return false;
        }

        return true;
    }

    private static bool OnSegment(MapPoint a, MapPoint b, MapPoint q) =>
        q.X >= Math.Min(a.X, b.X) && q.X <= Math.Max(a.X, b.X) && q.Y >= Math.Min(a.Y, b.Y) && q.Y <= Math.Max(a.Y, b.Y);

    private static double Cross(MapPoint a, MapPoint b, MapPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool InTriangle(MapPoint a, MapPoint b, MapPoint c, MapPoint p) =>
        Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0;

    private static bool Inside(List<MapPoint> loop, MapPoint p)
    {
        var inside = false;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            var a = loop[i];
            var b = loop[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }

        return inside;
    }
}
