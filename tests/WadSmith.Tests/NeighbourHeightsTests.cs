using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Presets over numbers (D27): a room drawn against another takes its heights, so no step appears by accident; one drawn alone takes the Look's.</summary>
public sealed class NeighbourHeightsTests
{
    [Fact]
    public void ARoomDrawnAgainstARaisedRoomTakesItsHeights()
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        var look = Look.For("doom");
        Assert.All(EditScript.Run(map, "room 0 0 256 256\nfloor 128 128 64\nceiling 128 128 256\nroom 256 0 512 256\nroom 1024 0 1280 256", look, "doom"), s => Assert.True(s.Done, s.Said));
        var g = Editor.Geometry.MapGeometry.Build(map.Frame);
        var against = g.SectorAt(new(384, 128))!;
        var alone = g.SectorAt(new(1152, 128))!;
        Assert.Equal((64.0, 256.0), (against.FloorHeight, against.CeilingHeight));
        Assert.Equal((look.FloorHeight, look.CeilingHeight), (alone.FloorHeight, alone.CeilingHeight));
    }
}
