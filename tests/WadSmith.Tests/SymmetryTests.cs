using System.Collections.Concurrent;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>Symmetry mode: rooms and objects repeated about the middle, one undo step.</summary>
public sealed class SymmetryTests
{
    [Theory]
    [InlineData(MirrorMode.LeftRight, "(-100,50)", "150")]
    [InlineData(MirrorMode.TopBottom, "(100,-50)", "330")]
    [InlineData(MirrorMode.FourWays, "(-100,50) (100,-50) (-100,-50)", "150 330 210")]
    [InlineData(MirrorMode.HalfTurn, "(-100,-50)", "210")]
    [InlineData(MirrorMode.QuarterTurns, "(-50,100) (-100,-50) (50,-100)", "120 210 300")]
    public void ImagesAndFacingsFollowTheMode(MirrorMode mode, string images, string facings)
    {
        var mirror = new Mirror(mode, new MapPoint(0, 0));
        Assert.Equal(images, string.Join(" ", mirror.Images(new MapPoint(100, 50)).Select(p => $"({p.X:0},{p.Y:0})")));
        Assert.Equal(facings, string.Join(" ", mirror.Facings(30)));
    }

    [Fact]
    public void AShapeAlreadySymmetricAboutTheMiddleHasNoCopy()
    {
        var across = new MapPoint[] { new(-64, 0), new(-64, 64), new(64, 64), new(64, 0) };
        Assert.Empty(new Mirror(MirrorMode.LeftRight, new MapPoint(0, 0)).Shapes(across));
        Assert.Single(new Mirror(MirrorMode.FourWays, new MapPoint(0, 0)).Shapes(across));
        Assert.Single(new Mirror(MirrorMode.LeftRight, new MapPoint(0, 0)).Shapes([new(100, 0), new(100, 64), new(164, 64), new(164, 0)]));
    }

    private static (EditorSession Session, ConcurrentQueue<Action> Ui, TempFolder Temp) Open()
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new ConcurrentQueue<Action>();
        return (EditorSession.Open(project, "MAP01", ui.Enqueue), ui, temp);
    }

    private static void PumpUntil(ConcurrentQueue<Action> ui, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            while (ui.TryDequeue(out var a)) a();
            Thread.Sleep(5);
        }

        Assert.True(done());
    }

    [Fact]
    public void ARoomDrawnInSymmetryModeComesWithItsMirrorImageAsOneUndoStep()
    {
        var (session, ui, temp) = Open();
        using var _ = temp;
        using var __ = session;
        session.Mirror = new Mirror(MirrorMode.LeftRight, new MapPoint(0, 0));
        session.DrawRoom([new(256, -64), new(256, 64), new(384, 64), new(384, -64)]);
        PumpUntil(ui, () => session.Map.Sectors.Count(s => s.OnMap) == 3 && session.Geometry.Version == session.Map.Version);

        var g = session.Geometry;
        Assert.NotNull(g.SectorAt(new MapPoint(320, 0)));
        Assert.NotNull(g.SectorAt(new MapPoint(-320, 0)));
        Assert.NotSame(g.SectorAt(new MapPoint(-320, 0)), g.SectorAt(new MapPoint(0, 0)));
        Assert.Equal(2, session.Selection.Count);
        DrawingTests.AssertSound(session.Map);

        // Both copies share the starter room's walls (drawn against it), and one Ctrl+Z takes both back.
        Assert.True(session.History.Undo());
        Assert.Equal(1, session.Map.Sectors.Count(s => s.OnMap));
        DrawingTests.AssertSound(session.Map);
    }

    [Fact]
    public void AnObjectPlacedInSymmetryModeHasItsCopiesFacingAsTheirImage()
    {
        var (session, _, temp) = Open();
        using var __ = temp;
        using var ___ = session;
        session.Mirror = new Mirror(MirrorMode.FourWays, new MapPoint(0, 0));
        session.PlaceThing(3001, new MapPoint(100, 50));
        var imps = session.Map.Things.Where(t => t.OnMap && t.Type == 3001).ToList();
        Assert.Equal(["(-100,-50) 270", "(-100,50) 90", "(100,-50) 270", "(100,50) 90"], imps.Select(t => $"({t.X:0},{t.Y:0}) {t.Angle}").Order());
        session.History.Undo();
        Assert.DoesNotContain(session.Map.Things, t => t.OnMap && t.Type == 3001);

        // On an axis there is no copy on top of itself, nor two copies on one spot.
        session.PlaceThing(3001, new MapPoint(0, 50));
        Assert.Equal(2, session.Map.Things.Count(t => t.OnMap && t.Type == 3001));
    }
}
