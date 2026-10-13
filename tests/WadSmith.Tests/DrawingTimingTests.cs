using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Drawing timed alone (the Timing collection): beside the parallel tests on a CI runner's two cores it read 0.27 ms, then 7.7 ms at best.</summary>
[Collection(TimingCollection.Name)]
public sealed class DrawingTimingTests(ITestOutputHelper output)
{
    private static readonly Look Look = Look.For("doom");

    private static MapPoint[] Box(double x0, double y0, double x1, double y1) => [new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0)];

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
}
