namespace WadSmith.Core.Graphics;

/// <summary>PCX pictures (ZSoft, version 5): 8-bit with the palette at the end, or 24-bit in three planes, run-length packed — Quake II's skins.</summary>
public static class Pcx
{
    public static bool Looks(ReadOnlySpan<byte> data) => data.Length > 128 && data[0] == 10 && data[2] == 1 && data[3] == 8;

    public static RgbaImage Decode(byte[] data, string name = "picture")
    {
        if (!Looks(data)) throw new CorruptDataException($"{name} is not a PCX picture WadSmith can read.");
        int Short(int at) => data[at] | (data[at + 1] << 8);
        var width = Short(8) - Short(4) + 1;
        var height = Short(10) - Short(6) + 1;
        int planes = data[65], perLine = Short(66);
        if (width <= 0 || height <= 0 || planes is not (1 or 3) || perLine < width) throw new CorruptDataException($"{name} is a PCX of a kind WadSmith does not read.");
        var line = new byte[perLine * planes];
        var image = new RgbaImage(width, height);
        var at = 128;
        var indexes = planes == 1 ? new byte[width * height] : null;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < line.Length;)
            {
                if (at >= data.Length) throw new CorruptDataException($"{name} ends early.");
                var b = data[at++];
                var count = 1;
                if ((b & 0xC0) == 0xC0)
                {
                    count = b & 0x3F;
                    if (at >= data.Length) throw new CorruptDataException($"{name} ends early.");
                    b = data[at++];
                }

                for (var k = 0; k < count && x < line.Length; k++) line[x++] = b;
            }

            for (var x = 0; x < width; x++)
            {
                if (indexes is not null) indexes[y * width + x] = line[x];
                else image.Set(x, y, line[x], line[perLine + x], line[perLine * 2 + x]);
            }
        }

        if (indexes is null) return image;
        if (data.Length < 769 || data[^769] != 12) throw new CorruptDataException($"{name} has no palette.");
        var palette = data.AsSpan(data.Length - 768);
        for (var i = 0; i < indexes.Length; i++)
        {
            var c = indexes[i] * 3;
            image.Set(i % width, i / width, palette[c], palette[c + 1], palette[c + 2]);
        }

        return image;
    }
}
