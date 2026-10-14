using System.Collections.Concurrent;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The read-only copy of the map that workers use, and the shapes built from it.</summary>
public sealed class FrameTests
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    [Fact]
    public void AChangedChunkedListLeavesTheOriginalAlone()
    {
        var b = ChunkedList<int>.Empty.ToBuilder();
        for (var i = 0; i < 2500; i++) b.Add(i);
        var first = b.ToList();
        b[5] = -5;
        b[2400] = -2400;
        b.Add(2500);
        var second = b.ToList();

        Assert.Equal(2500, first.Count);
        Assert.Equal(5, first[5]);
        Assert.Equal(2400, first[2400]);
        Assert.Equal(2501, second.Count);
        Assert.Equal(-5, second[5]);
        Assert.Equal(-2400, second[2400]);
        Assert.Equal(2500, second[2500]);
        Assert.Equal(1000, second[1000]);
        Assert.Throws<ArgumentOutOfRangeException>(() => first[2500]);
    }

    [Fact]
    public void AnEditPublishesANewFrameAndKeepsTheOldOne()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var sector = map.Sectors[0];
        var before = map.Frame;

        history.Run(new SetProperty<Sector, double>(sector, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, 40));
        var after = map.Frame;

        Assert.NotSame(before, after);
        Assert.Equal(0, before.Sectors[sector.Slot].FloorHeight);
        Assert.Equal(40, after.Sectors[sector.Slot].FloorHeight);
        Assert.Equal(map.Version, after.Version);

        // Lists the edit did not touch are shared, not copied.
        Assert.Same(before.Lines, after.Lines);

        history.Undo();
        Assert.Equal(0, map.Frame.Sectors[sector.Slot].FloorHeight);
    }

    public static TheoryData<string, string> GameMaps() => UdmfTests.GameMaps();

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void TheFrameKeptUpEditByEditMatchesOneBuiltFromScratch(string fileName, string game)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var lumps = MapLocator.Find(wad).First();
        var map = EditorMap.FromModel(MapConversion.Read(lumps, game));
        var history = new History(map);
        var rng = new Random(7);

        // Edits of every kind of element, spread over the lists (several chunks on a large map).
        for (var i = 0; i < 40; i++)
        {
            var s = map.Sectors[rng.Next(map.Sectors.Count)];
            history.Run(new SetProperty<Sector, int>(s, "Change brightness", x => x.Light, (x, v) => x.Light = v, rng.Next(256)));
            var v = map.Vertices[rng.Next(map.Vertices.Count)];
            history.Run(new SetProperty<Vertex, double>(v, "Move corner", x => x.X, (x, n) => x.X = n, v.X + 8));
            var side = map.Sides[rng.Next(map.Sides.Count)];
            history.Run(new SetProperty<Side, string>(side, "Change texture", x => x.Middle, (x, n) => x.Middle = n, "STARTAN3"));
            var line = map.Lines[rng.Next(map.Lines.Count)];
            history.Run(new SetProperty<Line, int>(line, "Change action", x => x.Special, (x, n) => x.Special = n, 1));
            if (map.Things.Count > 0)
            {
                var t = map.Things[rng.Next(map.Things.Count)];
                history.Run(new SetProperty<Thing, int>(t, "Turn", x => x.Angle, (x, n) => x.Angle = n, 270));
            }
        }

        for (var i = 0; i < 25; i++) history.Undo();

        Assert.True(Udmf(map.FrameFromScratch().ToModel()) == Udmf(map.Frame.ToModel()), $"{fileName} {lumps.Name}: the kept-up frame differs from a fresh one");
    }

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void HitTestsAgreeWithAScanOfTheWholeMap(string fileName, string game)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var lumps = MapLocator.Find(wad).First();
        var map = EditorMap.FromModel(MapConversion.Read(lumps, game));
        var geometry = MapGeometry.Build(map.Frame);
        var (x0, y0, x1, y1) = geometry.Extent();
        var rng = new Random(11);

        for (var i = 0; i < 400; i++)
        {
            var p = new MapPoint(x0 - 64 + rng.NextDouble() * (x1 - x0 + 128), y0 - 64 + rng.NextDouble() * (y1 - y0 + 128));
            var radius = rng.Next(2) == 0 ? 8 : 200;
            Assert.Same(ScanLines(map, p, radius), geometry.LineNear(p, radius));
            Assert.Same(ScanVertices(map, p, radius), geometry.VertexNear(p, radius));
            Assert.Same(ScanThings(map, p, radius), geometry.ThingNear(p, radius));
            Assert.Same(ScanSectors(map, geometry, p), geometry.SectorAt(p));
        }
    }

    [Fact]
    public void AWallCrossingTheWholeMapIsStillFound()
    {
        // A 30-by-30 grid of rooms with one wall from corner to corner: far too long to file in each
        // cell it crosses, so the grid keeps it on its short list of large items.
        var m = Cli.Commands.StressGenCommand.Build(30);
        var v1 = m.Vertices.FindIndex(v => v.X == 0 && v.Y == 0);
        var v2 = m.Vertices.FindIndex(v => v.X == 30 * 128 && v.Y == 30 * 128);
        m.Sides.Add(new MapSide { Sector = 0 });
        m.Lines.Add(new MapLine { V1 = v1, V2 = v2, Front = m.Sides.Count - 1 });
        var map = EditorMap.FromModel(m);
        var geometry = MapGeometry.Build(map.Frame);
        var diagonal = map.Lines[^1];

        // Room centres sit on the diagonal, 64 units from the room's own walls.
        for (var k = 0; k < 30; k++)
        {
            var p = new MapPoint(k * 128 + 64, k * 128 + 64);
            Assert.Same(diagonal, geometry.LineNear(p, 8));
            Assert.Same(ScanLines(map, p, 8), geometry.LineNear(p, 8));
        }
    }

    // The plain scans the grids must agree with: nearest within the radius, the later one on a tie.
    private static Line? ScanLines(EditorMap map, MapPoint p, double radius)
    {
        Line? best = null;
        var bestD = radius;
        foreach (var l in map.Lines)
        {
            var d = MapGeometry.DistanceToSegment(p, MapPoint.Of(l.Start), MapPoint.Of(l.End));
            if (d <= bestD)
            {
                bestD = d;
                best = l;
            }
        }

        return best;
    }

    private static Vertex? ScanVertices(EditorMap map, MapPoint p, double radius)
    {
        Vertex? best = null;
        var bestD = radius;
        foreach (var v in map.Vertices)
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

    private static Thing? ScanThings(EditorMap map, MapPoint p, double radius)
    {
        Thing? best = null;
        var bestD = radius;
        foreach (var t in map.Things)
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

    private static Sector? ScanSectors(EditorMap map, MapGeometry geometry, MapPoint p)
    {
        Sector? best = null;
        var bestArea = double.MaxValue;
        foreach (var s in map.Sectors)
        {
            var loops = geometry.Loops(s);
            if (loops.Count == 0) continue;
            var pts = loops.SelectMany(l => l).ToList();
            double minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X), minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
            if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY) continue;
            var inside = false;
            foreach (var loop in loops)
            {
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                {
                    if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y) && p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X) inside = !inside;
                }
            }

            var area = (maxX - minX) * (maxY - minY);
            if (inside && area < bestArea)
            {
                best = s;
                bestArea = area;
            }
        }

        return best;
    }

    [Fact]
    public void TheWorkerBuildsOnlyTheNewestOfABurst()
    {
        var ui = new ConcurrentQueue<Action>();
        var published = new List<int>();
        var builds = 0;
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var worker = new LatestOnWorker<int>(ui.Enqueue, published.Add, "a test value");

        // The first build holds the worker while nine more requests arrive; only the last of them is built.
        worker.Request(() =>
        {
            Interlocked.Increment(ref builds);
            started.Set();
            gate.Wait(TestContext.Current.CancellationToken);
            return 0;
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        for (var i = 1; i <= 9; i++)
        {
            var n = i;
            worker.Request(() =>
            {
                Interlocked.Increment(ref builds);
                return n;
            });
        }

        gate.Set();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (published.Count < 2 && DateTime.UtcNow < until)
        {
            while (ui.TryDequeue(out var a)) a();
            Thread.Sleep(5);
        }

        Thread.Sleep(50);
        while (ui.TryDequeue(out var a)) a();
        Assert.Equal([0, 9], published);
        Assert.Equal(2, builds);
    }
}
