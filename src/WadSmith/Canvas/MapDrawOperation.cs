using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WadSmith.Core.Preview;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.App.Canvas;

/// <summary>
/// The shapes of some elements (the selection, or the one under the pointer), read from a geometry's
/// frame, so a worker can build it for a large selection.
/// </summary>
internal sealed class SelectionOverlay
{
    public List<(float X1, float Y1, float X2, float Y2)> Lines { get; } = [];

    /// <summary>Wall faces: their wall, running so that the face is on its right (the side its room is on).</summary>
    public List<(float X1, float Y1, float X2, float Y2)> Faces { get; } = [];

    public List<(float X, float Y)> Points { get; } = [];

    public List<(float X, float Y, float R)> Things { get; } = [];

    public List<SKPath> Areas { get; } = [];

    /// <summary>The box round everything selected, in map units (null when nothing is on the map).</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? Box { get; private set; }

    /// <summary>
    /// What the selection is linked to by tags, drawn as dashed lines: a selected wall with an action to the areas it
    /// works on, a selected area to the walls elsewhere that work on it (switches, lines walked over), a selected
    /// teleporter to where it sends, and a selected arrival spot to the teleporters that send there.
    /// </summary>
    public List<(float X1, float Y1, float X2, float Y2)> Links { get; } = [];

    /// <summary>Whether the selection turns and resizes with handles: a room, or more than one wall, face, corner or object (one wall alone only stretches its room).</summary>
    public bool HasHandles => Box is not null && (Areas.Count > 0 || Lines.Count + Faces.Count + Points.Count + Things.Count > 1);

