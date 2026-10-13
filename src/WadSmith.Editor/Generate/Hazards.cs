namespace WadSmith.Editor.Generate;

/// <summary>
/// What a hazard is in its room (D59). The community study measured 316 maps of ten acclaimed megawads: their pools are long
/// (perimeter² / 4π·area 2.6), mostly against a wall (60%), about 270 across at the median and 750 at the 90th percentile,
/// and often crossed by a bridge — structure, never a patch in the middle of a floor.
/// </summary>
public enum PoolKind
{
    /// <summary>A channel along a wall no way comes through, from corner to corner.</summary>
    Channel,

    /// <summary>Half of a river across the room from wall to wall, the other half beyond the dry bridge on the room's middle line.</summary>
    River,

    /// <summary>A quarter of a flooded hall: liquid from the walls in to dry walkways along the middle lines.</summary>
    Flood,
}

/// <summary>
/// A pool sunk in a room's floor: what it is, its outline (corner by corner), a point inside it clear of pillars and islands
/// (where its heights are set), its floor picture, the damage it does every 32 tics, how far it is sunk, and its light effect.
/// </summary>
public sealed record PlannedPool(PoolKind Kind, IReadOnlyList<(int X, int Y)> Outline, (double X, double Y) Inside, string Flat, int Damage, int Depth, int Effect = 0)
{
    /// <summary>The rectangle round the outline.</summary>
    public Box Box => new(Outline.Min(p => p.X), Outline.Min(p => p.Y), Outline.Max(p => p.X), Outline.Max(p => p.Y));

    /// <summary>Whether a point lies inside the outline (even–odd).</summary>
    public bool Contains(double x, double y)
    {
        var inside = false;
        for (int i = 0, j = Outline.Count - 1; i < Outline.Count; j = i++)
        {
            var (a, b) = (Outline[i], Outline[j]);
            if ((a.Y > y) != (b.Y > y) && x < a.X + (y - a.Y) * (double)(b.X - a.X) / (b.Y - a.Y)) inside = !inside;
        }

        return inside;
    }
}

/// <summary>
/// The pools a made map may sink in its floors, per game family (water, which does no harm, and the game's own hurting floors
/// with the damage its maps give them: Doom's nukage 5 and lava 10, Heretic's sludge 4 and lava 5, Hexen's lava 5), and how a
/// room gets one: a channel along a wall with no way in, a river across the room with a bridge where the way runs, or a
/// flooded hall with dry walkways joining every way in and, in a big one, an island holding something worth the wade.
/// Pools are sunk at most 24, so a player who falls in walks out.
/// </summary>
public static class Hazards
{
    /// <summary>The pools of a family: the floor picture and the damage it does every 32 tics.</summary>
    public static IReadOnlyList<(string Flat, int Damage)> Of(string family) => family switch
    {
        "heretic" => [("FLTWAWA1", 0), ("FLTSLUD1", 4), ("FLTLAVA1", 5)],
        "hexen" => [("X_005", 0), ("X_001", 5)],
        "strife" => [("F_WATR01", 0)],
        _ => [("FWATER1", 0), ("NUKAGE1", 5), ("LAVA1", 10), ("BLOOD1", 0)],
    };

    /// <summary>The least a pool reaches along its length, and the band of floor a channel or river keeps from the room's middle lines.</summary>
    private const int MinLength = 160;
    private const int ClearOfMiddle = 112;

    /// <summary>
    /// Gives <paramref name="room"/> a hazard, when one fits: a flooded hall in a big room, a river across, or a channel along
    /// a wall without a way in, picked by weight among those that fit. <paramref name="ways"/> are the room's sides a
    /// corridor meets ("north", "south", "east", "west").
    /// </summary>
    internal static void Lay(MapPlan plan, PlannedRoom room, IReadOnlySet<string> ways, Seeded rng)
    {
        var kinds = Of(plan.Settings.Family);
        var (flat, damage) = rng.Chance(0.4) ? kinds[0] : rng.Pick(kinds);
        var forms = new List<(Func<bool> Make, int Weight)>();
        var b = room.Box;
        if (Math.Min(b.Width, b.Height) >= 640) forms.Add((() => Flood(plan, room, flat, damage, rng), room.Role == RoomRole.Arena ? 3 : 2));
        if (room.Pillars.Count == 0) forms.Add((() => River(plan, room, flat, damage, rng), 2));
        forms.Add((() => Channel(plan, room, ways, flat, damage, rng), 2));
        while (forms.Count > 0)
        {
            var roll = rng.Next(forms.Sum(f => f.Weight));
            var pick = 0;
            while (roll >= forms[pick].Weight) roll -= forms[pick++].Weight;
            if (forms[pick].Make()) return;
            forms.RemoveAt(pick);
        }
    }

