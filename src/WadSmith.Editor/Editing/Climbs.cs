using System.Globalization;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.Editing;

/// <summary>
/// A room between two floors too far apart to step between (D27): a corridor joining a low room to a high one by
/// opposite walls, where the player meets a ledge. "Add steps" cuts it into stairs, "Make a ramp" slopes its floor.
/// </summary>
/// <param name="Room">The room between.</param>
/// <param name="Low">The lower room beside it.</param>
/// <param name="LowWall">The wall it shares with the lower room.</param>
/// <param name="High">The higher room beside it.</param>
/// <param name="HighWall">The wall it shares with the higher room, opposite the low one.</param>
/// <param name="Length">How far the high wall is from the low one.</param>
public sealed record Climb(Sector Room, Sector Low, Line LowWall, Sector High, Line HighWall, double Length)
{
    public double Rise => High.FloorHeight - Low.FloorHeight;
}

/// <summary>Finding rooms that join two floors too far apart, and making the way between them climbable.</summary>
public static class Climbs
{
    /// <summary>The steepest ramp the player walks up: GZDoom slides the player off a floor steeper than 45°.</summary>
    public const double SteepestRamp = 1;

    /// <summary>
    /// The climb <paramref name="room"/> is, or null: it shares walls with a room whose floor is more than a step
    /// (<see cref="Stairs.MaxRise"/>) from its own, and the lowest and highest rooms beside it are more than a step
    /// apart, met at opposite, parallel walls. A sloped floor is already a ramp.
    /// </summary>
    public static Climb? Find(MapGeometry geometry, Sector room)
    {
        if (!room.OnMap || SlopePlane.Floor(room) is not null || geometry.WallBoundsAt(room.Slot) is not { } b) return null;
        var shared = new List<(Line Wall, Sector Other)>();
        foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
        {
            if (!l.OnMap || l.Back is not { } back) continue;
            var (front, behind) = (l.Front.Sector, back.Sector);
            if (ReferenceEquals(front, room) ^ ReferenceEquals(behind, room)) shared.Add((l, ReferenceEquals(front, room) ? behind : front));
        }

        if (shared.Count < 2 || !shared.Any(s => Math.Abs(s.Other.FloorHeight - room.FloorHeight) > Stairs.MaxRise)) return null;
        var low = shared.MinBy(s => s.Other.FloorHeight).Other;
        var high = shared.MaxBy(s => s.Other.FloorHeight).Other;
        if (high.FloorHeight - low.FloorHeight <= Stairs.MaxRise) return null;
        var lowWall = shared.Where(s => ReferenceEquals(s.Other, low)).MaxBy(s => s.Wall.Length).Wall;
        var highWall = shared.Where(s => ReferenceEquals(s.Other, high)).MaxBy(s => s.Wall.Length).Wall;

        // Opposite and parallel: the high wall level along the low one's direction, across the room from it.
        var (dx1, dy1) = Unit(lowWall);
        var (dx2, dy2) = Unit(highWall);
        if (Math.Abs(dx1 * dy2 - dy1 * dx2) > 1e-6) return null;
        var n = Slopes.Inward(lowWall, room);
        var length = (highWall.Start.X - lowWall.Start.X) * n.X + (highWall.Start.Y - lowWall.Start.Y) * n.Y;
        if (length < 2 * Stairs.MinDepth || Math.Abs(length - Slopes.Reach(geometry, room, lowWall)) > 0.5) return null;
        return new Climb(room, low, lowWall, high, highWall, length);
    }

    /// <summary>
    /// Slopes the room's floor from the low room's floor at the low wall to the high room's at the high wall, keeping
    /// the player's headroom over it, one step of <paramref name="e"/>; says why not when it would be too steep to walk.
    /// </summary>
    public static string? Ramp(StructureEdit e, MapGeometry geometry, Climb c)
    {
        if (c.Rise / c.Length > SteepestRamp)
        {
            return string.Create(CultureInfo.InvariantCulture, $"A ramp here would climb {c.Rise:0} in {c.Length:0}, steeper than the player walks up (no more than its length). Add steps instead, or make the room longer.");
        }

        var room = c.Room;
        e.Set(room, x => x.FloorHeight, (x, h) => x.FloorHeight = h, c.Low.FloorHeight);
        var headroom = c.High.FloorHeight + Stairs.Headroom;
        if (room.CeilingHeight < headroom) e.Set(room, x => x.CeilingHeight, (x, h) => x.CeilingHeight = h, headroom);
        Slopes.Set(e, room, Slopes.Ramp(geometry, room, c.LowWall, c.Low.FloorHeight, c.High.FloorHeight, ceiling: false), ceiling: false);
        return null;
    }

    /// <summary>The rectangle stairs fill to climb <paramref name="c"/>: from the low wall's end of the room to the high wall's; null when the room is not a rectangle square to the axes.</summary>
    public static (MapPoint From, MapPoint To, bool AlongX)? StairsRun(MapGeometry geometry, Climb c)
    {
        var loops = geometry.Loops(c.Room);
        if (loops.Count != 1 || loops[0].Length != 4) return null;
        var p = loops[0];
        var (minX, maxX, minY, maxY) = (p.Min(q => q.X), p.Max(q => q.X), p.Min(q => q.Y), p.Max(q => q.Y));
        if (p.Any(q => q.X != minX && q.X != maxX || q.Y != minY && q.Y != maxY)) return null;
        var alongX = c.LowWall.Start.X == c.LowWall.End.X;
        if (!alongX && c.LowWall.Start.Y != c.LowWall.End.Y) return null;
        if (alongX)
        {
            var fromLeft = c.LowWall.Start.X == minX;
            return (new MapPoint(fromLeft ? minX : maxX, minY), new MapPoint(fromLeft ? maxX : minX, maxY), true);
        }

        var fromBottom = c.LowWall.Start.Y == minY;
        return (new MapPoint(minX, fromBottom ? minY : maxY), new MapPoint(maxX, fromBottom ? maxY : minY), false);
    }

    private static (double X, double Y) Unit(Line l)
    {
        var (dx, dy) = (l.End.X - l.Start.X, l.End.Y - l.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 1e-9 ? (0, 0) : (dx / length, dy / length);
    }
}
