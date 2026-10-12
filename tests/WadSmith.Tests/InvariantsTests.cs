using WadSmith.Core.Maps;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The map rules WadSmith keeps: a sound map breaks none, and each fault planted in one is said.</summary>
public sealed class InvariantsTests
{
    /// <summary>A square room 0–128, its walls clockwise so the room is on their right.</summary>
    private static MapModel Square()
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        foreach (var (x, y) in new[] { (0, 0), (0, 128), (128, 128), (128, 0) }) m.Vertices.Add(new MapVertex { X = x, Y = y });
        m.Sectors.Add(new MapSector { FloorHeight = 0, CeilingHeight = 128, FloorTexture = "FLOOR4_8", CeilingTexture = "CEIL3_5", Light = 160 });
        for (var i = 0; i < 4; i++)
        {
            m.Sides.Add(new MapSide { Sector = 0, Middle = "STARTAN3" });
            m.Lines.Add(new MapLine { V1 = i, V2 = (i + 1) % 4, Front = i, Back = -1 });
        }

        return m;
    }

    [Fact]
    public void ASoundRoomBreaksNoRule() => Assert.Empty(Invariants.Check(EditorMap.FromModel(Square())));

    [Fact]
    public void EachFaultIsSaid()
    {
        // A wall turned round: the room is on its left.
        var turned = Square();
        (turned.Lines[0].V1, turned.Lines[0].V2) = (turned.Lines[0].V2, turned.Lines[0].V1);
        Assert.Contains(Invariants.Check(EditorMap.FromModel(turned)), f => f.Contains("on its left"));
        Assert.Contains(Invariants.Check(EditorMap.FromModel(turned)), f => f.Contains("open at"));

        // A wall missing: the room is open.
        var open = Square();
        open.Lines.RemoveAt(3);
        Assert.Contains(Invariants.Check(EditorMap.FromModel(open)), f => f.Contains("open at"));

        // A wall across the room from corner to corner, crossing another one drawn the other way.
        var crossed = Square();
        crossed.Sides.Add(new MapSide { Sector = 0, Middle = "-" });
        crossed.Sides.Add(new MapSide { Sector = 0, Middle = "-" });
        crossed.Sides.Add(new MapSide { Sector = 0, Middle = "-" });
        crossed.Sides.Add(new MapSide { Sector = 0, Middle = "-" });
        crossed.Lines.Add(new MapLine { V1 = 0, V2 = 2, Front = 4, Back = 5 });
        crossed.Lines.Add(new MapLine { V1 = 1, V2 = 3, Front = 6, Back = 7 });
        Assert.Contains(Invariants.Check(EditorMap.FromModel(crossed)), f => f.Contains("cross"));
        Assert.Contains(Invariants.Check(EditorMap.FromModel(crossed)), f => f.Contains("not marked two-sided"));

        // A corner standing in the middle of a wall.
        var corner = Square();
        corner.Vertices.Add(new MapVertex { X = 64, Y = 0 });
        Assert.Contains(Invariants.Check(EditorMap.FromModel(corner)), f => f.Contains("in the middle of a wall"));

        // An area without walls.
        var lonely = Square();
        lonely.Sectors.Add(new MapSector { FloorHeight = 0, CeilingHeight = 128, FloorTexture = "FLOOR4_8", CeilingTexture = "CEIL3_5" });
        Assert.Contains(Invariants.Check(EditorMap.FromModel(lonely)), f => f.Contains("no walls"));
    }
}
