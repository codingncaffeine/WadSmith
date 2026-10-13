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

    /// <summary>
    /// Ways round (the study: three loops at the community's median, ours had none): every map has at least its size's few
    /// loops, each closing a real cycle (its rooms were already joined without it, in the same part); every shortcut runs
    /// from a room of a later part back to a room of the part just before, through a plain door in the earlier room's wall.
    /// </summary>
    [Fact]
    public void EveryMapHasItsLoopsAndShortcutsOnlyLeadBack()
    {
        var shortcuts = 0;
        foreach (var plan in Sweep())
        {
            var seed = plan.Settings.Seed;
            var loops = plan.Links.Where(l => l.Loop && l.OneWayFrom < 0).ToList();
            Assert.True(loops.Count >= Planner.LoopsFor(plan.Settings.Size), $"{seed}: {loops.Count} loops, fewer than {Planner.LoopsFor(plan.Settings.Size)}");
            foreach (var loop in loops)
            {
                Assert.Equal(plan.Rooms[loop.A].Zone, plan.Rooms[loop.B].Zone);
                Assert.True(Joined(plan, loop), $"{seed}: the loop {loop.A}–{loop.B} closes no cycle");
            }

            foreach (var cut in plan.Links.Where(l => l.OneWayFrom >= 0))
            {
                shortcuts++;
                var (later, earlier) = (plan.Rooms[cut.OneWayFrom], plan.Rooms[cut.OneWayFrom == cut.A ? cut.B : cut.A]);
                Assert.Equal(later.Zone - 1, earlier.Zone);
                Assert.Equal((LinkKind.Door, (KeyKind?)null, earlier.Index), (cut.Kind, cut.Key, cut.DoorAt));
                Assert.True(Joined(plan, cut), $"{seed}: the shortcut {cut.A}–{cut.B} closes no cycle");
            }
        }

        Assert.True(shortcuts > 0, "no shortcut in the whole sweep");
    }

    /// <summary>
    /// A shortcut's door opens from the later part's side only: in the built map every wall within the doorway that opens
    /// it lies on that side of the doorway's middle, and there is one.
    /// </summary>
    [Fact]
    public void AShortcutDoorOpensFromTheLaterSideOnly()
    {
        var checkedDoors = 0;
        foreach (var plan in Sweep().Where(p => p.Links.Any(l => l.OneWayFrom >= 0)).Take(6))
        {
            var made = MapMaker.Make(plan.Settings);
            Assert.Empty(made.Failed);
            foreach (var cut in made.Plan.Links.Where(l => l.OneWayFrom >= 0))
            {
                var earlier = made.Plan.Rooms[cut.DoorAt];
                var (mx, my) = Planner.Mouth(cut, earlier);
                var inward = (X: Math.Sign(earlier.Box.CentreX - mx) * (cut.AlongX ? 1 : 0), Y: Math.Sign(earlier.Box.CentreY - my) * (cut.AlongX ? 0 : 1));
                var openers = made.Map.Lines.Where(l => l.OnMap && l.Special != 0)
                    .Select(l => (X: (l.Start.X + l.End.X) / 2, Y: (l.Start.Y + l.End.Y) / 2))
                    .Where(p => Math.Abs(p.X - mx) <= (cut.AlongX ? 40 : 72) && Math.Abs(p.Y - my) <= (cut.AlongX ? 72 : 40)).ToList();
                Assert.NotEmpty(openers);
                Assert.All(openers, p => Assert.True((p.X - mx) * inward.X + (p.Y - my) * inward.Y < 0, $"{plan.Settings.Seed}: the shortcut door opens from the earlier room at ({p.X}, {p.Y})"));
                checkedDoors++;
            }
        }

        Assert.True(checkedDoors > 0);
    }


    /// <summary>
    /// Windows (the study: about 140 see-through walls a map, ours had none): every map has at least one; each looks between
    /// two rooms the player reaches (neither a secret nor a closet) through a pane no one steps through (sill 32 or more
    /// above both floors, 40 or more of opening, under both nooks' ceilings); its nook starts at the first room's wall and
    /// the pane or the far nook ends at the other's; nothing of it overlaps a room, a closet, a corridor or another window.
    /// </summary>
    [Fact]
    public void EveryMapHasAWindowNoOneStepsThrough()
    {
        foreach (var plan in Sweep())
        {
            var seed = plan.Settings.Seed;
            Assert.True(plan.Windows.Count >= 1, $"{seed}: no window");
            var solid = plan.Rooms.Select(r => r.Box).Concat(plan.Links.Select(l => l.Box)).ToList();
            var parts = new List<Box>();
            foreach (var w in plan.Windows)
            {
                var (a, b) = (plan.Rooms[w.From], plan.Rooms[w.To]);
                Assert.DoesNotContain(a.Role, new[] { RoomRole.Secret, RoomRole.Closet });
                Assert.DoesNotContain(b.Role, new[] { RoomRole.Secret, RoomRole.Closet });
                Assert.True(w.Sill >= Math.Max(a.Floor, b.Floor) + 32, $"{seed}: a sill at {w.Sill} can be stepped onto");
                Assert.True(w.Top - w.Sill >= 40 && w.Top <= w.NookCeiling - 16 && (w.FarNook is null ? w.Top <= b.Ceiling - 16 : w.Top <= w.FarCeiling - 16), $"{seed}: the pane {w.Sill}–{w.Top} does not fit its nooks");
                bool Touches(Box p, Box r) => p.X0 == r.X1 || p.X1 == r.X0 || p.Y0 == r.Y1 || p.Y1 == r.Y0;
                Assert.True(Touches(w.Nook, a.Box), $"{seed}: the nook does not start at {a.Name}'s wall");
                Assert.True(Touches(w.FarNook ?? w.Pane, b.Box), $"{seed}: the window does not reach {b.Name}'s wall");
                foreach (var part in new[] { w.Nook, w.Pane }.Concat(w.FarNook is { } f ? [f] : []))
                {
                    bool Overlaps(Box o) => part.X0 < o.X1 && o.X0 < part.X1 && part.Y0 < o.Y1 && o.Y0 < part.Y1;
                    Assert.False(solid.Any(Overlaps), $"{seed}: a window's part {part} overlaps a room or corridor");
                    Assert.False(parts.Any(Overlaps), $"{seed}: two windows overlap at {part}");
                    parts.Add(part);
                }
            }
        }
    }

    /// <summary>In the built map a window's pane is an area at its sill and top, open on both sides (its walls two-sided).</summary>
    [Fact]
    public void AWindowIsBuiltAsAPaneOpenOnBothSides()
    {
        foreach (var plan in Sweep().Where((_, i) => i % 9 == 0))
        {
            var made = MapMaker.Make(plan.Settings);
            Assert.Empty(made.Failed);
            var g = Editor.Geometry.MapGeometry.Build(made.Map.Frame);
            foreach (var w in made.Plan.Windows)
            {
                var pane = g.SectorAt(new(w.Pane.CentreX, w.Pane.CentreY));
                Assert.NotNull(pane);
                Assert.Equal((w.Sill, w.Top), (pane!.FloorHeight, pane.CeilingHeight));
                var walls = made.Map.Lines.Where(l => l.OnMap && (l.Front.Sector == pane || l.Back?.Sector == pane)).ToList();
                Assert.Equal(2, walls.Count(l => l.Back is not null && Math.Abs(l.Length - (w.Pane.Width == 16 ? w.Pane.Height : w.Pane.Width)) < 1));
            }
        }
    }

    /// <summary>
    /// Height (the study: ledge edges, drops and a floor range our flat maps lacked): every map has a balcony or a drop;
    /// a balcony lies along its room's wall inside the room, 64 to 128 up with 96 of headroom, with stairs inside the room
    /// against it, long enough for steps the player climbs, clear of pools, columns and the dais, and with someone on it
    /// to shoot from it; a drop runs at the higher room's floor into a room tall enough to drop into; and the floors spread
    /// as each size's median at least.
    /// </summary>
    [Fact]
    public void EveryMapHasHeightToBeAboveTheEnemyOrToDropFrom()
    {
        var ranges = new Dictionary<MapSize, List<double>>();
        foreach (var plan in Sweep())
        {
            var seed = plan.Settings.Seed;
            Assert.True(plan.Rooms.Any(r => r.Ledge is not null) || plan.Links.Any(l => l.Kind == LinkKind.Drop), $"{seed}: no balcony and no drop");
            foreach (var room in plan.Rooms.Where(r => r.Ledge is not null))
            {
                var (b, ledge, stairs) = (room.Box, room.Ledge!.Value, room.LedgeStairs!.Value);
                bool Inside(Box x) => x.X0 >= b.X0 && x.X1 <= b.X1 && x.Y0 >= b.Y0 && x.Y1 <= b.Y1;
                bool Touch(Box x, Box y) => (x.X0 == y.X1 || x.X1 == y.X0) && x.Y0 < y.Y1 && y.Y0 < x.Y1 || (x.Y0 == y.Y1 || x.Y1 == y.Y0) && x.X0 < y.X1 && y.X0 < x.X1;
                Assert.True(Inside(ledge) && Inside(stairs), $"{seed} {room.Name}: the balcony or its stairs leave the room");
                Assert.True(room.LedgeSide switch { "north" => ledge.Y1 == b.Y1, "south" => ledge.Y0 == b.Y0, "east" => ledge.X1 == b.X1, _ => ledge.X0 == b.X0 }, $"{seed} {room.Name}: the balcony is not against its wall");
                Assert.True(Touch(stairs, ledge), $"{seed} {room.Name}: the stairs do not reach the balcony");
                Assert.True(room.LedgeRise is >= 64 and <= 128 && room.Ceiling - room.Floor - room.LedgeRise >= 96, $"{seed} {room.Name}: a balcony {room.LedgeRise} up under a ceiling {room.Ceiling - room.Floor} up");
                var steps = Math.Ceiling(room.LedgeRise / Editor.Smart.Stairs.ComfortRise) - 1;
                Assert.True((room.LedgeSide is "north" or "south" ? stairs.Height : stairs.Width) >= steps * Editor.Smart.Stairs.MinDepth, $"{seed} {room.Name}: the stairs are too short for their steps");
                foreach (var x in new[] { ledge, stairs })
                {
                    Assert.DoesNotContain(room.Pools, p => x.X0 < p.Box.X1 && p.Box.X0 < x.X1 && x.Y0 < p.Box.Y1 && p.Box.Y0 < x.Y1);
                    Assert.DoesNotContain(room.Pillars, p => x.X0 < p.X1 && p.X0 < x.X1 && x.Y0 < p.Y1 && p.Y0 < x.Y1);
                }

                Assert.Contains(plan.Things, t => t.Monster is not null && t.Room == room.Index && ledge.Contains(t.X, t.Y));
            }

            foreach (var drop in plan.Links.Where(l => l.Kind == LinkKind.Drop))
            {
                var (a, c) = (plan.Rooms[drop.A], plan.Rooms[drop.B]);
                var (low, high) = a.Floor < c.Floor ? (a, c) : (c, a);
                Assert.Equal(high.Floor, drop.Floor);
                Assert.True(high.Floor - low.Floor >= 48 && low.Ceiling >= drop.Floor + 96, $"{seed}: a drop from {high.Floor} into a room {low.Floor}–{low.Ceiling}");
            }

            var floors = plan.Rooms.Where(r => r.Role != RoomRole.Closet).Select(r => r.Floor + r.LedgeRise).Concat(plan.Rooms.Select(r => r.Floor)).ToList();
            (ranges.TryGetValue(plan.Settings.Size, out var list) ? list : ranges[plan.Settings.Size] = []).Add(floors.Max() - floors.Min());
        }

        foreach (var (size, least) in new[] { (MapSize.Small, 160.0), (MapSize.Medium, 224), (MapSize.Large, 320) })
        {
            var sorted = ranges[size].Order().ToList();
            Assert.True(sorted[sorted.Count / 2] >= least, $"{size}: floors span {sorted[sorted.Count / 2]} at the median, under {least}");
        }
    }

    /// <summary>In the built map a balcony stands at its rise above the room, and its stairs climb between the two.</summary>
    [Fact]
    public void ABalconyIsBuiltWithStairsUpToIt()
    {
        var seen = 0;
        foreach (var plan in Sweep().Where((_, i) => i % 7 == 0))
        {
            var made = MapMaker.Make(plan.Settings);
            Assert.Empty(made.Failed);
            var g = Editor.Geometry.MapGeometry.Build(made.Map.Frame);
            foreach (var room in made.Plan.Rooms.Where(r => r.Ledge is not null))
            {
                var (ledge, stairs) = (room.Ledge!.Value, room.LedgeStairs!.Value);
                Assert.Equal(room.Floor + room.LedgeRise, g.SectorAt(new(ledge.CentreX, ledge.CentreY))!.FloorHeight);
                var step = g.SectorAt(new(stairs.CentreX, stairs.CentreY))!.FloorHeight;
                Assert.True(step > room.Floor && step < room.Floor + room.LedgeRise, $"{plan.Settings.Seed} {room.Name}: the middle step is at {step}, the floor {room.Floor}, the balcony {room.Floor + room.LedgeRise}");
                seen++;
            }
        }

        Assert.True(seen > 0);
    }

    /// <summary>
    /// Events (the study: 38 walk-over events a map and 18% of the monsters released by one; ours 5 and none, closets
    /// aside): every key on a dais springs a trap — a closet in its room opened from each edge of the dais, so whichever
    /// way the player steps up — and across the sweep at least a tenth of the monsters are released by an event and at
    /// least three in ten wait in ambush.
    /// </summary>
    [Fact]
    public void TheKeySpringsATrapAndMonstersWaitAndAreReleased()
    {
        var (monsters, released, waiting) = (0, 0, 0);
        foreach (var plan in Sweep())
        {
            foreach (var key in plan.Rooms.Where(r => r.Key is not null && r.Dais is not null))
            {
                var d = key.Dais!.Value;
                var traps = plan.Rooms.Where(c => c.ClosetOf == key.Index).ToList();
                Assert.True(traps.Count > 0, $"{plan.Settings.Seed} {key.Name}: the key springs nothing");
                foreach (var trap in traps)
                {
                    Assert.Equal(4, trap.Triggers.Count);
                    Assert.All(trap.Triggers, t => Assert.True(((t.X == d.X0 || t.X == d.X1) && t.Y > d.Y0 && t.Y < d.Y1) || ((t.Y == d.Y0 || t.Y == d.Y1) && t.X > d.X0 && t.X < d.X1), $"{plan.Settings.Seed}: a trigger off the dais at {t}"));
                }
            }

            foreach (var m in plan.Things.Where(t => t.Monster is not null))
            {
                monsters++;
                if (plan.Rooms[m.Room].Role == RoomRole.Closet) released++;
                if (m.Ambush) waiting++;
            }
        }

        Assert.True(released >= monsters / 10, $"{released} of {monsters} monsters released by an event");
        Assert.True(waiting >= monsters * 3 / 10, $"{waiting} of {monsters} monsters in ambush");
    }

    /// <summary>
    /// Light with a source (the study: 20 light fixtures and 21 areas with a light effect a map; ours 4 and none): every
    /// map has fixtures and light effects; each patch of light holds one of the game's lamps or torches, is brighter
    /// than its room, lies inside it against a wall and keeps off its pools, columns, dais and balcony.
    /// </summary>
    [Fact]
    public void LightComesFromSomewhere()
    {
        var (maps, lit, flickering) = (0, 0, 0);
        foreach (var plan in Sweep())
        {
            maps++;
            var palette = FamilyPalette.For(plan.Settings.Family)!;
            if (plan.Rooms.Any(r => r.LightPools.Count > 0)) lit++;
            if (plan.Rooms.Any(r => r.Effect != 0 || r.LightPools.Any(p => p.Effect != 0) || r.Pools.Any(p => p.Effect != 0))) flickering++;
            foreach (var room in plan.Rooms)
            {
                var b = room.Box;
                foreach (var (patch, light, _) in room.LightPools)
                {
                    Assert.True(light > room.Light, $"{plan.Settings.Seed} {room.Name}: a patch no brighter than its room");
                    Assert.True(patch.X0 >= b.X0 && patch.X1 <= b.X1 && patch.Y0 >= b.Y0 && patch.Y1 <= b.Y1 && (patch.X0 == b.X0 || patch.X1 == b.X1 || patch.Y0 == b.Y0 || patch.Y1 == b.Y1), $"{plan.Settings.Seed} {room.Name}: a patch off its room's walls");
                    Assert.Contains(plan.Things, t => t.Room == room.Index && t.Role == ThingRole.Decoration && palette.Lights.Concat(palette.Torches).Contains(t.Number) && patch.Contains(t.X, t.Y));
                    var others = room.Pools.Select(p => p.Box).Concat(room.Pillars).Concat(new[] { room.Dais, room.Ledge, room.LedgeStairs }.OfType<Box>());
                    Assert.DoesNotContain(others, o => patch.X0 < o.X1 && o.X0 < patch.X1 && patch.Y0 < o.Y1 && o.Y0 < patch.Y1);
                }
            }
        }

        Assert.True(lit == maps && flickering == maps, $"of {maps} maps {lit} have light fixtures and {flickering} a light effect");
    }

    /// <summary>In the built map a patch of light is an area at its brightness, with its effect.</summary>
    [Fact]
    public void APatchOfLightIsBuiltBrighterWithItsEffect()
    {
        var seen = 0;
        foreach (var plan in Sweep().Where((_, i) => i % 7 == 0))
        {
            var made = MapMaker.Make(plan.Settings);
            Assert.Empty(made.Failed);
            var g = Editor.Geometry.MapGeometry.Build(made.Map.Frame);
            foreach (var (patch, light, effect) in made.Plan.Rooms.SelectMany(r => r.LightPools))
            {
                var area = g.SectorAt(new(patch.CentreX, patch.CentreY))!;
                Assert.Equal((light, effect), (area.Light, area.Special & 0xFF));
                seen++;
            }
        }

        Assert.True(seen > 0);
    }

    /// <summary>
    /// Supplies and power-ups (the study: ammunition 1.1× and health a fiftieth of the monsters' health on the community's
    /// hard sets, ours 1.6× and a twelfth; their berserk in 72% of maps, backpack 67%, megasphere 47%, a radiation suit by
    /// the nukage; ours only a soulsphere now and then): at the middle challenge the hard difficulty's ammunition is 1.1 to
    /// 1.6 times what its monsters can take and health 0.02 to 0.07 of their health, at the median of each size; a berserk
    /// and a backpack come in most maps and a megasphere in many of those with a secret; nearly every map with a hurting
    /// pool has a radiation suit; invulnerability waits only where bosses do; no power-up stands in a pool.
    /// </summary>
    [Fact]
    public void SuppliesAreTightAndPowerupsComeWhereTheyAreWanted()
    {
        var p = FamilyPalette.For("doom")!;
        var ammoOf = p.Ammo.SelectMany(a => new[] { (a.Small, a.SmallAmount * a.Damage), (a.Big, a.BigAmount * a.Damage) }).ToDictionary(x => x.Item1, x => x.Item2);
        var healthOf = new Dictionary<int, double> { [p.Health.Small] = p.Health.SmallAmount, [p.Health.Big] = p.Health.BigAmount, [p.Health.Super] = 100 };
        var (ammo, health) = (new List<double>(), new List<double>());
        var (maps, strength, pack, withSecret, boost, withHurting, suit) = (0, 0, 0, 0, 0, 0, 0);
        foreach (var plan in Sweep())
        {
            maps++;
            var hp = plan.Things.Where(t => t.Monster is not null).Sum(t => (double)t.Monster!.Health);
            var damage = p.StartDamage + plan.Things.Where(t => t.Role == ThingRole.Ammo).Sum(t => ammoOf.GetValueOrDefault(t.Number))
                + plan.Things.Where(t => t.Role == ThingRole.Weapon).Sum(t => p.Weapons.FirstOrDefault(w => w.Number == t.Number) is { } w ? w.Gives * (p.AmmoOf(w.Ammo)?.Damage ?? 0) : 0);
            ammo.Add(damage / hp);
            health.Add(plan.Things.Where(t => t.Role == ThingRole.Health).Sum(t => healthOf.GetValueOrDefault(t.Number)) / hp);
            var powers = plan.Things.Where(t => t.Role == ThingRole.Powerup).ToList();
            Assert.All(powers, t => Assert.Contains(t.Number, p.Powerups.Values));
            Assert.All(powers, t => Assert.False(plan.Rooms[t.Room].Pools.Any(pool => pool.Contains(t.X, t.Y)), $"{plan.Settings.Seed}: a power-up in a pool"));
            if (plan.Settings.Bosses == 0) Assert.DoesNotContain(powers, t => t.Number == p.Powerups["shield"]);
            if (powers.Any(t => t.Number == p.Powerups["strength"])) strength++;
            if (powers.Any(t => t.Number == p.Powerups["pack"])) pack++;
            if (plan.Rooms.Any(r => r.Role == RoomRole.Secret))
            {
                withSecret++;
                if (powers.Any(t => t.Number == p.Powerups["boost"])) boost++;
            }

            if (plan.Rooms.Any(r => r.Pools.Any(x => x.Damage > 0)))
            {
                withHurting++;
                if (powers.Any(t => t.Number == p.Powerups["suit"])) suit++;
            }
        }

        double Median(List<double> xs) => xs.Order().ElementAt(xs.Count / 2);
        Assert.InRange(Median(ammo), 1.1, 1.6);
        Assert.InRange(Median(health), 0.02, 0.07);
        Assert.True(strength >= maps * 6 / 10 && pack >= maps * 55 / 100, $"of {maps} maps {strength} have a berserk and {pack} a backpack");
        Assert.True(boost >= withSecret * 4 / 10, $"{boost} megaspheres in {withSecret} maps with a secret");
        Assert.True(suit >= withHurting * 7 / 10, $"{suit} radiation suits in {withHurting} maps with a hurting pool");
    }

    /// <summary>
    /// Variety and detail (the study: 61 wall pictures a map, 2–3 a room, a third of the walls off the square, 60% of the floor
    /// under the sky, rooms differing in size twice as much as ours): a map of medium size or more shows several looks and
    /// trims; most maps outside caves have bevelled halls (cut evenly, the middles kept straight); "some" sky puts a quarter
    /// of the open rooms or more under it; and most medium or larger maps have a great hall filling its cell.
    /// </summary>
    [Fact]
    public void MapsVaryTheirLooksShapesAndSizes()
    {
        var (squared, bevelled, open, sky, biggerMaps, greatHalls) = (0, 0, 0, 0, 0, 0);
        foreach (var plan in Sweep())
        {
            var seed = plan.Settings.Seed;
            var rooms = plan.Rooms.Where(r => r.Role is not (RoomRole.Closet or RoomRole.Secret)).ToList();
            if (plan.Settings.Size != MapSize.Small)
            {
                var pictures = rooms.Where(r => r.Look is not null).SelectMany(r => new[] { r.Look!.Look.Wall, r.Look.Look.TrimOrWall }).Distinct().Count();
                Assert.True(pictures >= 5, $"{seed}: only {pictures} wall pictures");
                biggerMaps++;
                if (rooms.Any(r => r.Role is RoomRole.Path or RoomRole.Side && Math.Min(r.Box.Width, r.Box.Height) >= 2 * (plan.MaxHalf - 32))) greatHalls++;
            }

            if (plan.Settings.Style != MapStyle.Caves)
            {
                squared++;
                if (rooms.Any(r => r.Cuts != default)) bevelled++;
                Assert.All(rooms.Where(r => r.Style != MapStyle.Caves && r.Cuts != default), r => Assert.True(r.Cuts.SW == r.Cuts.NW && r.Cuts.NW == r.Cuts.NE && r.Cuts.NE == r.Cuts.SE && Math.Min(r.Box.Width, r.Box.Height) / 2 - r.Cuts.SW >= 80, $"{seed} {r.Name}: a bevel of {r.Cuts}"));
            }

            var eligible = rooms.Where(r => r.Role is RoomRole.Path or RoomRole.Side or RoomRole.Arena or RoomRole.Start).ToList();
            (open, sky) = (open + eligible.Count, sky + eligible.Count(r => r.Outdoors));
        }

        Assert.True(bevelled >= squared * 7 / 10, $"{bevelled} of {squared} maps outside caves have bevelled halls");
        Assert.True(sky >= open / 4, $"{sky} of {open} rooms under the sky");
        Assert.True(greatHalls >= biggerMaps * 6 / 10, $"{greatHalls} of {biggerMaps} medium or larger maps have a great hall");
    }
    /// <summary>Whether the link's two rooms are joined by the other links.</summary>
    private static bool Joined(MapPlan plan, PlannedLink without)
    {
        var seen = new HashSet<int> { without.A };
        var queue = new Queue<int>([without.A]);
        while (queue.Count > 0)
        {
            var r = queue.Dequeue();
            foreach (var l in plan.Links.Where(l => l != without && (l.A == r || l.B == r)))
            {
                var o = l.A == r ? l.B : l.A;
                if (seen.Add(o)) queue.Enqueue(o);
            }
        }

        return seen.Contains(without.B);
    }
}
