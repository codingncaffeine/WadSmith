using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>What a drop from the shelves does to the map: textures on walls, floors and ceilings; objects placed.</summary>
public sealed class ShelfDropTests
{
    private static (EditorSession Session, TempFolder Temp) Open(MapModel m)
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(m);
        return (EditorSession.Open(project, "MAP01", _ => { }), temp);
    }

    [Fact]
    public void ATextureOnASolidWallBecomesTheWall()
    {
        var (session, temp) = Open(StarterRoom.Create("MAP01", "doom"));
        using var _ = temp;
        using var __ = session;

        // The starter room's south wall runs along y = -256; the drop lands just inside it.
        var south = session.Map.Lines.Single(l => l.Start.Y == -256 && l.End.Y == -256);
        Assert.Null(session.Paint("BRICK1", new MapPoint(0, -254), 4, ceiling: false));
        Assert.Equal("BRICK1", south.Front.Middle);
        Assert.Same(south.Front, Assert.Single(session.Selection.Items));
        Assert.Equal("Put a texture on a wall", session.History.UndoLabel);
        session.History.Undo();
        Assert.Equal("STARTAN3", south.Front.Middle);
    }

    [Fact]
    public void ATextureOnAWallBetweenRoomsGoesOnTheSideItWasDroppedOn()
    {
        var (session, temp) = Open(SceneTests.TwoRooms(32, 96));
        using var _ = temp;
        using var __ = session;
        var shared = session.Map.Lines[^1];

        // The shared wall is at x = 128, the front facing the west room.
        Assert.Null(session.Paint("METAL", new MapPoint(126, 64), 4, ceiling: false));
        Assert.Equal(("METAL", "METAL"), (shared.Front.Top, shared.Front.Bottom));
        Assert.Equal(("UPPER", "LOWER"), (shared.Back!.Top, shared.Back.Bottom));
        Assert.Null(session.Paint("WOOD1", new MapPoint(130, 64), 4, ceiling: false));
        Assert.Equal(("WOOD1", "WOOD1"), (shared.Back.Top, shared.Back.Bottom));
    }

    [Fact]
    public void ATextureInARoomGoesOnItsFloorOrItsCeiling()
    {
        var (session, temp) = Open(StarterRoom.Create("MAP01", "doom"));
        using var _ = temp;
        using var __ = session;
        var room = session.Map.Sectors[0];
        Assert.Null(session.Paint("NUKAGE1", new MapPoint(0, 0), 4, ceiling: false));
        Assert.Equal("NUKAGE1", room.FloorTexture);
        Assert.Null(session.Paint("F_SKY1", new MapPoint(0, 0), 4, ceiling: true));
        Assert.Equal("F_SKY1", room.CeilingTexture);
        Assert.Equal("CEIL3_5", StarterRoom.Create("MAP01", "doom").Sectors[0].CeilingTexture);
        Assert.Contains("wall or in a room", session.Paint("NUKAGE1", new MapPoint(5000, 5000), 4, ceiling: false));
    }

    [Fact]
    public void AnObjectIsPlacedOnEveryDifficultyAndInEveryModeAndSelected()
    {
        var (session, temp) = Open(StarterRoom.Create("MAP01", "doom"));
        using var _ = temp;
        using var __ = session;
        var imp = session.PlaceThing(3001, new MapPoint(64, 64));
        Assert.True(imp.OnMap);
        Assert.Equal((64.0, 64.0, 3001, 90), (imp.X, imp.Y, imp.Type, imp.Angle));
        Assert.All(new[] { "skill1", "skill2", "skill3", "skill4", "skill5", "single", "coop", "dm" }, f => Assert.True(imp.Flag(f), f));
        Assert.Same(imp, Assert.Single(session.Selection.Items));
        session.History.Undo();
        Assert.False(imp.OnMap);
    }
}
