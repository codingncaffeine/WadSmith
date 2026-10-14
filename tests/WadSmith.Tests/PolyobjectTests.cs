using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>
/// Polyobjects (D62): found as GZDoom finds them when a map loads — the walls of a start line or explicit lines, carried by the
/// spot less the anchor — and shown in the 3D view where they stand as the map starts, not in their pen.
/// </summary>
public sealed class PolyobjectTests
{
    /// <summary>
    /// A room 256 square and, east of it, a pen 128 square holding a polyobject 64 × 8 (one-sided walls facing out of it, DOOR3
    /// on them, the start line on its west end), its anchor in its middle and its spot <paramref name="spot"/> in the room;
    /// <paramref name="anchorType"/> and <paramref name="spotType"/> as given.
    /// </summary>
    internal static MapModel RoomWithPolyobject((double X, double Y) spot, int anchorType = 9300, int spotType = 9301, int number = 1)
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        void Sector(double floor, double ceiling) => m.Sectors.Add(new MapSector { FloorHeight = floor, CeilingHeight = ceiling, FloorTexture = "FLOOR4_8", CeilingTexture = "CEIL3_5", Light = 160 });
        int V(double x, double y)
        {
            m.Vertices.Add(new MapVertex { X = x, Y = y });
            return m.Vertices.Count - 1;
        }

        // A loop of one-sided walls through the corners in order; a room's run clockwise so the room is on their right (front).
        List<int> Loop(int sector, string texture, params (double X, double Y)[] corners)
        {
            var first = m.Vertices.Count;
            foreach (var (x, y) in corners) V(x, y);
            var lines = new List<int>();
            for (var i = 0; i < corners.Length; i++)
            {
                m.Sides.Add(new MapSide { Sector = sector, Middle = texture });
                m.Lines.Add(new MapLine { V1 = first + i, V2 = first + (i + 1) % corners.Length, Front = m.Sides.Count - 1 });
                lines.Add(m.Lines.Count - 1);
            }

            return lines;
        }

        Sector(0, 128);
        Loop(0, "STARTAN3", (0, 0), (0, 256), (256, 256), (256, 0));
        Sector(0, 128);
        Loop(1, "STARTAN3", (1000, 0), (1000, 128), (1128, 128), (1128, 0));

