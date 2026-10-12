using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Rooms as wholes: joining rooms into one, and turning a room drawn inside another (an island) into a
/// pillar, a pool or a platform.
/// </summary>
public static class Rooms
{
    /// <summary>How far a pool's water lies below the room round it, and how far a platform rises (the most the player steps up).</summary>
    public const double PoolDepth = 16;
    public const double PlatformRise = 24;

    /// <summary>
    /// Joins <paramref name="rooms"/> into the first through <paramref name="edit"/>: the others' walls and things
    /// become its own, the walls between them go, and a corner left in the middle of a straight wall goes too.
    /// Rooms that do not touch it still become part of it (one area in two places, as the game allows).
    /// Returns what to say, if anything.
    /// </summary>
    public static IReadOnlyList<string> Join(StructureEdit edit, MapGeometry geometry, IReadOnlyList<Sector> rooms)
    {
        if (geometry.Version != edit.Map.Version) throw new InvalidOperationException("The map's shapes are behind the map; join once they have caught up.");
        var keep = rooms[0];
        var loose = new HashSet<Vertex>(ReferenceEqualityComparer.Instance);
        var apart = 0;
        foreach (var gone in rooms.Skip(1).Where(r => r.OnMap && !ReferenceEquals(r, keep)).Distinct())
        {
            var touches = false;
            foreach (var w in Deleting.WallsOf(geometry, gone))
            {
                if (ReferenceEquals(w.Front.Sector, gone)) edit.Set(w.Front, s => s.Sector, (s, v) => s.Sector = v, keep);
                if (w.Back is { } b && ReferenceEquals(b.Sector, gone)) edit.Set(b, s => s.Sector, (s, v) => s.Sector = v, keep);
                if (!w.OnMap || w.Back is null || !ReferenceEquals(w.Front.Sector, keep) || !ReferenceEquals(w.Back.Sector, keep)) continue;
                touches = true;
                Deleting.RemoveWall(edit, w, loose);
            }

            if (!touches) apart++;
            edit.Remove(gone);
        }

        // Corners the joins left in the middle of a straight wall, or alone.
        var rerouted = new List<Line>();
        List<Line> WallsAt(Vertex v) => [.. geometry.LinesIn(v.X, v.Y, v.X, v.Y).Concat(rerouted).Distinct().Where(l => l.OnMap && (l.Start == v || l.End == v))];
        foreach (var v in loose.Where(v => v.OnMap).ToList())
        {
            var walls = WallsAt(v);
            if (walls.Count == 0) edit.Remove(v);
            else if (walls.Count == 2 && Straight(v, walls[0], walls[1]) && Deleting.Straighten(edit, v, walls[0], walls[1])) rerouted.Add(walls[0]);
        }

        return apart == 0 ? [] : [apart == 1
            ? "One of the rooms does not touch the first: it is part of it all the same, one area in two places."
            : $"{apart} of the rooms do not touch the first: they are part of it all the same, one area in several places."];
    }

    /// <summary>Whether the walls meeting at <paramref name="v"/> run on in one straight line.</summary>
    private static bool Straight(Vertex v, Line a, Line b)
    {
        var p = a.Start == v ? a.End : a.Start;
        var q = b.Start == v ? b.End : b.Start;
        var cross = (v.X - p.X) * (q.Y - v.Y) - (v.Y - p.Y) * (q.X - v.X);
        var dot = (v.X - p.X) * (q.X - v.X) + (v.Y - p.Y) * (q.Y - v.Y);
        return Math.Abs(cross) <= 1e-6 * a.Length * b.Length && dot > 0;
    }

    /// <summary>The room round <paramref name="room"/> when it is an island in one: every wall of it is shared with that room alone.</summary>
    public static Sector? Island(MapGeometry geometry, Sector room)
    {
        if (!room.OnMap) return null;
        var walls = Deleting.WallsOf(geometry, room);
        if (walls.Count == 0 || walls.Any(l => l.Back is null)) return null;
        var round = walls.Select(l => ReferenceEquals(l.Front.Sector, room) ? l.Back!.Sector : l.Front.Sector).Distinct().ToList();
        return round is [var outer] && !ReferenceEquals(outer, room) ? outer : null;
    }

    /// <summary>
    /// Makes an island a pool through <paramref name="edit"/>: its floor <see cref="PoolDepth"/> below the room round it,
    /// the game's water on it, and the room's wall texture on its sides below the room's floor.
    /// </summary>
    public static void Pool(StructureEdit edit, MapGeometry geometry, Sector room, Sector outer, Look look)
    {
        edit.Set(room, s => s.FloorHeight, (s, v) => s.FloorHeight = v, outer.FloorHeight - PoolDepth);
        edit.Set(room, s => s.FloorTexture, (s, v) => s.FloorTexture = v, look.Water);
        foreach (var l in Deleting.WallsOf(geometry, room))
        {
            var inside = ReferenceEquals(l.Front.Sector, room) ? l.Front : l.Back!;
            edit.Set(inside, s => s.Bottom, (s, v) => s.Bottom = v, look.Wall);
        }
    }

    /// <summary>
    /// Makes an island a platform through <paramref name="edit"/>: its floor <see cref="PlatformRise"/> above the room
    /// round it (a step the player takes), the game's step texture on its sides.
    /// </summary>
    public static void Platform(StructureEdit edit, MapGeometry geometry, Sector room, Sector outer, Look look)
    {
        edit.Set(room, s => s.FloorHeight, (s, v) => s.FloorHeight = v, outer.FloorHeight + PlatformRise);
        edit.Set(room, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, Math.Max(room.CeilingHeight, outer.FloorHeight + PlatformRise + 56));
        foreach (var l in Deleting.WallsOf(geometry, room))
        {
            var outside = ReferenceEquals(l.Front.Sector, room) ? l.Back! : l.Front;
            edit.Set(outside, s => s.Bottom, (s, v) => s.Bottom = v, look.Step);
        }
    }
}
