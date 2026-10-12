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

    public static SelectionOverlay Take(IReadOnlyList<Element> items, MapGeometry geometry)
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
                    o.Things.Add(((float)t.X, (float)t.Y, 16));
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
internal sealed class MapDrawOperation(Rect bounds, CanvasSnapshot? snapshot, SelectionOverlay? selection, SelectionOverlay? hover, MapCanvas.View view, bool showHeights) : ICustomDrawOperation
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
        var canvas = lease.SkCanvas;
        FrameStats.Gpu = lease.GrContext is not null;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
        canvas.Clear(Background);
        DrawGrid(canvas);

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
        if (showHeights) DrawHeights(canvas, snapshot.Heights.Visible(left, bottom, right, top), !dense);
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
        canvas.Flush();
        FrameStats.Draw(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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

    /// <summary>Areas coloured by floor height: deep blue for the lowest, through green, to warm red for the highest.</summary>
    private void DrawHeights(SKCanvas canvas, (List<int> Cells, int Shapes, bool All) visible, bool smooth)
    {
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = smooth };
        for (var g = 0; g < snapshot!.HeightShades.Length; g++)
        {
            fill.Color = HeightColour(snapshot.HeightShades[g]).WithAlpha(0x8C);
            snapshot.Heights.Draw(canvas, g, fill, visible);
        }
    }

    internal static SKColor HeightColour(double t)
    {
        t = Math.Clamp(t, 0, 1);
        // A perceptually even ramp (blue, teal, green, yellow, orange, red) that colour-blind viewers can still order by lightness.
        SKColor[] stops = [new(0x2B, 0x3A, 0x8C), new(0x1F, 0x86, 0x8C), new(0x4C, 0xAF, 0x50), new(0xD8, 0xC2, 0x3A), new(0xE8, 0x8A, 0x2E), new(0xD6, 0x3A, 0x2E)];
        var f = t * (stops.Length - 1);
        var i = Math.Min((int)f, stops.Length - 2);
        var k = f - i;
        byte Mix(byte a, byte b) => (byte)(a + (b - a) * k);
        return new SKColor(Mix(stops[i].Red, stops[i + 1].Red), Mix(stops[i].Green, stops[i + 1].Green), Mix(stops[i].Blue, stops[i + 1].Blue));
    }

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
        foreach (var t in picture.Things)
        {
            var x = Sx(t.X);
            var y = Sy(t.Y);
            if (x < -20 || y < -20 || x > view.Width + 20 || y > view.Height + 20) continue;
            var r = (float)Math.Max(3, t.Radius * view.Zoom);
            ring.Color = tick.Color = t.Type is >= 1 and <= 4 or 11 ? PlayerStart : ThingColour;
            canvas.DrawCircle(x, y, r, ring);
            var a = t.Angle * Math.PI / 180;
            canvas.DrawLine(x, y, x + (float)(Math.Cos(a) * r * 1.4), y - (float)(Math.Sin(a) * r * 1.4), tick);
        }
    }
}
