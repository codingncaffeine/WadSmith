using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// The Progression graph (D29): the walk from the start lists keys and locked doors in the order the player meets them,
/// then the exit; a planted softlock (a key behind its own door) and an exit with no way to it are called out. The
/// games as shipped are the control (ProblemTests: no exit or key out of reach in any of them).
/// </summary>
public sealed class ProgressionTests
{
    private static IReadOnlyList<string> Way(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return [.. Editor.App.MapReporting.Explain(map.Frame, null).Sections.Single(s => s.Heading == "FROM START TO EXIT").Lines.Select(l => l.Text)];
    }

    [Fact]
    public void ExplainThisMapSaysTheWayAndTheSoftlock()
    {
        Assert.Equal(["1. Pick up the red key.", "2. Open the red door.", "3. Pick up the blue key.", "4. Open the blue door.", "5. Reach the exit: the map can be finished."], Way(Rooms + "\nkey red 128 64\nkey blue 384 64"));
        Assert.Equal(
        [
            "The exit can't be reached: the red door stays shut, because its key can't be had first.",
            "The red key can't be reached: every way to it goes through a locked door the player can't open first.",
            "The blue key can't be reached: every way to it goes through a locked door the player can't open first.",
        ], Way(Rooms + "\nkey red 384 64\nkey blue 640 64"));
    }

    private static MapGeometry Built(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return MapGeometry.Build(map.Frame);
    }

    // Three rooms in a row: red door, then blue door, then the exit on the far wall.
    private const string Rooms = "room 0 0 256 256\nroom 256 0 512 256\nroom 512 0 768 256\nstart 64 128\ndoor 256 128\nlock 256 128 red\ndoor 512 128\nlock 512 128 blue\nexit 768 128";

    private static string Said(ProgressionResult r) => string.Join(" → ", r.Steps.Select(s => s.Colour is null ? s.Kind : $"{s.Colour} {s.Kind}"));

    [Fact]
    public void KeysAndDoorsAreListedInTheOrderThePlayerMeetsThem()
    {
        var walk = Progression.Walk(Built(Rooms + "\nkey red 128 64\nkey blue 384 64"));
        Assert.Equal("red key → red door → blue key → blue door → exit", Said(walk));
        Assert.True(walk.ExitReached);
        Assert.Empty(walk.KeysOutOfReach);
    }

    [Fact]
    public void AKeyBehindItsOwnDoorLeavesTheExitOutOfReach()
    {
        var g = Built(Rooms + "\nkey red 384 64\nkey blue 640 64");
        var walk = Progression.Walk(g);
        Assert.Equal("", Said(walk));
        Assert.False(walk.ExitReached);
        Assert.Equal(["red", "blue"], walk.KeysOutOfReach.Select(k => k.Colour));
        Assert.Equal(["red"], walk.LockedOnTheWay.Select(d => d.Colour));
        var problems = Problems.Check(g, null, Look.For("doom"), "doom").Select(p => p.Rule).ToList();
        Assert.Contains("exit-out-of-reach", problems);
        Assert.Contains("key-out-of-reach-red", problems);
        Assert.Contains(Problems.Check(g, null, Look.For("doom"), "doom"), p => p.Sentence == "The exit can't be reached from the player start: every way to it goes through the red door, and the player can't get its key first.");
    }

    [Fact]
    public void AnExitWithNoWayToItIsCalledOutAndOneInReachThroughAFenceIsNot()
    {
        var apart = Built("room 0 0 256 256\nstart 128 128\nroom 512 0 768 256\nexit 768 128");
        Assert.Contains(Problems.Check(apart, null, Look.For("doom"), "doom"), p => p.Sentence == "The exit can't be reached from the player start: no way from the start leads to it.");
        var near = Built("room 0 0 256 256\nstart 128 128\nexit 256 128");
        Assert.True(Progression.Walk(near).ExitReached);
        Assert.DoesNotContain(Problems.Check(near, null, Look.For("doom"), "doom"), p => p.Rule == "exit-out-of-reach");
    }
}
