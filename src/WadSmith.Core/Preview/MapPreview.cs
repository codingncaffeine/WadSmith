using SkiaSharp;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;

namespace WadSmith.Core.Preview;

/// <summary>
/// A picture of a map from above in the colours of the in-game automap: solid walls red, floor
/// height changes brown, ceiling height changes yellow, secrets and same-height openings left out
/// as the automap leaves them out. Drawn without anti-aliasing, so the same map always gives the
/// same pixels.
/// </summary>
public static class MapPreview
{
    public static readonly SKColor Background = new(0, 0, 0);
    public static readonly SKColor Wall = new(252, 0, 0);
    public static readonly SKColor FloorChange = new(188, 120, 72);
    public static readonly SKColor CeilingChange = new(252, 252, 0);
    public static readonly SKColor Opening = new(108, 108, 108);
    public static readonly SKColor ThingColour = new(0, 220, 0);

    public sealed record Options(int Size = 1024, int Margin = 16, bool Things = false, bool Openings = false);

    public static RgbaImage Render(MapModel map, Options? options = null)
    {
        options ??= new Options();
        var size = options.Size;
        var img = new RgbaImage(size, size);
        if (map.Vertices.Count == 0) return img;

        double minX = map.Vertices.Min(v => v.X), maxX = map.Vertices.Max(v => v.X);
        double minY = map.Vertices.Min(v => v.Y), maxY = map.Vertices.Max(v => v.Y);
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1);
        var scale = (size - 2 * options.Margin) / span;
        var offX = options.Margin + ((size - 2 * options.Margin) - (maxX - minX) * scale) / 2;
        var offY = options.Margin + ((size - 2 * options.Margin) - (maxY - minY) * scale) / 2;

        // Map Y grows north; picture Y grows down.
        SKPoint P(double x, double y) => new((float)(offX + (x - minX) * scale), (float)(size - (offY + (y - minY) * scale)));

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* px = img.Pixels)
            {
                using var surface = SKSurface.Create(info, (nint)px, info.RowBytes) ?? throw new InvalidOperationException("Skia could not make a drawing surface.");
                var canvas = surface.Canvas;
                canvas.Clear(Background);
                using var paint = new SKPaint { IsAntialias = false, StrokeWidth = 1, Style = SKPaintStyle.Stroke };

                // Openings first, then floor and ceiling changes, then walls on top, as the automap layers them.
                foreach (var pass in new[] { 0, 1, 2, 3 })
                {
                    foreach (var l in map.Lines)
                    {
                        if (l.V1 < 0 || l.V1 >= map.Vertices.Count || l.V2 < 0 || l.V2 >= map.Vertices.Count) continue;
                        if (l.Flag("dontdraw")) continue;
                        var colour = Classify(map, l, options.Openings);
                        if (colour is null || Pass(colour.Value) != pass) continue;
                        paint.Color = colour.Value;
                        var a = map.Vertices[l.V1];
                        var b = map.Vertices[l.V2];
                        canvas.DrawLine(P(a.X, a.Y), P(b.X, b.Y), paint);
                    }
                }

                if (options.Things)
                {
                    paint.Color = ThingColour;
                    paint.Style = SKPaintStyle.Fill;
                    foreach (var t in map.Things)
                    {
                        var p = P(t.X, t.Y);
                        canvas.DrawRect(p.X - 1, p.Y - 1, 3, 3, paint);
                    }
                }

                canvas.Flush();
            }
        }

        // Skia wrote opaque pixels; make sure alpha is full everywhere so the PNG has no holes.
        for (var i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
        return img;
    }

    private static int Pass(SKColor c) => c == Opening ? 0 : c == FloorChange ? 1 : c == CeilingChange ? 2 : 3;

    private static SKColor? Classify(MapModel map, MapLine l, bool openings)
    {
        if (l.Back < 0 || l.Back >= map.Sides.Count || l.Front < 0 || l.Front >= map.Sides.Count) return Wall;
        var fs = map.Sides[l.Front].Sector;
        var bs = map.Sides[l.Back].Sector;
        if (fs < 0 || fs >= map.Sectors.Count || bs < 0 || bs >= map.Sectors.Count) return Wall;
        if (l.Flag("secret")) return Wall;
        var f = map.Sectors[fs];
        var b = map.Sectors[bs];
        if (f.FloorHeight != b.FloorHeight) return FloorChange;
        if (f.CeilingHeight != b.CeilingHeight) return CeilingChange;
        return openings ? Opening : null;
    }
}
