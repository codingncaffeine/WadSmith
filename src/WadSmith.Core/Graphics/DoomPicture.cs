using System.Buffers.Binary;

namespace WadSmith.Core.Graphics;

/// <summary>
/// Doom's picture format (patches, sprites, menu graphics): a header with size and offsets, one
/// offset per column, and each column as "posts" of opaque pixels with gaps between them.
/// </summary>
public static class DoomPicture
{
    private const int MaxSize = 4096;

    /// <summary>
    /// Whether the bytes look like a Doom picture, checked the way GZDoom checks before it trusts
    /// one: sane size, every column offset inside the lump, and every post inside the lump.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13) return false;
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (width <= 0 || height <= 0 || width > MaxSize || height > MaxSize) return false;
        var first = 8 + 4 * width;
        if (data.Length < first) return false;
        for (var x = 0; x < width; x++)
        {
            var ofs = BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + 4 * x)..]);
            if (ofs < first || ofs >= data.Length) return false;
            var pos = (int)ofs;
            var guard = 0;
            while (true)
            {
                if (pos >= data.Length) return false;
                if (data[pos] == 0xFF) break;
                if (pos + 1 >= data.Length) return false;
                pos += data[pos + 1] + 4;
                if (++guard > 1024) return false;
            }
        }

        return true;
    }

    /// <summary>Decodes a Doom picture. Tall pictures (DeePsea's relative post tops) are read as GZDoom reads them.</summary>
    public static PalettedImage Decode(ReadOnlySpan<byte> data, string name = "picture")
    {
        if (!IsValid(data)) throw new CorruptDataException($"{name} is not a valid Doom picture.");
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var img = new PalettedImage(width, height)
        {
            OffsetX = BinaryPrimitives.ReadInt16LittleEndian(data[4..]),
            OffsetY = BinaryPrimitives.ReadInt16LittleEndian(data[6..]),
        };

        for (var x = 0; x < width; x++)
        {
            var pos = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + 4 * x)..]);
            var top = -1;
            while (data[pos] != 0xFF)
            {
                int delta = data[pos];
                top = delta <= top ? top + delta : delta;
                int length = data[pos + 1];
                var src = pos + 3;
                for (var i = 0; i < length; i++)
                {
                    var y = top + i;
                    if (y >= height || src + i >= data.Length) break;
                    var o = y * width + x;
                    img.Indices[o] = data[src + i];
                    img.Opaque[o] = true;
                }

                pos += length + 4;
            }
        }

        return img;
    }

    private static void WriteEmptyPost(MemoryStream columns, int delta)
    {
        columns.WriteByte((byte)delta);
        columns.WriteByte(0);
        columns.WriteByte(0);
        columns.WriteByte(0);
    }

    /// <summary>
    /// Encodes a paletted picture. Posts are at most 128 pixels long, as the classic tools write
    /// them; pictures taller than 254 use relative post tops (tall pictures).
    /// </summary>
    public static byte[] Encode(PalettedImage img)
    {
        if (img.Width > MaxSize || img.Height > MaxSize) throw new ArgumentException("A Doom picture can be at most 4096 pixels on a side.", nameof(img));
        using var columns = new MemoryStream();
        var offsets = new int[img.Width];
        var header = 8 + 4 * img.Width;
        for (var x = 0; x < img.Width; x++)
        {
            offsets[x] = header + (int)columns.Length;
            var lastTop = -1;
            var y = 0;
            while (y < img.Height)
            {
                if (!img.Opaque[y * img.Width + x])
                {
                    y++;
                    continue;
                }

                var start = y;
                while (y < img.Height && img.Opaque[y * img.Width + x] && y - start < 128) y++;
                var length = y - start;

                // A post's top is read as absolute when it is below the previous top, and as an
                // offset from the previous top otherwise (tall pictures). Tops past 254 therefore go
                // relative; empty posts climb when the gap is wider than one byte or the previous top.
                int delta;
                if (start <= 254 && start > lastTop)
                {
                    delta = start;
                }
                else
                {
                    if (lastTop < 0)
                    {
                        WriteEmptyPost(columns, 254);
                        lastTop = 254;
                    }

                    while (start - lastTop > Math.Min(254, lastTop))
                    {
                        var step = Math.Min(254, lastTop);
                        WriteEmptyPost(columns, step);
                        lastTop += step;
                    }

                    delta = start - lastTop;
                }

                columns.WriteByte((byte)delta);
                columns.WriteByte((byte)length);
                columns.WriteByte(img.Indices[start * img.Width + x]);
                for (var i = start; i < start + length; i++) columns.WriteByte(img.Indices[i * img.Width + x]);
                columns.WriteByte(img.Indices[(start + length - 1) * img.Width + x]);
                lastTop = start;
            }

            columns.WriteByte(0xFF);
        }

        var output = new byte[header + columns.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)img.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)img.Height);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(4), (short)img.OffsetX);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(6), (short)img.OffsetY);
        for (var x = 0; x < img.Width; x++) BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8 + 4 * x), offsets[x]);
        columns.ToArray().CopyTo(output, header);
        return output;
    }
}

/// <summary>Flats (floor and ceiling pictures) and full-screen raw pictures: plain palette indices, no header.</summary>
public static class RawPicture
{
    /// <summary>
    /// A flat's size from its byte count, as GZDoom decides it: an exact square of 8, 16, 32, 128,
    /// 256, 512 or 1024 is that size; anything else is 64×64 (Heretic's 64×65 flats and its tiny
    /// sky placeholder included).
    /// </summary>
    public static int FlatSize(int byteCount) => byteCount switch
    {
        8 * 8 => 8,
        16 * 16 => 16,
        32 * 32 => 32,
        128 * 128 => 128,
        256 * 256 => 256,
        512 * 512 => 512,
        1024 * 1024 => 1024,
        _ => 64,
    };

    /// <summary>Reads a flat; a lump shorter than its size leaves the rest at palette index 0.</summary>
    public static PalettedImage DecodeFlat(ReadOnlySpan<byte> data)
    {
        var size = FlatSize(data.Length);
        var img = new PalettedImage(size, size);
        data[..Math.Min(data.Length, size * size)].CopyTo(img.Indices);
        Array.Fill(img.Opaque, true);
        return img;
    }

    /// <summary>A 320×200 full-screen picture stored as raw indices (Heretic's and Hexen's title screens).</summary>
    public static PalettedImage DecodeFullscreen(ReadOnlySpan<byte> data)
    {
        if (data.Length != 64000) throw new CorruptDataException("A full-screen raw picture is exactly 64000 bytes.");
        var img = new PalettedImage(320, 200);
        data.CopyTo(img.Indices);
        Array.Fill(img.Opaque, true);
        return img;
    }

    public static byte[] EncodeFlat(PalettedImage img) => img.Indices.ToArray();
}
