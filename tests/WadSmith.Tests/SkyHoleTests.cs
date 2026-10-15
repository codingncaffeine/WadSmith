using WadSmith.Core.Maps;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// Under the sky, what is solid keeps a ceiling of its own: the engine draws no wall between two skies, so a column, a shut
/// door, a door's frame, a window's lintel or a switch's alcove with the sky as its ceiling shows as a hole of sky. The tools
/// give them the floor's picture there, and the rules every map WadSmith makes keeps include no hole of sky.
/// </summary>
public sealed class SkyHoleTests
{
    private static readonly Editor.Look Outdoors = Editor.Look.For("doom") with { Ceiling = "F_SKY1", CeilingHeight = 256 };

    /// <summary>Yards drawn under the sky, 256 tall.</summary>
    private static (EditorMap Map, History History, List<Sector> Rooms) Drawn(params (double X0, double Y0, double X1, double Y1)[] boxes)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        var history = new History(map);
        var rooms = new List<Sector>();
        foreach (var (x0, y0, x1, y1) in boxes)
        {
            var geometry = MapGeometry.Build(map.Frame);
            history.Record("Draw a room", e => rooms.AddRange(ShapeDrawing.Draw(e, geometry, [new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0)], Outdoors)));
        }

        return (map, history, rooms);
    }

    private static List<string> SkyHoles(EditorMap map) => [.. Invariants.Check(map).Where(f => f.Contains("hole of sky", StringComparison.Ordinal))];

    [Fact]
    public void ADoorAndItsFramesInAnOpenAirPassageHaveACeilingOfTheirOwn()
    {
        // Two yards joined by an open-air passage 128 tall; the door's picture is 72 tall, so it is framed.
        var (map, history, rooms) = Drawn((0, 0, 256, 256), (256, 96, 384, 160), (384, 0, 640, 256));
        history.Record("Lower the passage", e => e.Set(rooms[1], x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, 128.0));
        var wall = map.Lines.Single(l => l.OnMap && l.Start.X == 256 && l.End.X == 256 && l.Back is not null);
        var geometry = MapGeometry.Build(map.Frame);
        (Sector? Door, string? Why) made = (null, null);
        history.Record("Make a door", e => made = Doors.Make(e, geometry, wall, Outdoors, heightOf: _ => 72));
        Assert.Null(made.Why);
        Assert.False(MapScene.IsSky(made.Door!.CeilingTexture));
        var frames = map.Sectors.Where(s => s.OnMap && s.CeilingHeight - s.FloorHeight == 72).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.False(MapScene.IsSky(f.CeilingTexture)));
        Assert.Empty(SkyHoles(map));
    }

    [Fact]
    public void ASwitchsAlcoveInAYardHasACeilingOfItsOwn()
    {
        var (map, history, rooms) = Drawn((0, 0, 256, 256));
        var wall = map.Lines.Single(l => l.OnMap && l.Start.X == 0 && l.End.X == 0);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Place a switch", e => Switches.Place(e, geometry, wall, null, Outdoors, Outdoors.Switch, _ => (64, 128)));
        var alcove = map.Sectors.Single(s => s.OnMap && !ReferenceEquals(s, rooms[0]));
        Assert.Equal(128, alcove.CeilingHeight - alcove.FloorHeight);
        Assert.False(MapScene.IsSky(alcove.CeilingTexture));
    }

    [Fact]
    public void AWindowBetweenTwoYardsKeepsTheWallAboveIt()
    {
        // Through a thick wall: the window's opening between the yards.
        var (map, history, _) = Drawn((0, 0, 128, 128), (144, 0, 272, 128));
        var wall = map.Lines.Single(l => l.OnMap && l.Start.X == 128 && l.End.X == 128);
        var geometry = MapGeometry.Build(map.Frame);
        (Sector? Window, string? Why) made = (null, null);
        history.Record("Make a window", e => made = WallWindows.Make(e, geometry, wall, 32, 96, Outdoors));
        Assert.Null(made.Why);
        Assert.False(MapScene.IsSky(made.Window!.CeilingTexture));

        // In a wall two yards share: the window, and the rest of the thin wall closed at its sill.
        var (shared, sharedHistory, _) = Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var between = shared.Lines.Single(l => l.OnMap && l.Start.X == 128 && l.End.X == 128 && l.Back is not null);
        var sharedGeometry = MapGeometry.Build(shared.Frame);
        sharedHistory.Record("Make a window", e => made = WallWindows.Make(e, sharedGeometry, between, 32, 96, Outdoors));
        Assert.Null(made.Why);
        var pieces = shared.Sectors.Where(s => s.OnMap && s.CeilingHeight < 256).ToList();
        Assert.True(pieces.Count >= 2, $"{pieces.Count} pieces");
        Assert.All(pieces, s => Assert.False(MapScene.IsSky(s.CeilingTexture)));
        Assert.Empty(SkyHoles(shared));
    }

    [Fact]
    public void TheShapeRulesFindAColumnOfSky()
    {
        var (map, history, _) = Drawn((0, 0, 512, 512));
        var column = new List<Sector>();
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Draw a column", e => column.AddRange(ShapeDrawing.Draw(e, geometry, [new(224, 224), new(224, 288), new(288, 288), new(288, 224)], Outdoors)));
        history.Record("Close it", e => e.Set(column[0], x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, column[0].FloorHeight));
        Assert.True(MapScene.IsSky(column[0].CeilingTexture));

        Assert.Contains("(256, 256)", Assert.Single(SkyHoles(map)), StringComparison.Ordinal);
        history.Record("Give it a ceiling", e => e.Set(column[0], x => x.CeilingTexture, (x, v) => x.CeilingTexture = v, column[0].FloorTexture));
        Assert.Empty(SkyHoles(map));

        // A wall closed up at the sky (its floor raised to it) stands up to the sky, as beside a door in an open-air passage.
        var wall = new List<Sector>();
        geometry = MapGeometry.Build(map.Frame);
        history.Record("Draw a wall", e => wall.AddRange(ShapeDrawing.Draw(e, geometry, [new(64, 64), new(64, 96), new(192, 96), new(192, 64)], Outdoors)));
        history.Record("Raise it", e => e.Set(wall[0], x => x.FloorHeight, (x, v) => x.FloorHeight = v, wall[0].CeilingHeight));
        Assert.True(MapScene.IsSky(wall[0].CeilingTexture));
        Assert.Empty(SkyHoles(map));
    }
}
