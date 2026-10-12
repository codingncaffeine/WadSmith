using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Floors and ceilings for the 3D view: triangles that cover each area exactly.</summary>
public sealed class TriangulatorTests
{
    private static double Area(List<MapPoint> t)
    {
        double sum = 0;
        for (var i = 0; i < t.Count; i += 3) sum += Triangulator.SignedArea([t[i], t[i + 1], t[i + 2]]);
        return sum;
    }

    [Fact]
    public void ASquareWithASquareHoleKeepsTheRing()
    {
        // Traced as maps are: outline clockwise (area on the right), hole counter-clockwise.
        MapPoint[] outer = [new(0, 0), new(0, 100), new(100, 100), new(100, 0)];
        MapPoint[] hole = [new(40, 40), new(60, 40), new(60, 60), new(40, 60)];
        var t = Triangulator.Triangulate([outer, hole]);
        Assert.Equal(10000 - 400, Area(t), 6);
        Assert.All(Enumerable.Range(0, t.Count / 3), i => Assert.True(Triangulator.SignedArea([t[3 * i], t[3 * i + 1], t[3 * i + 2]]) > 0));
    }

    public static TheoryData<string, string> GameMaps() => UdmfTests.GameMaps();

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void EveryClosedAreaIsCoveredExactly(string fileName, string game)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var bad = new List<string>();
        var checkedAreas = 0;
        foreach (var lumps in MapLocator.Find(wad))
        {
            var map = EditorMap.FromModel(MapConversion.Read(lumps, game));
            var geometry = MapGeometry.Build(map.Frame);
            foreach (var s in map.Sectors)
            {
                var loops = geometry.Loops(s);
                if (loops.Count == 0 || !Simple(loops)) continue;
                checkedAreas++;
                var expected = -loops.Sum(l => Triangulator.SignedArea(l));

                // An area whose outline encloses the void (traced inside out) has no finite floor to compare.
                if (expected <= 0) continue;
                var got = Area(Triangulator.Triangulate(loops));
                if (Math.Abs(got - expected) > Math.Max(1, Math.Abs(expected) * 1e-6)) bad.Add($"{lumps.Name} #{s.Slot} expected {expected:0} got {got:0}");
            }
        }

        Assert.True(checkedAreas > 0);
        Assert.True(bad.Count == 0, $"{bad.Count} of {checkedAreas} areas not covered exactly: " + string.Join("; ", bad.Take(12)));
    }

    /// <summary>
    /// Loops that neither cross themselves nor each other, share no corner and do not run along each
    /// other (the shapes the contract is exact for). The rest (one area in about 85,000 across the base
    /// games, such as a hole whose edge lies on the outline) still gets triangles, with slivers at most.
    /// </summary>
    private static bool Simple(IReadOnlyList<MapPoint[]> loops)
    {
        var corners = new HashSet<MapPoint>();
        foreach (var l in loops)
        {
            foreach (var p in l)
            {
                if (!corners.Add(p)) return false;
            }
        }

        var edges = loops.SelectMany((l, loop) => l.Select((p, i) => (A: p, B: l[(i + 1) % l.Length], Loop: loop))).ToList();
        for (var i = 0; i < edges.Count; i++)
        {
            for (var j = i + 1; j < edges.Count; j++)
            {
                if (Crosses(edges[i].A, edges[i].B, edges[j].A, edges[j].B)) return false;
                if (edges[i].Loop != edges[j].Loop && Overlap(edges[i].A, edges[i].B, edges[j].A, edges[j].B)) return false;
            }
        }

        return true;
    }

    /// <summary>Two edges on one line that share more than a point.</summary>
    private static bool Overlap(MapPoint a, MapPoint b, MapPoint c, MapPoint d)
    {
        static double Cross(MapPoint o, MapPoint p, MapPoint q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        if (Cross(a, b, c) != 0 || Cross(a, b, d) != 0) return false;
        var horizontal = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
        double A(MapPoint p) => horizontal ? p.X : p.Y;
        var lo = Math.Max(Math.Min(A(a), A(b)), Math.Min(A(c), A(d)));
        var hi = Math.Min(Math.Max(A(a), A(b)), Math.Max(A(c), A(d)));
        return hi > lo;
    }

    private static bool Crosses(MapPoint a, MapPoint b, MapPoint c, MapPoint d)
    {
        if (a == c || a == d || b == c || b == d) return false;
        static double Cross(MapPoint o, MapPoint p, MapPoint q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        var d1 = Cross(c, d, a);
        var d2 = Cross(c, d, b);
        var d3 = Cross(a, b, c);
        var d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
