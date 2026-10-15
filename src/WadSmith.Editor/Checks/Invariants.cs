using System.Globalization;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Editor.Checks;

/// <summary>
/// The rules every map WadSmith makes keeps: walls have length and their own faces, point at corners and areas on the
/// map, and are two-sided exactly when they have a back; no two walls share both corners or cross, no corner sits in the
/// middle of a wall; every area is closed (as many of its walls arrive at each corner as leave it), has walls, and lies
/// on the right of every wall that faces it; and nothing closed shows as a hole of sky. Uses the map's shapes to look only
/// near each wall, so it is quick on any map.
/// </summary>
public static class Invariants
{
    private const int Shown = 5;

    /// <summary>Every rule broken, in sentences (at most a few of each kind, then how many more); empty when the map is sound.</summary>
    public static IReadOnlyList<string> Check(EditorMap map)
    {
        var faults = new Dictionary<string, List<string>>();
        void Fault(string kind, string sentence)
        {
            if (!faults.TryGetValue(kind, out var list)) faults[kind] = list = [];
            list.Add(sentence);
        }

        static string At(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"({x:0.##}, {y:0.##})");
        var g = MapGeometry.Build(map.Frame);
        var lines = map.Lines.Where(l => l.OnMap).ToList();
        var pairs = new HashSet<(long, long)>();
        foreach (var l in lines)
        {
            var mid = At((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);
            if (!l.Start.OnMap || !l.End.OnMap || !l.Front.OnMap || !(l.Back?.OnMap ?? true)) Fault("off", $"The wall at {mid} points at a corner or face that is not on the map.");
            if (l.Length <= 0) Fault("length", $"The wall at {mid} has no length.");
            if (!ReferenceEquals(l, l.Front.Line) || (l.Back is { } b && !ReferenceEquals(l, b.Line))) Fault("faces", $"The wall at {mid} shares a face with another wall.");
            if ((l.Back is not null) != l.Flag("twosided")) Fault("twosided", $"The wall at {mid} is {(l.Back is null ? "marked two-sided with one face" : "not marked two-sided with two faces")}.");
            if (!pairs.Add((Math.Min(l.Start.Id, l.End.Id), Math.Max(l.Start.Id, l.End.Id)))) Fault("twice", $"Two walls run between the same corners at {mid}.");

            // Walls near this one (by the shapes' grid) that cross it.
            foreach (var other in g.LinesIn(Math.Min(l.Start.X, l.End.X), Math.Min(l.Start.Y, l.End.Y), Math.Max(l.Start.X, l.End.X), Math.Max(l.Start.Y, l.End.Y)))
            {
                if (other.OnMap && other.Id > l.Id && Cross(l, other)) Fault("cross", $"The walls at {mid} and {At((other.Start.X + other.End.X) / 2, (other.Start.Y + other.End.Y) / 2)} cross.");
            }
        }

        foreach (var v in map.Vertices.Where(v => v.OnMap))
        {
            foreach (var l in g.LinesIn(v.X, v.Y, v.X, v.Y))
            {
                if (l.OnMap && l.Start != v && l.End != v && OnWall(v, l)) Fault("corner", $"The corner at {At(v.X, v.Y)} sits in the middle of a wall.");
            }
        }

        var balance = new Dictionary<(Sector, Vertex), int>();
        void Count(Sector s, Vertex from, Vertex to)
        {
            balance[(s, from)] = balance.GetValueOrDefault((s, from)) + 1;
            balance[(s, to)] = balance.GetValueOrDefault((s, to)) - 1;
        }

        foreach (var l in lines)
        {
            Count(l.Front.Sector, l.Start, l.End);
            if (l.Back is { } b) Count(b.Sector, l.End, l.Start);
        }

        foreach (var ((_, v), n) in balance.Where(kv => kv.Value != 0)) Fault("open", $"An area is open at {At(v.X, v.Y)}.");
        var used = lines.SelectMany(l => new[] { l.Front.Sector, l.Back?.Sector }).OfType<Sector>().ToHashSet();
        foreach (var s in map.Sectors.Where(s => s.OnMap && !used.Contains(s))) Fault("wallless", "An area has no walls.");
        foreach (var s in used.Where(s => !s.OnMap)) Fault("offarea", "A face belongs to an area that is not on the map.");

        // A column or a shut door with the sky as its ceiling, lower than every area beside it and one of those under the sky,
        // shows as a hole of sky: the engine draws no wall between two skies. A wall closed up at the sky (its floor raised to
        // it, as beside a door in an open-air passage) stands up to the sky and is left alone. The original games use such
        // areas on purpose here and there, so this is a rule of the maps WadSmith makes, not a Problem of every map.
        foreach (var s in map.Sectors.Where(s => s.OnMap && s.CeilingHeight <= s.FloorHeight && MapScene.IsSky(s.CeilingTexture)))
        {
            if (g.WallBoundsAt(s.Slot) is not { } b) continue;
            var beside = new List<Sector>();
            foreach (var l in g.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                if (!l.OnMap || l.Back is not { } back) continue;
                var other = ReferenceEquals(l.Front.Sector, s) ? back.Sector : ReferenceEquals(back.Sector, s) ? l.Front.Sector : null;
                if (other is not null && !ReferenceEquals(other, s)) beside.Add(other);
            }

            if (beside.Count > 0 && beside.All(n => n.CeilingHeight > s.CeilingHeight) && beside.Any(n => MapScene.IsSky(n.CeilingTexture)))
            {
                Fault("sky", $"The closed area at {At((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2)} has the sky as its ceiling beside more sky: it shows as a hole of sky.");
            }
        }

        foreach (var l in lines.Where(l => l.Length > 0))
        {
            var (dx, dy) = ((l.End.X - l.Start.X) / l.Length, (l.End.Y - l.Start.Y) / l.Length);
            var (mx, my) = ((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);
            if (!ReferenceEquals(l.Front.Sector, g.SectorAt(new MapPoint(mx + dy / 8, my - dx / 8)))) Fault("facing", $"The wall at {At(mx, my)} has its front's area on its left.");
            if (l.Back is { } back && !ReferenceEquals(back.Sector, g.SectorAt(new MapPoint(mx - dy / 8, my + dx / 8)))) Fault("facing", $"The wall at {At(mx, my)} has its back's area on its right.");
        }

        return [.. faults.Values.SelectMany(list => list.Count <= Shown ? list : [.. list.Take(Shown), $"… and {list.Count - Shown} more like that."])];
    }

    private static bool Cross(Line p, Line q)
    {
        static double Side(Vertex a, Vertex b, Vertex c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var (d1, d2) = (Side(q.Start, q.End, p.Start), Side(q.Start, q.End, p.End));
        var (d3, d4) = (Side(p.Start, p.End, q.Start), Side(p.Start, p.End, q.End));
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static bool OnWall(Vertex v, Line l)
    {
        var p = new MapPoint(v.X, v.Y);
        return MapGeometry.DistanceToSegment(p, MapPoint.Of(l.Start), MapPoint.Of(l.End)) < 1e-6 && p.DistanceTo(MapPoint.Of(l.Start)) > 1e-6 && p.DistanceTo(MapPoint.Of(l.End)) > 1e-6;
    }
}
