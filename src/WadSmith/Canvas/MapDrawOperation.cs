using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WadSmith.Core.Preview;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.App.Canvas;

/// <summary>
/// The shapes of some elements (the selection, or the one under the pointer), read from a geometry's
/// frame, so a worker can build it for a large selection.
/// </summary>
internal sealed class SelectionOverlay
{
    public List<(float X1, float Y1, float X2, float Y2)> Lines { get; } = [];

    public List<(float X, float Y)> Points { get; } = [];

    public List<(float X, float Y, float R)> Things { get; } = [];

    public List<SKPath> Areas { get; } = [];

    public static SelectionOverlay Take(IReadOnlyList<Element> items, MapGeometry geometry, MapLook look)
    {
        var o = new SelectionOverlay();
        var f = geometry.Frame;
        foreach (var e in items)
        {
            var slot = e.Slot;
            if (slot < 0) continue;
            switch (e)
            {
                case Line when slot < f.Lines.Count && f.Lines[slot] is { Element: not null } l:
                    var a = f.Vertices[l.V1];
                    var b = f.Vertices[l.V2];
                    o.Lines.Add(((float)a.X, (float)a.Y, (float)b.X, (float)b.Y));
                    break;
                case Vertex when slot < f.Vertices.Count && f.Vertices[slot] is { Element: not null } v:
                    o.Points.Add(((float)v.X, (float)v.Y));
                    break;
                case Thing when slot < f.Things.Count && f.Things[slot] is { Element: not null } t:
                    o.Things.Add(((float)t.X, (float)t.Y, look.Radius(t.Type)));
                    break;
                case Sector:
                    var loops = geometry.LoopsAt(slot);
                    if (loops.Count == 0) break;
                    var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                    CanvasSnapshot.AddLoops(path, loops);
                    o.Areas.Add(path);
                    break;
            }
        }

        return o;
    }
}

