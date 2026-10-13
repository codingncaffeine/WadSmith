using System.Buffers.Binary;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Classic;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// WadSmith's own node builder (6b.2): every point inside a room walks the BSP, as the engine does, to a subsector of that
/// room; every subsector is convex and of one room; every wall is in the blockmap block its middle lies in.
/// </summary>
public sealed class NodeBuilderTests
{
    private static ClassicMap Classic(MapModel classicModel) => MapConversion.ToClassic(classicModel, MapFormat.Doom);

    /// <summary>The lump of the map with this name.</summary>
    private static byte[] Lump(ClassicMap c, string name) => c.WriteLumps().Single(l => LumpName.Comparer.Equals(l.Name, name)).Data;

    /// <summary>The engine's walk (R_PointInSubsector): from the last node, front when the point is right of the partition.</summary>
    private static int SubsectorAt(byte[] nodes, double x, double y)
    {
        var count = nodes.Length / 28;
        if (count == 0) return 0;
        var n = count - 1;
        while (true)
        {
            var r = nodes.AsSpan(n * 28);
            double nx = BinaryPrimitives.ReadInt16LittleEndian(r), ny = BinaryPrimitives.ReadInt16LittleEndian(r[2..]);
            double dx = BinaryPrimitives.ReadInt16LittleEndian(r[4..]), dy = BinaryPrimitives.ReadInt16LittleEndian(r[6..]);
            var front = (y - ny) * dx < dy * (x - nx);
            var child = BinaryPrimitives.ReadUInt16LittleEndian(r[(front ? 24 : 26)..]);
            if ((child & 0x8000) != 0) return child & 0x7FFF;
            n = child;
        }
    }

    /// <summary>Builds the nodes for a classic map and checks the walk, the subsectors and the blockmap against the map's own shapes.</summary>
    private static (NodeStats Stats, int Points) Check(ClassicMap c, string what)
    {
        var stats = NodeBuilder.Build(c);
        var (segs, ss, nodes, blockmap) = (Lump(c, "SEGS"), Lump(c, "SSECTORS"), Lump(c, "NODES"), Lump(c, "BLOCKMAP"));
        Assert.Equal((stats.Segs * 12, stats.Subsectors * 4, stats.Nodes * 28), (segs.Length, ss.Length, nodes.Length));
        Assert.Equal((c.Sectors.Count * c.Sectors.Count + 7) / 8, Lump(c, "REJECT").Length);

        int SectorOfSeg(int seg)
        {
            var r = segs.AsSpan(seg * 12);
            var line = c.Lines[BinaryPrimitives.ReadUInt16LittleEndian(r[6..])];
            return c.Sides[BinaryPrimitives.ReadInt16LittleEndian(r[8..]) == 0 ? line.Front : line.Back].Sector;
        }

        // Each subsector: of one room, and convex (every seg's ends on or right of every other seg).
        for (var i = 0; i < stats.Subsectors; i++)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(ss.AsSpan(i * 4)), first = BinaryPrimitives.ReadUInt16LittleEndian(ss.AsSpan(i * 4 + 2));
            Assert.True(count > 0, $"{what}: subsector {i} is empty");
            var sector = SectorOfSeg(first);
            for (var k = first; k < first + count; k++) Assert.True(SectorOfSeg(k) == sector, $"{what}: subsector {i} mixes rooms");
        }

        // Every point well inside a room walks to a subsector of that room.
        var model = MapConversion.Read(MapLocator.Find(WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, [(c.Name, ReadOnlyMemory<byte>.Empty), .. c.WriteLumps().Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))]))).Single(), "doom");
        var editor = EditorMap.FromModel(model);
        var geometry = MapGeometry.Build(editor.Frame);
        var (x0, y0, x1, y1) = geometry.Extent();
        var points = 0;
        var stepX = Math.Max(4, (x1 - x0) / 120);
        var stepY = Math.Max(4, (y1 - y0) / 120);
        for (var x = x0 + stepX / 2; x < x1; x += stepX)
        {
            for (var y = y0 + stepY / 2; y < y1; y += stepY)
            {
                var p = new MapPoint(x, y);
                if (geometry.SectorAt(p) is not { } room || geometry.LineNear(p, 2) is not null) continue;
                var sub = SubsectorAt(nodes, x, y);
                var first = BinaryPrimitives.ReadUInt16LittleEndian(ss.AsSpan(sub * 4 + 2));
                Assert.True(SectorOfSeg(first) == room.Slot, $"{what}: ({x:0},{y:0}) is in room {room.Slot}, the nodes say {SectorOfSeg(first)}");
                points++;
            }
        }

        // Every wall is listed in the block its middle lies in.
        int ox = BinaryPrimitives.ReadInt16LittleEndian(blockmap), oy = BinaryPrimitives.ReadInt16LittleEndian(blockmap.AsSpan(2));
        int columns = BinaryPrimitives.ReadUInt16LittleEndian(blockmap.AsSpan(4));
        for (var i = 0; i < c.Lines.Count; i++)
        {
            var (a, b) = (c.Vertices[c.Lines[i].V1], c.Vertices[c.Lines[i].V2]);
            var (mx, my) = ((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            var block = (int)((my - oy) / 128) * columns + (int)((mx - ox) / 128);
            var at = BinaryPrimitives.ReadUInt16LittleEndian(blockmap.AsSpan(8 + block * 2)) * 2;
            var listed = new List<int>();
            for (var o = at + 2; BinaryPrimitives.ReadUInt16LittleEndian(blockmap.AsSpan(o)) != 0xFFFF; o += 2) listed.Add(BinaryPrimitives.ReadUInt16LittleEndian(blockmap.AsSpan(o)));
            Assert.Contains(i, listed);
        }

        return (stats, points);
    }

    [Fact]
    public void ARoomWithACorridorADoorAndAPillarIsPartitionedAsTheEngineWalksIt()
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room 0 0 512 512\nroom 512 224 640 288\nroom 640 0 1024 512\nroom 192 192 320 320\nstart 64 64\ndoor 512 256", Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        var model = map.ToModel();
        model.Namespace = "doom";
        var (stats, points) = Check(Classic(model), "rooms");
        Assert.True(stats.Nodes > 0 && stats.Subsectors == stats.Nodes + 1, $"{stats}");
        Assert.True(points > 1000, $"{points} points");
    }

    [Fact]
    public void ASingleConvexRoomIsOneSubsectorWithNoNodes()
    {
        var model = StarterRoom.Create("MAP01", "doom");
        model.Namespace = "doom";
        var (stats, _) = Check(Classic(model), "one room");
        Assert.Equal((4, 1, 0, 0), (stats.Segs, stats.Subsectors, stats.Nodes, stats.AddedVertices));
    }

    public static TheoryData<string, string> FreedoomMaps() => new() { { "freedoom2.wad", "MAP01" }, { "freedoom2.wad", "MAP02" }, { "freedoom1.wad", "E1M1" } };

    [Theory]
    [MemberData(nameof(FreedoomMaps))]
    public void AGamesOwnMapIsPartitionedAsTheEngineWalksIt(string file, string slot)
    {
        var lumps = MapLocator.Find(WadContainer.Open(GameFiles.Require(file))).Single(m => m.Name == slot);
        var c = ClassicMap.Read(lumps);
        var (stats, points) = Check(c, $"{file} {slot}");
        Assert.True(stats.Subsectors == stats.Nodes + 1 && points > 1000, $"{stats}, {points} points");
    }
}
