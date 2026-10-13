using WadSmith.Editor.Generate;

namespace WadSmith.Tests;

/// <summary>
/// The community floor (D59): what every made map keeps, whatever its seed, size and style, from the study of ten acclaimed
/// megawads (local-notes/GENERATOR-STUDY.md). One sweep of plans over styles, sizes and seeds; each slice of 8b.10–8b.17 adds
/// its own floor here.
/// </summary>
public sealed class GeneratorFloorTests
{
    private static IEnumerable<MapPlan> Sweep(string family = "doom")
    {
        foreach (var style in Enum.GetValues<MapStyle>())
        {
            foreach (var size in new[] { MapSize.Small, MapSize.Medium, MapSize.Large })
            {
                for (var i = 0; i < 6; i++)
                {
                    yield return Planner.Plan(new GeneratorSettings
                    {
                        Family = family, GameId = family == "doom" ? "doom2" : family, Slot = family == "heretic" ? "E1M1" : "MAP01",
                        Seed = $"floor-{style}-{size}-{i}".ToLowerInvariant(), Size = size, Style = style,
                    });
                }
            }
        }
    }

    /// <summary>Points every 16 along a line, ends included.</summary>
    private static IEnumerable<(double X, double Y)> Along((double X, double Y) a, (double X, double Y) b)
    {
        var steps = Math.Max(1, (int)(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / 16));
        for (var k = 0; k <= steps; k++) yield return (a.X + (b.X - a.X) * k / steps, a.Y + (b.Y - a.Y) * k / steps);
    }

    /// <summary>
    /// Hazards are structure, never a patch (the user: "random small spots on the floor that have lava … looks terrible";
    /// the study: 60% of the community's pools lie against a wall, and they are long): every pool reaches a wall of its room
    /// and runs at least 160 along it; the way stays dry — from every corridor's mouth straight to the room's middle, and in
    /// front of each closet's door; no pickup stands in a pool except on an island, and every island holds one. Across the
    /// sweep every kind of hazard appears and the liquid covers a share of the floor like the community's lower quartile.
    /// </summary>
    [Fact]
    public void HazardsAreStructureAndTheWayStaysDry()
    {
        var kinds = new HashSet<PoolKind>();
        double liquid = 0, floor = 0;
        foreach (var plan in Sweep())
        {
            var seed = plan.Settings.Seed;
            foreach (var room in plan.Rooms.Where(r => r.Role != RoomRole.Closet))
            {
                var b = room.Box;
                floor += b.Width * (double)b.Height;
                foreach (var pool in room.Pools)
                {
                    kinds.Add(pool.Kind);
                    var p = pool.Box;
                    liquid += p.Width * (double)p.Height;
                    Assert.True(p.X0 == b.X0 || p.X1 == b.X1 || p.Y0 == b.Y0 || p.Y1 == b.Y1, $"{seed} {room.Name}: a {pool.Kind} at {p} touches no wall of {b}");
                    Assert.True(Math.Max(p.Width, p.Height) >= 160 && Math.Min(p.Width, p.Height) >= 64, $"{seed} {room.Name}: a {pool.Kind} {p.Width}×{p.Height} is a patch");
                    Assert.True(pool.Depth is > 0 and <= 24, $"{seed} {room.Name}: a pool sunk {pool.Depth} cannot be walked out of");
                }

                var middle = Planner.Middle(plan, room);
                foreach (var link in plan.Links.Where(l => l.A == room.Index || l.B == room.Index))
                {
                    var wet = Along(Planner.Mouth(link, room), middle).FirstOrDefault(q => room.Pools.Any(pool => pool.Contains(q.X, q.Y)));
                    Assert.True(wet == default, $"{seed} {room.Name}: the way from a corridor to the middle is under liquid at {wet}");
                }

                foreach (var closet in plan.Rooms.Where(c => c.ClosetOf == room.Index))
                {
                    var (dx, dy) = closet.ClosetDoor;
                    var inward = (X: Math.Sign(middle.X - dx) * (Math.Abs(dy - b.Y0) < 1 || Math.Abs(dy - b.Y1) < 1 ? 0 : 48), Y: Math.Sign(middle.Y - dy) * (Math.Abs(dx - b.X0) < 1 || Math.Abs(dx - b.X1) < 1 ? 0 : 48));
                    Assert.False(room.Pools.Any(pool => pool.Contains(dx + inward.X, dy + inward.Y)), $"{seed} {room.Name}: a closet opens into a pool");
                }

                foreach (var island in room.Islands)
                {
                    Assert.Contains(plan.Things, t => t.Room == room.Index && t.Role is ThingRole.Armor or ThingRole.Health && island.Contains(t.X, t.Y));
                }
            }

            foreach (var t in plan.Things.Where(t => t.Monster is null && t.Room >= 0))
            {
                var room = plan.Rooms[t.Room];
                if (room.Islands.Any(i => i.Contains(t.X, t.Y))) continue;
                Assert.False(room.Pools.Any(pool => pool.Contains(t.X, t.Y)), $"{seed} {room.Name}: a {t.Role} stands in a pool at ({t.X}, {t.Y})");
            }
        }

        Assert.Equal(Enum.GetValues<PoolKind>().ToHashSet(), kinds);
        Assert.True(liquid / floor >= 0.04, $"liquid covers {liquid / floor:P1} of the rooms' floor");
    }
}