    /// <summary>The cut off the corner of the room's quarter (sx, sy): +1 east or north, −1 west or south.</summary>
    private static int CutAt(PlannedRoom room, int sx, int sy) => (sx, sy) switch
    {
        (> 0, > 0) => room.Cuts.NE,
        (> 0, _) => room.Cuts.SE,
        (_, > 0) => room.Cuts.NW,
        _ => room.Cuts.SW,
    };

    private static bool Apart(Box a, Box b, int margin) => a.X1 + margin <= b.X0 || a.X0 - margin >= b.X1 || a.Y1 + margin <= b.Y0 || a.Y0 - margin >= b.Y1;

    private static bool ClearOfPillars(PlannedRoom room, Box box, int margin) => room.Pillars.All(p => Apart(box, p, margin));

    /// <summary>
    /// The whole floor sunk under liquid but for walkways along the room's two middle lines (where every corridor meets it
    /// and the dais stands), each quarter a pool from the walkways out to the walls, round any cut corner. A quarter big
    /// enough holds an island at the room's own floor height (its reward is placed with the objects).
    /// </summary>
    private static bool Flood(MapPlan plan, PlannedRoom room, string flat, int damage, Seeded rng)
    {
        var b = room.Box;
        var (cx, cy) = Planner.Middle(plan, room);
        var (mx, my) = ((int)cx, (int)cy);
        var walk = Math.Max(rng.Pick([64, 80, 96]), room.Dais is { } d ? d.Width / 2 + 32 : 0);

        // Pillars stand at least 112 from the middle lines: a walkway wider than that would run into them.
        if (room.Pillars.Count > 0 && walk > 96) return false;
        var pools = new List<PlannedPool>();
        foreach (var (sx, sy) in new[] { (1, 1), (1, -1), (-1, -1), (-1, 1) })
        {
            var (ix, iy) = (mx + sx * walk, my + sy * walk);
            var (ox, oy) = (sx > 0 ? b.X1 : b.X0, sy > 0 ? b.Y1 : b.Y0);
            var cut = CutAt(room, sx, sy);
            if (Math.Abs(ox - ix) < MinLength || Math.Abs(oy - iy) < MinLength || Math.Abs(ox - ix) <= cut + 32 || Math.Abs(oy - iy) <= cut + 32) return false;
            List<(int X, int Y)> outline = [(ix, iy), (ix, oy)];
            if (cut > 0) outline.AddRange([(ox - sx * cut, oy), (ox, oy - sy * cut)]);
            else outline.Add((ox, oy));
            outline.Add((ox, iy));
            pools.Add(new PlannedPool(PoolKind.Flood, outline, (ix + sx * 8, iy + sy * 8), flat, damage, 16));
        }

        room.Pools.AddRange(pools);

        // An island in one quarter that has room for it: a square at the room's floor, clear of pillars and the cut corner.
        var quarters = new List<(int X, int Y)> { (1, 1), (1, -1), (-1, -1), (-1, 1) };
        rng.Shuffle(quarters);
        foreach (var (sx, sy) in quarters)
        {
            var (ix, iy) = (mx + sx * walk, my + sy * walk);
            var (ox, oy) = (sx > 0 ? b.X1 : b.X0, sy > 0 ? b.Y1 : b.Y0);
            if (Math.Abs(ox - ix) < 352 || Math.Abs(oy - iy) < 352) continue;
            var half = rng.Pick([48, 64]);
            var (qx, qy) = ((ix + ox) / 2, (iy + oy) / 2);
            var island = new Box(qx - half, qy - half, qx + half, qy + half);
            if (!ClearOfPillars(room, island, 48)) continue;
            if (!new[] { (island.X0, island.Y0), (island.X0, island.Y1), (island.X1, island.Y1), (island.X1, island.Y0) }.All(p => room.Holds(p.Item1, p.Item2, 32, wet: true))) continue;
            room.Islands.Add(island);
            break;
        }

        return true;
    }

