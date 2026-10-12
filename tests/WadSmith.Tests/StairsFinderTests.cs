using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Stairs recognised in any map: the Stairs tool's, hand-drawn ones, and not a single raised room.</summary>
public sealed class StairsFinderTests(ITestOutputHelper output)
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    /// <summary>A room at 0, stairs made by the Stairs tool up to a room at 64.</summary>
    private static (EditorMap Map, Editor.Editing.History History, IReadOnlyList<Sector> Steps) Flight()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (512, 0, 768, 256));
        history.Record("High", e => e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64.0));
        IReadOnlyList<Sector> steps = [];
        history.Record("Stairs", e => steps = Stairs.Make(e, MapGeometry.Build(map.Frame), new MapPoint(256, 64), new MapPoint(512, 192), Look).Steps);
        return (map, history, steps);
    }

    [Fact]
    public void TheStairsToolsStairsAreStairs()
    {
        var (map, _, steps) = Flight();
        var g = MapGeometry.Build(map.Frame);
        var flight = StairsFinder.Recognise(g, steps[1])!;
        Assert.Equal([16.0, 32.0, 48.0], flight.Heights);
        Assert.Equal((0.0, 64.0, 16.0, 3), (flight.Below, flight.Above, flight.Rise, flight.Count));
        Assert.Equal(new HashSet<Sector>(steps), new HashSet<Sector>(flight.Steps));
        Assert.All(steps, s => Assert.NotNull(StairsFinder.Recognise(g, s)));
        Assert.Null(StairsFinder.Recognise(g, map.Sectors[0]));
    }

    [Fact]
    public void ALedgeTooHighBesideTheTopStepIsNotWhereTheStairsGo()
    {
        var (map, history, steps) = Flight();
        history.Record("Ledge", e =>
        {
            foreach (var ledge in ShapeDrawing.Draw(e, MapGeometry.Build(map.Frame), [new(448, 192), new(448, 256), new(512, 256), new(512, 192)], Look))
            {
                ledge.CeilingHeight = 300;
                ledge.FloorHeight = 200;
            }
        });
        var flight = StairsFinder.Recognise(MapGeometry.Build(map.Frame), steps[0])!;
        Assert.Equal((0.0, 64.0, 16.0), (flight.Below, flight.Above, flight.Rise));
    }

    [Fact]
    public void ChangingTheRiseSetsEveryStepAndEvenOutSpreadsThem()
    {
        var (map, history, steps) = Flight();
        var flight = StairsFinder.Recognise(MapGeometry.Build(map.Frame), steps[0])!;
        history.Record("Rise", e => StairsFinder.SetRise(e, flight, 20));
        Assert.Equal([20.0, 40.0, 60.0], steps.Select(s => s.FloorHeight).Order());
        var set = StairsFinder.Recognise(MapGeometry.Build(map.Frame), steps[0])!;
        Assert.Equal((20.0, false, "climbing from 0 to 64, 20 a step and 4 onto the top"), (set.StepRise, set.Even, set.Describe()));
        Assert.All(steps, s => Assert.True(s.CeilingHeight - s.FloorHeight >= Stairs.Headroom));

        var uneven = StairsFinder.Recognise(MapGeometry.Build(map.Frame), steps[0])!;
        history.Record("Even", e => StairsFinder.SetRise(e, uneven, uneven.Rise));
        Assert.Equal([16.0, 32.0, 48.0], steps.Select(s => s.FloorHeight).Order());
        var even = StairsFinder.Recognise(MapGeometry.Build(map.Frame), steps[0])!;
        Assert.Equal((16.0, true, "climbing from 0 to 64, 16 a step"), (even.StepRise, even.Even, even.Describe()));
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void ARaisedRoomOrAStepTooHighIsNoStairs()
    {
        // The control: drawn rooms at 0, 16, 32 and 48 are a flight of two steps.
        var (control, controlHistory, boxes) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128), (384, 0, 512, 128));
        controlHistory.Record("Heights", e =>
        {
            for (var i = 1; i < 4; i++) e.Set(boxes[i], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 16.0 * i);
        });
        var two = StairsFinder.Recognise(MapGeometry.Build(control.Frame), boxes[2])!;
        Assert.Equal([16.0, 32.0], two.Heights);
        Assert.Equal((0.0, 48.0), (two.Below, two.Above));

        // One raised room between two: one step only.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128));
        history.Record("Heights", e =>
        {
            e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 16.0);
            e.Set(rooms[2], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 32.0);
        });
        Assert.Null(StairsFinder.Recognise(MapGeometry.Build(map.Frame), rooms[1]));

        // Rises of 32: too high to step, so not stairs.
        (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128), (384, 0, 512, 128));
        history.Record("Heights", e =>
        {
            e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 32.0);
            e.Set(rooms[2], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64.0);
            e.Set(rooms[3], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 96.0);
        });
        Assert.Null(StairsFinder.Recognise(MapGeometry.Build(map.Frame), rooms[1]));

        // 0, 16, 32 then 64: the top rise is too high, so the step at 32 leads nowhere and one step is left.
        (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128), (384, 0, 512, 128));
        history.Record("Heights", e =>
        {
            e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 16.0);
            e.Set(rooms[2], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 32.0);
            e.Set(rooms[3], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64.0);
        });
        Assert.Null(StairsFinder.Recognise(MapGeometry.Build(map.Frame), rooms[1]));
        Assert.Null(StairsFinder.Recognise(MapGeometry.Build(map.Frame), rooms[2]));

        // 0 then 32, 48, 64: the bottom rise is too high, so the step at 32 is no step and one is left.
        (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128), (384, 0, 512, 128));
        history.Record("Heights", e =>
        {
            e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 32.0);
            e.Set(rooms[2], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 48.0);
            e.Set(rooms[3], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64.0);
        });
        Assert.Null(StairsFinder.Recognise(MapGeometry.Build(map.Frame), rooms[1]));
        Assert.Null(StairsFinder.Recognise(MapGeometry.Build(map.Frame), rooms[2]));
    }

    [Fact]
    public void TwoFlightsSideBySideStayTwo()
    {
        // Two rows of steps touching along their length, the back row 40 higher: 0, 8, 16, 24 and 40, 48, 56, 64.
        var (map, history, rooms) = MovingTests.Drawn(
            (0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128), (384, 0, 512, 128),
            (0, 128, 128, 256), (128, 128, 256, 256), (256, 128, 384, 256), (384, 128, 512, 256));
        double[] floors = [0, 8, 16, 24, 40, 48, 56, 64];
        history.Record("Heights", e =>
        {
            for (var i = 1; i < floors.Length; i++)
            {
                var k = i;
                e.Set(rooms[k], s => s.FloorHeight, (s, v) => s.FloorHeight = v, floors[k]);
            }
        });
        var g = MapGeometry.Build(map.Frame);
        Assert.Equal([8.0, 16.0], StairsFinder.Recognise(g, rooms[1])!.Heights);
        Assert.Equal([48.0, 56.0], StairsFinder.Recognise(g, rooms[5])!.Heights);
    }

    /// <summary>The flights in the games as shipped, counted (there is no mark in the data to hold them against).</summary>
    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void FlightsInTheOriginalGamesAreCounted(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var (flights, steps) = (0, 0);
        foreach (var lumps in MapLocator.Find(WadContainer.Open(GameFiles.Require(fileName))))
        {
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(MapConversion.Read(lumps, "doom"), xlat, []));
            var g = MapGeometry.Build(map.Frame);
            var seen = new HashSet<Sector>(ReferenceEqualityComparer.Instance);
            foreach (var s in map.Sectors.Where(s => s.OnMap && !seen.Contains(s)))
            {
                if (StairsFinder.Recognise(g, s) is not { } flight) continue;
                flights++;
                steps += flight.Count;
                seen.UnionWith(flight.Steps);
                Assert.True(flight.Heights.Zip(flight.Heights.Skip(1)).All(p => p.Second - p.First <= Stairs.MaxRise), $"{lumps.Name}: a flight steps more than 24");
            }
        }

        output.WriteLine($"{fileName}: {flights} flights, {steps} steps");
        Assert.True(flights > 0);
    }
}
