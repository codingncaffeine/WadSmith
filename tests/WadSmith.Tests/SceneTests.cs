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
    internal static MapModel TwoRooms(double floor2, double ceiling2, string ceilingFlat2 = "CEIL", string ceilingFlat1 = "CEIL", bool upperUnpegged = false, bool lowerUnpegged = false)
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

    [Fact]
    public void ASpriteStandsOnItsFootPointAsDoomOffsetsSay()
    {
        // A 40 by 56 picture whose foot point is 20 pixels from its left edge and 52 below its top.
        var m = StarterRoom.Create("MAP01", "doom");
        var scene = MapScene.Build(MapGeometry.Build(EditorMap.FromModel(m).Frame), _ => (64, 128), type => type == 1 ? new SpriteShape(40, 56, 20, 52, 1, 1) : null);
        var batch = Assert.Single(scene.Sprites);
        Assert.Equal(6, batch.VertexCount);
        var corners = Enumerable.Range(0, 6).Select(i => batch.Vertices.Skip(i * SpriteBatch.Stride).Take(SpriteBatch.Stride).ToArray()).ToList();
        Assert.Equal([-20f, 20f], corners.Select(c => c[3]).Distinct().Order());
        Assert.Equal([-4f, 52f], corners.Select(c => c[4]).Distinct().Order());
        Assert.All(corners, c => Assert.Equal(m.Things[0].X, c[0]));
        Assert.All(corners, c => Assert.Equal(192, c[7]));
    }
}

/// <summary>Walking as the player does in the 3D view's Walk camera.</summary>
public sealed class WalkerTests
{
    /// <summary>Two rooms side by side; the second's floor at <paramref name="step"/>, its ceiling at <paramref name="ceiling"/>; the shared wall optionally impassable.</summary>
    private static MapGeometry Rooms(double step, double ceiling = 128, bool impassable = false)
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        foreach (var (x, y) in new[] { (0, 0), (128, 0), (128, 128), (0, 128), (256, 0), (256, 128) }) m.Vertices.Add(new MapVertex { X = x, Y = y });
        m.Sectors.Add(new MapSector { FloorHeight = 0, CeilingHeight = 128, FloorTexture = "F", CeilingTexture = "C" });
        m.Sectors.Add(new MapSector { FloorHeight = step, CeilingHeight = ceiling, FloorTexture = "F", CeilingTexture = "C" });
        void Wall(int v1, int v2, int sector)
        {
            m.Sides.Add(new MapSide { Sector = sector });
            m.Lines.Add(new MapLine { V1 = v1, V2 = v2, Front = m.Sides.Count - 1 });
        }

        Wall(0, 3, 0);
        Wall(3, 2, 0);
        Wall(1, 0, 0);
        Wall(2, 5, 1);
        Wall(5, 4, 1);
        Wall(4, 1, 1);
        m.Sides.Add(new MapSide { Sector = 0 });
        m.Sides.Add(new MapSide { Sector = 1 });
        var shared = new MapLine { V1 = 2, V2 = 1, Front = m.Sides.Count - 2, Back = m.Sides.Count - 1 };
        if (impassable) shared.Extra["blocking"] = true;
        m.Lines.Add(shared);
        return MapGeometry.Build(EditorMap.FromModel(m).Frame);
    }

    private static readonly MapPoint Here = new(100, 64);
    private static readonly MapPoint There = new(156, 64);

    [Fact]
    public void AStepOfTwentyFourIsClimbedAndOneMoreIsNot()
    {
        Assert.Equal(24 + Walker.EyeHeight, Walker.Step(Rooms(24), Here, 0, There));
        Assert.Null(Walker.Step(Rooms(25), Here, 0, There));
    }

    [Fact]
    public void WallsTheVoidAndLowCeilingsStopTheWalk()
    {
        var open = Rooms(0);
        Assert.Equal(Walker.EyeHeight, Walker.Step(open, Here, 0, There));
        Assert.Null(Walker.Step(open, Here, 0, new MapPoint(-20, 64)));
        Assert.Null(Walker.Step(Rooms(0, impassable: true), Here, 0, There));
        Assert.Null(Walker.Step(Rooms(0, ceiling: 55), Here, 0, There));
        Assert.NotNull(Walker.Step(Rooms(0, ceiling: 56), Here, 0, There));
    }

    [Fact]
    public void ALowerFloorIsDroppedToAtOnce()
    {
        Assert.Equal(-64 + Walker.EyeHeight, Walker.Step(Rooms(-64), Here, 0, There));
    }
}

/// <summary>Clicking in the 3D view: what a ray from the camera meets first.</summary>
public sealed class PickTests
{
    private static (MapScene Scene, EditorMap Map) Room()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var scene = MapScene.Build(MapGeometry.Build(map.Frame), _ => (64, 128), type => type == 1 ? new SpriteShape(40, 56, 20, 52, 1, 1) : null);
        return (scene, map);
    }

    [Fact]
    public void LookingDownPicksTheRoomAndAcrossPicksTheWall()
    {
        var (scene, map) = Room();
        Assert.Same(map.Sectors[0], scene.Pick((0, 100, 64), (0, 0, -1), 90));
        Assert.Same(map.Sectors[0], scene.Pick((0, 100, 64), (0, 0, 1), 90));

        // Facing north from the middle: the north wall's face (y = 256).
        var face = Assert.IsType<Side>(scene.Pick((0, 0, 64), (0, 1, 0), 90));
        Assert.Equal(256, face.Line!.Start.Y);
        Assert.Equal(256, face.Line.End.Y);
        Assert.Same(face, face.Line.Front);
    }

    [Fact]
    public void ASpriteInTheWayIsPickedBeforeTheWallBehindIt()
    {
        var (scene, map) = Room();
        var start = map.Things[0];

        // From north of the player start, looking south through it: the sprite comes first.
        Assert.Same(start, scene.Pick((start.X, start.Y + 100, 30), (0, -1, 0), -90));
    }

    [Fact]
    public void AWallSeenFromBehindIsNotPicked()
    {
        var (scene, _) = Room();

        // From outside the room, looking in through its south wall: the far (north) wall's front is hit, not the near wall's back.
        var face = Assert.IsType<Side>(scene.Pick((0, -400, 64), (0, 1, 0), 90));
        Assert.Equal(256, face.Line!.Start.Y);
    }

    [Fact]
    public void TheSelectionsTrianglesAreGatheredForTinting()
    {
        var (scene, map) = Room();
        var (surfaces, sprites) = scene.Highlight(new HashSet<Element> { map.Lines[0], map.Things[0] });
        Assert.Equal(6 * SceneBatch.Stride, surfaces.Length);
        Assert.Equal(6 * SpriteBatch.Stride, Assert.Single(sprites).Value.Length);
    }
}
