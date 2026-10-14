using System.Collections.Concurrent;
using SkiaSharp;
using WadSmith.Core.Graphics;
using WadSmith.Editor;

namespace WadSmith.App.Canvas;

/// <summary>How the 2D view paints the map (D45).</summary>
internal enum MapViewMode
{
    /// <summary>Each area shows its floor texture, lit by its brightness.</summary>
    Floors,

    /// <summary>Each area shows its ceiling texture.</summary>
    Ceilings,

    /// <summary>Areas coloured by floor height (D26).</summary>
    Heights,

    /// <summary>Walls only, in the automap's colours.</summary>
    Lines,

    /// <summary>Areas in greys by brightness, black for 0 and white for 255 (Eureka's lighting view).</summary>
    Brightness,

    /// <summary>Areas coloured by sound zone: the rooms a shot is heard in without crossing a wall that blocks sound (Eureka's sound view).</summary>
    Sound,
}

/// <summary>
/// A floor or ceiling picture ready to draw: the image, smaller copies at half, quarter, eighth and
/// sixteenth size (so drawing without a GPU can pick the one that fits the zoom and stay fast), and
/// its average colour for views too far out to show detail.
/// </summary>
internal sealed record FlatPicture(SKImage Image, SKImage[] Smaller, SKColor Average)
{
    /// <summary>The copy at 1/2^level size (0 is the image itself).</summary>
    public SKImage Level(int level) => level <= 0 ? Image : Smaller[Math.Min(level, Smaller.Length) - 1];

    public static FlatPicture Of(SKImage image, SKColor average)
    {
        var smaller = new List<SKImage>();
        var previous = image;
        for (var level = 1; level <= 4; level++)
        {
            var w = Math.Max(1, image.Width >> level);
            var h = Math.Max(1, image.Height >> level);
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var pixels = new SKPixmap(info, System.Runtime.InteropServices.Marshal.AllocHGlobal(info.BytesSize));
            try
            {
                previous.ScalePixels(pixels, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                previous = SKImage.FromPixelCopy(pixels);
                smaller.Add(previous);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(pixels.GetPixels());
            }
        }

        return new FlatPicture(image, [.. smaller], average);
    }
}

/// <summary>
/// The game's pictures as Skia images for the canvas: floors, ceilings and object sprites, made from
/// the session's <see cref="GameResources"/> the first time a picture needs them (on the worker that
/// builds the picture), then kept.
/// </summary>
internal sealed class MapLook(GameResources? resources)
{
    private readonly ConcurrentDictionary<string, FlatPicture?> _flats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, SKImage?> _sprites = new();

    public GameResources? Resources => resources;

    private int _texturesVersion;

    public FlatPicture? Flat(string name)
    {
        if (resources is null || name is "" or "-") return null;

        // A texture written again under its name (edited in another program): the pictures made before are made again.
        if (resources.TexturesVersion != _texturesVersion)
        {
            _flats.Clear();
            _texturesVersion = resources.TexturesVersion;
        }

        return _flats.GetOrAdd(name, n =>
        {
            // The sky's floor picture is only a marker; show the sky itself so outdoor areas read as outdoors.
            var image = (IsSky(n) ? resources.Textures.GetWall("SKY1") : null) ?? resources.Flat(n);
            return image is null ? null : FlatPicture.Of(ToImage(image), Average(image));
        });
    }

    /// <summary>The sky marker every Doom-engine game uses for open-air ceilings.</summary>
    public static bool IsSky(string flat) => flat.Equals("F_SKY1", StringComparison.OrdinalIgnoreCase) || flat.Equals("F_SKY", StringComparison.OrdinalIgnoreCase);

    private int _picturesVersion;

    public SKImage? Sprite(int type)
    {
        if (resources is null) return null;
        // A model turned and baked again: its pictures are read afresh.
        if (resources.PicturesVersion != _picturesVersion)
        {
            _sprites.Clear();
            _picturesVersion = resources.PicturesVersion;
        }

        return _sprites.GetOrAdd(type, t => resources.SpriteFor(t) is { } image ? ToImage(image) : null);
    }

    /// <summary>The object's radius in map units (16 when the game does not define it).</summary>
    public float Radius(int type) => (float)(resources?.Things.Find(type)?.Radius ?? 16);

    private static SKImage ToImage(RgbaImage image) =>
        SKImage.FromPixelCopy(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul), image.Pixels);

    private static SKColor Average(RgbaImage image)
    {
        long r = 0, g = 0, b = 0, n = 0;
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            if (p[i + 3] == 0) continue;
            r += p[i];
            g += p[i + 1];
            b += p[i + 2];
            n++;
        }

        return n == 0 ? new SKColor(0x2A, 0x2A, 0x2E) : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}
