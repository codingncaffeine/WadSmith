namespace WadSmith.Core.Graphics;

/// <summary>Truevision TGA pictures (true colour and greyscale, plain or run-length packed): the skins of many Quake-era and MD3 models.</summary>
public static class Tga
{
    /// <summary>Whether <paramref name="data"/> has the shape of a TGA this reader takes; TGA has no signature, so the header is checked field by field.</summary>
    public static bool Looks(ReadOnlySpan<byte> data) =>
        data.Length > 18 && data[1] == 0 && data[2] is 2 or 3 or 10 or 11 && data[16] is 8 or 24 or 32 && (data[12] | (data[13] << 8)) > 0 && (data[14] | (data[15] << 8)) > 0;

    public static RgbaImage Decode(byte[] data, string name = "picture")
    {
        if (!Looks(data)) throw new CorruptDataException($"{name} is not a TGA picture WadSmith can read.");
        int idLength = data[0], type = data[2], width = data[12] | (data[13] << 8), height = data[14] | (data[15] << 8), bits = data[16], descriptor = data[17];
        var mapLength = data[5] | (data[6] << 8);
        var pos = 18 + idLength + (data[1] == 1 ? mapLength * ((data[7] + 7) / 8) : 0);
        var bytes = bits / 8;
        var image = new RgbaImage(width, height);
        var topDown = (descriptor & 0x20) != 0;
        var rle = type >= 9;
        var pixel = new byte[4];
        var count = 0;
        var repeat = false;
        for (var i = 0; i < width * height; i++)
        {
            if (rle && count == 0)
            {
                if (pos >= data.Length) throw new CorruptDataException($"{name} ends early.");
                var header = data[pos++];
                repeat = (header & 0x80) != 0;
                count = (header & 0x7F) + 1;
                if (repeat) Read();
            }

            if (!rle || !repeat) Read();
            if (rle) count--;
            var x = i % width;
            var y = topDown ? i / width : height - 1 - i / width;
            if (bytes == 1) image.Set(x, y, pixel[0], pixel[0], pixel[0]);
            else image.Set(x, y, pixel[2], pixel[1], pixel[0], bytes == 4 ? pixel[3] : (byte)255);
        }

        return image;

        void Read()
        {
            if (pos + bytes > data.Length) throw new CorruptDataException($"{name} ends early.");
            for (var b = 0; b < bytes; b++) pixel[b] = data[pos++];
        }
    }

    /// <summary>A picture in any format a model's skin comes in: TGA here, the rest through <see cref="ImageCodec"/>.</summary>
    public static RgbaImage DecodeAny(byte[] data, string name) => ImageCodec.IsKnownImage(data) ? ImageCodec.Decode(data, name) : Decode(data, name);
}