/// <summary>Draws the map on the render thread through a Skia lease.</summary>
internal sealed class MapDrawOperation(Rect bounds, CanvasSnapshot? snapshot, SelectionOverlay? selection, SelectionOverlay? hover, MapCanvas.View view) : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => Bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var lease = feature.Lease();
        FrameStats.Gpu = lease.GrContext is not null;
        new MapPainter(snapshot, selection, hover, view).Paint(lease.SkCanvas, lease.GrContext is not null);
        lease.SkCanvas.Flush();
        FrameStats.Draw(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

/// <summary>
/// Paints the map onto any Skia canvas: the window's (through <see cref="MapDrawOperation"/>) or an
/// offscreen picture such as a version's thumbnail.
/// </summary>
internal sealed class MapPainter(CanvasSnapshot? snapshot, SelectionOverlay? selection, SelectionOverlay? hover, MapCanvas.View view)
{
    private static readonly SKColor Background = new(0x0A, 0x07, 0x08);
    private static readonly SKColor GridMinor = new(0x1C, 0x15, 0x16);
    private static readonly SKColor GridMajor = new(0x2E, 0x24, 0x25);
    private static readonly SKColor Accent = new(0xE0, 0x58, 0x2A);
    private static readonly SKColor Ember = new(0xE0, 0x98, 0x59);
    private static readonly SKColor PlayerStart = new(0x5F, 0xD3, 0x5F);
    private static readonly SKColor ThingColour = new(0xC9, 0xC2, 0xBC);

    /// <summary>Above this many walls on screen they are drawn one pixel wide.</summary>
    internal const int DenseWalls = 20_000;

    /// <summary>Paints the view's rectangle; <paramref name="gpu"/> says whether the canvas draws on the GPU.</summary>
    public void Paint(SKCanvas canvas, bool gpu, bool grid = true)
    {
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)view.Width, (float)view.Height));
        canvas.Clear(Background);
        if (grid) DrawGrid(canvas);

        if (snapshot is null)
        {
            canvas.Restore();
            return;
        }

        var m = view.Matrix;
        var px = (float)(1 / view.Zoom);

        // What the view covers in map units, a few pixels wider so strokes at the edge stay whole.
        var left = (float)(view.Cx - view.Width / 2 / view.Zoom) - 4 * px;
        var right = (float)(view.Cx + view.Width / 2 / view.Zoom) + 4 * px;
        var bottom = (float)(view.Cy - view.Height / 2 / view.Zoom) - 4 * px;
        var top = (float)(view.Cy + view.Height / 2 / view.Zoom) + 4 * px;

        // A view crowded with walls draws them one pixel wide, as other editors always do, and fills
        // areas without smoothing their edges (the walls cover them); the usual look costs too much
        // when tens of thousands of walls are on screen at once.
        var walls = snapshot.Walls.Visible(left, bottom, right, top);
        var dense = walls.Shapes > DenseWalls;

        canvas.Save();
        canvas.Concat(in m);
        if (snapshot.Areas is { } areas) DrawAreas(canvas, areas, areas.Visible(left, bottom, right, top), dense, gpu);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

        // Openings first, then steps, then solid walls on top: the automap's order.
        foreach (var (style, colour, width) in new[]
        {
            (WallStyle.Opening, new SKColor(0x6C, 0x6C, 0x6C, 0x90), 1f),
            (WallStyle.FloorStep, MapPreview.FloorChange, 1.4f),
            (WallStyle.CeilingStep, MapPreview.CeilingChange, 1.4f),
            (WallStyle.Solid, MapPreview.Wall, 1.6f),
        })
        {
            paint.Color = colour;
            paint.StrokeWidth = dense ? 0 : width * px;
            snapshot.Walls.Draw(canvas, (int)style, paint, walls);
        }

        // Selection: areas filled, walls thick, in the accent colour; the hover in ember.
        if (selection is not null) DrawOverlay(canvas, selection, Accent, px, 3.2f, 0x40);
        if (hover is not null) DrawOverlay(canvas, hover, Ember, px, 2.4f, 0x22);
        canvas.Restore();

        DrawPoints(canvas, snapshot);
        canvas.Restore();
    }

    private void DrawGrid(SKCanvas canvas)
    {
        foreach (var (step, colour) in new[] { (64.0, GridMinor), (512.0, GridMajor) })
        {
            if (step * view.Zoom < 10) continue;
            using var paint = new SKPaint { Color = colour, StrokeWidth = 1, IsAntialias = false };
            var left = view.Cx - view.Width / 2 / view.Zoom;
            var right = view.Cx + view.Width / 2 / view.Zoom;
            var bottom = view.Cy - view.Height / 2 / view.Zoom;
            var top = view.Cy + view.Height / 2 / view.Zoom;
            for (var x = Math.Floor(left / step) * step; x <= right; x += step)
            {
                var sx = (float)Math.Round((x - view.Cx) * view.Zoom + view.Width / 2) + 0.5f;
                canvas.DrawLine(sx, 0, sx, (float)view.Height, paint);
            }

            for (var y = Math.Floor(bottom / step) * step; y <= top; y += step)
            {
                var sy = (float)Math.Round(view.Height / 2 - (y - view.Cy) * view.Zoom) + 0.5f;
                canvas.DrawLine(0, sy, (float)view.Width, sy, paint);
            }
        }
    }

    /// <summary>
    /// The areas: textures (sharp up close, smoothed further out, their average colour far out) or
    /// height colours. Textured areas are filled without edge smoothing, so neighbours meet without a
    /// seam; the walls drawn on top cover the edges.
    /// </summary>
    private void DrawAreas(SKCanvas canvas, CellPaths areas, (List<int> Cells, int Shapes, bool All) visible, bool dense, bool gpu)
    {
        var heights = snapshot!.Mode == MapViewMode.Heights;
        var texel = view.Zoom;
        var detail = texel >= 1 ? AreaDetail.Sharp : texel * 64 >= 4 ? AreaDetail.Smooth : AreaDetail.Average;
        var smooth = heights && !dense;

        // Without a GPU, smoothing every pixel costs four times as much; a texture shrunk to the zoom
        // and drawn plainly looks the same at a quarter of the cost.
        var level = detail == AreaDetail.Smooth && !gpu ? Math.Clamp((int)Math.Floor(Math.Log2(1 / texel)), 0, 4) : 0;
        areas.DrawAll(canvas, g => snapshot.PaintFor(g, detail, smooth, level), visible);
    }

    internal static SKColor HeightColour(double t) => HeightRamp.At(t);

    private static void DrawOverlay(SKCanvas canvas, SelectionOverlay o, SKColor colour, float px, float width, byte fillAlpha)
    {
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = colour.WithAlpha(fillAlpha), IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = colour, StrokeWidth = width * px, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        foreach (var a in o.Areas)
        {
            canvas.DrawPath(a, fill);
            stroke.StrokeWidth = 1.5f * px;
            canvas.DrawPath(a, stroke);
        }

        stroke.StrokeWidth = width * px;
        foreach (var (x1, y1, x2, y2) in o.Lines) canvas.DrawLine(x1, y1, x2, y2, stroke);
        foreach (var (x, y) in o.Points) canvas.DrawRect(x - 5 * px, y - 5 * px, 10 * px, 10 * px, stroke);
        foreach (var (x, y, r) in o.Things) canvas.DrawCircle(x, y, Math.Max(r, 6 * px), stroke);
    }

    /// <summary>Corners (when zoomed in) and things, in screen pixels so they stay readable at any zoom.</summary>
    private void DrawPoints(SKCanvas canvas, CanvasSnapshot picture)
    {
        float Sx(float x) => (float)((x - view.Cx) * view.Zoom + view.Width / 2);
        float Sy(float y) => (float)(view.Height / 2 - (y - view.Cy) * view.Zoom);

        if (view.Zoom > 0.25)
        {
            using var v = new SKPaint { Color = new SKColor(0xB3, 0xA9, 0xA6), IsAntialias = false };
            var pts = picture.Vertices;
            for (var i = 0; i < pts.Length; i += 2)
            {
                var x = Sx(pts[i]);
                var y = Sy(pts[i + 1]);
                if (x < -4 || y < -4 || x > view.Width + 4 || y > view.Height + 4) continue;
                canvas.DrawRect(x - 1.5f, y - 1.5f, 3, 3, v);
            }
        }

        using var ring = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true };
        using var tick = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true };
        using var sprite = new SKPaint { IsAntialias = false };
        foreach (var t in picture.Things)
        {
            var x = Sx(t.X);
            var y = Sy(t.Y);
            var r = (float)Math.Max(3, t.Radius * view.Zoom);
            if (x < -r - 20 || y < -r - 20 || x > view.Width + r + 20 || y > view.Height + r + 20) continue;
            ring.Color = tick.Color = t.Type is >= 1 and <= 4 or 11 ? PlayerStart : ThingColour;
            var a = t.Angle * Math.PI / 180;

            // The object's own sprite, fitted to the space it takes up but never smaller than 14 pixels,
            // so items stay recognisable when the whole map is in view; the ring keeps its true size.
            if (t.Sprite is { } image && r >= 3)
            {
                var scale = Math.Max(2 * r, 14) / Math.Max(image.Width, image.Height);
                var w = image.Width * scale;
                var h = image.Height * scale;
                var sampling = scale >= 1 ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                canvas.DrawImage(image, new SKRect(x - w / 2, y - h / 2, x + w / 2, y + h / 2), sampling, sprite);
                ring.Color = ring.Color.WithAlpha(0x70);
                canvas.DrawCircle(x, y, r, ring);
                canvas.DrawLine(x + (float)(Math.Cos(a) * r), y - (float)(Math.Sin(a) * r), x + (float)(Math.Cos(a) * r * 1.35), y - (float)(Math.Sin(a) * r * 1.35), tick);
                continue;
            }

            canvas.DrawCircle(x, y, r, ring);
            canvas.DrawLine(x, y, x + (float)(Math.Cos(a) * r * 1.4), y - (float)(Math.Sin(a) * r * 1.4), tick);
        }
    }
}

