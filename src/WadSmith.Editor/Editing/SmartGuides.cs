using WadSmith.Editor.Geometry;

namespace WadSmith.Editor.Editing;

/// <summary>A thin line the plan shows while a corner is placed: the corner it lines up with, or the side's direction.</summary>
public readonly record struct Guide(MapPoint From, MapPoint To);

/// <summary>
/// Smart guides (as Figma's) for drawing corner by corner and dragging rooms: a corner lines up with the corners straight
/// above, below or beside it when close to them, which beats the grid (remixed maps are full of corners off the grid), and a
/// side from the previous corner settles on a whole 45° when close to one; Shift holds every side to the nearest 15°.
/// </summary>
public static class SmartGuides
{
    /// <param name="raw">Where the pointer is.</param>
    /// <param name="snapped">Where the corner goes on the grid (or on a wall, or a corner of the map).</param>
    /// <param name="onCorner">Whether <paramref name="snapped"/> is one of the map's corners: it then stays there.</param>
    /// <param name="corners">The corners to line up with: the map's near the pointer and the shape's own.</param>
    /// <param name="previous">The corner before this one, for the side's direction; null for the first corner.</param>
    /// <param name="tolerance">How close counts, in map units (a few pixels).</param>
    /// <param name="grid">The grid's step, for the steps a diagonal side takes.</param>
    /// <param name="fifteen">Whether every side keeps to the nearest 15° (Shift).</param>
    public static (MapPoint At, IReadOnlyList<Guide> Guides) Place(MapPoint raw, MapPoint snapped, bool onCorner, IReadOnlyList<MapPoint> corners, MapPoint? previous, double tolerance, double grid, bool fifteen)
    {
        if (onCorner) return (snapped, []);
        if (fifteen && previous is { } from)
        {
            // Shift: the side keeps to the nearest 15°, as long as the pointer is from the corner before.
            var (dx, dy) = (raw.X - from.X, raw.Y - from.Y);
            var length = Math.Sqrt(dx * dx + dy * dy);
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 12)) * (Math.PI / 12);
            var held = new MapPoint(Math.Round(from.X + Math.Cos(angle) * length), Math.Round(from.Y + Math.Sin(angle) * length));
            return (held, [new Guide(from, held)]);
        }

        // Lined up with a corner straight above or below (same X), and beside (same Y): the nearest within reach.
        MapPoint? alongX = null, alongY = null;
        foreach (var c in corners)
        {
            if (Math.Abs(c.X - raw.X) <= tolerance && (alongX is not { } bx || Math.Abs(c.X - raw.X) < Math.Abs(bx.X - raw.X) || (c.X == bx.X && c.DistanceTo(raw) < bx.DistanceTo(raw)))) alongX = c;
            if (Math.Abs(c.Y - raw.Y) <= tolerance && (alongY is not { } by || Math.Abs(c.Y - raw.Y) < Math.Abs(by.Y - raw.Y) || (c.Y == by.Y && c.DistanceTo(raw) < by.DistanceTo(raw)))) alongY = c;
        }

        // A side close to a whole 45° from the corner before.
        (double X, double Y)? ray = null;
        if (previous is { } last && !(alongX is not null && alongY is not null))
        {
            var best = tolerance;
            for (var k = 0; k < 8; k++)
            {
                var (ux, uy) = (Math.Round(Math.Cos(k * Math.PI / 4), 12), Math.Round(Math.Sin(k * Math.PI / 4), 12));
                var (rx, ry) = (raw.X - last.X, raw.Y - last.Y);
                if (rx * ux + ry * uy <= 0) continue;
                var off = Math.Abs(rx * uy - ry * ux);
                if (off <= best)
                {
                    best = off;
                    ray = (ux, uy);
                }
            }
        }

        var guides = new List<Guide>();
        MapPoint at;
        if (ray is { } u && previous is { } p)
        {
            if (u.X == 0 || u.Y == 0)
            {
                // Straight across or up and down: the other way stays on the grid, or on a corner it lines up with.
                at = u.Y == 0 ? new MapPoint(alongX?.X ?? snapped.X, p.Y) : new MapPoint(p.X, alongY?.Y ?? snapped.Y);
            }
            else if (alongX is { } ax)
            {
                at = new MapPoint(ax.X, Math.Round(p.Y + (ax.X - p.X) * u.Y / u.X));
            }
            else if (alongY is { } ay)
            {
                at = new MapPoint(Math.Round(p.X + (ay.Y - p.Y) * u.X / u.Y), ay.Y);
            }
            else
            {
                // A diagonal: whole grid steps along both ways, as far as the pointer.
                var step = Math.Max(1, grid);
                var n = Math.Round(((raw.X - p.X) * u.X + (raw.Y - p.Y) * u.Y) * Math.Abs(u.X) / step);
                at = new MapPoint(p.X + Math.Sign(u.X) * n * step, p.Y + Math.Sign(u.Y) * n * step);
            }

            guides.Add(new Guide(p, at));
        }
        else
        {
            at = new MapPoint(alongX?.X ?? snapped.X, alongY?.Y ?? snapped.Y);
        }

        if (alongX is { } gx && gx.X == at.X) guides.Add(new Guide(gx, at));
        if (alongY is { } gy && gy.Y == at.Y) guides.Add(new Guide(gy, at));
        return (at, guides);
    }
}
