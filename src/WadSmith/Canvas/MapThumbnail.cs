using SkiaSharp;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.App.Canvas;

/// <summary>A small picture of a whole map as the editor shows it (floors and sprites, D45), for the Versions list.</summary>
internal static class MapThumbnail
{
    /// <summary>Paints <paramref name="frame"/> fitted into the size and returns it as PNG; whole-map work, so call it on a worker.</summary>
    public static byte[] Render(MapFrame frame, MapLook look, int width, int height)
    {
        var geometry = MapGeometry.Build(frame);
        var picture = CanvasSnapshot.Take(geometry, look, MapViewMode.Floors);
        var (x0, y0, x1, y1) = geometry.Extent();
        var zoom = Math.Min((width - 12) / Math.Max(x1 - x0, 64), (height - 12) / Math.Max(y1 - y0, 64));
        var view = new MapCanvas.View((x0 + x1) / 2, (y0 + y1) / 2, zoom, width, height);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)) ?? throw new InvalidOperationException("No surface for a map picture.");
        new MapPainter(picture, null, null, view).Paint(surface.Canvas, gpu: false, grid: false);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
