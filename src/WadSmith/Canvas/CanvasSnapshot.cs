using SkiaSharp;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.App.Canvas;

/// <summary>How a wall is drawn, in the in-game automap's colours (D26).</summary>
internal enum WallStyle
{
    /// <summary>A two-sided wall with no height change: dim, as the automap hides it.</summary>
    Opening,
    FloorStep,
    CeilingStep,
    Solid,
}

/// <summary>How far in the view is, which decides how area pictures are sampled.</summary>
internal enum AreaDetail
{
    /// <summary>Texels larger than pixels: sharp, as the game draws them.</summary>
    Sharp,

    /// <summary>Texels smaller than pixels: smoothed.</summary>
    Smooth,

    /// <summary>Too far out for detail: each texture's average colour.</summary>
    Average,
}

/// <summary>One group of areas painted alike: a texture at a brightness, or a solid colour.</summary>
internal sealed record AreaStyle(FlatPicture? Flat, byte Light, SKColor Solid, WadSmith.Editor.Scene.FlatTransform? Transform = null);

/// <summary>
/// The map's picture for the render thread, built on a worker from one <see cref="MapGeometry"/> and
/// its frame for one view mode: wall paths by style, area paths grouped by how they are painted,
/// corners and things, grouped into cells so a frame draws only what it can see. Read-only once
/// built, except the paints, which the render thread makes on first use and keeps.
/// </summary>
internal sealed class CanvasSnapshot
{
    public readonly record struct ThingMark(float X, float Y, float Angle, int Type, float Radius, SKImage? Sprite);

    private readonly Dictionary<(int Group, AreaDetail Detail, bool Smooth, int Level), SKPaint> _paints = [];

    private CanvasSnapshot(long version, MapViewMode mode, CellPaths walls, CellPaths? areas, AreaStyle[] styles, float[] vertices, ThingMark[] things)
    {
        Version = version;
        Mode = mode;
        Walls = walls;
        Areas = areas;
        AreaStyles = styles;
        Vertices = vertices;
        Things = things;
    }

    public long Version { get; }

    public MapViewMode Mode { get; }

    /// <summary>The walls, one group per <see cref="WallStyle"/>.</summary>
    public CellPaths Walls { get; }

    /// <summary>The areas, one group per <see cref="AreaStyles"/> entry; null in the Lines view.</summary>
    public CellPaths? Areas { get; }

    public AreaStyle[] AreaStyles { get; }

    /// <summary>x, y of every corner.</summary>
    public float[] Vertices { get; }

    public ThingMark[] Things { get; }