    /// <summary>
    /// A river across the room from wall to wall, off the middle line its corridors meet on, with a dry bridge where it
    /// crosses the other middle line (the way through): two pools, one each side of the bridge. Its ends must meet the walls
    /// where they run straight (never across a cut corner).
    /// </summary>
    private static bool River(MapPlan plan, PlannedRoom room, string flat, int damage, Seeded rng)
    {
        var b = room.Box;
        var c = room.Cuts;
        var (cx, cy) = Planner.Middle(plan, room);
        var (mx, my) = ((int)cx, (int)cy);
        var width = rng.Pick([96, 128, 160]);
        const int bridge = 64;
        var options = new List<Func<PlannedPool[]?>>();

        // Running north–south (a band in x), on the east or the west side of the middle.
        foreach (var side in new[] { 1, -1 })
        {
            options.Add(() =>
            {
                var (from, to) = side > 0 ? (mx + ClearOfMiddle, b.X1 - 96) : (b.X0 + 96, mx - ClearOfMiddle);
                if (to - from < width || my - bridge - b.Y0 < MinLength || b.Y1 - (my + bridge) < MinLength) return null;
                var x0 = side > 0 ? from + rng.Next((to - from - width) / 16 + 1) * 16 : to - width - rng.Next((to - from - width) / 16 + 1) * 16;
                var x1 = x0 + width;
                if (x0 < b.X0 + Math.Max(c.NW, c.SW) || x1 > b.X1 - Math.Max(c.NE, c.SE)) return null;
                return
                [
                    new PlannedPool(PoolKind.River, [(x0, b.Y0), (x0, my - bridge), (x1, my - bridge), (x1, b.Y0)], ((x0 + x1) / 2.0, (b.Y0 + my - bridge) / 2.0), flat, damage, 24),
                    new PlannedPool(PoolKind.River, [(x0, my + bridge), (x0, b.Y1), (x1, b.Y1), (x1, my + bridge)], ((x0 + x1) / 2.0, (my + bridge + b.Y1) / 2.0), flat, damage, 24),
                ];
            });

            // Running east–west (a band in y), north or south of the middle.
            options.Add(() =>
            {
                var (from, to) = side > 0 ? (my + ClearOfMiddle, b.Y1 - 96) : (b.Y0 + 96, my - ClearOfMiddle);
                if (to - from < width || mx - bridge - b.X0 < MinLength || b.X1 - (mx + bridge) < MinLength) return null;
                var y0 = side > 0 ? from + rng.Next((to - from - width) / 16 + 1) * 16 : to - width - rng.Next((to - from - width) / 16 + 1) * 16;
                var y1 = y0 + width;
                if (y0 < b.Y0 + Math.Max(c.SW, c.SE) || y1 > b.Y1 - Math.Max(c.NW, c.NE)) return null;
                return
                [
                    new PlannedPool(PoolKind.River, [(b.X0, y0), (b.X0, y1), (mx - bridge, y1), (mx - bridge, y0)], ((b.X0 + mx - bridge) / 2.0, (y0 + y1) / 2.0), flat, damage, 24),
                    new PlannedPool(PoolKind.River, [(mx + bridge, y0), (mx + bridge, y1), (b.X1, y1), (b.X1, y0)], ((mx + bridge + b.X1) / 2.0, (y0 + y1) / 2.0), flat, damage, 24),
                ];
            });
        }

        rng.Shuffle(options);
        foreach (var option in options)
        {
            if (option() is not { } river) continue;
            room.Pools.AddRange(river);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A channel along a side of the room no corridor meets, from corner to corner of its straight run, 64 to 128 wide and
    /// clear of pillars; sometimes the opposite side too.
    /// </summary>
    private static bool Channel(MapPlan plan, PlannedRoom room, IReadOnlySet<string> ways, string flat, int damage, Seeded rng)
    {
        var b = room.Box;
        var c = room.Cuts;
        var (cx, cy) = Planner.Middle(plan, room);
        var (mx, my) = ((int)cx, (int)cy);
        var sides = new List<string> { "north", "south", "east", "west" }.Where(s => !ways.Contains(s)).ToList();
        rng.Shuffle(sides);
        var made = 0;
        foreach (var side in sides)
        {
            foreach (var width in new[] { rng.Pick([96, 128]), 64 })
            {
                (int X0, int Y0, int X1, int Y1) r = side switch
                {
                    "north" => (b.X0 + c.NW, b.Y1 - width, b.X1 - c.NE, b.Y1),
                    "south" => (b.X0 + c.SW, b.Y0, b.X1 - c.SE, b.Y0 + width),
                    "east" => (b.X1 - width, b.Y0 + c.SE, b.X1, b.Y1 - c.NE),
                    _ => (b.X0, b.Y0 + c.SW, b.X0 + width, b.Y1 - c.NW),
                };
                var length = side is "north" or "south" ? r.X1 - r.X0 : r.Y1 - r.Y0;
                var clear = side switch { "north" => r.Y0 - my, "south" => my - r.Y1, "east" => r.X0 - mx, _ => mx - r.X1 };
                var box = new Box(r.X0, r.Y0, r.X1, r.Y1);
                if (length < 256 || clear < ClearOfMiddle + 16 || !ClearOfPillars(room, box, 32)) continue;
                if (room.Dais is { } dais && !Apart(box, dais, 32)) continue;
                room.Pools.Add(new PlannedPool(PoolKind.Channel, [(r.X0, r.Y0), (r.X0, r.Y1), (r.X1, r.Y1), (r.X1, r.Y0)], (box.CentreX, box.CentreY), flat, damage, 24));
                made++;
                break;
            }

            if (made > 0 && !rng.Chance(0.35)) break;
            if (made == 2) break;
        }

        return made > 0;
    }
}
