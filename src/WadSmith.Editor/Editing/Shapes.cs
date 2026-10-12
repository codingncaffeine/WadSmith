using WadSmith.Editor.Geometry;

namespace WadSmith.Editor.Editing;

/// <summary>Shapes a room can be drawn as besides a rectangle.</summary>
public static class Shapes
{
    /// <summary>The fewest and most sides a round room is drawn with, and how many it starts with.</summary>
    public const int MinSides = 3;
    public const int MaxSides = 64;
    public const int DefaultSides = 16;

    /// <summary>
    /// A round (or oval) room in the box from <paramref name="a"/> to <paramref name="b"/>: <paramref name="sides"/>
    /// corners on the ellipse that fills the box, turned so a side lies flat along the top and bottom, on whole
    /// units (corners that land on each other are kept once).
    /// </summary>
    public static IReadOnlyList<MapPoint> Ellipse(MapPoint a, MapPoint b, int sides)
    {
        sides = Math.Clamp(sides, MinSides, MaxSides);
        var (cx, cy) = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var (rx, ry) = (Math.Abs(b.X - a.X) / 2, Math.Abs(b.Y - a.Y) / 2);

        // Corners half a step round from straight down, so the bottom and top are sides, not points.
        var points = new List<MapPoint>();
        for (var i = 0; i < sides; i++)
        {
            var angle = -Math.PI / 2 + Math.PI / sides + 2 * Math.PI * i / sides;
            var p = new MapPoint(Math.Round(cx + rx * Math.Cos(angle)), Math.Round(cy + ry * Math.Sin(angle)));
            if (points.Count == 0 || p.DistanceTo(points[^1]) > 0.5) points.Add(p);
        }

        if (points.Count > 1 && points[^1].DistanceTo(points[0]) <= 0.5) points.RemoveAt(points.Count - 1);
        return points;
    }
}
