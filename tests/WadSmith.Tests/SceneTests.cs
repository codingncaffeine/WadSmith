using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>The map as triangles for the 3D view: wall parts, pegging, sky and floors.</summary>
public sealed class SceneTests
{
    private static MapScene Scene(MapModel m) => MapScene.Build(MapGeometry.Build(EditorMap.FromModel(m).Frame), name => name == "TALL" ? (64, 256) : (64, 128));

    private static IEnumerable<float[]> Corners(SceneBatch b)
    {
        for (var i = 0; i < b.Vertices.Count; i += SceneBatch.Stride) yield return [.. b.Vertices.Skip(i).Take(SceneBatch.Stride)];
    }

    /// <summary>Two rooms side by side, 128 square, sharing the wall at x = 128.</summary>
    private static MapModel TwoRooms(double floor2, double ceiling2, string ceilingFlat2 = "CEIL", string ceilingFlat1 = "CEIL", bool upperUnpegged = false, bool lowerUnpegged = false)
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        foreach (var (x, y) in new[] { (0, 0), (128, 0), (128, 128), (0, 128), (256, 0), (256, 128) }) m.Vertices.Add(new MapVertex { X = x, Y = y });
        m.Sectors.Add(new MapSector { FloorHeight = 0, CeilingHeight = 128, FloorTexture = "FLOOR", CeilingTexture = ceilingFlat1, Light = 160 });
        m.Sectors.Add(new MapSector { FloorHeight = floor2, CeilingHeight = ceiling2, FloorTexture = "FLOOR", CeilingTexture = ceilingFlat2, Light = 200 });
        void Wall(int v1, int v2, int sector)
        {
            m.Sides.Add(new MapSide { Sector = sector, Middle = "WALL" });
            m.Lines.Add(new MapLine { V1 = v1, V2 = v2, Front = m.Sides.Count - 1 });
        }

        // Room 1 walls (area on the right: clockwise), then room 2's.
        Wall(0, 3, 0);
        Wall(3, 2, 0);
        Wall(1, 0, 0);
        Wall(2, 5, 1);
        Wall(5, 4, 1);
        Wall(4, 1, 1);
        m.Sides.Add(new MapSide { Sector = 0, Top = "UPPER", Bottom = "LOWER" });
        m.Sides.Add(new MapSide { Sector = 1, Top = "UPPER", Bottom = "LOWER" });
        var shared = new MapLine { V1 = 2, V2 = 1, Front = m.Sides.Count - 2, Back = m.Sides.Count - 1 };
        if (upperUnpegged) shared.Extra["dontpegtop"] = true;
        if (lowerUnpegged) shared.Extra["dontpegbottom"] = true;
        m.Lines.Add(shared);
        return m;
    }

    [Fact]
    public void TheStarterRoomHasFourWallsAFloorAndACeiling()
    {
        var scene = Scene(StarterRoom.Create("MAP01", "doom"));
        var walls = scene.Batches.Where(b => b.Kind == SurfaceKind.Wall).Sum(b => b.VertexCount);
        var floors = scene.Batches.Where(b => b.Kind == SurfaceKind.Floor).Sum(b => b.VertexCount);
        var ceilings = scene.Batches.Where(b => b.Kind is SurfaceKind.Ceiling or SurfaceKind.Sky).Sum(b => b.VertexCount);
        Assert.Equal(4 * 6, walls);
        Assert.Equal(floors, ceilings);
        Assert.True(floors >= 6);
        Assert.Single(scene.Things);
    }

    [Fact]
    public void AStepUpShowsALowerPartOnlyFromBelow()
    {
        var scene = Scene(TwoRooms(floor2: 24, ceiling2: 128));
        var lower = scene.Batches.Single(b => b.Texture == "LOWER");
        Assert.Equal(6, lower.VertexCount);
        Assert.All(Corners(lower), c => Assert.Equal(160, c[5]));
        Assert.Equal([0f, 24f], Corners(lower).Select(c => c[2]).Distinct().Order());
        Assert.DoesNotContain(scene.Batches, b => b.Texture == "UPPER");
    }

    [Fact]
    public void ALowerCeilingBeyondShowsAnUpperPartHungFromIt()
    {
        var scene = Scene(TwoRooms(floor2: 0, ceiling2: 96));
        var upper = scene.Batches.Single(b => b.Texture == "UPPER");
        Assert.Equal(6, upper.VertexCount);

        // Not unpegged: the texture's bottom sits on the lower ceiling, so v at 96 is the texture's height.
        Assert.All(Corners(upper).Where(c => c[2] == 96), c => Assert.Equal(128, c[4]));
        Assert.All(Corners(upper).Where(c => c[2] == 128), c => Assert.Equal(96, c[4]));

        // Unpegged: the texture's top sits at the higher ceiling.
        var unpegged = Scene(TwoRooms(floor2: 0, ceiling2: 96, upperUnpegged: true)).Batches.Single(b => b.Texture == "UPPER");
        Assert.All(Corners(unpegged).Where(c => c[2] == 128), c => Assert.Equal(0, c[4]));
    }

    [Fact]
    public void NoUpperPartBetweenTwoSkies()
    {
        Assert.Contains(Scene(TwoRooms(0, 96)).Batches, b => b.Texture == "UPPER");
        Assert.DoesNotContain(Scene(TwoRooms(0, 96, "F_SKY1", "F_SKY1")).Batches, b => b.Texture == "UPPER");
        Assert.Contains(Scene(TwoRooms(0, 96, "F_SKY1", "F_SKY1")).Batches, b => b.Kind == SurfaceKind.Sky);
    }

    [Fact]
    public void AWallAnchoredAtTheBottomStartsItsTextureAtTheFloor()
    {
        var m = StarterRoom.Create("MAP01", "doom");
        var line = m.Lines[0];
        line.Extra["dontpegbottom"] = true;
        var side = m.Sides[line.Front];
        var name = side.Middle;
        var sector = m.Sectors[side.Sector];
        var scene = MapScene.Build(MapGeometry.Build(EditorMap.FromModel(m).Frame), _ => (64, 72));
        var corners = Corners(scene.Batches.Single(b => b.Texture == name.ToUpperInvariant())).ToList();

        // That wall's corners at floor height have v = the texture's height (its bottom row) plus the offset.
        Assert.Contains(corners, c => c[2] == sector.FloorHeight && c[4] == 72 + side.OffsetY);
    }
}
