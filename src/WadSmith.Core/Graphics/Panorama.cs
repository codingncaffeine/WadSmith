namespace WadSmith.Core.Graphics;

/// <summary>
/// 360° pictures as GZDoom skyboxes (D16, D42): an equirectangular picture (twice as wide as tall; north in the middle,
/// east a quarter to its right, straight up along the top edge) resampled onto the six faces GZDoom draws. Each face pixel's
/// direction comes from GZDoom's own skybox cube (hw_skydome.cpp: the faces' corners, the box turned half round), so the
/// faces line up with the world as the engine shows them.
/// </summary>
public static class Panorama
{
    /// <summary>The faces in GLDEFS order: north, east, south, west, top, bottom.</summary>
    public static readonly IReadOnlyList<string> Faces = ["north", "east", "south", "west", "top", "bottom"];

    /// <summary>The six faces, each <paramref name="size"/> pixels square.</summary>
    public static IReadOnlyList<RgbaImage> ToCube(RgbaImage picture, int size)
    {
        var faces = new List<RgbaImage>(6);
        for (var face = 0; face < 6; face++)
        {
            var image = new RgbaImage(size, size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var (dx, dy, dz) = Direction(face, (x + 0.5) / size, (y + 0.5) / size);
                    var (r, g, b) = Sample(picture, dx, dy, dz);
                    image.Set(x, y, r, g, b);
                }
            }

            faces.Add(image);
        }

        return faces;
    }

    /// <summary>
    /// The world direction (x east, y north, z up) GZDoom shows at (<paramref name="u"/>, <paramref name="v"/>) of a face: the
    /// face's point on its cube in GL's axes (y up), then the cube's half turn, GL z becoming north.
    /// </summary>
    public static (double X, double Y, double Z) Direction(int face, double u, double v)
    {
        double a = 1 - 2 * u, b = 1 - 2 * v;
        var (gx, gy, gz) = face switch
        {
            0 => (a, b, -1.0),
            1 => (-1.0, b, -a),
            2 => (-a, b, 1.0),
            3 => (1.0, b, a),
            4 => (a, 1.0, -b),
            _ => (a, -1.0, -b),
        };
        var (x, y, z) = (-gx, -gz, gy);
        var length = Math.Sqrt(x * x + y * y + z * z);
        return (x / length, y / length, z / length);
    }

    /// <summary>The picture's colour in a direction: bilinear, wrapping round the sides, held at the poles.</summary>
    public static (byte R, byte G, byte B) Sample(RgbaImage picture, double x, double y, double z)
    {
        var u = 0.5 + Math.Atan2(x, y) / (2 * Math.PI);
        var v = 0.5 - Math.Asin(Math.Clamp(z, -1, 1)) / Math.PI;
        var fx = u * picture.Width - 0.5;
        var fy = Math.Clamp(v * picture.Height - 0.5, 0, picture.Height - 1);
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        int Wrap(int i) => ((i % picture.Width) + picture.Width) % picture.Width;
        var y1 = Math.Min(y0 + 1, picture.Height - 1);
        var (p00, p10, p01, p11) = (picture.Get(Wrap(x0), y0), picture.Get(Wrap(x0 + 1), y0), picture.Get(Wrap(x0), y1), picture.Get(Wrap(x0 + 1), y1));
        byte Mix(byte c00, byte c10, byte c01, byte c11) => (byte)Math.Clamp(Math.Round(((c00 * (1 - tx)) + (c10 * tx)) * (1 - ty) + ((c01 * (1 - tx)) + (c11 * tx)) * ty), 0, 255);
        return (Mix(p00.R, p10.R, p01.R, p11.R), Mix(p00.G, p10.G, p01.G, p11.G), Mix(p00.B, p10.B, p01.B, p11.B));
    }
}

/// <summary>
/// Radiance HDR pictures (<c>.hdr</c>, RGBE, flat or run-length scanlines) — the 360° photographs HDRI libraries give —
/// brought to ordinary colours: exposed so the picture's middle grey sits at middle grey, then Reinhard's curve and sRGB.
/// </summary>
public static class RadianceHdr
{
    public static bool Looks(ReadOnlySpan<byte> data) => data.StartsWith("#?RADIANCE"u8) || data.StartsWith("#?RGBE"u8);

