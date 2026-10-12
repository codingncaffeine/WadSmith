using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Edit scripts: maps built line by line with the editor's tools, and the Phase 3 proof's ten rooms.</summary>
public sealed class EditScriptTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WadSmith.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository was not found above the tests.");
    }

    private static EditorMap Empty() => EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });

    /// <summary>
    /// The Phase 3 proof: tools/scripts/ten-rooms.txt builds ten rooms (ten areas after the rooms, none overlapping), then
    /// joins them with stairs, a window, four doors (one locked with the red key, its key reachable), a secret, a
    /// teleporter and the exit; every line is done, every shape rule holds, and the Problems panel finds nothing.
    /// GZDoom (2026-10-12): all four doors open from both rooms, the teleporter sends to (−480, 0), the secret counts.
    /// </summary>
    [Fact]
    public void TheTenRoomScriptBuildsASoundMapWithNothingToFix()
    {
        var script = File.ReadAllText(Path.Combine(Repo(), "tools", "scripts", "ten-rooms.txt"));
        var rooms = string.Join('\n', script.Split('\n').TakeWhile(l => !l.StartsWith("start", StringComparison.Ordinal)));
        var map = Empty();
        Assert.All(EditScript.Run(map, rooms, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        Assert.Equal(10, map.Sectors.Count(s => s.OnMap));
        Assert.Empty(Invariants.Check(map));

        map = Empty();
        var steps = EditScript.Run(map, script, Look.For("doom"), "doom");
        Assert.All(steps, s => Assert.True(s.Done, $"line {s.Line} ({s.Text}): {s.Said}"));
        Assert.Empty(Invariants.Check(map));
        var g = MapGeometry.Build(map.Frame);
        Assert.Empty(Problems.Check(g, null, Look.For("doom"), "doom"));
        var areas = map.Sectors.Where(s => s.OnMap).ToList();
        Assert.Equal(4, areas.Count(s => Doors.Recognise(g, s) is not null));
        Assert.Single(areas, s => Doors.Recognise(g, s)?.Settings.Lock == 129);
        Assert.Single(areas, s => WallWindows.Recognise(g, s) is not null);
        Assert.Single(areas, s => Secrets.Recognise(g, s) is not null);
        Assert.Contains(map.Lines, l => l.OnMap && Teleports.IsTeleport(l));
        Assert.Single(map.Lines, l => l.OnMap && Exits.IsExit(l));
        Assert.Subset(areas.Select(s => (int)s.FloorHeight).ToHashSet(), new HashSet<int> { 16, 32, 48, 64 });
    }

    [Fact]
    public void ALineThatCannotBeDoneSaysWhyAndTheRestGoOn()
    {
        var map = Empty();
        var steps = EditScript.Run(map, """
            room 0 0 256 256
            door 2000 2000      # nothing there
            frobnicate 1 2
            room 1 2
            look Nowhere
            lift 128 128        # no lower room beside it
            room 256 0 512 256
            """, Look.For("doom"), "doom");
        Assert.Equal([true, false, false, false, false, false, true], steps.Select(s => s.Done));
        Assert.Contains("no wall near", steps[1].Said);
        Assert.Equal("no command called frobnicate", steps[2].Said);
        Assert.Equal("something is missing after the command", steps[3].Said);
        Assert.Contains("no look called Nowhere", steps[4].Said);
        Assert.Contains("lower floor", steps[5].Said);
        Assert.Equal(2, map.Sectors.Count(s => s.OnMap));
        Assert.Empty(Invariants.Check(map));
    }
}