        // The polyobject: its walls face out into the pen (counter-clockwise, so the pen is on their right).
        var poly = Loop(1, "DOOR3", (1032, 60), (1096, 60), (1096, 68), (1032, 68));
        m.Lines[poly[3]].Special = 1;
        m.Lines[poly[3]].Args[0] = number;
        m.Things.Add(new MapThing { X = 1064, Y = 64, Type = anchorType, Angle = number });
        m.Things.Add(new MapThing { X = spot.X, Y = spot.Y, Type = spotType, Angle = number });
        return m;
    }

    [Fact]
    public void APolyobjectIsItsStartLinesLoopCarriedFromItsAnchorToItsSpot()
    {
        var map = EditorMap.FromModel(RoomWithPolyobject((128, 128)));
        var poly = Assert.Single(MapGeometry.Build(map.Frame).Polyobjects("doom"));
        Assert.Equal((1, 4, PolySpot.Pushes, 0, 0, -936.0, 64.0), (poly.Number, poly.Lines.Count, poly.Kind, poly.Mirror, poly.Sound, poly.Dx, poly.Dy));
        Assert.Equal([8, 9, 10, 11], poly.Lines.Order());

        // Hexen's own numbers (3000, 3001) place it in Hexen only: in Doom, 3001 is an imp.
        var hexen = EditorMap.FromModel(RoomWithPolyobject((128, 128), 3000, 3001));
        Assert.Single(MapGeometry.Build(hexen.Frame).Polyobjects("hexen"));
        Assert.Empty(MapGeometry.Build(hexen.Frame).Polyobjects("doom"));

        // No anchor, no polyobject: GZDoom leaves it unplaced too.
        var lost = RoomWithPolyobject((128, 128));
        lost.Things.RemoveAt(0);
        Assert.Empty(MapGeometry.Build(EditorMap.FromModel(lost).Frame).Polyobjects("doom"));
    }

    [Fact]
    public void ThePolyobjectStandsInTheRoomInThe3DViewNotInItsPen()
    {
        var map = EditorMap.FromModel(RoomWithPolyobject((128, 128)));
        var scene = MapScene.Build(MapGeometry.Build(map.Frame), _ => (64, 72), family: "doom");
        var door = scene.Batches.Single(b => b.Texture == "DOOR3");

        // Its four faces, each a quad (two triangles) from the room's floor to its ceiling, round (96..160, 124..132).
        Assert.Equal(4 * 6, door.VertexCount);
        var corners = Enumerable.Range(0, door.VertexCount).Select(i => (X: door.Vertices[i * SceneBatch.Stride], Y: door.Vertices[i * SceneBatch.Stride + 1], Z: door.Vertices[i * SceneBatch.Stride + 2])).ToList();
        Assert.All(corners, c => Assert.InRange(c.X, 96, 160));
        Assert.All(corners, c => Assert.InRange(c.Y, 124, 132));
        Assert.Equal([0f, 128f], corners.Select(c => c.Z).Distinct().Order());

        // Without its anchor it stays drawn in the pen, as the map holds it.
        var lost = RoomWithPolyobject((128, 128));
        lost.Things.RemoveAt(0);
        var pen = MapScene.Build(MapGeometry.Build(EditorMap.FromModel(lost).Frame), _ => (64, 72)).Batches.Single(b => b.Texture == "DOOR3");
        Assert.All(Enumerable.Range(0, pen.VertexCount), i => Assert.InRange(pen.Vertices[i * SceneBatch.Stride], 1032, 1096));
    }

    [Fact]
    public void ThePlanDrawsThePolyobjectWhereItStandsAsWellAsInItsPen()
    {
        var map = EditorMap.FromModel(RoomWithPolyobject((128, 128)));
        var snapshot = WadSmith.App.Canvas.CanvasSnapshot.Take(MapGeometry.Build(map.Frame), new WadSmith.App.Canvas.MapLook(null), WadSmith.App.Canvas.MapViewMode.Lines);
        var standing = snapshot.Walls.Whole[(int)WadSmith.App.Canvas.WallStyle.Polyobject]!;
        Assert.Equal((96f, 124f, 160f, 132f), (standing.Bounds.Left, standing.Bounds.Top, standing.Bounds.Right, standing.Bounds.Bottom));
        // The pen and the polyobject's own walls in it are drawn as walls, as the map holds them.
        Assert.Equal(1128f, snapshot.Walls.Whole[(int)WadSmith.App.Canvas.WallStyle.Solid]!.Bounds.Right);
    }

    [Fact]
    public void APenAndAPolyobjectThatIsNoDoorSayWhatTheyAre()
    {
        var map = EditorMap.FromModel(RoomWithPolyobject((128, 128)));
        var g = MapGeometry.Build(map.Frame);
        var pen = map.Sectors[1];
        var said = WadSmith.Editor.App.Explaining.Explain(pen, g, null)!;
        Assert.Equal("Polyobjects' pen", said.Heading);
        var wall = map.Lines[9];
        Assert.Equal(("Polyobject", true), (WadSmith.Editor.App.Explaining.Explain(wall, g, null)!.Heading, WadSmith.Editor.App.Explaining.Explain(wall, g, null)!.Text.StartsWith("Polyobject 1: ", StringComparison.Ordinal)));
        Assert.Equal("Room", WadSmith.Editor.App.Explaining.Explain(map.Sectors[0], g, null)!.Heading);
    }

    [Fact]
    public void APieceInsideAWallIsNotSeen()
    {
        // Its spot at the room's east wall: the half beyond it stands in no area, as a door slid into a wall does.
        var map = EditorMap.FromModel(RoomWithPolyobject((256, 128)));
        var door = MapScene.Build(MapGeometry.Build(map.Frame), _ => (64, 72)).Batches.Single(b => b.Texture == "DOOR3");
        var xs = Enumerable.Range(0, door.VertexCount).Select(i => door.Vertices[i * SceneBatch.Stride]).ToList();
        Assert.True(xs.Max() <= 256, string.Join(", ", xs));
    }

    [Fact]
    public void EveryPolyobjectOfHexenIsFound()
    {
        // Each of Hexen's 156 start spots with an anchor names a polyobject whose walls are found: 12 in its first map.
        var hexen = ResourceContainer.Open(GameFiles.Require("HEXEN.WAD"));
        var (spots, found) = (0, 0);
        var counts = new Dictionary<string, int>();
        foreach (var lumps in MapLocator.Find(hexen))
        {
            var model = MapTranslation.ToZDoom(MapConversion.Read(lumps, "hexen"), null, []);
            var anchors = model.Things.Where(t => t.Type == 3000).Select(t => t.Angle).ToHashSet();
            spots += model.Things.Count(t => t.Type is 3001 or 3002 && anchors.Contains(t.Angle));
            var polys = MapGeometry.Build(EditorMap.FromModel(model).Frame).Polyobjects("hexen");
            Assert.All(polys, p => Assert.True(p.Lines.Count >= 3, $"{lumps.Name} po {p.Number}: {p.Lines.Count} walls"));
            found += polys.Count;
            counts[lumps.Name] = polys.Count;
        }

        Assert.Equal(spots, found);
        Assert.Equal(12, counts["MAP01"]);
    }

    [Fact]
    public void HexensOwnSlidingDoorsAreFoundAsDoorsWithTheHalfTheirActionsName()
    {
        // MAP05's two double sliding doors: each half names the other as its mirror; the faces name 19 and 14.
        var lumps = MapLocator.Find(ResourceContainer.Open(GameFiles.Require("HEXEN.WAD"))).Single(m => m.Name == "MAP05");
        var map = EditorMap.FromModel(MapTranslation.ToZDoom(MapConversion.Read(lumps, "hexen"), null, []));
        var g = MapGeometry.Build(map.Frame);
        var doors = map.Sectors.Where(s => s.OnMap).Select(s => WadSmith.Editor.Smart.PolyDoors.Recognise(g, s, "hexen")).OfType<WadSmith.Editor.Smart.PolyDoor>().DistinctBy(d => d.Number).OrderBy(d => d.Number).ToList();
        Assert.Equal([(14, 15), (19, 20)], doors.Select(d => (d.Halves[0].Number, d.Halves[1].Number)));
        Assert.All(doors, d => Assert.Equal(WadSmith.Editor.Smart.DoorKind.Slides, d.Kind));
    }
}
