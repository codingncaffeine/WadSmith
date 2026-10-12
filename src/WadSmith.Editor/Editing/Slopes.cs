using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Editor.Editing;

/// <summary>
/// The Ramp tool (§3.7, §3.12): a room's floor or ceiling sloped from one of its walls, where it keeps the room's height,
/// to a height at the far side; written as the plane fields GZDoom reads (<see cref="SlopePlane"/>), and read back from
/// them so the Inspector can say which wall a slope starts from.
/// </summary>
public static class Slopes
{
    /// <summary>The room's walls, each with words for where it is ("the north wall"), going round the room.</summary>
    public static IReadOnlyList<(Line Wall, string Words)> Walls(MapGeometry geometry, Sector room)
    {
        if (geometry.WallBoundsAt(room.Slot) is not { } b) return [];
        var walls = geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, room) || (l.Back is { } back && ReferenceEquals(back.Sector, room)))).ToList();
        if (walls.Count == 0) return [];
        var (cx, cy) = (walls.Average(l => (l.Start.X + l.End.X) / 2), walls.Average(l => (l.Start.Y + l.End.Y) / 2));
        var named = new List<(Line Wall, string Words)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var wall in walls.OrderBy(l => Math.Atan2((l.Start.Y + l.End.Y) / 2 - cy, (l.Start.X + l.End.X) / 2 - cx)))
        {
            var words = $"the {Compass(Math.Atan2((wall.Start.Y + wall.End.Y) / 2 - cy, (wall.Start.X + wall.End.X) / 2 - cx))} wall";
            counts[words] = counts.GetValueOrDefault(words) + 1;
            named.Add((wall, counts[words] == 1 ? words : $"{words} ({counts[words]})"));
        }

        return named;
    }

    private static string Compass(double angle)
    {
        var eighth = (int)Math.Round(angle / (Math.PI / 4));
        return ((eighth + 8) % 8) switch { 0 => "east", 1 => "north-east", 2 => "north", 3 => "north-west", 4 => "west", 5 => "south-west", 6 => "south", _ => "south-east" };
    }

    /// <summary>The unit normal of <paramref name="wall"/> pointing into <paramref name="room"/>.</summary>
    public static (double X, double Y) Inward(Line wall, Sector room)
    {
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return (0, 1);
        // The front is on the right of the wall's direction.
        return ReferenceEquals(wall.Front.Sector, room) ? (dy / length, -dx / length) : (-dy / length, dx / length);
    }

    /// <summary>How far the room reaches from <paramref name="wall"/> along its inward normal (its farthest corner).</summary>
    public static double Reach(MapGeometry geometry, Sector room, Line wall)
    {
        var n = Inward(wall, room);
        var p0 = wall.Start;
        var reach = 0.0;
        foreach (var loop in geometry.Loops(room))
        {
            foreach (var p in loop) reach = Math.Max(reach, (p.X - p0.X) * n.X + (p.Y - p0.Y) * n.Y);
        }

        return reach;
    }

    /// <summary>
    /// The plane that keeps the floor (or ceiling) at <paramref name="low"/> along <paramref name="wall"/> and reaches
    /// <paramref name="far"/> at the room's farthest point from it; null when the room has no reach from that wall.
    /// </summary>
    public static SlopePlane? Ramp(MapGeometry geometry, Sector room, Line wall, double low, double far, bool ceiling)
    {
        var reach = Reach(geometry, room, wall);
        if (reach < 1) return null;
        return SlopePlane.Rising(new MapPoint(wall.Start.X, wall.Start.Y), Inward(wall, room), low, (far - low) / reach, ceiling);
    }

    /// <summary>
    /// What a plane means for the room: the wall it is level along where the room's own height holds (the nearest to it
    /// when none is exact), and the height it reaches at the far side; null when the plane is level along no wall.
    /// </summary>
    public static (Line Wall, double Far)? Recognise(MapGeometry geometry, Sector room, SlopePlane plane, bool ceiling)
    {
        var height = ceiling ? room.CeilingHeight : room.FloorHeight;
        (Line Wall, double Off)? best = null;
        foreach (var (wall, _) in Walls(geometry, room))
        {
            var (zs, ze) = (plane.Z(wall.Start.X, wall.Start.Y), plane.Z(wall.End.X, wall.End.Y));
            if (Math.Abs(zs - ze) > 0.05) continue;
            var off = Math.Abs(zs - height);
            if (best is null || off < best.Value.Off) best = (wall, off);
        }

        if (best is not { } found) return null;
        var n = Inward(found.Wall, room);
        var farthest = (X: found.Wall.Start.X, Y: found.Wall.Start.Y);
        var reach = double.MinValue;
        foreach (var loop in geometry.Loops(room))
        {
            foreach (var p in loop)
            {
                var along = (p.X - found.Wall.Start.X) * n.X + (p.Y - found.Wall.Start.Y) * n.Y;
                if (along > reach) (reach, farthest) = (along, (p.X, p.Y));
            }
        }

        return (found.Wall, Math.Round(plane.Z(farthest.X, farthest.Y), 2));
    }

    /// <summary>Writes the plane's fields (or takes them away for null), one step of an edit.</summary>
    public static void Set(StructureEdit e, Sector room, SlopePlane? plane, bool ceiling)
    {
        if (plane is { } p)
        {
            foreach (var (name, value) in p.Fields(ceiling)) e.Field(room, name, value);
        }
        else
        {
            foreach (var name in SlopePlane.FieldNames(ceiling)) e.Field(room, name, null);
        }
    }
}