    /// <summary>Builds the picture: whole-map work, so call it on a worker.</summary>
    public static CanvasSnapshot Take(MapGeometry geometry, MapLook look, MapViewMode mode, IReadOnlySet<string>? hidden = null)
    {
        var f = geometry.Frame;

        // Groups hidden for clarity: their rooms and objects are left out, and walls with no room left showing.
        bool Hidden(KeyValuePair<string, Core.Maps.Udmf.UdmfValue>[] extra) => hidden is { Count: > 0 } && Grouping.GroupIn(extra) is { } name && hidden.Contains(name);
        bool HiddenRoom(int side) => side < 0 || f.Sides[side].Sector < 0 || Hidden(f.Sectors[f.Sides[side].Sector].Extra);

        // Walls: ends and style.
        var segments = new List<(float X1, float Y1, float X2, float Y2, int Style)>(f.Lines.Count);
        var shownCorners = hidden is { Count: > 0 } ? new HashSet<int>() : null;
        foreach (var l in f.Lines)
        {
            if (l.Element is null || (hidden is { Count: > 0 } && HiddenRoom(l.Front) && HiddenRoom(l.Back))) continue;
            shownCorners?.Add(l.V1);
            shownCorners?.Add(l.V2);
            var a = f.Vertices[l.V1];
            var b = f.Vertices[l.V2];
            segments.Add(((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, (int)StyleOf(f, l)));
        }

        var walls = CellPaths.Build(4, segments.Count, i => segments[i].Style, i =>
        {
            var s = segments[i];
            return (Math.Min(s.X1, s.X2), Math.Min(s.Y1, s.Y2), Math.Max(s.X1, s.X2), Math.Max(s.Y1, s.Y2));
        }, (i, path) =>
        {
            var s = segments[i];
            path.MoveTo(s.X1, s.Y1);
            path.LineTo(s.X2, s.Y2);
        });

        var (areas, styles) = mode == MapViewMode.Lines ? (null, []) : BuildAreas(geometry, look, mode, hidden);

        var verts = new List<float>(f.Vertices.Count * 2);
        for (var i = 0; i < f.Vertices.Count; i++)
        {
            var v = f.Vertices[i];
            if (v.Element is null || (shownCorners is not null && !shownCorners.Contains(i))) continue;
            verts.Add((float)v.X);
            verts.Add((float)v.Y);
        }

        var things = f.Things.Where(t => t.Element is not null && !Hidden(t.Extra)).Select(t => new ThingMark((float)t.X, (float)t.Y, t.Angle, t.Type, look.Radius(t.Type), look.Sprite(t.Type))).ToArray();
        return new CanvasSnapshot(f.Version, mode, walls, areas, styles, [.. verts], things);
    }

    /// <summary>
    /// The areas grouped by how they are painted: by texture and brightness for Floors and Ceilings,
    /// by floor height for Heights. Outlines run with their area on the right, so outer loops and
    /// holes wind opposite ways and many areas can share one non-zero path without a hole being filled.
    /// </summary>
    private static (CellPaths Areas, AreaStyle[] Styles) BuildAreas(MapGeometry geometry, MapLook look, MapViewMode mode, IReadOnlySet<string>? hidden)
    {
        var f = geometry.Frame;
        var sectors = new List<int>();
        for (var s = 0; s < f.Sectors.Count; s++)
        {
            if (f.Sectors[s].Element is not null && geometry.LoopsAt(s).Count > 0 && !(hidden is { Count: > 0 } && Grouping.GroupIn(f.Sectors[s].Extra) is { } name && hidden.Contains(name))) sectors.Add(s);
        }

        var styles = new List<AreaStyle>();
        var groupOf = new int[sectors.Count];
        if (mode == MapViewMode.Sound)
        {
            // Rooms joined across open walls that do not block sound share a zone, as a shot travels in the game
            // (P_RecursiveSound: a wall with no opening stops it; one sound-blocking wall it crosses, two it does not).
            var zones = SoundZones.Of(f);
            var index = new Dictionary<int, int>();
            for (var i = 0; i < sectors.Count; i++)
            {
                var zone = zones[sectors[i]];
                if (!index.TryGetValue(zone, out var g))
                {
                    g = index[zone] = styles.Count;
                    styles.Add(new AreaStyle(null, 255, SoundZones.Colour(g).WithAlpha(HeightRamp.Alpha)));
                }

                groupOf[i] = g;
            }
        }
        else if (mode == MapViewMode.Brightness)
        {
            // One grey per brightness the map uses, as the game's light levels go: 0 black, 255 white.
            var index = new Dictionary<int, int>();
            for (var i = 0; i < sectors.Count; i++)
            {
                var light = Math.Clamp(f.Sectors[sectors[i]].Light, 0, 255);
                if (!index.TryGetValue(light, out var g))
                {
                    g = index[light] = styles.Count;
                    styles.Add(new AreaStyle(null, 255, new SKColor((byte)light, (byte)light, (byte)light)));
                }

                groupOf[i] = g;
            }
        }
        else if (mode == MapViewMode.Heights)
        {
            var (min, max) = geometry.FloorRange ?? (0, 0);
            var span = Math.Max(max - min, 1);
            var heights = sectors.Select(s => f.Sectors[s].FloorHeight).Distinct().Order().ToArray();
            var bands = heights.Length <= 64 ? heights.Length : 64;
            for (var g = 0; g < bands; g++)
            {
                var shade = heights.Length <= 64 ? (heights[g] - min) / span : g / 63.0;
                styles.Add(new AreaStyle(null, 255, HeightRamp.At(shade).WithAlpha(HeightRamp.Alpha)));
            }

            for (var i = 0; i < sectors.Count; i++)
            {
                var h = f.Sectors[sectors[i]].FloorHeight;
                groupOf[i] = heights.Length <= 64 ? Array.BinarySearch(heights, h) : (int)Math.Round((h - min) / span * 63);
            }
        }
        else
        {
            var index = new Dictionary<(string Flat, int Light, WadSmith.Editor.Scene.FlatTransform Transform), int>();
            for (var i = 0; i < sectors.Count; i++)
            {
                var sector = f.Sectors[sectors[i]];
                var name = mode == MapViewMode.Ceilings ? sector.CeilingTexture : sector.FloorTexture;
                var light = Math.Clamp(sector.Light, 0, 255);
                var transform = WadSmith.Editor.Scene.FlatTransform.Of(sector.Extra, mode == MapViewMode.Ceilings);
                if (!index.TryGetValue((name, light, transform), out var g))
                {
                    g = index[(name, light, transform)] = styles.Count;
                    styles.Add(new AreaStyle(look.Flat(name), Shown(light), new SKColor(0x2A, 0x2A, 0x2E), transform));
                }

                groupOf[i] = g;
            }
        }

        var areas = CellPaths.Build(styles.Count, sectors.Count, i => groupOf[i], i =>
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var loop in geometry.LoopsAt(sectors[i]))
            {
                foreach (var p in loop)
                {
                    x0 = Math.Min(x0, (float)p.X);
                    y0 = Math.Min(y0, (float)p.Y);
                    x1 = Math.Max(x1, (float)p.X);
                    y1 = Math.Max(y1, (float)p.Y);
                }
            }

            return (x0, y0, x1, y1);
        }, (i, path) => AddLoops(path, geometry.LoopsAt(sectors[i])));
        return (areas, [.. styles]);
    }

    /// <summary>Texture to map for an area's texture fields: the inverse of <see cref="WadSmith.Editor.Scene.FlatTransform.Texel"/>.</summary>
    internal static SKMatrix ToMap(WadSmith.Editor.Scene.FlatTransform t)
    {
        if (t.IsPlain) return SKMatrix.CreateScale(1, -1);
        var toTexel = SKMatrix.Concat(SKMatrix.CreateScale((float)t.XScale, (float)t.YScale), SKMatrix.Concat(SKMatrix.CreateTranslation((float)t.XPan, (float)t.YPan), SKMatrix.Concat(SKMatrix.CreateRotation((float)(-t.Rotation * Math.PI / 180)), SKMatrix.CreateScale(1, -1))));
        return toTexel.TryInvert(out var back) ? back : SKMatrix.CreateScale(1, -1);
    }

    /// <summary>
    /// How bright an area is drawn from its light level: dark rooms stay readable from above (the
    /// darkest is drawn at a third of full brightness), and the order of brightness is kept.
    /// </summary>
    internal static byte Shown(int light) => (byte)Math.Round(85 + 170 * (light / 255.0));

    /// <summary>
    /// The paint for one area group at one level of detail; made on the render thread the first time,
    /// then kept. <paramref name="level"/> above 0 draws a smaller copy of the texture without
    /// smoothing (fast without a GPU); 0 with <see cref="AreaDetail.Smooth"/> uses true mipmaps.
    /// </summary>
    public SKPaint PaintFor(int group, AreaDetail detail, bool smooth, int level = 0)
    {
        if (_paints.TryGetValue((group, detail, smooth, level), out var paint)) return paint;
        var style = AreaStyles[group];
        paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = smooth };
        if (Mode is MapViewMode.Heights or MapViewMode.Brightness or MapViewMode.Sound)
        {
            paint.Color = style.Solid;
        }
        else if (style.Flat is { } flat && detail != AreaDetail.Average)
        {
            var sampling = detail == AreaDetail.Sharp || level > 0 ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            // Floors line up with the map's grid as in the game (texture x is map x, texture y minus map y), turned, moved and
            // scaled by the area's own fields: the shader takes texture to map, the way back of the texel a map point shows.
            var image = flat.Level(level);
            var stretch = flat.Image.Width / (float)image.Width;
            paint.Shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, sampling, SKMatrix.Concat(ToMap(style.Transform ?? WadSmith.Editor.Scene.FlatTransform.Plain), SKMatrix.CreateScale(stretch, stretch)));
            paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(style.Light, style.Light, style.Light), SKBlendMode.Modulate);
        }
        else
        {
            var c = style.Flat?.Average ?? style.Solid;
            paint.Color = new SKColor((byte)(c.Red * style.Light / 255), (byte)(c.Green * style.Light / 255), (byte)(c.Blue * style.Light / 255));
        }

        _paints[(group, detail, smooth, level)] = paint;
        return paint;
    }

    /// <summary>Appends an area's loops to a path as closed figures.</summary>
    public static void AddLoops(SKPath path, IReadOnlyList<MapPoint[]> loops)
    {
        foreach (var loop in loops)
        {
            path.MoveTo((float)loop[0].X, (float)loop[0].Y);
            for (var i = 1; i < loop.Length; i++) path.LineTo((float)loop[i].X, (float)loop[i].Y);
            path.Close();
        }
    }

    public static WallStyle StyleOf(MapFrame f, LineFrame l)
    {
        if (l.Back < 0) return WallStyle.Solid;
        var fs = f.Sides[l.Front].Sector;
        var bs = f.Sides[l.Back].Sector;
        if (fs < 0 || bs < 0) return WallStyle.Solid;
        var front = f.Sectors[fs];
        var back = f.Sectors[bs];
        if (front.FloorHeight != back.FloorHeight) return WallStyle.FloorStep;
        if (front.CeilingHeight != back.CeilingHeight) return WallStyle.CeilingStep;
        return WallStyle.Opening;
    }
}

