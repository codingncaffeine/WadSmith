using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The player's box at a point, gaps too narrow for it and openings too low (D20).</summary>
public sealed class FitTests(ITestOutputHelper output)
{
    /// <summary>Areas drawn one after another as the Room tool draws them, each from its corners.</summary>
    private static (EditorMap Map, History History, List<Sector> Areas) Shapes(params MapPoint[][] shapes)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        var history = new History(map);
        var areas = new List<Sector>();
        foreach (var corners in shapes)
        {
            var geometry = MapGeometry.Build(map.Frame);
            history.Record("Draw", e => areas.AddRange(ShapeDrawing.Draw(e, geometry, corners, Editor.Look.For("doom"))));
        }

        return (map, history, areas);
    }

    /// <summary>A box's corners, turned 45° (and grown by √2) when <paramref name="slanted"/>, so every corner stays whole.</summary>
    private static MapPoint[] Box(double x0, double y0, double x1, double y1, bool slanted = false)
    {
        MapPoint P(double x, double y) => slanted ? new MapPoint(x - y, x + y) : new MapPoint(x, y);
        return [P(x0, y0), P(x0, y1), P(x1, y1), P(x1, y0)];
    }

    /// <summary>Two rooms joined by a corridor <paramref name="width"/> wide.</summary>
    private static MapGeometry Corridor(double width, bool slanted = false)
    {
        var (map, _, _) = Shapes(Box(0, 0, 256, 256, slanted), Box(256, 128 - width / 2, 384, 128 + width / 2, slanted), Box(384, 0, 640, 256, slanted));
        return MapGeometry.Build(map.Frame);
    }

    [Fact]
    public void ACorridorThinnerThanThePlayerIsAGapAndOneJustWideEnoughIsNot()
    {
        var gap = Assert.Single(Fit.Gaps(Corridor(24)));
        Assert.Equal((24.0, 32.0), (gap.Width, gap.Needed));
        Assert.All(gap.Band, p => Assert.InRange(p.X, 255.9, 384.1));
        Assert.Empty(Fit.Gaps(Corridor(32)));
    }

    [Fact]
    public void ASlantedCorridorNeedsMoreThan32BecauseThePlayersBoxIsSquare()
    {
        // 24 × √2 ≈ 33.9 across: wider than the player, but the square box needs 32 × √2 ≈ 45.3 on a diagonal.
        var gap = Assert.Single(Fit.Gaps(Corridor(24, slanted: true)));
        Assert.Equal(24 * Math.Sqrt(2), gap.Width, 6);
        Assert.Equal(32 * Math.Sqrt(2), gap.Needed, 6);
        Assert.Empty(Fit.Gaps(Corridor(34, slanted: true)));
    }

    [Fact]
    public void ADoorwayTooNarrowBetweenTwoRoomsIsAGapAndAWideOneIsNot()
    {
        // Two rooms touching along 24 only: the opening is a doorway with no corridor.
        var (map, _, _) = Shapes(Box(0, 0, 256, 256), Box(256, 232, 512, 488));
        var doorway = Assert.Single(Fit.Gaps(MapGeometry.Build(map.Frame)));
        Assert.Equal((24.0, 32.0), (doorway.Width, doorway.Needed));
        (map, _, _) = Shapes(Box(0, 0, 256, 256), Box(256, 200, 512, 456));
        Assert.Empty(Fit.Gaps(MapGeometry.Build(map.Frame)));
    }

    [Fact]
    public void TwoRoomsWithAThinGapOfNothingBetweenThemHaveNoGap()
    {
        // Walls 16 apart, but void between them: nobody stands there.
        var (map, _, _) = Shapes(Box(0, 0, 256, 256), Box(272, 0, 528, 256));
        Assert.Empty(Fit.Gaps(MapGeometry.Build(map.Frame)));
    }

    [Fact]
    public void ADeadEndSlotIsNoPassage()
    {
        var (map, _, _) = Shapes(Box(0, 0, 256, 256), Box(256, 120, 320, 136));
        Assert.Empty(Fit.Gaps(MapGeometry.Build(map.Frame)));
    }

    [Fact]
    public void APillarTooCloseToAWallLeavesAGap()
    {
        var (map, history, areas) = Shapes(Box(0, 0, 512, 512), Box(20, 200, 84, 264));
        var pillar = areas.Single(a => a.FloorHeight == 0 && MapGeometry.Build(map.Frame).WallBoundsAt(a.Slot) is { MaxX: < 100 });
        Assert.Empty(Fit.Gaps(MapGeometry.Build(map.Frame)));
        history.Record("Raise", e => e.Set(pillar, s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64.0));
        var gap = Assert.Single(Fit.Gaps(MapGeometry.Build(map.Frame)));
        Assert.Equal(20.0, gap.Width);
    }

    [Fact]
    public void AnOpeningUnderThePlayersHeightBetweenTwoRoomsIsLow()
    {
        var (map, history, areas) = Shapes(Box(0, 0, 256, 256), Box(256, 0, 512, 256));
        Assert.Empty(Fit.LowPassages(MapGeometry.Build(map.Frame)));
        history.Record("Heights", e =>
        {
            e.Set(areas[0], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, 56.0);
            e.Set(areas[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 24.0);
        });
        var g = MapGeometry.Build(map.Frame);
        var low = Assert.Single(Fit.LowPassages(g));
        Assert.Equal(32.0, low.Opening);
        Assert.Equal(Fit.Height, g.Frame.Sectors[areas[0].Slot].CeilingHeight - g.Frame.Sectors[areas[0].Slot].FloorHeight);

        // The same opening into an area a door action names: its heights change in play.
        history.Record("Tag", e => e.Set(areas[1], s => s.Tag, (s, v) => s.Tag = v, 5));
        var wall = map.Lines.First(l => l.OnMap && l.Back is not null);
        history.Record("Switch", e =>
        {
            e.Set(wall, l => l.Special, (l, v) => l.Special = v, Editor.Smart.Doors.DoorOpen);
            e.Set(wall, l => l.Args[0], (l, v) => l.Args[0] = v, 5);
        });
        Assert.Empty(Fit.LowPassages(MapGeometry.Build(map.Frame)));
    }

    [Fact]
    public void TheGhostFitsInTheRoomTouchesAWallAtSixteenAndNotInALowRoom()
    {
        var (map, history, areas) = Shapes(Box(0, 0, 256, 256));
        var g = MapGeometry.Build(map.Frame);
        Assert.True(Fit.At(g, new MapPoint(128, 128)).Fits);
        Assert.True(Fit.At(g, new MapPoint(16, 128)).Fits);
        var against = Fit.At(g, new MapPoint(8, 128));
        Assert.False(against.Fits);
        Assert.Contains("wall", against.Why);
        Assert.False(Fit.At(g, new MapPoint(-50, 128)).Inside);

        history.Record("Low", e => e.Set(areas[0], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, 48.0));
        var low = Fit.At(MapGeometry.Build(map.Frame), new MapPoint(128, 128));
        Assert.Equal((true, false, 0.0, 48.0), (low.Inside, low.Fits, low.Floor, low.Ceiling));
        Assert.Contains("48 high", low.Why);
    }

    [Fact]
    public void TheGhostStandsOnTheHighestFloorUnderIt()
    {
        var (map, history, areas) = Shapes(Box(0, 0, 256, 256), Box(256, 0, 512, 256));
        history.Record("Step", e => e.Set(areas[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 16.0));
        var astride = Fit.At(MapGeometry.Build(map.Frame), new MapPoint(250, 128));
        Assert.Equal((true, 16.0, 128.0), (astride.Fits, astride.Floor, astride.Ceiling));

        // A ledge too high to step onto is in the way from below, though not from above.
        history.Record("Ledge", e => e.Set(areas[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 40.0));
        var g = MapGeometry.Build(map.Frame);
        var below = Fit.At(g, new MapPoint(250, 128));
        Assert.False(below.Fits);
        Assert.Contains("ledge 40 up", below.Why);
        Assert.True(Fit.At(g, new MapPoint(262, 128)).Fits);
    }

    /// <summary>What the checks find in the games as shipped, counted (the noise they would make on a real map).</summary>
    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void TheShippedGamesAreCounted(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var (gaps, lows, maps) = (0, 0, 0);
        var kinds = new Dictionary<GapKind, int>();
        var worst = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var lumps in MapLocator.Find(WadContainer.Open(GameFiles.Require(fileName))))
        {
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(MapConversion.Read(lumps, "doom"), xlat, []));
            var g = MapGeometry.Build(map.Frame);
            var (found, low) = (Fit.Gaps(g), Fit.LowPassages(g));
            maps++;
            gaps += found.Count;
            foreach (var x in found) kinds[x.Kind] = kinds.GetValueOrDefault(x.Kind) + 1;
            lows += low.Count;
            worst.Add($"{lumps.Name}: {found.Count} gaps{string.Concat(found.Where(x => x.Kind != GapKind.Pinch).Take(4).Select(x => $" [{x.Width:0.#}/{x.Needed:0.#} {x.Kind.ToString()[0]} at {x.Band[0].X:0},{x.Band[0].Y:0}]"))}, {low.Count} low{string.Concat(low.Take(3).Select(x => $" [{x.Opening:0} wall {x.Wall}]"))}");
        }

        output.WriteLine($"{fileName}: {maps} maps, {gaps} gaps ({string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}"))}), {lows} low openings, {clock.ElapsedMilliseconds} ms");
        foreach (var w in worst) output.WriteLine("  " + w);
    }
}
