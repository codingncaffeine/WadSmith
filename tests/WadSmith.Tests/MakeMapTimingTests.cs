using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Generate;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// The page's preview (D50, §3.14 "within a second"): planning a Huge map, building it with the editor's tools and running
/// the checks the page shows, on a worker. Timed alone, the median of five seeds: about 170 ms here, on two CPUs too; the band is the plan's second.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class MakeMapTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void AHugeMapsPreviewIsReadyWithinASecond()
    {
        var times = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var settings = new GeneratorSettings { Seed = $"timing-{i}", Size = MapSize.Huge, Style = MapStyle.Mixed, Keys = 3, Bosses = 2 };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var plan = Planner.Plan(settings);
            var made = MapMaker.Build(plan, new Lent(m => m.Number, null, (_, n) => n));
            var g = MapGeometry.Build(made.Map.Frame);
            _ = Progression.Walk(g, "doom");
            _ = Problems.Check(g, null, Look.For("doom"), "doom");
            if (i > 0) times.Add(clock.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        output.WriteLine($"Huge preview: {string.Join(", ", times.Select(t => $"{t:0} ms"))}; median {median:0} ms");
        Assert.InRange(median, 0, 1000);
    }
}
