using System.Buffers.Binary;
using System.Text;

namespace WadSmith.Core.Grids;

/// <summary>A Wolfenstein 3D level: its name, its size in tiles (64 × 64), and its first two planes (walls and floors, objects).</summary>
public sealed record WolfLevel(string Name, int Width, int Height, ushort[] Walls, ushort[] Objects);

/// <summary>
/// Wolfenstein 3D's levels (and Spear of Destiny's, and the shareware's) from the user's own copy: <c>MAPHEAD.*</c> (the
/// RLEW tag, then each level's header offset) and <c>GAMEMAPS.*</c> (each level's planes, Carmack-packed then RLEW-packed,
/// as id's TED5 writes them). The editor turns a level into rooms (its GridRooms).
/// </summary>
public static class WolfMaps
{
    /// <summary>The levels of the game whose <c>MAPHEAD.*</c> is <paramref name="mapHead"/> (its <c>GAMEMAPS.*</c> beside it, same extension).</summary>
    public static IReadOnlyList<WolfLevel> Read(string mapHead)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(mapHead)) ?? ".";
        var extension = Path.GetExtension(mapHead);
        var gameMaps = Directory.EnumerateFiles(folder).FirstOrDefault(f => string.Equals(Path.GetFileName(f), "GAMEMAPS" + extension, StringComparison.OrdinalIgnoreCase))
            ?? throw new CorruptDataException($"GAMEMAPS{extension} is not beside {Path.GetFileName(mapHead)}.");
        return Read(File.ReadAllBytes(mapHead), File.ReadAllBytes(gameMaps));
    }

    public static IReadOnlyList<WolfLevel> Read(byte[] head, byte[] maps)
    {
        if (head.Length < 2) throw new CorruptDataException("MAPHEAD is too short.");
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(head);
        var levels = new List<WolfLevel>();
        for (var i = 0; 2 + i * 4 + 4 <= head.Length && i < 100; i++)
        {
            var at = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(2 + i * 4));
            if (at <= 0 || at + 38 > maps.Length) continue;
            int Int(int o) => BinaryPrimitives.ReadInt32LittleEndian(maps.AsSpan(at + o));
            int Short(int o) => BinaryPrimitives.ReadUInt16LittleEndian(maps.AsSpan(at + o));
            int width = Short(18), height = Short(20);
            if (width <= 0 || height <= 0 || width > 256 || height > 256) throw new CorruptDataException($"Level {i + 1} says it is {width} × {height}.");
            var span = maps.AsSpan(at + 22, 16);
            var end = span.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
            var planes = new ushort[2][];
            for (var p = 0; p < 2; p++)
            {
                int start = Int(p * 4), length = Short(12 + p * 2);
                if (start < 0 || start + length > maps.Length) throw new CorruptDataException($"Level {name} ends early.");
                planes[p] = Rlew(Carmack(maps.AsSpan(start, length)), tag, width * height, name);
            }

            levels.Add(new WolfLevel(name, width, height, planes[0], planes[1]));
        }

        return levels;
    }

    /// <summary>Carmack's packing: words, where a high byte of 0xA7 (near) or 0xA8 (far) with a count copies earlier words.</summary>
    public static ushort[] Carmack(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new CorruptDataException("A level's plane is empty.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data) / 2;
        var output = new ushort[length];
        int o = 0, i = 2;
        while (o < length)
        {
            if (i + 2 > data.Length) throw new CorruptDataException("A level's plane ends early.");
            var word = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            i += 2;
            int count = word & 0xFF, kind = word >> 8;
            if (kind is 0xA7 or 0xA8 && count == 0)
            {
                // An escaped word whose high byte only looked like a pointer.
                if (i >= data.Length) throw new CorruptDataException("A level's plane ends early.");
                output[o++] = (ushort)((word & 0xFF00) | data[i++]);
            }
            else if (kind == 0xA7)
            {
                if (i >= data.Length) throw new CorruptDataException("A level's plane ends early.");
                var from = o - data[i++];
                for (var k = 0; k < count && o < length; k++) output[o++] = from + k >= 0 && from + k < o ? output[from + k] : (ushort)0;
            }
            else if (kind == 0xA8)
            {
                if (i + 2 > data.Length) throw new CorruptDataException("A level's plane ends early.");
                var from = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
                i += 2;
                for (var k = 0; k < count && o < length; k++) output[o++] = from + k < o ? output[from + k] : (ushort)0;
            }
            else
            {
                output[o++] = word;
            }
        }

        return output;
    }

    /// <summary>RLEW: the tag word, a count and a value repeat the value; anything else stands for itself. The first word is the size in bytes.</summary>
    public static ushort[] Rlew(ushort[] words, ushort tag, int cells, string name)
    {
        var output = new ushort[cells];
        int o = 0, i = 1;
        while (o < cells && i < words.Length)
        {
            if (words[i] == tag)
            {
                if (i + 2 >= words.Length) throw new CorruptDataException($"Level {name} ends early.");
                int count = words[i + 1], value = words[i + 2];
                for (var k = 0; k < count && o < cells; k++) output[o++] = (ushort)value;
                i += 3;
            }
            else
            {
                output[o++] = words[i++];
            }
        }

        if (o < cells) throw new CorruptDataException($"Level {name} has fewer tiles than its size says.");
        return output;
    }
}
