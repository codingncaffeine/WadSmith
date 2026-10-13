using WadSmith.Core.Grids;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Grid maps as rooms (Phase 11): Wolfenstein 3D's packed levels read, and tiles turned into rooms, doors and walls.</summary>
public sealed class GridMapTests
{
    private const ushort Tag = 0xABCD;

    /// <summary>
    /// MAPHEAD and GAMEMAPS holding one 5 × 3 level, "Test": a wall row, wall · floor · door (passed east–west) · floor ·
    /// wall, a wall row; a player start facing east on the left floor. The walls are an RLEW run; the objects plane uses a
    /// Carmack near copy.
    /// </summary>
    internal static (byte[] Head, byte[] Maps) Wolf()
    {
        ushort[] walls = [1, 1, 1, 1, 1, 1, 107, 90, 107, 1, 1, 1, 1, 1, 1];
        ushort[] objects = [0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 0, 0, 0, 0, 0];
        // RLEW: size in bytes, then words; the first five walls as one run.
        var rlewWalls = new List<ushort> { 30, Tag, 5, 1 };
        rlewWalls.AddRange(walls[5..]);
        var rlewObjects = new List<ushort> { 30 };
        rlewObjects.AddRange(objects);
        static byte[] Carmack(List<ushort> words, bool copy)
        {
            // Literal words, except (when copy) the last five written as a near copy of the five words ten back (all zero).
            var output = new List<ushort>();
            var literal = copy ? words.Count - 5 : words.Count;
            output.AddRange(words.Take(literal));
            var bytes = new List<byte>();
            void Word(ushort w) { bytes.Add((byte)w); bytes.Add((byte)(w >> 8)); }
            Word((ushort)(words.Count * 2));
            foreach (var w in output) Word(w);
            if (copy) { Word(0xA705); bytes.Add(10); }
            return [.. bytes];
        }


        var planes = new[] { Carmack(rlewWalls, false), Carmack(rlewObjects, true) };
        var maps = new MemoryStream();
        var w = new BinaryWriter(maps);
        w.Write("TED5v1.0"u8);
        var offsets = new List<int>();
        foreach (var p in planes)
        {
            offsets.Add((int)maps.Length);
            w.Write(p);
        }

        var header = (int)maps.Length;
        w.Write(offsets[0]); w.Write(offsets[1]); w.Write(0);
        w.Write((ushort)planes[0].Length); w.Write((ushort)planes[1].Length); w.Write((ushort)0);
        w.Write((ushort)5); w.Write((ushort)3);
        var name = new byte[16];
        "Test"u8.CopyTo(name);
        w.Write(name);
        var head = new MemoryStream();
        var h = new BinaryWriter(head);
        h.Write(Tag);
        h.Write(header);
        for (var i = 1; i < 100; i++) h.Write(0);
        return (head.ToArray(), maps.ToArray());
    }

    [Fact]
    public void AWolfLevelIsUnpacked()
    {
        var (head, maps) = Wolf();
        var level = WolfMaps.Read(head, maps).Single();
        Assert.Equal(("Test", 5, 3), (level.Name, level.Width, level.Height));
        Assert.Equal([1, 1, 1, 1, 1, 1, 107, 90, 107, 1, 1, 1, 1, 1, 1], level.Walls.Select(v => (int)v));
        Assert.Equal(20, level.Objects[6]);
        Assert.All(level.Objects.Where((_, i) => i != 6), v => Assert.Equal(0, v));
    }

    [Fact]
    public void TilesBecomeTwoRoomsJoinedByADoor()
    {
        var (head, maps) = Wolf();
        var grid = GridRooms.FromWolf(WolfMaps.Read(head, maps).Single());
        var look = Look.For("doom");
        var map = GridRooms.Build(grid, "MAP01", look, ["STARTAN3", "BROWN1"]);
        Assert.Equal(3, map.Sectors.Count);
        Assert.Single(map.Sectors, s => s.CeilingHeight == 0);
        Assert.Equal(10, map.Lines.Count);
        var faces = map.Lines.Where(l => l.Special == WadSmith.Editor.Smart.Doors.DoorRaise).ToList();
        Assert.Equal(2, faces.Count);
        Assert.All(faces, l =>
        {
            // The room in front, the door behind, the door's picture above.
            Assert.NotEqual(0, map.Sectors[map.Sides[l.Front].Sector].CeilingHeight);
            Assert.Equal(0, map.Sectors[map.Sides[l.Back].Sector].CeilingHeight);
            Assert.Equal(look.Door, map.Sides[l.Front].Top);
        });
        Assert.Equal(2, map.Lines.Count(l => map.Sides[l.Front].Middle == look.Track && l.Extra.Contains("dontpegbottom")));
        Assert.Equal((1.5 * GridRooms.Cell, -1.5 * GridRooms.Cell, 0), (map.Things.Single().X, map.Things.Single().Y, map.Things.Single().Angle));
        Assert.Empty(Invariants.Check(EditorMap.FromModel(map)));
    }

    [Fact]
    public void TheUsersWolfensteinLevelsAllReadAndTheFirstBuildsCleanly()
    {
        var head = GameFiles.Require("MAPHEAD.WL6");
        var levels = WolfMaps.Read(head);
        Assert.Equal(60, levels.Count);
        Assert.All(levels, l => Assert.Equal((64, 64), (l.Width, l.Height)));
        var grid = GridRooms.FromWolf(levels[0]);
        Assert.Single(grid.Starts);
        var map = GridRooms.Build(grid, "MAP01", Look.For("doom"), ["STARTAN3", "BROWN1", "GRAY1"]);
        Assert.InRange(map.Sectors.Count(s => s.CeilingHeight == 0), 10, 60);
        Assert.Empty(Invariants.Check(EditorMap.FromModel(map)));
    }
}