    public static SelectionOverlay Take(IReadOnlyList<Element> items, MapGeometry geometry, MapLook look)
    {
        var o = new SelectionOverlay();
        var f = geometry.Frame;
        HashSet<int>? faces = null;
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
                case Side when slot < f.Sides.Count:
                    (faces ??= []).Add(slot);
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

        // A face is found through its wall: one pass over the walls, only when faces are selected.
        if (faces is not null)
        {
            foreach (var l in f.Lines)
            {
                if (l.Element is null) continue;
                var a = f.Vertices[l.V1];
                var b = f.Vertices[l.V2];
                if (faces.Contains(l.Front)) o.Faces.Add(((float)a.X, (float)a.Y, (float)b.X, (float)b.Y));
                if (l.Back >= 0 && faces.Contains(l.Back)) o.Faces.Add(((float)b.X, (float)b.Y, (float)a.X, (float)a.Y));
            }
        }

        var (x0, y0, x1, y1) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        void Add(double x, double y) => (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
        foreach (var (ax, ay, bx, by) in o.Lines.Concat(o.Faces))
        {
            Add(ax, ay);
            Add(bx, by);
        }

        foreach (var (x, y) in o.Points) Add(x, y);
        foreach (var (x, y, _) in o.Things) Add(x, y);
        foreach (var path in o.Areas)
        {
            var b = path.Bounds;
            Add(b.Left, b.Top);
            Add(b.Right, b.Bottom);
        }

        if (x0 <= x1) o.Box = (x0, y0, x1, y1);
        AddLinks(o, items, geometry);
        return o;
    }

    /// <summary>The tag links of the selected walls and areas, from the frame alone (a worker builds the overlay).</summary>
    private static void AddLinks(SelectionOverlay o, IReadOnlyList<Element> items, MapGeometry geometry)
    {
        var f = geometry.Frame;
        (float, float)? Middle(int sector) => geometry.WallBoundsAt(sector) is { } b ? ((float)((b.MinX + b.MaxX) / 2), (float)((b.MinY + b.MaxY) / 2)) : null;
        (float, float) Mid(LineFrame l) => ((float)((f.Vertices[l.V1].X + f.Vertices[l.V2].X) / 2), (float)((f.Vertices[l.V1].Y + f.Vertices[l.V2].Y) / 2));
        // Where a teleport wall sends: the arrival spots with its id (in its tagged areas when it names both), else every
        // arrival spot in the areas with its tag.
        static bool Arrival(int type) => type is Teleports.ArrivalType or 9043 or 9044;
        IEnumerable<(float, float)> Arrivals(LineFrame l)
        {
            var (tid, tag) = l.Special == Teleports.TeleportNoFog ? (l.Args[0], l.Args[2]) : (l.Args[0], l.Args[1]);
            if (tid != 0) return geometry.ThingsWithTid(tid).Select(i => f.Things[i]).Where(t => t.Element is not null && Arrival(t.Type)).Select(t => ((float)t.X, (float)t.Y));
            return geometry.SectorsTagged(tag).SelectMany(s => geometry.WallBoundsAt(s) is { } b ? geometry.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Select(i => f.Things[i]).Where(t => t.Element is not null && Arrival(t.Type) && geometry.InOrOnArea(s, new MapPoint(t.X, t.Y))) : []).Select(t => ((float)t.X, (float)t.Y));
        }

        foreach (var e in items.Take(256))
        {
            var slot = e.Slot;
            if (e is Line && slot >= 0 && slot < f.Lines.Count && f.Lines[slot] is { Element: not null, Special: Teleports.Teleport or Teleports.TeleportNoFog } sender)
            {
                var (sx, sy) = Mid(sender);
                foreach (var (ax, ay) in Arrivals(sender).Take(64)) o.Links.Add((sx, sy, ax, ay));
                continue;
            }

            if (e is Thing && slot >= 0 && slot < f.Things.Count && f.Things[slot] is { Element: not null } spot && Arrival(spot.Type))
            {
                // The walls that send here: by its id, or by the tag of an area it stands in.
                var tags = geometry.SectorSlotsIn(spot.X - 1, spot.Y - 1, spot.X + 1, spot.Y + 1).Where(s => f.Sectors[s].Tag != 0 && geometry.InOrOnArea(s, new MapPoint(spot.X, spot.Y))).Select(s => f.Sectors[s].Tag).ToHashSet();
                var near = geometry.LinesWithArg(0, spot.Tid).Concat(tags.SelectMany(t => geometry.LinesWithArg(1, t).Concat(geometry.LinesWithArg(2, t)))).Distinct();
                foreach (var i in near.Take(64))
                {
                    if (f.Lines[i] is { Element: not null, Special: Teleports.Teleport or Teleports.TeleportNoFog } teleport && Arrivals(teleport).Contains(((float)spot.X, (float)spot.Y)))
                    {
                        var (mx, my) = Mid(teleport);
                        o.Links.Add((mx, my, (float)spot.X, (float)spot.Y));
                    }
                }

                continue;
            }

            if (e is Line && slot >= 0 && slot < f.Lines.Count && f.Lines[slot] is { Element: not null, Special: not 0 } line && line.Args[0] != 0 && Switches.NamesAreas(line.Special))
            {
                var (mx, my) = Mid(line);
                foreach (var s in geometry.SectorsTagged(line.Args[0]).Take(64))
                {
                    if (Middle(s) is { } c) o.Links.Add((mx, my, c.Item1, c.Item2));
                }
            }
            else if (e is Sector && slot >= 0 && slot < f.Sectors.Count && f.Sectors[slot] is { Element: not null, Tag: not 0 } area && Middle(slot) is { } c)
            {
                foreach (var i in geometry.LinesNaming(area.Tag).Take(64))
                {
                    if (f.Lines[i] is { Element: not null, Special: not 0 } by && Switches.NamesAreas(by.Special))
                    {
                        var (mx, my) = Mid(by);
                        o.Links.Add((mx, my, c.Item1, c.Item2));
                    }
                }
            }
        }
    }
}

/// <summary>
/// A room being drawn: the rectangle between two corners in map units (null before the drag starts),
/// the point the pointer snaps to, and the grid step it snaps to.
/// </summary>
internal sealed record DrawPreview(MapPoint? From, MapPoint? To, MapPoint? Snap, double Step, IReadOnlyList<MapPoint>? Corners = null, IReadOnlyList<MapPoint>? Round = null, IReadOnlyList<WadSmith.Editor.Editing.Guide>? Guides = null);

/// <summary>
/// What is being moved and by how much (or turned, resized or mirrored: <paramref name="Placement"/>): its
/// walls and objects drawn at their new place while the map catches up.
/// </summary>
internal sealed record MovePreview(WadSmith.Editor.Editing.MoveSet Set, double Dx, double Dy, MapPoint? MergeAt, WadSmith.Editor.Editing.Placement? Placement = null);

/// <summary>Draws the map on the render thread through a Skia lease.</summary>
/// <summary>Where the player cannot pass in one version of the map (marks to draw, map units), built on a worker.</summary>
internal sealed record FitMarks(long Version, IReadOnlyList<WadSmith.Editor.Geometry.MapPoint[]> Bands, IReadOnlyList<(WadSmith.Editor.Geometry.MapPoint A, WadSmith.Editor.Geometry.MapPoint B)> Low)
{
    public static FitMarks Of(WadSmith.Editor.Geometry.MapGeometry geometry)
    {
        var f = geometry.Frame;
        var low = new List<(WadSmith.Editor.Geometry.MapPoint, WadSmith.Editor.Geometry.MapPoint)>();
        foreach (var p in WadSmith.Editor.Checks.Fit.LowPassages(geometry))
        {
            var l = f.Lines[p.Wall];
            low.Add((new(f.Vertices[l.V1].X, f.Vertices[l.V1].Y), new(f.Vertices[l.V2].X, f.Vertices[l.V2].Y)));
        }

        return new FitMarks(f.Version, [.. WadSmith.Editor.Checks.Fit.Gaps(geometry).Select(g => g.Band)], low);
    }
}

/// <summary>What the plan shows of the player's fit: the marks, and the player's box at the pointer (null when off the map).</summary>
internal sealed record FitOverlay(FitMarks? Marks, WadSmith.Editor.Geometry.MapPoint? Ghost, bool GhostFits);

internal sealed class MapDrawOperation(Rect bounds, CanvasSnapshot? snapshot, SelectionOverlay? selection, SelectionOverlay? hover, MapCanvas.View view, ThreeD.Camera3D? camera = null, DrawPreview? preview = null, MovePreview? move = null, FitOverlay? fit = null) : ICustomDrawOperation
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
        new MapPainter(snapshot, selection, hover, view, camera, preview, move, fit).Paint(lease.SkCanvas, lease.GrContext is not null);
        lease.SkCanvas.Flush();
        FrameStats.Draw(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

/// <summary>
/// Paints the map onto any Skia canvas: the window's (through <see cref="MapDrawOperation"/>) or an
/// offscreen picture such as a version's thumbnail.
/// </summary>
internal sealed class MapPainter(CanvasSnapshot? snapshot, SelectionOverlay? selection, SelectionOverlay? hover, MapCanvas.View view, ThreeD.Camera3D? camera = null, DrawPreview? preview = null, MovePreview? move = null, FitOverlay? fit = null)
{
    private static readonly SKColor Background = new(0x0A, 0x07, 0x08);
    private static readonly SKColor GridFine = new(0x15, 0x10, 0x11);
    private static readonly SKColor GridMinor = new(0x1C, 0x15, 0x16);
    private static readonly SKColor GridMajor = new(0x2E, 0x24, 0x25);
    private static readonly SKColor Accent = new(0xE0, 0x58, 0x2A);
    private static readonly SKColor Ember = new(0xE0, 0x98, 0x59);
    private static readonly SKColor PlayerStart = new(0x5F, 0xD3, 0x5F);
    private static readonly SKColor ThingColour = new(0xC9, 0xC2, 0xBC);
    private static readonly SKColor Misfit = new(0xE8, 0x4A, 0x4A);
    private static readonly SKColor Guide = new(0xE0, 0x4F, 0xC8);

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
            DrawPreview(canvas);
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

        if (fit is not null) DrawFit(canvas, fit, px);

        // Selection: areas filled, walls thick, in the accent colour; the hover in ember.
        if (selection is not null) DrawOverlay(canvas, selection, Accent, px, 3.2f, 0x40);
        if (hover is not null) DrawOverlay(canvas, hover, Ember, px, 2.4f, 0x22);
        if (move is not null) DrawMove(canvas, move, px);
        canvas.Restore();

        DrawPoints(canvas, snapshot);
        if (camera is { } eye) DrawCamera(canvas, eye);
        DrawPreview(canvas);
        canvas.Restore();
    }

    /// <summary>Gaps and openings the player cannot pass, in red, and the player's box at the pointer (map units).</summary>
    private void DrawFit(SKCanvas canvas, FitOverlay fit, float px)
    {
        if (fit.Marks is { } marks)
        {
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = Misfit.WithAlpha(0x50), IsAntialias = true };
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = Misfit.WithAlpha(0xD8), StrokeWidth = 1.4f * px, IsAntialias = true };
            using var band = new SKPath();
            foreach (var b in marks.Bands)
            {
                band.MoveTo((float)b[0].X, (float)b[0].Y);
                for (var i = 1; i < b.Length; i++) band.LineTo((float)b[i].X, (float)b[i].Y);
                band.Close();
            }

            canvas.DrawPath(band, fill);
            canvas.DrawPath(band, edge);
            using var low = new SKPaint { Style = SKPaintStyle.Stroke, Color = Misfit.WithAlpha(0xD8), StrokeWidth = 4f * px, StrokeCap = SKStrokeCap.Butt, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([6 * px, 4 * px], 0) };
            foreach (var (a, b) in marks.Low) canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, low);
        }

        // The player's box only where it is big enough to read.
        if (fit.Ghost is not { } g || 2 * WadSmith.Editor.Checks.Fit.Radius * view.Zoom < 6) return;
        var r = (float)WadSmith.Editor.Checks.Fit.Radius;
        var colour = fit.GhostFits ? PlayerStart : Misfit;
        var box = new SKRect((float)g.X - r, (float)g.Y - r, (float)g.X + r, (float)g.Y + r);
        using var inside = new SKPaint { Style = SKPaintStyle.Fill, Color = colour.WithAlpha(0x38), IsAntialias = true };
        using var outline = new SKPaint { Style = SKPaintStyle.Stroke, Color = colour, StrokeWidth = 1.6f * px, IsAntialias = true };
        canvas.DrawRect(box, inside);
        canvas.DrawRect(box, outline);
    }

    /// <summary>The walls and objects being moved, at their new place (map units).</summary>
    private static void DrawMove(SKCanvas canvas, MovePreview move, float px)
    {
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = Accent, StrokeWidth = 2.4f * px, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        var p = move.Placement ?? WadSmith.Editor.Editing.Placement.Identity with { Tx = move.Dx, Ty = move.Dy };
        SKPoint At(double x, double y)
        {
            var q = p.Apply(x, y);
            return new SKPoint((float)q.X, (float)q.Y);
        }

        foreach (var (x1, y1, x2, y2) in move.Set.Carried) canvas.DrawLine(At(x1, y1), At(x2, y2), stroke);
        foreach (var (mx, my, fx, fy) in move.Set.Stretched) canvas.DrawLine(At(mx, my), new SKPoint((float)fx, (float)fy), stroke);
        foreach (var (x, y) in move.Set.ThingSpots) canvas.DrawCircle(At(x, y), 10 * px, stroke);

        // The corner the grabbed one will become when let go.
        if (move.MergeAt is { } m)
        {
            using var ring = new SKPaint { Style = SKPaintStyle.Stroke, Color = Ember, StrokeWidth = 2 * px, IsAntialias = true };
            canvas.DrawCircle((float)m.X, (float)m.Y, 9 * px, ring);
        }
    }

    /// <summary>The room being drawn, filled faintly with a solid edge, and a cross where the pointer snaps (screen pixels).</summary>
    private void DrawPreview(SKCanvas canvas)
    {
        if (preview is null) return;
        float Sx(double x) => (float)((x - view.Cx) * view.Zoom + view.Width / 2);
        float Sy(double y) => (float)(view.Height / 2 - (y - view.Cy) * view.Zoom);
        if (preview is { Round: { Count: > 2 } round })
        {
            // A round room being drawn: its corners on the ellipse in the dragged box.
            using var path = new SKPath();
            path.MoveTo(Sx(round[0].X), Sy(round[0].Y));
            foreach (var p in round.Skip(1)) path.LineTo(Sx(p.X), Sy(p.Y));
            path.Close();
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = Accent.WithAlpha(0x38), IsAntialias = true };
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = Accent, StrokeWidth = 2, IsAntialias = true };
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, edge);
        }
        else if (preview is { From: { } a, To: { } b })
        {
            var rect = SKRect.Create(Math.Min(Sx(a.X), Sx(b.X)), Math.Min(Sy(a.Y), Sy(b.Y)), Math.Abs(Sx(b.X) - Sx(a.X)), Math.Abs(Sy(b.Y) - Sy(a.Y)));
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = Accent.WithAlpha(0x38), IsAntialias = true };
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = Accent, StrokeWidth = 2, IsAntialias = true };
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, edge);
        }

        // A shape drawn corner by corner: its sides so far, the side to the pointer, and a faint line back to the start.
        if (preview.Corners is { Count: > 0 } corners)
        {
            using var side = new SKPaint { Style = SKPaintStyle.Stroke, Color = Accent, StrokeWidth = 2, IsAntialias = true };
            using var faint = new SKPaint { Style = SKPaintStyle.Stroke, Color = Accent.WithAlpha(0x70), StrokeWidth = 1.5f, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
            using var dot = new SKPaint { Style = SKPaintStyle.Fill, Color = Accent, IsAntialias = true };
            for (var i = 0; i + 1 < corners.Count; i++) canvas.DrawLine(Sx(corners[i].X), Sy(corners[i].Y), Sx(corners[i + 1].X), Sy(corners[i + 1].Y), side);
            if (preview.Snap is { } tip)
            {
                canvas.DrawLine(Sx(corners[^1].X), Sy(corners[^1].Y), Sx(tip.X), Sy(tip.Y), side);
                if (corners.Count >= 2) canvas.DrawLine(Sx(tip.X), Sy(tip.Y), Sx(corners[0].X), Sy(corners[0].Y), faint);
            }

            foreach (var c in corners) canvas.DrawRect(Sx(c.X) - 3, Sy(c.Y) - 3, 6, 6, dot);
        }

        // Smart guides: thin dashed lines from what the corner lines up with, a little past both ends.
        if (preview.Guides is { Count: > 0 } guides)
        {
            using var guide = new SKPaint { Style = SKPaintStyle.Stroke, Color = Guide, StrokeWidth = 1, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([4, 3], 0) };
            foreach (var g in guides)
            {
                var (x0, y0, x1, y1) = (Sx(g.From.X), Sy(g.From.Y), Sx(g.To.X), Sy(g.To.Y));
                var length = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                if (length < 1) continue;
                var (ux, uy) = ((x1 - x0) / length * 14, (y1 - y0) / length * 14);
                canvas.DrawLine(x0 - ux, y0 - uy, x1 + ux, y1 + uy, guide);
                canvas.DrawCircle(x0, y0, 2.5f, guide);
            }
        }

        if (preview.Snap is { } s)
        {
            using var cross = new SKPaint { Style = SKPaintStyle.Stroke, Color = Ember, StrokeWidth = 1.5f, IsAntialias = true };
            var (x, y) = (Sx(s.X), Sy(s.Y));
            canvas.DrawLine(x - 7, y, x + 7, y, cross);
            canvas.DrawLine(x, y - 7, x, y + 7, cross);
        }
    }

    /// <summary>Where the 3D camera stands, as a wedge pointing the way it looks (screen pixels, so it reads at any zoom).</summary>
    private void DrawCamera(SKCanvas canvas, ThreeD.Camera3D eye)
    {
        var x = (float)((eye.X - view.Cx) * view.Zoom + view.Width / 2);
        var y = (float)(view.Height / 2 - (eye.Y - view.Cy) * view.Zoom);
        var a = eye.Yaw * Math.PI / 180;
        var half = 34 * Math.PI / 180;
        using var wedge = new SKPath();
        wedge.MoveTo(x, y);
        wedge.LineTo(x + (float)(Math.Cos(a - half) * 46), y - (float)(Math.Sin(a - half) * 46));
        wedge.LineTo(x + (float)(Math.Cos(a + half) * 46), y - (float)(Math.Sin(a + half) * 46));
        wedge.Close();
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = Accent.WithAlpha(0x50), IsAntialias = true };
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = Accent, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawPath(wedge, fill);
        canvas.DrawPath(wedge, edge);
        using var dot = new SKPaint { Style = SKPaintStyle.Fill, Color = Accent, IsAntialias = true };
        canvas.DrawCircle(x, y, 5, dot);
    }

    private void DrawGrid(SKCanvas canvas)
    {
        // While a room is being drawn, the finer grid it snaps to shows too, fainter than the usual one.
        var steps = new List<(double, SKColor)>();
        if (preview is { Step: < 64 and > 0 } p) steps.Add((p.Step, GridFine));
        steps.Add((64.0, GridMinor));
        steps.Add((512.0, GridMajor));
        foreach (var (step, colour) in steps)
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

        // A face: its wall, and a tick from the wall's middle into the room the face looks at.
        foreach (var (x1, y1, x2, y2) in o.Faces)
        {
            canvas.DrawLine(x1, y1, x2, y2, stroke);
            var (dx, dy) = (x2 - x1, y2 - y1);
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= 0) continue;
            var tick = 12 * px / length;
            var (mx, my) = ((x1 + x2) / 2, (y1 + y2) / 2);
            canvas.DrawLine(mx, my, mx + dy * tick, my - dx * tick, stroke);
        }
        foreach (var (x, y) in o.Points) canvas.DrawRect(x - 5 * px, y - 5 * px, 10 * px, 10 * px, stroke);
        foreach (var (x, y, r) in o.Things) canvas.DrawCircle(x, y, Math.Max(r, 6 * px), stroke);

        // Tag links: dashed, from the wall that works on an area to the area's middle.
        if (o.Links.Count == 0) return;
        using var dash = new SKPaint { Style = SKPaintStyle.Stroke, Color = colour, StrokeWidth = 1.5f * px, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([6 * px, 4 * px], 0) };
        foreach (var (x1, y1, x2, y2) in o.Links) canvas.DrawLine(x1, y1, x2, y2, dash);
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