    public static RgbaImage Decode(byte[] data, string name = "picture")
    {
        if (!Looks(data)) throw new CorruptDataException($"{name} is not a Radiance HDR picture.");
        var at = 0;
        string Line()
        {
            var start = at;
            while (at < data.Length && data[at] != '\n') at++;
            var text = System.Text.Encoding.ASCII.GetString(data, start, at - start);
            at++;
            return text;
        }

        string line;
        do
        {
            if (at >= data.Length) throw new CorruptDataException($"{name} ends early.");
            line = Line();
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal) && line != "FORMAT=32-bit_rle_rgbe") throw new CorruptDataException($"{name} is in {line[7..]}, which WadSmith does not read.");
        }
        while (line.Length > 0);

        var size = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (size.Length != 4 || size[0] != "-Y" || size[2] != "+X" || !int.TryParse(size[1], out var height) || !int.TryParse(size[3], out var width) || width <= 0 || height <= 0 || (long)width * height > 1 << 28)
        {
            throw new CorruptDataException($"{name} lies in an order WadSmith does not read ({string.Join(' ', size)}).");
        }

        var rgbe = new byte[width * height * 4];
        var scan = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            if (at + 4 > data.Length) throw new CorruptDataException($"{name} ends early.");
            if (width >= 8 && width < 32768 && data[at] == 2 && data[at + 1] == 2 && ((data[at + 2] << 8) | data[at + 3]) == width)
            {
                at += 4;
                for (var c = 0; c < 4; c++)
                {
                    for (var x = 0; x < width;)
                    {
                        if (at >= data.Length) throw new CorruptDataException($"{name} ends early.");
                        int count = data[at++];
                        if (count > 128)
                        {
                            count -= 128;
                            if (at >= data.Length || x + count > width) throw new CorruptDataException($"{name} ends early.");
                            var value = data[at++];
                            for (var k = 0; k < count; k++) scan[(x++) * 4 + c] = value;
                        }
                        else
                        {
                            if (count == 0 || at + count > data.Length || x + count > width) throw new CorruptDataException($"{name} ends early.");
                            for (var k = 0; k < count; k++) scan[(x++) * 4 + c] = data[at++];
                        }
                    }
                }
            }
            else
            {
                if (at + width * 4 > data.Length) throw new CorruptDataException($"{name} ends early.");
                Array.Copy(data, at, scan, 0, width * 4);
                at += width * 4;
            }

            Array.Copy(scan, 0, rgbe, y * width * 4, width * 4);
        }

        // Light per pixel; the median sets the exposure.
        var light = new float[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            var e = rgbe[i * 4 + 3];
            var f = e == 0 ? 0f : MathF.ScaleB(1f, e - 136);
            for (var c = 0; c < 3; c++) light[i * 3 + c] = rgbe[i * 4 + c] * f;
        }

        var luma = new float[width * height];
        for (var i = 0; i < luma.Length; i++) luma[i] = 0.2126f * light[i * 3] + 0.7152f * light[i * 3 + 1] + 0.0722f * light[i * 3 + 2];
        var sorted = luma.Where(l => l > 0).Order().ToArray();
        var middle = sorted.Length == 0 ? 1f : sorted[sorted.Length / 2];
        var exposure = 0.18f / Math.Max(middle, 1e-6f);
        var image = new RgbaImage(width, height);
        for (var i = 0; i < width * height; i++)
        {
            byte Tone(float v)
            {
                var x = v * exposure;
                x /= 1 + x;
                var s = x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - 0.055f;
                return (byte)Math.Clamp((int)MathF.Round(s * 255), 0, 255);
            }

            image.Set(i % width, i / width, Tone(light[i * 3]), Tone(light[i * 3 + 1]), Tone(light[i * 3 + 2]));
        }

        return image;
    }
}
