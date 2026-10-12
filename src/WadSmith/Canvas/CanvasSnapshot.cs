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
/// An immutable picture of the map for the render thread: wall segments by style, corners and
/// things, and the areas' outlines for fills. Taken on the UI thread when the map's version changes
/// (cheap copies only); the render thread builds its Skia paths from it once and keeps them.
/// </summary>
internal sealed class CanvasSnapshot
{
    public readonly record struct ThingMark(float X, float Y, float Angle, int Type, float Radius);

    private SKPath?[]? _paths;

    private CanvasSnapshot(long version, float[][] segments, float[] vertices, ThingMark[] things, (SKPath Path, double Floor)[] areas, (double Min, double Max) heights)
    {
        Version = version;
        Segments = segments;
        Vertices = vertices;
        Things = things;
        Areas = areas;
        FloorRange = heights;
    }

    public long Version { get; }

    /// <summary>Per <see cref="WallStyle"/>: x1, y1, x2, y2 for every wall of that style.</summary>
    public float[][] Segments { get; }

    public float[] Vertices { get; }

    public ThingMark[] Things { get; }

    /// <summary>Every area's outline (in map units) and its floor height, for the heights overlay.</summary>
    public (SKPath Path, double Floor)[] Areas { get; }

    public (double Min, double Max) FloorRange { get; }

    public static CanvasSnapshot Take(EditorMap map, MapGeometry geometry)
    {
        var lists = new List<float>[4];
        for (var i = 0; i < 4; i++) lists[i] = [];
        foreach (var l in map.Lines)
        {
            var style = StyleOf(l);
            var list = lists[(int)style];
            list.Add((float)l.Start.X);
            list.Add((float)l.Start.Y);
            list.Add((float)l.End.X);
            list.Add((float)l.End.Y);
        }

        var verts = new float[map.Vertices.Count * 2];
        for (var i = 0; i < map.Vertices.Count; i++)
        {
            verts[i * 2] = (float)map.Vertices[i].X;
            verts[i * 2 + 1] = (float)map.Vertices[i].Y;
        }

        var things = map.Things.Select(t => new ThingMark((float)t.X, (float)t.Y, t.Angle, t.Type, 16)).ToArray();

        var areas = new List<(SKPath, double)>();
        foreach (var s in map.Sectors)
        {
            var loops = geometry.Loops(s);
            if (loops.Count == 0) continue;
            var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var loop in loops)
            {
                path.MoveTo((float)loop[0].X, (float)loop[0].Y);
                for (var i = 1; i < loop.Length; i++) path.LineTo((float)loop[i].X, (float)loop[i].Y);
                path.Close();
            }

            areas.Add((path, s.FloorHeight));
        }

        var range = map.Sectors.Count == 0 ? (0.0, 0.0) : (map.Sectors.Min(s => s.FloorHeight), map.Sectors.Max(s => s.FloorHeight));
        return new CanvasSnapshot(map.Version, lists.Select(l => l.ToArray()).ToArray(), verts, things, [.. areas], range);
    }

    public static WallStyle StyleOf(Line l)
    {
        if (l.Back is null) return WallStyle.Solid;
        var f = l.Front.Sector;
        var b = l.Back.Sector;
        if (f.FloorHeight != b.FloorHeight) return WallStyle.FloorStep;
        if (f.CeilingHeight != b.CeilingHeight) return WallStyle.CeilingStep;
        return WallStyle.Opening;
    }

    /// <summary>The walls of one style as one path, built on first use (on the render thread).</summary>
    public SKPath PathFor(WallStyle style)
    {
        _paths ??= new SKPath?[4];
        if (_paths[(int)style] is { } p) return p;
        var path = new SKPath();
        var s = Segments[(int)style];
        for (var i = 0; i < s.Length; i += 4)
        {
            path.MoveTo(s[i], s[i + 1]);
            path.LineTo(s[i + 2], s[i + 3]);
        }

        _paths[(int)style] = path;
        return path;
    }
}
