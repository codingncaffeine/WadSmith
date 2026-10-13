using System.Buffers.Binary;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Maps.Classic;

// The binary records of the Doom and Hexen map formats, exactly as stored. Every field keeps its raw
// value (unsigned where the format is read unsigned by modern engines) so a map read and written
// back is byte-identical. Meaning is given to these values by the conversion to the map model.

public record struct ClassicVertex(short X, short Y);

/// <summary>
/// A Doom-format thing (10 bytes) or a Hexen-format thing (20 bytes). The Hexen-only fields are zero
/// for Doom-format maps.
/// </summary>
public record struct ClassicThing(short X, short Y, short Angle, ushort Type, ushort Flags, short Tid = 0, short Z = 0, byte Special = 0, byte Arg0 = 0, byte Arg1 = 0, byte Arg2 = 0, byte Arg3 = 0, byte Arg4 = 0);

/// <summary>
/// A Doom-format linedef (14 bytes: special and tag) or a Hexen-format linedef (16 bytes: a one-byte
/// special and five arguments). <see cref="NoSide"/> marks a missing side.
/// </summary>
public record struct ClassicLine(ushort V1, ushort V2, ushort Flags, ushort Special, ushort Tag, ushort Front, ushort Back, byte Arg0 = 0, byte Arg1 = 0, byte Arg2 = 0, byte Arg3 = 0, byte Arg4 = 0)
{
    public const ushort NoSide = 0xFFFF;
}

public record struct ClassicSide(short OffsetX, short OffsetY, NameField Upper, NameField Lower, NameField Middle, ushort Sector);

public record struct ClassicSector(short FloorHeight, short CeilingHeight, NameField FloorTexture, NameField CeilingTexture, short Light, ushort Special, ushort Tag);

/// <summary>A Doom- or Hexen-format map: its five editable record lists plus every other lump kept as it was.</summary>
public sealed class ClassicMap
{
    public const int DoomThingSize = 10;
    public const int HexenThingSize = 20;
    public const int DoomLineSize = 14;
    public const int HexenLineSize = 16;
    public const int SideSize = 30;
    public const int VertexSize = 4;
    public const int SectorSize = 26;

    public required string Name { get; init; }

    public required MapFormat Format { get; init; }

    public List<ClassicVertex> Vertices { get; } = [];

    public List<ClassicThing> Things { get; } = [];

    public List<ClassicLine> Lines { get; } = [];

    public List<ClassicSide> Sides { get; } = [];

    public List<ClassicSector> Sectors { get; } = [];

    /// <summary>The lumps this class does not interpret (SEGS, NODES, REJECT, BEHAVIOR …), in their original order and position.</summary>
    public List<(string Name, byte[] Data)> OtherLumps { get; } = [];

    /// <summary>The order the lumps came in, so writing reproduces it.</summary>
    public List<string> LumpOrder { get; } = [];

    public static ClassicMap Read(MapLumps map)
    {
        if (map.Format == MapFormat.Udmf) throw new ArgumentException("A UDMF map is not a classic map.", nameof(map));
        var hexen = map.Format == MapFormat.Hexen;
        var m = new ClassicMap { Name = map.Name, Format = map.Format };

        foreach (var (name, data) in map.Lumps)
        {
            m.LumpOrder.Add(name);
            switch (LumpName.Normalize(name))
            {
                case "THINGS":
                    ReadRecords(data, hexen ? HexenThingSize : DoomThingSize, map.Name, name, r => m.Things.Add(hexen ? ReadHexenThing(r) : ReadDoomThing(r)));
                    break;
                case "LINEDEFS":
                    ReadRecords(data, hexen ? HexenLineSize : DoomLineSize, map.Name, name, r => m.Lines.Add(hexen ? ReadHexenLine(r) : ReadDoomLine(r)));
                    break;
                case "SIDEDEFS":
                    ReadRecords(data, SideSize, map.Name, name, r => m.Sides.Add(new ClassicSide(
                        BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadInt16LittleEndian(r[2..]),
                        NameField.Read(r.Slice(4, 8)), NameField.Read(r.Slice(12, 8)), NameField.Read(r.Slice(20, 8)),
                        BinaryPrimitives.ReadUInt16LittleEndian(r[28..]))));
                    break;
                case "VERTEXES":
                    ReadRecords(data, VertexSize, map.Name, name, r => m.Vertices.Add(new ClassicVertex(BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadInt16LittleEndian(r[2..]))));
                    break;
                case "SECTORS":
                    ReadRecords(data, SectorSize, map.Name, name, r => m.Sectors.Add(new ClassicSector(
                        BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadInt16LittleEndian(r[2..]),
                        NameField.Read(r.Slice(4, 8)), NameField.Read(r.Slice(12, 8)),
                        BinaryPrimitives.ReadInt16LittleEndian(r[20..]), BinaryPrimitives.ReadUInt16LittleEndian(r[22..]), BinaryPrimitives.ReadUInt16LittleEndian(r[24..]))));
                    break;
                default:
                    m.OtherLumps.Add((name, data));
                    break;
            }
        }

        return m;
    }

