using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Copy and paste: rooms with their walls and things, joined to the map where they land.</summary>
public sealed class CopyingTests
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static (IReadOnlyList<Element> Pasted, int Crossings) Paste(EditorMap map, History history, MapClip clip, double x, double y)
    {
        (IReadOnlyList<Element>, int) result = ([], 0);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Paste", e => result = Copying.Paste(e, geometry, clip, new MapPoint(x, y), Look.For("doom")));
        return result;
    }

    private static List<Line> WallList(EditorMap map) => [.. map.Lines.Where(l => l.OnMap)];

    [Fact]
    public void ARoomPastedApartIsTheSameRoomWithItsThings()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var before = Udmf(map.ToModel());
        var imp = new Thing { X = 64, Y = 64, Type = 3001 };
        history.Record("Add", e => e.Add(imp));
        before = Udmf(map.ToModel());
        var clip = Copying.Copy(MapGeometry.Build(map.Frame), [map.Sectors[0]]);

        // The imp comes along; the player start stays (a map has one).
        Assert.Equal("a room and a thing", clip.Describe());
        Assert.Equal((512.0, 512.0), clip.Size);

        var (pasted, crossings) = Paste(map, history, clip, 2048, 0);
        Assert.Equal(0, crossings);
        DrawingTests.AssertSound(map);
        var room = Assert.Single(pasted.OfType<Sector>());
        Assert.Equal((map.Sectors[0].FloorTexture, map.Sectors[0].CeilingHeight, map.Sectors[0].Light), (room.FloorTexture, room.CeilingHeight, room.Light));
        Assert.Same(room, MapGeometry.Build(map.Frame).SectorAt(new MapPoint(2048, 0)));
        var copied = Assert.Single(pasted.OfType<Thing>());
        Assert.Equal((3001, 2048.0 + 64, 64.0), (copied.Type, copied.X, copied.Y));
        Assert.Equal(8, WallList(map).Count);

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void ARoomPastedFlushBesideItselfSharesTheWall()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var clip = Copying.Copy(MapGeometry.Build(map.Frame), [map.Sectors[0]]);
        Paste(map, history, clip, 512, 0);
        DrawingTests.AssertSound(map);
        Assert.Equal(7, WallList(map).Count);
        Assert.Single(WallList(map), l => l.Back is not null);
    }

    [Fact]
    public void AWallSharedWithARoomLeftBehindIsTheCopysSolidWall()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var clip = Copying.Copy(MapGeometry.Build(map.Frame), [rooms[1]]);
        Assert.Equal("a room", clip.Describe());
        var (pasted, _) = Paste(map, history, clip, 1000, 1000);
        DrawingTests.AssertSound(map);
        var copy = Assert.Single(pasted.OfType<Sector>());
        var walls = WallList(map).Where(l => l.Front.Sector == copy).ToList();
        Assert.Equal(4, walls.Count);
        Assert.All(walls, l => Assert.True(l.Back is null && l.Flag("blocking") && l.Front.Middle == "STARTAN3", $"{l.Front.Middle} {l.Flag("blocking")}"));
    }

    [Fact]
    public void PastingTwiceMakesTwoRoomsOfTheirOwn()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var clip = Copying.Copy(MapGeometry.Build(map.Frame), [map.Sectors[0]]);
        Paste(map, history, clip, 2048, 0);
        Paste(map, history, clip, 4096, 0);
        DrawingTests.AssertSound(map);
        Assert.Equal(3, map.Sectors.Count(s => s.OnMap));
        Assert.Single(map.Things, t => t.OnMap && t.Type == 1);
    }

    [Fact]
    public void ThingsOnTheirOwnAreCopiedAndAThingOutsideACopiedRoomStays()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var imp = new Thing { X = 1000, Y = 1000, Type = 3001 };
        history.Record("Add", e => e.Add(imp));
        Assert.Equal("a room", Copying.Copy(MapGeometry.Build(map.Frame), [map.Sectors[0]]).Describe());
        Assert.Equal("a thing", Copying.Copy(MapGeometry.Build(map.Frame), [map.Things[0]]).Describe());
        var clip = Copying.Copy(MapGeometry.Build(map.Frame), [imp]);
        Assert.Equal("a thing", clip.Describe());
        var (pasted, _) = Paste(map, history, clip, 0, 64);
        Assert.Equal(3001, Assert.IsType<Thing>(Assert.Single(pasted)).Type);
    }
}
