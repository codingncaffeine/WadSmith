using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WadSmith.Core.Preview;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.App.Canvas;

/// <summary>The selected and hovered elements' shapes, taken on the UI thread (small).</summary>
internal sealed class SelectionOverlay
{
    public List<(float X1, float Y1, float X2, float Y2)> Lines { get; } = [];

    public List<(float X, float Y)> Points { get; } = [];

    public List<(float X, float Y, float R)> Things { get; } = [];

    public List<SKPath> Areas { get; } = [];

    public SelectionOverlay? Hover { get; private set; }

    public static SelectionOverlay Take(EditorSession session, MapGeometry geometry, Element? hover)
    {
        var o = new SelectionOverlay();
        foreach (var e in session.Selection.Items) o.Add(e, geometry);
        if (hover is not null && !session.Selection.Contains(hover))
        {
            o.Hover = new SelectionOverlay();
            o.Hover.Add(hover, geometry);
        }

        return o;
    }

    private void Add(Element e, MapGeometry geometry)
    {
        switch (e)
        {
            case Line l:
                Lines.Add(((float)l.Start.X, (float)l.Start.Y, (float)l.End.X, (float)l.End.Y));
                break;
            case Vertex v:
                Points.Add(((float)v.X, (float)v.Y));
                break;
            case Thing t:
                Things.Add(((float)t.X, (float)t.Y, 16));
                break;
            case Sector s:
                var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                foreach (var loop in geometry.Loops(s))
                {
                    path.MoveTo((float)loop[0].X, (float)loop[0].Y);
                    for (var i = 1; i < loop.Length; i++) path.LineTo((float)loop[i].X, (float)loop[i].Y);
                    path.Close();
                }

                Areas.Add(path);
                break;
        }
    }
}

/// <summary>Draws the map on the render thread through a Skia lease.</summary>
internal sealed class MapDrawOperation(Rect bounds, CanvasSnapshot snapshot, SelectionOverlay selection, MapCanvas.View view, bool showHeights) : ICustomDrawOperation
{
    private static readonly SKColor Background = new(0x0A, 0x07, 0x08);
    private static readonly SKColor GridMinor = new(0x1C, 0x15, 0x16);
    private static readonly SKColor GridMajor = new(0x2E, 0x24, 0x25);
    private static readonly SKColor Accent = new(0xE0, 0x58, 0x2A);
    private static readonly SKColor Ember = new(0xE0, 0x98, 0x59);
    private static readonly SKColor PlayerStart = new(0x5F, 0xD3, 0x5F);
    private static readonly SKColor ThingColour = new(0xC9, 0xC2, 0xBC);

    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => Bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
        canvas.Clear(Background);
        DrawGrid(canvas);

        var m = view.Matrix;
        var px = (float)(1 / view.Zoom);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

        canvas.Save();
        canvas.Concat(in m);
        if (showHeights) DrawHeights(canvas);

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
            paint.StrokeWidth = width * px;
            canvas.DrawPath(snapshot.PathFor(style), paint);
        }

        // Selection: areas filled, walls thick, in the accent colour; the hover in ember.
        DrawOverlay(canvas, selection, Accent, px, 3.2f, 0x40);
        if (selection.Hover is { } hover) DrawOverlay(canvas, hover, Ember, px, 2.4f, 0x22);
        canvas.Restore();

        DrawPoints(canvas);
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

    /// <summary>Areas coloured by floor height: deep blue for the lowest, through green, to warm red for the highest.</summary>
    private void DrawHeights(SKCanvas canvas)
    {
        var (min, max) = snapshot.FloorRange;
        var span = Math.Max(max - min, 1);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        foreach (var (path, floor) in snapshot.Areas.OrderBy(a => a.Floor))
        {
            fill.Color = HeightColour((floor - min) / span).WithAlpha(0x8C);
            canvas.DrawPath(path, fill);
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
    private void DrawPoints(SKCanvas canvas)
    {
        float Sx(float x) => (float)((x - view.Cx) * view.Zoom + view.Width / 2);
        float Sy(float y) => (float)(view.Height / 2 - (y - view.Cy) * view.Zoom);

        if (view.Zoom > 0.25)
        {
            using var v = new SKPaint { Color = new SKColor(0xB3, 0xA9, 0xA6), IsAntialias = false };
            var pts = snapshot.Vertices;
            for (var i = 0; i < pts.Length; i += 2) canvas.DrawRect(Sx(pts[i]) - 1.5f, Sy(pts[i + 1]) - 1.5f, 3, 3, v);
        }

        using var ring = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true };
        using var tick = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true };
        foreach (var t in snapshot.Things)
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