    /// <summary>
    /// Sets lumps this class does not interpret (a node build's SEGS, NODES, BLOCKMAP …), keeping their place in the lump order
    /// when the map had them and adding them after the others when it did not.
    /// </summary>
    public void SetLumps(IReadOnlyDictionary<string, byte[]> lumps)
    {
        // Each lump in the order keeps its own data, found by name: a map made in memory lists the node lumps in its
        // order before it has them, and pairing by place gave SEGS a Hexen map's BEHAVIOR and BEHAVIOR nothing (2026-10-13).
        var records = new HashSet<string>(["THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SECTORS"], StringComparer.OrdinalIgnoreCase);
        var pool = new List<(string Name, byte[] Data)>(OtherLumps);
        var rebuilt = new List<(string Name, byte[] Data)>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in LumpOrder)
        {
            if (records.Contains(LumpName.Normalize(name))) continue;
            var own = pool.FindIndex(l => LumpName.Comparer.Equals(l.Name, name));
            var existing = own >= 0 ? pool[own] : (name, []);
            if (own >= 0) pool.RemoveAt(own);
            if (lumps.TryGetValue(LumpName.Normalize(name), out var data))
            {
                rebuilt.Add((name, data));
                placed.Add(LumpName.Normalize(name));
            }
            else
            {
                rebuilt.Add(existing);
            }
        }

        // Lumps the order did not name are kept, at the end of it.
        foreach (var left in pool)
        {
            LumpOrder.Add(left.Name);
            rebuilt.Add(left);
        }
        foreach (var (name, data) in lumps)
        {
            if (placed.Contains(name)) continue;
            LumpOrder.Add(name);
            rebuilt.Add((name, data));
        }