/// <summary>
/// Shapes split into a grid of cells by where their middles fall: per cell its bounds and one path per
/// group it holds, plus one path per group for the whole map (drawn when every cell is on screen), so
/// a frame draws only the cells it can see.
/// </summary>
internal sealed class CellPaths
{
    private CellPaths(int cells, int groups)
    {
        MinX = new float[cells];
        MinY = new float[cells];
        MaxX = new float[cells];
        MaxY = new float[cells];
        Counts = new int[cells];
        Cells = new List<(int Group, SKPath Path)>[cells];
        for (var i = 0; i < cells; i++) Cells[i] = [];
        Whole = new SKPath?[groups];
    }

    public float[] MinX { get; }

    public float[] MinY { get; }

    public float[] MaxX { get; }

    public float[] MaxY { get; }

    /// <summary>Shapes per cell.</summary>
    public int[] Counts { get; }

    /// <summary>Per cell, a path for each group it holds, in group order.</summary>
    public List<(int Group, SKPath Path)>[] Cells { get; }

    /// <summary>Per group, every shape of the group.</summary>
    public SKPath?[] Whole { get; }

    public int CellCount => Counts.Length;

    /// <summary>About 256 shapes per cell, at most 64 by 64 cells.</summary>
    public static CellPaths Build(int groups, int count, Func<int, int> groupOf, Func<int, (float MinX, float MinY, float MaxX, float MaxY)> boundsOf, Action<int, SKPath> append)
    {
        var side = Math.Clamp((int)Math.Sqrt(count / 256.0), 1, 64);
        var bounds = new (float MinX, float MinY, float MaxX, float MaxY)[count];
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            var b = bounds[i] = boundsOf(i);
            x0 = Math.Min(x0, b.MinX);
            y0 = Math.Min(y0, b.MinY);
            x1 = Math.Max(x1, b.MaxX);
            y1 = Math.Max(y1, b.MaxY);
        }

