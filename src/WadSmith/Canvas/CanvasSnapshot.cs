using SkiaSharp;
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

/// <summary>
/// The map's picture for the render thread, built on a worker from one <see cref="MapGeometry"/> and
/// its frame: wall paths by style, area fills by floor height, corners and things, grouped into cells
/// so a frame draws only what it can see. Read-only once built; the UI thread never builds one.
/// </summary>
internal sealed class CanvasSnapshot
{
    public readonly record struct ThingMark(float X, float Y, float Angle, int Type, float Radius);

    private CanvasSnapshot(long version, CellPaths walls, CellPaths heights, double[] heightShades, float[] vertices, ThingMark[] things)
    {
        Version = version;
        Walls = walls;
        Heights = heights;
        HeightShades = heightShades;
        Vertices = vertices;
        Things = things;
    }

    public long Version { get; }

    /// <summary>The walls, one group per <see cref="WallStyle"/>.</summary>
    public CellPaths Walls { get; }

    /// <summary>The areas, one group per floor height band, lowest first.</summary>
    public CellPaths Heights { get; }

    /// <summary>Per height group, where it sits from the lowest floor (0) to the highest (1).</summary>
    public double[] HeightShades { get; }

    /// <summary>x, y of every corner.</summary>
    public float[] Vertices { get; }

    public ThingMark[] Things { get; }

    /// <summary>Builds the picture: whole-map work, so call it on a worker.</summary>
    public static CanvasSnapshot Take(MapGeometry geometry)
    {
        var f = geometry.Frame;

        // Walls: ends and style.
        var segments = new List<(float X1, float Y1, float X2, float Y2, int Style)>(f.Lines.Count);
        foreach (var l in f.Lines)
        {
            if (l.Element is null) continue;
            var a = f.Vertices[l.V1];
            var b = f.Vertices[l.V2];
            segments.Add(((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, (int)StyleOf(f, l)));
        }

        var walls = CellPaths.Build(4, segments.Count, SKPathFillType.Winding, i => segments[i].Style, i =>
        {
            var s = segments[i];
            return (Math.Min(s.X1, s.X2), Math.Min(s.Y1, s.Y2), Math.Max(s.X1, s.X2), Math.Max(s.Y1, s.Y2));
        }, (i, path) =>
        {
            var s = segments[i];
            path.MoveTo(s.X1, s.Y1);
            path.LineTo(s.X2, s.Y2);
        });

        // Areas, grouped by floor height (one group per height, or 64 bands when there are more).
        // Outlines run with their area on the right, so outer loops and holes wind opposite ways and
        // many areas can share one non-zero path without a hole being filled.
        var sectors = new List<int>();
        double min = double.MaxValue, max = double.MinValue;
        for (var s = 0; s < f.Sectors.Count; s++)
        {
            if (f.Sectors[s].Element is null || geometry.LoopsAt(s).Count == 0) continue;
            sectors.Add(s);
            min = Math.Min(min, f.Sectors[s].FloorHeight);
            max = Math.Max(max, f.Sectors[s].FloorHeight);
        }

        var heights = sectors.Select(s => f.Sectors[s].FloorHeight).Distinct().Order().ToArray();
        var span = Math.Max(max - min, 1);
        double[] shades;
        Func<double, int> groupOfHeight;
        if (heights.Length <= 64)
        {
            shades = [.. heights.Select(h => (h - min) / span)];
            groupOfHeight = h => Array.BinarySearch(heights, h);
        }
        else
        {
            shades = [.. Enumerable.Range(0, 64).Select(i => i / 63.0)];
            groupOfHeight = h => (int)Math.Round((h - min) / span * 63);
        }

        var areas = CellPaths.Build(shades.Length, sectors.Count, SKPathFillType.Winding, i => groupOfHeight(f.Sectors[sectors[i]].FloorHeight), i =>
        {
            var loops = geometry.LoopsAt(sectors[i]);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var loop in loops)
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

        var verts = new List<float>(f.Vertices.Count * 2);
        foreach (var v in f.Vertices)
        {
            if (v.Element is null) continue;
            verts.Add((float)v.X);
            verts.Add((float)v.Y);
        }

        var things = f.Things.Where(t => t.Element is not null).Select(t => new ThingMark((float)t.X, (float)t.Y, t.Angle, t.Type, 16)).ToArray();
        return new CanvasSnapshot(f.Version, walls, areas, shades, [.. verts], things);
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
/// group, plus one path per group for the whole map (drawn when every cell is on screen), so a frame
/// draws only the cells it can see.
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
        Paths = new SKPath?[cells][];
        for (var i = 0; i < cells; i++) Paths[i] = new SKPath?[groups];
        Whole = new SKPath?[groups];
    }

    public float[] MinX { get; }

    public float[] MinY { get; }

    public float[] MaxX { get; }

    public float[] MaxY { get; }

    /// <summary>Shapes per cell.</summary>
    public int[] Counts { get; }

    /// <summary>Per cell, per group; null where a cell has nothing of that group.</summary>
    public SKPath?[][] Paths { get; }

    /// <summary>Per group, every shape of the group.</summary>
    public SKPath?[] Whole { get; }

    public int CellCount => Counts.Length;

    /// <summary>About 256 shapes per cell, at most 64 by 64 cells.</summary>
    public static CellPaths Build(int groups, int count, SKPathFillType fill, Func<int, int> groupOf, Func<int, (float MinX, float MinY, float MaxX, float MaxY)> boundsOf, Action<int, SKPath> append)
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
            append(i, cells.Paths[c][g] ??= new SKPath { FillType = fill });
            append(i, cells.Whole[g] ??= new SKPath { FillType = fill });
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
            if (Paths[c][group] is { } path) canvas.DrawPath(path, paint);
        }
    }
}
