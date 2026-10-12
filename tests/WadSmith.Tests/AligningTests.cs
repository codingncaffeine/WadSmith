using WadSmith.Core.Maps;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>Lining textures up along a wall: columns continue by each wall's length, rows follow the ceilings.</summary>
public sealed class AligningTests
{
    /// <summary>A room 100 by 60 whose four walls all carry WALL, drawn clockwise as seen from above so each front faces inward.</summary>
    private static MapModel Box()
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        foreach (var (x, y) in new[] { (0, 0), (0, 60), (100, 60), (100, 0) }) m.Vertices.Add(new MapVertex { X = x, Y = y });
        m.Sectors.Add(new MapSector { FloorHeight = 0, CeilingHeight = 128, FloorTexture = "FLOOR", CeilingTexture = "CEIL", Light = 160 });
        for (var i = 0; i < 4; i++)
        {
            m.Sides.Add(new MapSide { Sector = 0, Middle = "WALL" });
            m.Lines.Add(new MapLine { V1 = i, V2 = (i + 1) % 4, Front = i });
        }

        m.Things.Add(new MapThing { X = 50, Y = 30, Type = 1 });
        return m;
    }

    [Fact]
    public void TheColumnsRunOnRoundTheCornersWrappedToTheTexturesWidth()
    {
        var map = EditorMap.FromModel(Box());
        var geometry = MapGeometry.Build(map.Frame);
        var history = new History(map);
        var first = map.Lines[0].Front; // (0,0) → (0,60): 60 long
        var (before, after) = Aligning.Chain(geometry, first, WallPart.Middle);
        Assert.Empty(before);
        Assert.Equal([map.Lines[1].Front, map.Lines[2].Front, map.Lines[3].Front], after);

        var n = 0;
        history.Record("Line the textures up", e => n = Aligning.Align(e, geometry, first, WallPart.Middle, _ => 64));
        Assert.Equal(3, n);
        Assert.Equal([0.0, 60.0, 160 % 64.0, 220 % 64.0], map.Lines.Select(l => l.Front.OffsetX));
        Assert.All(map.Lines, l => Assert.Equal(0, l.Front.OffsetY));
        history.Undo();
        Assert.All(map.Lines, l => Assert.Equal(0, l.Front.OffsetX));

        // Without the width, the offsets just grow; from the third wall, the walk goes on round to the second.
        var third = map.Lines[2].Front;
        third.OffsetX = 10;
        history.Record("Line the textures up", e => Aligning.Align(e, geometry, third, WallPart.Middle, _ => null));
        Assert.Equal([10 + 60 + 100.0, 10 + 60 + 100 + 60.0, 10.0, 10 + 60.0], map.Lines.Select(l => l.Front.OffsetX));
    }

    [Fact]
    public void TheChainStopsWhereTheTextureChanges()
    {
        var model = Box();
        model.Sides[2].Middle = "OTHER";
        var map = EditorMap.FromModel(model);
        var geometry = MapGeometry.Build(map.Frame);
        var (before, after) = Aligning.Chain(geometry, map.Lines[0].Front, WallPart.Middle);
        Assert.Equal([map.Lines[3].Front], before);
        Assert.Equal([map.Lines[1].Front], after);
        Assert.Empty(Aligning.Chain(geometry, map.Lines[2].Front, WallPart.Middle).After);
    }

    /// <summary>Two one-sided walls that hang their texture from ceilings 32 apart: the second's rows are moved up by 32 so they meet.</summary>
    [Fact]
    public void TheRowsFollowTheCeilingsOnOneSidedWalls()
    {
        var map = EditorMap.FromModel(SceneTests.TwoRooms(floor2: 0, ceiling2: 160));
        var geometry = MapGeometry.Build(map.Frame);
        var history = new History(map);
        var south1 = map.Lines.Single(l => l.Start.Y == 0 && l.End.Y == 0 && l.Start.X == 128).Front;
        var south2 = map.Lines.Single(l => l.Start.Y == 0 && l.End.Y == 0 && l.Start.X == 256).Front;
        // The walk goes round the outside of both rooms (the shared wall has no middle texture) and reaches room 2's south wall last.
        var (before, after) = Aligning.Chain(geometry, south1, WallPart.Middle);
        Assert.Empty(before);
        Assert.Equal(5, after.Count);
        Assert.Same(south2, after[^1]);
        history.Record("Line the textures up", e => Aligning.Align(e, geometry, south1, WallPart.Middle, _ => 64));
        Assert.Equal((0.0, -32.0), (south2.OffsetX, south2.OffsetY));
        Assert.All(after.Take(2), s => Assert.Equal(0, s.OffsetY));
    }
}
