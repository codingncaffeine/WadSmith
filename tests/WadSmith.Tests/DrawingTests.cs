using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Drawing a shape into a map: corners joined, walls split, regions made areas, nothing removed.</summary>
public sealed class DrawingTests(ITestOutputHelper output)
{
    private static readonly Look Look = Look.For("doom");

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    /// <summary>The starter room: -256 to 256 both ways, the walls running clockwise.</summary>
    private static (EditorMap Map, History History) Starter()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        return (map, new History(map));
    }

    private static MapPoint[] Box(double x0, double y0, double x1, double y1) => [new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0)];

    private static IReadOnlyList<Sector> Draw(EditorMap map, History history, MapPoint[] shape)
    {
        IReadOnlyList<Sector> made = [];
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Draw a room", e => made = ShapeDrawing.Draw(e, geometry, shape, Look));
        return made;
    }

    private static Sector? At(EditorMap map, double x, double y) => MapGeometry.Build(map.Frame).SectorAt(new MapPoint(x, y));

    private static List<Line> Lines(EditorMap map) => map.Lines.Where(l => l.OnMap).ToList();

    /// <summary>
    /// The rules every drawn map keeps: walls have length and faces of their own, no two walls share both
    /// corners or cross, no corner sits in the middle of a wall, every area is closed (as many of its walls
    /// arrive at each corner as leave it), every area on the map has walls, two-sided means two faces,
    /// and the frame kept up edit by edit matches one built from scratch.
    /// </summary>
    internal static void AssertSound(EditorMap map)
    {
        var lines = Lines(map);
        var pairs = new HashSet<(long, long)>();
        foreach (var l in lines)
        {
            Assert.True(l.Start.OnMap && l.End.OnMap && l.Front.OnMap && (l.Back?.OnMap ?? true), "a wall points at something off the map");
            Assert.True(l.Length > 0, "a wall has no length");
            Assert.Same(l, l.Front.Line);
            if (l.Back is { } b) Assert.Same(l, b.Line);
            Assert.Equal(l.Back is not null, l.Flag("twosided"));
            Assert.True(pairs.Add((Math.Min(l.Start.Id, l.End.Id), Math.Max(l.Start.Id, l.End.Id))), "two walls share both corners");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = i + 1; j < lines.Count; j++) Assert.False(Cross(lines[i], lines[j]), $"walls {i} and {j} cross");
        }

        foreach (var v in map.Vertices.Where(v => v.OnMap))
        {
            foreach (var l in lines)
            {
                if (l.Start == v || l.End == v) continue;
                Assert.False(OnWall(v, l), $"a corner at ({v.X}, {v.Y}) sits in the middle of a wall");
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

        Assert.All(balance, kv => Assert.True(kv.Value == 0, $"an area is open at ({kv.Key.Item2.X}, {kv.Key.Item2.Y})"));
        var used = lines.SelectMany(l => new[] { l.Front.Sector, l.Back?.Sector }).OfType<Sector>().ToHashSet();
        Assert.All(map.Sectors.Where(s => s.OnMap), s => Assert.Contains(s, used));
        Assert.All(used, s => Assert.True(s.OnMap, "a face belongs to an area off the map"));
        Assert.Equal(Udmf(map.FrameFromScratch().ToModel()), Udmf(map.ToModel()));
    }

    private static bool Encloses(MapPoint[] loop, MapPoint p)
    {
        var inside = false;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            var (a, b) = (loop[i], loop[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }

        return inside;
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

    /// <summary>Draws, checks, then checks that undo gives back the map exactly and redo the drawing exactly.</summary>
    private static (EditorMap Map, IReadOnlyList<Sector> Made) DrawAndCheck(MapPoint[] shape)
    {
        var (map, history) = Starter();
        var before = Udmf(map.ToModel());
        var made = Draw(map, history, shape);
        AssertSound(map);
        var after = Udmf(map.ToModel());
        Assert.NotEqual(before, after);
        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
        AssertSound(map);
        history.Redo();
        Assert.Equal(after, Udmf(map.ToModel()));
        AssertSound(map);
        return (map, made);
    }

    [Fact]
    public void ARoomInTheVoidHasFourSolidWallsAndTheGamesLook()
    {
        var (map, made) = DrawAndCheck(Box(512, -128, 768, 128));
        var room = Assert.Single(made);
        Assert.Same(room, At(map, 640, 0));
        Assert.Equal(("FLOOR4_8", "CEIL3_5", 128.0), (room.FloorTexture, room.CeilingTexture, room.CeilingHeight));
        var walls = Lines(map).Where(l => l.Front.Sector == room).ToList();
        Assert.Equal(4, walls.Count);
        Assert.All(walls, l => Assert.True(l.Back is null && l.Flag("blocking") && l.Front.Middle == "STARTAN3"));
        Assert.Equal(8, Lines(map).Count);
    }

    [Fact]
    public void ARoomBesideAnotherSharesTheWallBetweenThem()
    {
        var (map, made) = DrawAndCheck(Box(256, -256, 768, 256));
        var room = Assert.Single(made);
        Assert.Equal(7, Lines(map).Count);
        var shared = Assert.Single(Lines(map), l => l.Back is not null);
        Assert.Same(room, shared.Back!.Sector);
        Assert.Same(map.Sectors[0], shared.Front.Sector);

        // The wall that gained a second side lets the player through and keeps its look on the upper and lower parts.
        Assert.False(shared.Flag("blocking"));
        Assert.Equal(("STARTAN3", "-", "STARTAN3"), (shared.Front.Top, shared.Front.Middle, shared.Front.Bottom));
        Assert.Same(room, At(map, 512, 0));
        Assert.Same(map.Sectors[0], At(map, 0, 0));
    }

    [Fact]
    public void ATallerRoomBesideAnotherSharesThePartTheyHaveInCommon()
    {
        var (map, made) = DrawAndCheck(Box(256, -512, 768, 512));
        var room = Assert.Single(made);
        Assert.Equal(4 + 5, Lines(map).Count);
        Assert.Single(Lines(map), l => l.Back is not null);
        Assert.Same(room, At(map, 300, 400));
    }

    [Fact]
    public void ARoomInsideAnotherLooksLikeItAndLeavesItClosedAroundIt()
    {
        var (map, made) = DrawAndCheck(Box(-64, -64, 64, 64));
        var inner = Assert.Single(made);
        var outer = map.Sectors[0];
        Assert.Equal((outer.FloorHeight, outer.CeilingHeight, outer.FloorTexture, outer.CeilingTexture, outer.Light), (inner.FloorHeight, inner.CeilingHeight, inner.FloorTexture, inner.CeilingTexture, inner.Light));
        Assert.Same(inner, At(map, 0, 0));
        Assert.Same(outer, At(map, 200, 0));
        var walls = Lines(map).Where(l => l.Front.Sector == inner).ToList();
        Assert.Equal(4, walls.Count);
        Assert.All(walls, l => Assert.Same(outer, l.Back?.Sector));
    }

    [Fact]
    public void ARoomOverlappingAnotherMakesAnAreaOnEachSideOfItsWall()
    {
        var (map, made) = DrawAndCheck(Box(128, -64, 384, 64));
        Assert.Equal(2, made.Count);
        var inside = At(map, 192, 0);
        var outside = At(map, 320, 0);
        Assert.Contains(inside, made);
        Assert.Contains(outside, made);
        Assert.NotSame(inside, outside);
        Assert.Same(map.Sectors[0], At(map, 0, 0));
        Assert.Equal(map.Sectors[0].CeilingHeight, inside!.CeilingHeight);
        Assert.Equal(3, map.Sectors.Count(s => s.OnMap));

        // The starter room's east wall, split twice, keeps its texture running on as if it were one wall.
        var east = Lines(map).Where(l => l.Start.X == 256 && l.End.X == 256).OrderByDescending(l => l.Start.Y).ToList();
        Assert.Equal([0.0, 192.0, 320.0], east.Select(l => l.Front.OffsetX));
        Assert.Same(outside, east[1].Back?.Sector);
        Assert.Same(inside, east[1].Front.Sector);
    }

    [Fact]
    public void ARoomAroundAnotherLeavesItAsItWasInside()
    {
        var (map, made) = DrawAndCheck(Box(-512, -512, 512, 512));
        var around = Assert.Single(made);
        var starter = map.Sectors[0];
        Assert.Same(starter, At(map, 0, 0));
        Assert.Same(around, At(map, 400, 0));
        Assert.Equal(192, starter.Light);
        Assert.All(Lines(map).Where(l => l.Front.Sector == starter), l => Assert.Same(around, l.Back?.Sector));
    }

    /// <summary>The starter room with a solid pillar in the middle: walls from -32 to 32 facing out into the room, nothing inside.</summary>
    private static (EditorMap Map, History History) WithPillar()
    {
        var m = StarterRoom.Create("MAP01", "doom");
        var first = m.Vertices.Count;
        m.Vertices.AddRange([new MapVertex { X = -32, Y = -32 }, new MapVertex { X = 32, Y = -32 }, new MapVertex { X = 32, Y = 32 }, new MapVertex { X = -32, Y = 32 }]);
        for (var i = 0; i < 4; i++)
        {
            // Counter-clockwise round the pillar, so the room is on each wall's right.
            m.Sides.Add(new MapSide { Sector = 0, Middle = "PILLAR" });
            var line = new MapLine { V1 = first + i, V2 = first + (i + 1) % 4, Front = m.Sides.Count - 1 };
            line.SetFlag("blocking", true);
            m.Lines.Add(line);
        }

        var map = EditorMap.FromModel(m);
        return (map, new History(map));
    }

    [Fact]
    public void ARoomDrawnAroundAPillaredRoomLeavesThePillarSolid()
    {
        var (map, history) = WithPillar();
        AssertSound(map);
        var around = Assert.Single(Draw(map, history, Box(-512, -512, 512, 512)));
        AssertSound(map);
        Assert.Null(At(map, 0, 0));
        Assert.Same(map.Sectors[0], At(map, 100, 0));
        Assert.Same(around, At(map, 400, 0));
        Assert.All(Lines(map).Where(l => l.Front.Middle == "PILLAR"), l => Assert.Null(l.Back));
    }

    [Fact]
    public void DrawingOverAnEmptyLoopOfWallsFillsIt()
    {
        var (map, history) = WithPillar();
        var filled = Assert.Single(Draw(map, history, Box(-32, -32, 32, 32)));
        AssertSound(map);
        Assert.Same(filled, At(map, 0, 0));
        Assert.Equal(8, Lines(map).Count);
        Assert.All(Lines(map).Where(l => l.Back is not null), l => Assert.Same(filled, l.Back!.Sector));
        Assert.Equal(4, Lines(map).Count(l => l.Back is not null));
    }

    [Fact]
    public void DrawingOverABrokenAreaMakesItOneWholeArea()
    {
        // A square whose north wall belongs to a second area: neither area is closed.
        var m = StarterRoom.Create("MAP01", "doom");
        m.Sectors.Add(new MapSector { FloorHeight = 0, CeilingHeight = 64, FloorTexture = "F", CeilingTexture = "C", Light = 100 });
        var north = m.Lines.Single(l => m.Vertices[l.V1].Y == 256 && m.Vertices[l.V2].Y == 256);
        m.Sides[north.Front].Sector = 1;
        var map = EditorMap.FromModel(m);
        var history = new History(map);
        var whole = Assert.Single(Draw(map, history, Box(-256, -256, 256, 256)));
        AssertSound(map);
        Assert.Single(map.Sectors, s => s.OnMap);
        Assert.Same(whole, At(map, 0, 0));
    }

    [Fact]
    public void DrawingOverASquareOfWallsWithTheRoomOnBothSidesMakesItARoom()
    {
        // A trick of old maps: walls with the same area on both sides (here a square of them in the starter room).
        var m = StarterRoom.Create("MAP01", "doom");
        var first = m.Vertices.Count;
        m.Vertices.AddRange([new MapVertex { X = -32, Y = -32 }, new MapVertex { X = -32, Y = 32 }, new MapVertex { X = 32, Y = 32 }, new MapVertex { X = 32, Y = -32 }]);
        for (var i = 0; i < 4; i++)
        {
            m.Sides.Add(new MapSide { Sector = 0 });
            m.Sides.Add(new MapSide { Sector = 0 });
            var line = new MapLine { V1 = first + i, V2 = first + (i + 1) % 4, Front = m.Sides.Count - 2, Back = m.Sides.Count - 1 };
            line.SetFlag("twosided", true);
            m.Lines.Add(line);
        }

        var map = EditorMap.FromModel(m);
        var room = Assert.Single(Draw(map, new History(map), Box(-32, -32, 32, 32)));
        AssertSound(map);
        Assert.Same(room, At(map, 0, 0));
        Assert.Same(map.Sectors[0], At(map, 100, 0));
    }

    [Fact]
    public void DrawingARoomExactlyOverOneChangesNothing()
    {
        var (map, history) = Starter();
        var before = Udmf(map.ToModel());
        Assert.Empty(Draw(map, history, Box(-256, -256, 256, 256)));
        Assert.False(history.CanUndo);
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void EitherWayRoundDrawsTheSameRoom()
    {
        var (a, ha) = Starter();
        var (b, hb) = Starter();
        Draw(a, ha, Box(128, -64, 384, 64));
        Draw(b, hb, [.. Box(128, -64, 384, 64).Reverse()]);
        Assert.Equal(Udmf(a.ToModel()), Udmf(b.ToModel()));
    }

    [Fact]
    public void AShapeCrossingItselfIsRefusedAndTheMapIsLeftAlone()
    {
        var (map, history) = Starter();
        var before = Udmf(map.ToModel());
        Assert.Throws<ArgumentException>(() => Draw(map, history, [new(512, 0), new(768, 256), new(768, 0), new(512, 256)]));
        Assert.False(history.CanUndo);
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    public static TheoryData<string, string> GameMaps() => UdmfTests.GameMaps();

    /// <summary>
    /// On every map of every base game, a small room drawn round the player start: it becomes the area
    /// there, the walls near it neither cross nor leave a corner in the middle of a wall, and undo gives
    /// back the map exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(GameMaps))]
    public void ARoomDrawnRoundEveryPlayerStartIsSoundAndUndoesExactly(string fileName, string game)
    {
        var wad = Core.Containers.WadContainer.Open(GameFiles.Require(fileName));
        var drawn = 0;
        foreach (var lumps in MapLocator.Find(wad))
        {
            var map = EditorMap.FromModel(MapConversion.Read(lumps, game));
            if (map.Things.FirstOrDefault(t => t.Type == 1) is not { } start) continue;
            var history = new History(map);
            var before = Udmf(map.ToModel());
            var (x, y) = (Math.Round(start.X), Math.Round(start.Y));
            var under = At(map, x, y);

            // The outline round the start of the area it stands in (an area may have parts all over the map).
            var loop = under is null ? null : MapGeometry.Build(map.Frame).LoopsAt(under.Slot).FirstOrDefault(l => Encloses(l, new MapPoint(x, y)));
            var made = Draw(map, history, Box(x - 16, y - 16, x + 16, y + 16));
            var now = At(map, x, y);

            // The start is in a new area, or on a pad wholly inside the room, which stays as it was (the room was drawn round it).
            var pad = ReferenceEquals(now, under) && loop is not null && loop.All(p => p.X >= x - 16 && p.X <= x + 16 && p.Y >= y - 16 && p.Y <= y + 16);
            Assert.True(made.Contains(now) || pad, $"{fileName} {lumps.Name}: the start at ({x}, {y}) is in neither a new area nor a pad of its own ({made.Count} made)");

            var near = Lines(map).Where(l => Math.Max(l.Start.X, l.End.X) >= x - 64 && Math.Min(l.Start.X, l.End.X) <= x + 64 && Math.Max(l.Start.Y, l.End.Y) >= y - 64 && Math.Min(l.Start.Y, l.End.Y) <= y + 64).ToList();
            var fresh = made.ToHashSet();
            foreach (var l in near.Where(l => fresh.Contains(l.Front.Sector) || (l.Back is { } b && fresh.Contains(b.Sector))))
            {
                Assert.All(near, o => Assert.False(Cross(l, o), $"{fileName} {lumps.Name}: a drawn wall crosses another"));
                Assert.All(map.Vertices.Where(v => v.OnMap && Math.Abs(v.X - x) <= 64 && Math.Abs(v.Y - y) <= 64 && v != l.Start && v != l.End), v => Assert.False(OnWall(v, l), $"{fileName} {lumps.Name}: a corner sits in a drawn wall"));
            }

            Assert.Equal(Udmf(map.FrameFromScratch().ToModel()), Udmf(map.ToModel()));
            history.Undo();
            Assert.True(before == Udmf(map.ToModel()), $"{fileName} {lumps.Name}: undo does not give back the map exactly");
            drawn++;
        }

        Assert.True(drawn > 0);
    }

    /// <summary>
    /// Drawing works near the shape only, so a room drawn across twelve rooms of the 100,000-wall grid
    /// takes about as long as on a one-room map: measured 0.3 ms warm; the bound (the fastest of three, so a
    /// collection pause does not count) catches one whole-map pass, about 34 ms on this map. The grid
    /// rooms wholly inside a drawn one stay as they were.
    /// </summary>
    [Fact]
    public void DrawingOnAHundredThousandWallMapStaysQuick()
    {
        var map = EditorMap.FromModel(Cli.Commands.StressGenCommand.Build(223));
        var history = new History(map);
        Assert.True(map.Lines.Count > 99_000);
        IReadOnlyList<Sector> made = [];
        double Time(MapGeometry geometry, MapPoint[] box)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            history.Record("Draw a room", e => made = ShapeDrawing.Draw(e, geometry, box, Look));
            return System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var first = Time(MapGeometry.Build(map.Frame), Box(1000, 1000, 1300, 1250));
        Assert.Equal(10, made.Count);
        var warm = Time(MapGeometry.Build(map.Frame), Box(9000, 9000, 9300, 9250));
        Assert.Equal(8, made.Count);
        warm = Math.Min(warm, Time(MapGeometry.Build(map.Frame), Box(13000, 9000, 13300, 9250)));
        warm = Math.Min(warm, Time(MapGeometry.Build(map.Frame), Box(5000, 17000, 5300, 17250)));
        output.WriteLine($"draw on the 100k grid: first {first:0.00} ms (with the first-use compile), then at best {warm:0.00} ms");
        Assert.True(warm < 5, $"drawing took {warm:0.0} ms at best");
    }

    [Fact]
    public void RoomsDrawnOneAfterAnotherStaySound()
    {
        var (map, history) = Starter();
        Draw(map, history, Box(256, -128, 512, 128));
        Draw(map, history, Box(512, -256, 1024, 256));
        Draw(map, history, Box(-128, 128, 128, 640));
        Draw(map, history, Box(600, -64, 900, 64));
        AssertSound(map);
        Assert.Equal(6, map.Sectors.Count(s => s.OnMap));
        while (history.Undo())
        {
            AssertSound(map);
        }

        Assert.Equal(Udmf(EditorMap.FromModel(StarterRoom.Create("MAP01", "doom")).ToModel()), Udmf(map.ToModel()));
    }
}
