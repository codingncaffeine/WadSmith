using System.Diagnostics;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Tests that time the code run alone, after the parallel tests: a test running beside them on a shared core reads several times slower.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}

[Collection(TimingCollection.Name)]
public sealed class ProblemTimingTests(ITestOutputHelper output)
{
    /// <summary>
    /// The checks run after every edit, on a worker. What must never happen is a rule that grows faster than the map: on a
    /// grid four times the walls a linear rule takes about four times as long, a quadratic one sixteen. The ratio is the
    /// band because it holds on any machine (measured 2026-10-12: 215 ms on the 100k grid here, 1,121 ms on a Windows CI
    /// runner); the ceiling only catches a rule gone wildly slow. The two grids are timed turn and turn about, the best
    /// of three each, so a pause on the machine lands on one round, not on one arm (the first run of this test read
    /// 9.8× on a CI runner with the small grid timed beside the other tests). Every wall has a part in a picture the
    /// game lacks, so the rules that list walls list them all.
    /// </summary>
    [Fact]
    public void CheckingGrowsWithTheMapNotFaster()
    {
        static MapGeometry Grid(int n)
        {
            var model = Cli.Commands.StressGenCommand.Build(n);
            foreach (var side in model.Sides) side.Top = "NOSUCH";
            return MapGeometry.Build(EditorMap.FromModel(model).Frame);
        }

        static double Time(MapGeometry geometry)
        {
            var started = Stopwatch.GetTimestamp();
            Problems.Check(geometry, ProblemTests.Freedoom.Value, Look.For("doom"), "doom");
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var (small, big) = (Grid(111), Grid(223));
        Time(small);
        Time(big);
        var (bestSmall, bestBig) = (double.MaxValue, double.MaxValue);
        for (var round = 0; round < 3; round++)
        {
            bestSmall = Math.Min(bestSmall, Time(small));
            bestBig = Math.Min(bestBig, Time(big));
        }

        output.WriteLine($"checks: {bestSmall:0} ms on the 25k-wall grid, {bestBig:0} ms on the 100k one ({bestBig / bestSmall:0.0}×)");
        Assert.True(bestBig < 8 * bestSmall, $"{bestBig:0} ms against {bestSmall:0} ms on a quarter of the walls");
        Assert.True(bestBig < 5000, $"{bestBig:0} ms");
    }
}