/// <summary>
/// The heights view's colours: a perceptually even ramp (blue, teal, green, yellow, orange, red) that
/// colour-blind viewers can still order by lightness. The canvas and the legend both read it.
/// </summary>
internal static class HeightRamp
{
    public static readonly SKColor[] Stops = [new(0x2B, 0x3A, 0x8C), new(0x1F, 0x86, 0x8C), new(0x4C, 0xAF, 0x50), new(0xD8, 0xC2, 0x3A), new(0xE8, 0x8A, 0x2E), new(0xD6, 0x3A, 0x2E)];

    /// <summary>How strongly areas are tinted over the canvas background.</summary>
    public const byte Alpha = 0x8C;

    /// <summary>The colour at <paramref name="t"/> from 0 (lowest floor) to 1 (highest).</summary>
    public static SKColor At(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var f = t * (Stops.Length - 1);
        var i = Math.Min((int)f, Stops.Length - 2);
        var k = f - i;
        byte Mix(byte a, byte b) => (byte)(a + (b - a) * k);
        return new SKColor(Mix(Stops[i].Red, Stops[i + 1].Red), Mix(Stops[i].Green, Stops[i + 1].Green), Mix(Stops[i].Blue, Stops[i + 1].Blue));
    }

    /// <summary>The ramp as a left-to-right brush, for the legend.</summary>
    public static LinearGradientBrush Brush()
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
        for (var i = 0; i < Stops.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(Stops[i].Red, Stops[i].Green, Stops[i].Blue), i / (double)(Stops.Length - 1)));
        }

        return brush;
    }
}
