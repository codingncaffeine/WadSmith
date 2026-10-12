using System.Buffers.Binary;
using SkiaSharp;

namespace WadSmith.Core.Graphics;

/// <summary>PNG, JPEG, GIF, WebP and BMP through Skia, plus the <c>grAb</c> chunk Doom tools keep offsets in.</summary>
public static class ImageCodec
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(PngSignature);

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static bool IsGif(ReadOnlySpan<byte> data) => data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8);

    public static bool IsWebp(ReadOnlySpan<byte> data) => data.Length > 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    public static bool IsBmp(ReadOnlySpan<byte> data) => data.Length > 26 && data[0] == 'B' && data[1] == 'M';

    /// <summary>Any picture format Skia reads.</summary>
    public static bool IsKnownImage(ReadOnlySpan<byte> data) => IsPng(data) || IsJpeg(data) || IsGif(data) || IsWebp(data) || IsBmp(data);

    /// <summary>Decodes the first frame of an image; a PNG's grAb chunk becomes the offsets.</summary>
    public static RgbaImage Decode(byte[] data, string name = "picture")
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw new CorruptDataException($"{name} is not a picture WadSmith can read.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var img = new RgbaImage(info.Width, info.Height);
        unsafe
        {
            fixed (byte* p = img.Pixels)
            {
                var result = codec.GetPixels(info, (nint)p);
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    throw new CorruptDataException($"{name} could not be decoded ({result}).");
            }
        }

        if (IsPng(data) && ReadGrab(data) is { } grab)
        {
            img.OffsetX = grab.X;
            img.OffsetY = grab.Y;
        }

        return img;
    }

    /// <summary>Encodes as PNG, writing the offsets as a grAb chunk when either is non-zero.</summary>
    public static byte[] EncodePng(RgbaImage img)
    {
        var info = new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        byte[] png;
        unsafe
        {
            fixed (byte* p = img.Pixels)
            {
                using var pixmap = new SKPixmap(info, (nint)p, info.RowBytes);
                using var encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Skia could not encode the picture.");
                png = encoded.ToArray();
            }
        }

        return img.OffsetX != 0 || img.OffsetY != 0 ? WithGrab(png, img.OffsetX, img.OffsetY) : png;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    /// <summary>The CRC-32 PNG chunks carry (over the chunk type and data).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>The offsets from a PNG's grAb chunk, or null when it has none.</summary>
    public static (int X, int Y)? ReadGrab(ReadOnlySpan<byte> png)
    {
        var pos = 8;
        while (pos + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[pos..]);
            var type = png.Slice(pos + 4, 4);
            if (length < 0 || pos + 12 + length > png.Length) return null;
            if (type.SequenceEqual("grAb"u8) && length == 8)
            {
                return (BinaryPrimitives.ReadInt32BigEndian(png[(pos + 8)..]), BinaryPrimitives.ReadInt32BigEndian(png[(pos + 12)..]));
            }

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return null;
            pos += 12 + length;
        }

        return null;
    }

    /// <summary>The PNG with a grAb chunk (replacing any old one) right after its header chunk.</summary>
    public static byte[] WithGrab(ReadOnlySpan<byte> png, int x, int y)
    {
        if (!IsPng(png)) throw new ArgumentException("Not a PNG.", nameof(png));
        using var output = new MemoryStream(png.Length + 20);
        output.Write(png[..8]);
        var pos = 8;
        var inserted = false;
        Span<byte> grab = stackalloc byte[20];
        while (pos + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[pos..]);
            var chunk = png.Slice(pos, 12 + length);
            var type = chunk.Slice(4, 4);
            if (!type.SequenceEqual("grAb"u8)) output.Write(chunk);
            if (!inserted && type.SequenceEqual("IHDR"u8))
            {
                BinaryPrimitives.WriteUInt32BigEndian(grab, 8);
                "grAb"u8.CopyTo(grab[4..]);
                BinaryPrimitives.WriteInt32BigEndian(grab[8..], x);
                BinaryPrimitives.WriteInt32BigEndian(grab[12..], y);
                BinaryPrimitives.WriteUInt32BigEndian(grab[16..], Crc32(grab[4..16]));
                output.Write(grab);
                inserted = true;
            }

            pos += 12 + length;
        }

        return output.ToArray();
    }
}
