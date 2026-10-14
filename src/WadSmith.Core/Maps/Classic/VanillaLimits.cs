namespace WadSmith.Core.Maps.Classic;

/// <summary>Vanilla's fixed tables a view can overflow (6b.4).</summary>
public enum VanillaTable
{
    /// <summary>Floor and ceiling planes: past 128 the game stops ("R_FindPlane: no more visplanes").</summary>
    Visplanes,

    /// <summary>Wall pieces: past 256 the rest go undrawn.</summary>
    WallPieces,

    /// <summary>Objects drawn: past 128 the rest vanish.</summary>
    Sprites,

    /// <summary>The columns kept for objects drawn over walls: past 20,480 the game writes over its own memory.</summary>
    Openings,
}

/// <summary>The worst view found from a room that overflows a table: how many it holds, from where and facing which way.</summary>
public sealed record VanillaOverflow(VanillaTable Table, int Count, int Most, int Room, double X, double Y, double Degrees);

/// <summary>
/// Where a player's view overflows vanilla's tables (6b.4): from every player start, and from points spread across every room
/// a player can stand in, each looking eight ways round, counted by <see cref="VanillaView"/>; the worst view per table and room.
/// </summary>
public static class VanillaLimits
{
    /// <param name="built">A map whose nodes are built.</param>
    /// <param name="spacing">The distance between the points tried across rooms.</param>
    /// <param name="perRoom">The most points tried in one room.</param>
    /// <param name="cancel">Stops the sweep when a newer map is waiting.</param>
    public static IReadOnlyList<VanillaOverflow> Find(ClassicMap built, double spacing = 96, int perRoom = 3, CancellationToken cancel = default)
    {
        var view = new VanillaView(built);
        var worst = new Dictionary<(VanillaTable, int), VanillaOverflow>();
        foreach (var (x, y, room) in Points(built, view, spacing, perRoom))
        {
            for (var degrees = 0; degrees < 360; degrees += 45)
            {
                cancel.ThrowIfCancellationRequested();
                var load = view.Count(x, y, degrees);
                Note(VanillaTable.Visplanes, load.Visplanes, VanillaView.MostVisplanes);
                Note(VanillaTable.WallPieces, load.WallPieces, VanillaView.MostWallPieces);
                Note(VanillaTable.Sprites, load.Sprites, VanillaView.MostSprites);
                Note(VanillaTable.Openings, load.Openings, VanillaView.MostOpenings);

                void Note(VanillaTable table, int count, int most)
                {
                    if (count <= most || (worst.TryGetValue((table, room), out var had) && had.Count >= count)) return;
                    worst[(table, room)] = new VanillaOverflow(table, count, most, room, x, y, degrees);
                }
            }
        }

        return [.. worst.Values.OrderBy(o => o.Table).ThenByDescending(o => o.Count)];
    }

    /// <summary>The player starts, then up to <paramref name="perRoom"/> points spread over each room a player fits in.</summary>
    private static IEnumerable<(double X, double Y, int Room)> Points(ClassicMap map, VanillaView view, double spacing, int perRoom)
    {
        foreach (var t in map.Things.Where(t => t.Type is >= 1 and <= 4))
        {
            var room = view.RoomAt(t.X, t.Y);
            if (room >= 0) yield return (t.X, t.Y, room);
        }

        if (map.Vertices.Count == 0) yield break;
        var walls = Walls(map);
        double minX = map.Vertices.Min(v => v.X), maxX = map.Vertices.Max(v => v.X), minY = map.Vertices.Min(v => v.Y), maxY = map.Vertices.Max(v => v.Y);
        var inside = new Dictionary<int, List<(double X, double Y)>>();
        for (var x = minX + spacing / 2; x < maxX; x += spacing)
        {
            for (var y = minY + spacing / 2; y < maxY; y += spacing)
            {
                var room = view.RoomAt(x, y);
                if (room < 0 || !view.Standable(room) || !walls.TryGetValue(room, out var around) || !Inside(around, x, y)) continue;
                if (!inside.TryGetValue(room, out var list)) inside[room] = list = [];
                list.Add((x, y));
            }
        }

        foreach (var (room, list) in inside)
        {
            var take = Math.Min(perRoom, list.Count);
            for (var i = 0; i < take; i++)
            {
                var (x, y) = list[(int)((i + 0.5) * list.Count / take)];
                yield return (x, y, room);
            }
        }
    }

    /// <summary>Each room's walls: those with the room on one side only.</summary>
    private static Dictionary<int, List<(double X1, double Y1, double X2, double Y2)>> Walls(ClassicMap map)
    {
        var walls = new Dictionary<int, List<(double, double, double, double)>>();
        foreach (var l in map.Lines)
        {
            if (l.V1 >= map.Vertices.Count || l.V2 >= map.Vertices.Count) continue;
            var front = l.Front < map.Sides.Count ? map.Sides[l.Front].Sector : -1;
            var back = l.Back != ClassicLine.NoSide && l.Back < map.Sides.Count ? map.Sides[l.Back].Sector : -1;
            if (front == back) continue;
            var (a, b) = (map.Vertices[l.V1], map.Vertices[l.V2]);
            foreach (var room in new[] { front, back })
            {
                if (room < 0) continue;
                if (!walls.TryGetValue(room, out var list)) walls[room] = list = [];
                list.Add((a.X, a.Y, b.X, b.Y));
            }
        }

        return walls;
    }

    /// <summary>Whether a point is inside the walls round it (even–odd), which the nodes alone cannot say for a point in the void.</summary>
    private static bool Inside(List<(double X1, double Y1, double X2, double Y2)> walls, double x, double y)
    {
        var inside = false;
        foreach (var (x1, y1, x2, y2) in walls)
        {
            if ((y1 > y) != (y2 > y) && x < x1 + (y - y1) * (x2 - x1) / (y2 - y1)) inside = !inside;
        }

        return inside;
    }
}