        var cells = new CellPaths(side * side, groups);
        if (count == 0) return cells;
        var w = Math.Max((x1 - x0) / side, 1f);
        var h = Math.Max((y1 - y0) / side, 1f);
        for (var i = 0; i < count; i++)
        {
            var g = groupOf(i);
            var b = bounds[i];
            var cx = Math.Clamp((int)(((b.MinX + b.MaxX) / 2 - x0) / w), 0, side - 1);
            var cy = Math.Clamp((int)(((b.MinY + b.MaxY) / 2 - y0) / h), 0, side - 1);
            var c = cy * side + cx;
            var list = cells.Cells[c];
            var at = list.FindIndex(e => e.Group >= g);
            if (at < 0 || list[at].Group != g)
            {
                var entry = (g, new SKPath { FillType = SKPathFillType.Winding });
                if (at < 0) list.Add(entry);
                else list.Insert(at, entry);
                at = at < 0 ? list.Count - 1 : at;
            }

            append(i, list[at].Path);
            append(i, cells.Whole[g] ??= new SKPath { FillType = SKPathFillType.Winding });
            if (cells.Counts[c] == 0)
            {
                (cells.MinX[c], cells.MinY[c], cells.MaxX[c], cells.MaxY[c]) = b;
            }
            else
            {
                cells.MinX[c] = Math.Min(cells.MinX[c], b.MinX);
                cells.MinY[c] = Math.Min(cells.MinY[c], b.MinY);
                cells.MaxX[c] = Math.Max(cells.MaxX[c], b.MaxX);
                cells.MaxY[c] = Math.Max(cells.MaxY[c], b.MaxY);
            }

            cells.Counts[c]++;
        }

