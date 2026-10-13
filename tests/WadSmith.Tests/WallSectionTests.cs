using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// A texture dropped on a wall covers the whole straight wall, through the pieces it was cut into (switches keep their
/// pictures); with Shift it covers a section as wide as the picture where it was dropped (the user, 2026-10-12).
/// </summary>
public sealed class WallSectionTests
{
    private static EditorSession Open(TempFolder temp)
    {
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        return EditorSession.Open(project, "MAP01", a => a());
    }

    /// <summary>Waits for the session's shapes to catch up with the map after an edit (they are rebuilt on a worker).</summary>
    private static void CaughtUp(EditorSession s)
    {
        for (var i = 0; i < 400 && s.Geometry.Version != s.Map.Version; i++) Thread.Sleep(5);
        Assert.Equal(s.Map.Version, s.Geometry.Version);
    }

    private static List<Line> West(EditorSession s) => [.. s.Map.Lines.Where(l => l.OnMap && l.Start.X == -256 && l.End.X == -256).OrderBy(l => Math.Min(l.Start.Y, l.End.Y))];

    [Fact]
    public void ATextureCoversTheWholeStraightWallButNotItsSwitch()
    {
        using var temp = new TempFolder();
        using var session = Open(temp);

        // The west wall cut in three by the exit switch (flat, 64 wide, no picture sizes here).
        session.MakeExit(session.Map.Lines[0], false, new(-256, 0));
        CaughtUp(session);
        Assert.Equal(3, West(session).Count);

        session.Paint("BROWN1", new(-250, 200), 8, ceiling: false);
        Assert.Equal(["BROWN1", "SW1EXIT", "BROWN1"], West(session).Select(l => l.Front.Middle));

        // The other walls are another straight wall: untouched. One undo takes the whole wall back.
        Assert.All(session.Map.Lines.Where(l => l.OnMap && l.Start.X != -256), l => Assert.Equal("STARTAN3", l.Front.Middle));
        session.History.Undo();
        Assert.Equal(["STARTAN3", "SW1EXIT", "STARTAN3"], West(session).Select(l => l.Front.Middle));
    }

    [Fact]
    public void WithShiftItCoversASectionAsWideAsThePictureWhereItWasDropped()
    {
        using var temp = new TempFolder();
        using var session = Open(temp);
        var north = session.Map.Lines.Single(l => l.OnMap && l.Start.Y == 256 && l.End.Y == 256);
        session.Paint("COMPTALL", new(100, 250), 8, ceiling: false, section: true);
        var pieces = session.Map.Lines.Where(l => l.OnMap && l.Start.Y == 256 && l.End.Y == 256).OrderBy(l => Math.Min(l.Start.X, l.End.X)).ToList();
        Assert.Equal([(324.0, "STARTAN3"), (64.0, "COMPTALL"), (124.0, "STARTAN3")], pieces.Select(l => (l.Length, l.Front.Middle)));
        Assert.Equal("COMPTALL", Assert.IsType<Side>(session.Selection.Items.Single()).Middle);
        Assert.False(Exits.IsExit(pieces[1]));
    }
}