        OtherLumps.Clear();
        OtherLumps.AddRange(rebuilt);
    }

    /// <summary>The map's lumps after its name marker, in the original order.</summary>
    public IReadOnlyList<(string Name, byte[] Data)> WriteLumps()
    {
        var hexen = Format == MapFormat.Hexen;
        var result = new List<(string, byte[])>();
        var others = new Queue<(string Name, byte[] Data)>(OtherLumps);
        foreach (var name in LumpOrder)
        {
            switch (LumpName.Normalize(name))
            {
                case "THINGS":
                    result.Add((name, WriteRecords(Things, hexen ? HexenThingSize : DoomThingSize, hexen ? WriteHexenThing : WriteDoomThing)));
                    break;
                case "LINEDEFS":
                    result.Add((name, WriteRecords(Lines, hexen ? HexenLineSize : DoomLineSize, hexen ? WriteHexenLine : WriteDoomLine)));
                    break;
                case "SIDEDEFS":
                    result.Add((name, WriteRecords(Sides, SideSize, static (s, r) =>
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(r, s.OffsetX);
                        BinaryPrimitives.WriteInt16LittleEndian(r[2..], s.OffsetY);
                        s.Upper.Write(r.Slice(4, 8));
                        s.Lower.Write(r.Slice(12, 8));
                        s.Middle.Write(r.Slice(20, 8));
                        BinaryPrimitives.WriteUInt16LittleEndian(r[28..], s.Sector);
                    })));
                    break;
                case "VERTEXES":
                    result.Add((name, WriteRecords(Vertices, VertexSize, static (v, r) =>
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(r, v.X);
                        BinaryPrimitives.WriteInt16LittleEndian(r[2..], v.Y);
                    })));
                    break;
                case "SECTORS":
                    result.Add((name, WriteRecords(Sectors, SectorSize, static (s, r) =>
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(r, s.FloorHeight);
                        BinaryPrimitives.WriteInt16LittleEndian(r[2..], s.CeilingHeight);
                        s.FloorTexture.Write(r.Slice(4, 8));
                        s.CeilingTexture.Write(r.Slice(12, 8));
                        BinaryPrimitives.WriteInt16LittleEndian(r[20..], s.Light);
                        BinaryPrimitives.WriteUInt16LittleEndian(r[22..], s.Special);
                        BinaryPrimitives.WriteUInt16LittleEndian(r[24..], s.Tag);
                    })));
                    break;
                default:
                    result.Add(others.Count > 0 ? others.Dequeue() : (name, []));
                    break;
            }
        }

        return result;
    }

    private delegate void RecordReader(ReadOnlySpan<byte> record);

    private delegate void RecordWriter<T>(T item, Span<byte> record);

    private static void ReadRecords(byte[] data, int size, string map, string lump, RecordReader read)
    {
        if (data.Length % size != 0)
            throw new CorruptDataException($"{map}'s {lump} lump is {data.Length} bytes, which is not a whole number of {size}-byte records.");
        for (var o = 0; o < data.Length; o += size) read(data.AsSpan(o, size));
    }

    private static byte[] WriteRecords<T>(List<T> items, int size, RecordWriter<T> write)
    {
        var data = new byte[items.Count * size];
        for (var i = 0; i < items.Count; i++) write(items[i], data.AsSpan(i * size, size));
        return data;
    }

    private static ClassicThing ReadDoomThing(ReadOnlySpan<byte> r) => new(
        BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadInt16LittleEndian(r[2..]),
        BinaryPrimitives.ReadInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), BinaryPrimitives.ReadUInt16LittleEndian(r[8..]));

    private static ClassicThing ReadHexenThing(ReadOnlySpan<byte> r) => new(
        X: BinaryPrimitives.ReadInt16LittleEndian(r[2..]), Y: BinaryPrimitives.ReadInt16LittleEndian(r[4..]),
        Angle: BinaryPrimitives.ReadInt16LittleEndian(r[8..]), Type: BinaryPrimitives.ReadUInt16LittleEndian(r[10..]), Flags: BinaryPrimitives.ReadUInt16LittleEndian(r[12..]),
        Tid: BinaryPrimitives.ReadInt16LittleEndian(r), Z: BinaryPrimitives.ReadInt16LittleEndian(r[6..]),
        Special: r[14], Arg0: r[15], Arg1: r[16], Arg2: r[17], Arg3: r[18], Arg4: r[19]);

    private static void WriteDoomThing(ClassicThing t, Span<byte> r)
    {
        BinaryPrimitives.WriteInt16LittleEndian(r, t.X);
        BinaryPrimitives.WriteInt16LittleEndian(r[2..], t.Y);
        BinaryPrimitives.WriteInt16LittleEndian(r[4..], t.Angle);
        BinaryPrimitives.WriteUInt16LittleEndian(r[6..], t.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(r[8..], t.Flags);
    }

    private static void WriteHexenThing(ClassicThing t, Span<byte> r)
    {
        BinaryPrimitives.WriteInt16LittleEndian(r, t.Tid);
        BinaryPrimitives.WriteInt16LittleEndian(r[2..], t.X);
        BinaryPrimitives.WriteInt16LittleEndian(r[4..], t.Y);
        BinaryPrimitives.WriteInt16LittleEndian(r[6..], t.Z);
        BinaryPrimitives.WriteInt16LittleEndian(r[8..], t.Angle);
        BinaryPrimitives.WriteUInt16LittleEndian(r[10..], t.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(r[12..], t.Flags);
        r[14] = t.Special;
        r[15] = t.Arg0;
        r[16] = t.Arg1;
        r[17] = t.Arg2;
        r[18] = t.Arg3;
        r[19] = t.Arg4;
    }

    private static ClassicLine ReadDoomLine(ReadOnlySpan<byte> r) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
        BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), BinaryPrimitives.ReadUInt16LittleEndian(r[8..]),
        BinaryPrimitives.ReadUInt16LittleEndian(r[10..]), BinaryPrimitives.ReadUInt16LittleEndian(r[12..]));

    private static ClassicLine ReadHexenLine(ReadOnlySpan<byte> r) => new(
        V1: BinaryPrimitives.ReadUInt16LittleEndian(r), V2: BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
        Flags: BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), Special: r[6], Tag: 0,
        Front: BinaryPrimitives.ReadUInt16LittleEndian(r[12..]), Back: BinaryPrimitives.ReadUInt16LittleEndian(r[14..]),
        Arg0: r[7], Arg1: r[8], Arg2: r[9], Arg3: r[10], Arg4: r[11]);

    private static void WriteDoomLine(ClassicLine l, Span<byte> r)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(r, l.V1);
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], l.V2);
        BinaryPrimitives.WriteUInt16LittleEndian(r[4..], l.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(r[6..], l.Special);
        BinaryPrimitives.WriteUInt16LittleEndian(r[8..], l.Tag);
        BinaryPrimitives.WriteUInt16LittleEndian(r[10..], l.Front);
        BinaryPrimitives.WriteUInt16LittleEndian(r[12..], l.Back);
    }

    private static void WriteHexenLine(ClassicLine l, Span<byte> r)
    {
        if (l.Special > byte.MaxValue) throw new InvalidOperationException($"A Hexen-format line cannot hold action {l.Special}; the format stores one byte.");
        BinaryPrimitives.WriteUInt16LittleEndian(r, l.V1);
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], l.V2);
        BinaryPrimitives.WriteUInt16LittleEndian(r[4..], l.Flags);
        r[6] = (byte)l.Special;
        r[7] = l.Arg0;
        r[8] = l.Arg1;
        r[9] = l.Arg2;
        r[10] = l.Arg3;
        r[11] = l.Arg4;
        BinaryPrimitives.WriteUInt16LittleEndian(r[12..], l.Front);
        BinaryPrimitives.WriteUInt16LittleEndian(r[14..], l.Back);
    }
}