        return cells;
    }

    /// <summary>The cells with shapes inside the map-unit rectangle, how many shapes they hold, and whether that is every cell.</summary>
    public (List<int> Cells, int Shapes, bool All) Visible(float left, float bottom, float right, float top)
    {
        var visible = new List<int>();
        var shapes = 0;
        var filled = 0;
        for (var c = 0; c < CellCount; c++)
        {
            if (Counts[c] == 0) continue;
            filled++;
            if (MaxX[c] < left || MinX[c] > right || MaxY[c] < bottom || MinY[c] > top) continue;
            visible.Add(c);
            shapes += Counts[c];
        }

        return (visible, shapes, visible.Count == filled);
    }

    /// <summary>Draws one group: the whole-map path when every cell shows, otherwise the visible cells' paths.</summary>
    public void Draw(SKCanvas canvas, int group, SKPaint paint, (List<int> Cells, int Shapes, bool All) visible)
    {
        if (visible.All)
        {
            if (Whole[group] is { } whole) canvas.DrawPath(whole, paint);
            return;
        }

        foreach (var c in visible.Cells)
        {
            foreach (var (g, path) in Cells[c])
            {
                if (g == group) canvas.DrawPath(path, paint);
            }
        }
    }

    /// <summary>Draws every group, each with its own paint, lowest group first.</summary>
    public void DrawAll(SKCanvas canvas, Func<int, SKPaint> paintFor, (List<int> Cells, int Shapes, bool All) visible)
    {
        if (visible.All)
        {
            for (var g = 0; g < Whole.Length; g++)
            {
                if (Whole[g] is { } whole) canvas.DrawPath(whole, paintFor(g));
            }

            return;
        }

        foreach (var c in visible.Cells)
        {
            foreach (var (g, path) in Cells[c]) canvas.DrawPath(path, paintFor(g));
        }
    }
}
