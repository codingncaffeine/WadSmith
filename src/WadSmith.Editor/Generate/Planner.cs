namespace WadSmith.Editor.Generate;

/// <summary>
/// The level generator's plan (D50, §3.14), a pure function of the settings and the seed: a way from the start to the exit
/// laid as a self-avoiding walk over a lattice of cells, one room to a cell; locked doors on the way with each key in a side
/// room before its door; side rooms, secrets and loops (loops only between rooms with the same keys behind them, so no loop
/// passes a locked door); floors that drift, climbing by stairs or lifts; then the objects: the start, keys, weapons in the
/// game's order, monsters by a budget that grows along the way and with the challenge, bosses in an arena before the exit,
/// and enough ammunition and health for the monsters placed. Each room keeps to its cell with a band of at least 64 to its
/// edge, so rooms never touch and corridors, which run along the cells' middles, never cross a room or each other.
/// </summary>
public static class Planner
{
    private static readonly (int Di, int Dj)[] Directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>The middle of a room's cell: where its corridors' middle lines cross, which cut corners and pillars leave clear.</summary>
    public static (double X, double Y) Middle(MapPlan plan, PlannedRoom room) => (room.Cell.I * plan.Cell, room.Cell.J * plan.Cell);

    /// <summary>Plans a map; throws when the settings name a game family the generator does not know.</summary>
    public static MapPlan Plan(GeneratorSettings settings)
    {
        var palette = FamilyPalette.For(settings.Family) ?? throw new ArgumentException($"Maps cannot be made for {settings.Family} games yet.");
        var root = new Seeded(SeedWords.Tidy(settings.Seed));
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var plan = new MapPlan { Settings = settings };
            if (!Lay(plan, palette, root.Fork($"layout {attempt}"))) continue;
            Heights(plan, root.Fork("heights"));
            Shape(plan, root.Fork("shapes"));
            if (settings.Traps) Closets(plan, root.Fork("closets"));
            Dress(plan, root.Fork("looks"));
            Populate(plan, palette, root.Fork("things"));
            return plan;
        }

        throw new InvalidOperationException("No layout could be found for these settings.");
    }

    private static int KeysFor(GeneratorSettings s, FamilyPalette p) => Math.Clamp(s.Keys, 0, p.Keys.Count);

    // ── Layout ──────────────────────────────────────────────────────────────────────────────────────────

    private static bool Lay(MapPlan plan, FamilyPalette palette, Seeded rng)
    {
        var s = plan.Settings;
        var keys = KeysFor(s, palette);
        var arena = s.Bosses > 0;
        var secrets = s.Secrets ? s.Size switch { MapSize.Small => 1, MapSize.Medium => 2, MapSize.Large => 3, _ => 4 } : 0;
        var minPath = 3 + keys + (arena ? 1 : 0);
        var total = Math.Max(GeneratorSettings.RoomsFor(s.Size), minPath + keys + secrets);
        var pathLength = Math.Clamp((int)Math.Round(total * 0.55), minPath, total - keys - secrets);

        var cells = new Dictionary<(int, int), int>();
        bool Free((int I, int J) c) => !cells.ContainsKey(c);
        int FreeAround((int I, int J) c) => Directions.Count(d => Free((c.I + d.Di, c.J + d.Dj)));

        // The way: a self-avoiding walk that keeps a free cell beside each step, going on straight about a third of the time.
        var at = (I: 0, J: 0);
        var dir = rng.Next(4);
        // A mixed map changes style with each part (each locked door starts a new one).
        MapStyle[] kinds = [MapStyle.Base, MapStyle.Caves, MapStyle.Castle];
        var zoneStyles = Enumerable.Range(0, keys + 1).Select(_ => s.Style == MapStyle.Mixed ? rng.Pick(kinds) : s.Style).ToList();
        if (s.Style == MapStyle.Mixed && keys == 0) zoneStyles[0] = rng.Pick(kinds);
        PlannedRoom Add((int I, int J) cell, RoomRole role, int zone)
        {
            var room = new PlannedRoom { Index = plan.Rooms.Count, Cell = cell, Role = role, Zone = zone, Style = zoneStyles[zone] };
            plan.Rooms.Add(room);
            cells[cell] = room.Index;
            return room;
        }

        // Locked doors on the way: spread along it, after the start room and before the arena.
        var lastLockable = pathLength - (arena ? 3 : 2);
        var locks = new List<int>();
        for (var k = 0; k < keys; k++)
        {
            var edge = (int)Math.Round((k + 1) * (lastLockable + 1.0) / (keys + 1));
            edge = Math.Clamp(edge, 1, lastLockable);
            while (locks.Contains(edge) && edge < lastLockable) edge++;
            if (locks.Contains(edge)) return false;
            locks.Add(edge);
        }

        int ZoneOf(int pathIndex) => locks.Count(e => e < pathIndex);
        Add(at, RoomRole.Start, 0);
        plan.Path.Add(0);
        for (var k = 1; k < pathLength; k++)
        {
            var options = Enumerable.Range(0, 4).Where(d => Free((at.I + Directions[d].Di, at.J + Directions[d].Dj)) && FreeAround((at.I + Directions[d].Di, at.J + Directions[d].Dj)) >= 2).ToList();
            if (options.Count == 0) return false;
            if (!(options.Contains(dir) && rng.Chance(0.35))) dir = rng.Pick(options);
            at = (at.I + Directions[dir].Di, at.J + Directions[dir].Dj);
            var role = k == pathLength - 1 ? RoomRole.Exit : arena && k == pathLength - 2 ? RoomRole.Arena : RoomRole.Path;
            var room = Add(at, role, ZoneOf(k));
            Link(plan, plan.Path[^1], room.Index, rng);
            if (locks.Contains(k - 1))
            {
                var link = plan.Links[^1];
                link.Kind = LinkKind.Locked;
                link.Key = palette.Keys[locks.IndexOf(k - 1)];
                link.DoorAt = room.Index;
            }

            plan.Path.Add(room.Index);
        }

        for (var k = 0; k < plan.Path.Count; k++) plan.Rooms[plan.Path[k]].Progress = plan.Path.Count == 1 ? 0 : k / (plan.Path.Count - 1.0);

        // A branch hangs a new room off one of the candidates on a free cell beside it.
        PlannedRoom? Branch(IEnumerable<PlannedRoom> candidates, RoomRole role)
        {
            var open = candidates.Where(c => FreeAround(c.Cell) > 0).ToList();
            if (open.Count == 0) return null;
            var parent = rng.Pick(open);
            var ways = Directions.Select(d => (parent.Cell.I + d.Di, parent.Cell.J + d.Dj)).Where(c => Free(c)).ToList();
            var room = Add(rng.Pick(ways), role, parent.Zone);
            room.Progress = parent.Progress;
            Link(plan, parent.Index, room.Index, rng);
            return room;
        }

        bool Walkable(PlannedRoom r) => r.Role is RoomRole.Start or RoomRole.Path or RoomRole.Side;

        // Each key in a side room in the zone before its door.
        for (var k = 0; k < keys; k++)
        {
            var zone = k;
            if (Branch(plan.Rooms.Where(r => r.Zone == zone && Walkable(r)), RoomRole.Key) is not { } keyRoom) return false;
            keyRoom.Key = palette.Keys[k];
        }

        for (var k = 0; k < secrets; k++)
        {
            if (Branch(plan.Rooms.Where(r => Walkable(r) && r.Role != RoomRole.Start), RoomRole.Secret) is null) break;
        }

        while (plan.Rooms.Count < total)
        {
            if (Branch(plan.Rooms.Where(r => Walkable(r) || r.Role == RoomRole.Key), RoomRole.Side) is null) break;
        }

        // Loops: neighbours not yet joined, with the same keys behind them, and neither a dead end by design.
        var linked = new HashSet<(int, int)>(plan.Links.Select(l => (Math.Min(l.A, l.B), Math.Max(l.A, l.B))));
        foreach (var room in plan.Rooms.ToList())
        {
            foreach (var (di, dj) in Directions.Take(2))
            {
                if (!cells.TryGetValue((room.Cell.I + di, room.Cell.J + dj), out var other)) continue;
                var o = plan.Rooms[other];
                if (linked.Contains((Math.Min(room.Index, other), Math.Max(room.Index, other)))) continue;
                if (o.Zone != room.Zone || !Walkable(room) || !Walkable(o) || !rng.Chance(0.3)) continue;
                Link(plan, room.Index, other, rng).Loop = true;
            }
        }

        return true;
    }

    /// <summary>Sizes the rooms of a new link's ends (once each) and lays the corridor between them along their cells' middle line.</summary>
    private static PlannedLink Link(MapPlan plan, int a, int b, Seeded rng)
    {
        foreach (var r in new[] { plan.Rooms[a], plan.Rooms[b] }) Size(plan, r, rng);
        var (ra, rb) = (plan.Rooms[a], plan.Rooms[b]);
        var alongX = ra.Cell.J == rb.Cell.J;
        var (first, second) = (alongX ? ra.Cell.I < rb.Cell.I : ra.Cell.J < rb.Cell.J) ? (ra, rb) : (rb, ra);
        var narrow = ra.Role is RoomRole.Secret || rb.Role is RoomRole.Secret || ((ra.Role is RoomRole.Side or RoomRole.Key || rb.Role is RoomRole.Side or RoomRole.Key) && rng.Chance(0.5));
        var half = narrow ? 32 : 64;
        Box box;
        if (alongX)
        {
            var y = first.Cell.J * plan.Cell;
            box = new Box(first.Box.X1, y - half, second.Box.X0, y + half);
        }
        else
        {
            var x = first.Cell.I * plan.Cell;
            box = new Box(x - half, first.Box.Y1, x + half, second.Box.Y0);
        }

        var link = new PlannedLink { A = a, B = b, Box = box, AlongX = alongX, DoorAt = b };
        plan.Links.Add(link);
        return link;
    }

    /// <summary>Gives a room its rectangle round its cell's middle, once: its reach each way by what it is for and its style.</summary>
    private static void Size(MapPlan plan, PlannedRoom room, Seeded rng)
    {
        if (room.Box.Width > 0) return;
        var castle = room.Style == MapStyle.Castle;
        var (min, max) = room.Role switch
        {
            RoomRole.Start => castle ? (256, 352) : (192, 288),
            RoomRole.Key => castle ? (192, 320) : (128, 256),
            RoomRole.Secret => (96, 128),
            RoomRole.Arena => castle ? (448, plan.MaxHalf) : (384, plan.MaxHalf),
            RoomRole.Exit => castle ? (224, 320) : (160, 256),
            RoomRole.Side => castle ? (224, 448) : room.Style == MapStyle.Caves ? (192, 384) : (160, 352),
            _ => castle ? (320, plan.MaxHalf) : room.Style == MapStyle.Caves ? (224, 416) : (192, 416),
        };
        int Reach() => Math.Min(rng.Between(min / 32, max / 32) * 32, plan.MaxHalf);
        var (cx, cy) = (room.Cell.I * plan.Cell, room.Cell.J * plan.Cell);
        room.Box = new Box(cx - Reach(), cy - Reach(), cx + Reach(), cy + Reach());
    }

    // ── Shapes: caves' cut corners, castle halls' pillars ─────────────────────────────────────────────────

    /// <summary>
    /// Cuts a cave room's corners (each by its own amount, keeping every side's middle — where corridors and the exit meet
    /// it — straight for 80 either way of the cell's middle line) and stands pillars in a castle hall's floor (in rows, clear
    /// of the cell's middle lines so the way across and the room's middle stay open, 128 or more from the walls).
    /// </summary>
    private static void Shape(MapPlan plan, Seeded rng)
    {
        foreach (var room in plan.Rooms.Where(r => r.Role != RoomRole.Secret))
        {
            var b = room.Box;
            var (cx, cy) = Middle(plan, room);
            var (west, east, south, north) = ((int)(cx - b.X0), (int)(b.X1 - cx), (int)(cy - b.Y0), (int)(b.Y1 - cy));
            if (room.Style == MapStyle.Caves)
            {
                int Cut(int across, int along)
                {
                    var most = Math.Min(across, along) - 80;
                    return most < 48 ? 0 : rng.Between(3, most / 16) * 16;
                }

                room.Cuts = (Cut(west, south), Cut(west, north), Cut(east, north), Cut(east, south));
            }
            else if (room.Style == MapStyle.Castle && b.Width >= 640 && b.Height >= 640 && room.Role is not (RoomRole.Key or RoomRole.Exit))
            {
                var step = rng.Pick([192, 224, 256]);
                for (var x = b.X0 + 160; x + 64 <= b.X1 - 128; x += step)
                {
                    for (var y = b.Y0 + 160; y + 64 <= b.Y1 - 128; y += step)
                    {
                        if (Math.Abs(x + 32 - cx) < 144 || Math.Abs(y + 32 - cy) < 144) continue;
                        room.Pillars.Add(new Box(x, y, x + 64, y + 64));
                    }
                }
            }

            // A dais a step high round the room's middle: always under a key, sometimes in a hall or the arena. It keeps inside
            // the band the pools and pillars leave clear round the middle lines.
            var dais = room.Role == RoomRole.Key ? 64 : room.Role is RoomRole.Path or RoomRole.Arena && rng.Chance(0.25) ? 96 : 0;
            if (dais > 0 && Math.Min(Math.Min(west, east), Math.Min(south, north)) >= dais + 64)
            {
                room.Dais = new Box((int)cx - dais, (int)cy - dais, (int)cx + dais, (int)cy + dais);
            }

            // A pool in a quarter of a big room, clear of the room's middle lines (where the ways across run), its walls,
            // its cut corners and its pillars: water, or the game's hurting floor.
            if (plan.Settings.Hazards && room.Role is RoomRole.Path or RoomRole.Side or RoomRole.Arena && rng.Chance(0.4))
            {
                var kinds = Hazards.Of(plan.Settings.Family);
                var (flat, damage) = rng.Chance(0.4) ? kinds[0] : rng.Pick(kinds);
                List<(int X, int Y)> quarters = [(1, 1), (1, -1), (-1, 1), (-1, -1)];
                rng.Shuffle(quarters);
                foreach (var (sx, sy) in quarters)
                {
                    var (x0, x1) = sx > 0 ? (cx + 112, b.X1 - 64.0) : (b.X0 + 64.0, cx - 112);
                    var (y0, y1) = sy > 0 ? (cy + 112, b.Y1 - 64.0) : (b.Y0 + 64.0, cy - 112);
                    var width = (int)Math.Min(256, Math.Floor((x1 - x0) / 32) * 32);
                    var height = (int)Math.Min(256, Math.Floor((y1 - y0) / 32) * 32);
                    if (width < 96 || height < 96) continue;
                    var px = (int)(sx > 0 ? x0 : x1 - width);
                    var py = (int)(sy > 0 ? y0 : y1 - height);
                    var pool = new Box(px, py, px + width, py + height);
                    bool Overlaps(Box p) => !(pool.X1 <= p.X0 - 32 || pool.X0 >= p.X1 + 32 || pool.Y1 <= p.Y0 - 32 || pool.Y0 >= p.Y1 + 32);
                    var inside = new[] { (pool.X0, pool.Y0), (pool.X0, pool.Y1), (pool.X1, pool.Y1), (pool.X1, pool.Y0) }.All(p => room.Holds(p.Item1, p.Item2, 16));
                    if (!inside || room.Pillars.Any(Overlaps)) continue;
                    room.Pools.Add((pool, flat, damage));
                    break;
                }
            }
        }
    }

    // ── Closets ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Monster closets: a small room 96 deep against a side of a room that has no corridor and no exit switch, inside the
    /// room's cell (so it touches nothing else), whose door opens the first time the player walks into the room through its
    /// entrance. Only rooms whose entrance line is free for the trigger (no door, lock or lift on it) get one.
    /// </summary>
    private static void Closets(MapPlan plan, Seeded rng)
    {
        const int depth = 96;
        foreach (var room in plan.Rooms.Where(r => r.Role is RoomRole.Path or RoomRole.Side or RoomRole.Key or RoomRole.Arena).ToList())
        {
            if (room.Role != RoomRole.Arena && !rng.Chance(0.35)) continue;
            var entry = plan.Links.FirstOrDefault(l => !l.Loop && Math.Max(l.A, l.B) == room.Index);
            if (entry is null || entry.Kind == LinkKind.Lift || entry.Kind is LinkKind.Door or LinkKind.Locked && entry.DoorAt == room.Index) continue;
            var (cx, cy) = Middle(plan, room);
            var b = room.Box;
            var c = room.Cuts;

            // The sides with no corridor: each with its flat stretch (between cut corners) along it.
            var sides = new List<(string Side, int From, int To)>();
            bool Free(string side) => !plan.Links.Any(l => (l.A == room.Index || l.B == room.Index) && SideOf(l, room) == side);
            if (Free("north") && b.Y1 - cy + depth <= plan.MaxHalf) sides.Add(("north", b.X0 + c.NW, b.X1 - c.NE));
            if (Free("south") && cy - b.Y0 + depth <= plan.MaxHalf) sides.Add(("south", b.X0 + c.SW, b.X1 - c.SE));
            if (Free("east") && b.X1 - cx + depth <= plan.MaxHalf) sides.Add(("east", b.Y0 + c.SE, b.Y1 - c.NE));
            if (Free("west") && cx - b.X0 + depth <= plan.MaxHalf) sides.Add(("west", b.Y0 + c.SW, b.Y1 - c.NW));
            rng.Shuffle(sides);
            var wanted = room.Role == RoomRole.Arena ? 2 : 1;
            foreach (var (side, from, to) in sides.Where(x => x.To - x.From >= 160 + 32).Take(wanted))
            {
                var width = Math.Min(rng.Between(4, 6) * 32, to - from - 32);
                var start = from + 16 + rng.Next((to - from - 32 - width) / 16 + 1) * 16;
                Box box = side switch
                {
                    "north" => new(start, b.Y1, start + width, b.Y1 + depth),
                    "south" => new(start, b.Y0 - depth, start + width, b.Y0),
                    "east" => new(b.X1, start, b.X1 + depth, start + width),
                    _ => new(b.X0 - depth, start, b.X0, start + width),
                };
                var door = side switch
                {
                    "north" => (start + width / 2.0, (double)b.Y1),
                    "south" => (start + width / 2.0, (double)b.Y0),
                    "east" => ((double)b.X1, start + width / 2.0),
                    _ => ((double)b.X0, start + width / 2.0),
                };
                var closet = new PlannedRoom
                {
                    Index = plan.Rooms.Count, Cell = room.Cell, Role = RoomRole.Closet, Zone = room.Zone, Style = room.Style, Progress = room.Progress,
                    Box = box, Floor = room.Floor, Ceiling = room.Floor + 112, ClosetOf = room.Index, ClosetDoor = door, Trigger = Mouth(entry, room),
                };
                plan.Rooms.Add(closet);
            }
        }
    }

    /// <summary>Which side of <paramref name="room"/> a corridor meets: north, south, east or west.</summary>
    private static string SideOf(PlannedLink link, PlannedRoom room)
    {
        var (x, y) = Mouth(link, room);
        var b = room.Box;
        return Math.Abs(y - b.Y1) < 1 ? "north" : Math.Abs(y - b.Y0) < 1 ? "south" : Math.Abs(x - b.X1) < 1 ? "east" : "west";
    }

    // ── Heights and the kinds of link ────────────────────────────────────────────────────────────────────

    private static void Heights(MapPlan plan, Seeded rng)
    {
        var s = plan.Settings;
        var done = new HashSet<int> { plan.Path[0] };
        plan.Rooms[plan.Path[0]].Floor = 0;

        // Tree links first (in the order they were made, each from a room with a floor to a new one), loops after.
        foreach (var link in plan.Links.Where(l => !l.Loop))
        {
            var (from, to) = done.Contains(link.A) ? (plan.Rooms[link.A], plan.Rooms[link.B]) : (plan.Rooms[link.B], plan.Rooms[link.A]);
            var length = link.AlongX ? link.Box.Width : link.Box.Height;
            double delta;
            if (link.Kind == LinkKind.Locked || to.Role == RoomRole.Secret)
            {
                delta = rng.Pick([-16, -8, 0, 0, 8, 16]);
                if (link.Kind != LinkKind.Locked) link.Kind = LinkKind.Door;

                // A secret's door is flush with the room's wall, where it can pass for the wall.
                if (to.Role == RoomRole.Secret) link.DoorAt = from.Index;
            }
            else if (s.Lifts && length >= 256 && rng.Chance(0.12))
            {
                link.Kind = LinkKind.Lift;
                delta = rng.Between(6, 8) * 16 * (rng.Chance(0.6) ? 1 : -1);
            }
            else if (s.Stairs && rng.Chance(0.25))
            {
                link.Kind = LinkKind.Stairs;
                delta = rng.Between(3, 6) * 16 * (rng.Chance(0.6) ? 1 : -1);
            }
            else
            {
                delta = rng.Pick([-24, -16, -8, 0, 0, 0, 8, 16, 24]);
                link.Kind = rng.Chance(to.Role is RoomRole.Path or RoomRole.Arena or RoomRole.Exit ? 0.45 : 0.3) ? LinkKind.Door : LinkKind.Open;

                // A door at either end of its corridor: the end leaving one room or the end entering the next.
                if (link.Kind == LinkKind.Door && rng.Chance(0.5)) link.DoorAt = from.Index;
            }

            if (from.Floor + delta is < -256 or > 512) delta = -delta;
            to.Floor = from.Floor + delta;
            done.Add(to.Index);
        }

        // A loop joins floors already set: a step, stairs or a lift as the difference allows, or it is left out.
        foreach (var link in plan.Links.Where(l => l.Loop).ToList())
        {
            var d = Math.Abs(plan.Rooms[link.A].Floor - plan.Rooms[link.B].Floor);
            var length = link.AlongX ? link.Box.Width : link.Box.Height;
            if (d <= 24) link.Kind = rng.Chance(0.4) ? LinkKind.Door : LinkKind.Open;
            else if (s.Stairs && d <= (length / 16 + 1) * 20) link.Kind = LinkKind.Stairs;
            else if (s.Lifts && length >= 256) link.Kind = LinkKind.Lift;
            else plan.Links.Remove(link);
        }

        foreach (var room in plan.Rooms)
        {
            // Caves are low, castle halls tall.
            var height = room.Role switch
            {
                RoomRole.Arena => rng.Between(10, 12) * 32,
                RoomRole.Secret => 128,
                RoomRole.Key => rng.Between(4, 6) * 32,
                _ when room.Style == MapStyle.Caves => rng.Between(4, 6) * 32,
                _ when room.Style == MapStyle.Castle => rng.Between(7, 11) * 32,
                _ => rng.Between(4, 8) * 32,
            };
            room.Ceiling = room.Floor + height;
        }

        foreach (var link in plan.Links)
        {
            var (a, b) = (plan.Rooms[link.A], plan.Rooms[link.B]);
            link.Floor = Math.Min(a.Floor, b.Floor);
            link.Ceiling = Math.Max(a.Floor, b.Floor) + 128;
        }
    }

    // ── Looks, light and the sky ────────────────────────────────────────────────────────────────────────

    private static void Dress(MapPlan plan, Seeded rng)
    {
        var s = plan.Settings;
        var looks = s.LooksOrOwn();
        var indoor = looks.Where(l => !l.Outdoors).ToList();
        var outdoor = looks.Where(l => l.Outdoors).ToList();
        if (indoor.Count == 0) indoor = [.. GeneratorSettings.OwnLooks(s.Family).Where(l => !l.Outdoors)];
        rng.Shuffle(indoor);

        // Each style prefers looks that suit it, by their names; with none of those chosen, any look does.
        IReadOnlyList<LookChoice> Suiting(MapStyle style)
        {
            string[] words = style switch
            {
                MapStyle.Caves => ["stone", "rock", "hell", "flesh", "moss", "waste", "forest", "brown brick"],
                MapStyle.Castle => ["stone", "castle", "monastery", "wood", "sandstone", "brick", "brown"],
                _ => ["tech", "metal", "brown halls", "grey brick", "brick"],
            };
            var suiting = indoor.Where(l => words.Any(w => l.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            return suiting.Count > 0 ? suiting : indoor;
        }

        var zoneLooks = new Dictionary<(int, MapStyle), LookChoice>();
        var share = s.Outdoors switch { OutdoorShare.None => 0, OutdoorShare.Some => 0.2, _ => 0.45 };
        var counts = new Dictionary<RoomRole, int>();
        foreach (var room in plan.Rooms)
        {
            // A closet is part of its room: the same look and light.
            if (room.Role == RoomRole.Closet)
            {
                var of = plan.Rooms[room.ClosetOf];
                (room.Look, room.Light, room.Name) = (of.Look, of.Light, $"Closet off {of.Name}");
                continue;
            }

            var sky = outdoor.Count > 0 && room.Role is RoomRole.Path or RoomRole.Side or RoomRole.Arena or RoomRole.Start && rng.Chance(share);
            var suits = Suiting(room.Style);
            if (!zoneLooks.TryGetValue((room.Zone, room.Style), out var zoneLook)) zoneLooks[(room.Zone, room.Style)] = zoneLook = suits[(room.Zone + zoneLooks.Count) % suits.Count];
            room.Look = sky ? rng.Pick(outdoor) : rng.Chance(0.15) ? rng.Pick(suits) : zoneLook;
            room.Outdoors = sky;
            if (sky) room.Ceiling = Math.Max(room.Ceiling, room.Floor + rng.Between(8, 10) * 32);
            room.Dark = s.DarkPlaces && !sky && room.Role is RoomRole.Path or RoomRole.Side or RoomRole.Key && rng.Chance(room.Style == MapStyle.Caves ? 0.4 : 0.22);
            room.Light = sky ? 224 : room.Dark ? rng.Pick([96, 112, 128]) : room.Role == RoomRole.Secret ? 160 : room.Style == MapStyle.Caves ? rng.Pick([128, 144, 160]) : rng.Pick([160, 176, 192, 208]);
            counts[room.Role] = counts.GetValueOrDefault(room.Role) + 1;
            room.Name = room.Role switch
            {
                RoomRole.Start => "Entry hall",
                RoomRole.Arena => "Arena",
                RoomRole.Exit => "Exit",
                RoomRole.Secret => "Secret cache",
                RoomRole.Key => $"{Capital(room.Key?.Colour ?? "")} key room",
                _ => sky ? $"Courtyard {counts[room.Role]}" : room.Box.Width * room.Box.Height > 400_000 ? $"Hall {counts[room.Role]}" : $"Chamber {counts[room.Role]}",
            };
        }
    }

    private static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    // ── Objects ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Spots(PlannedRoom room)
    {
        public readonly List<(double X, double Y, double R)> Taken = [];

        public PlannedRoom Room { get; } = room;

        public (double X, double Y)? Find(double radius, Seeded rng, (double X, double Y)? near = null)
        {
            var b = Room.Box;
            var inset = radius + 24;
            if (b.Width < 2 * inset || b.Height < 2 * inset) return null;
            for (var tries = 0; tries < 40; tries++)
            {
                double x, y;
                if (near is { } n && tries < 20)
                {
                    x = Math.Clamp(n.X + rng.Between(-6, 6) * 16, b.X0 + inset, b.X1 - inset);
                    y = Math.Clamp(n.Y + rng.Between(-6, 6) * 16, b.Y0 + inset, b.Y1 - inset);
                }
                else
                {
                    x = b.X0 + inset + rng.Next((int)((b.Width - 2 * inset) / 16) + 1) * 16;
                    y = b.Y0 + inset + rng.Next((int)((b.Height - 2 * inset) / 16) + 1) * 16;
                }

                if (Room.Holds(x, y, radius + 16) && Taken.All(t => Math.Abs(t.X - x) >= t.R + radius + 8 || Math.Abs(t.Y - y) >= t.R + radius + 8))
                {
                    Taken.Add((x, y, radius));
                    return (x, y);
                }
            }

            return null;
        }
    }

    private static void Populate(MapPlan plan, FamilyPalette palette, Seeded rng)
    {
        var s = plan.Settings;
        var spots = plan.Rooms.Select(r => new Spots(r)).ToList();

        // Where each room is entered from: the middle of the corridor's mouth on its wall.
        var entries = new Dictionary<int, (double X, double Y)>();
        foreach (var link in plan.Links.Where(l => !l.Loop))
        {
            var into = Math.Max(link.A, link.B);
            entries[into] = Mouth(link, plan.Rooms[into]);
        }

        // Keep the mouths of every corridor clear, so nothing stands in a doorway.
        foreach (var link in plan.Links)
        {
            foreach (var end in new[] { link.A, link.B })
            {
                var (x, y) = Mouth(link, plan.Rooms[end]);
                spots[end].Taken.Add((x, y, 64));
            }
        }

        // Each closet's door and the strip in front of it kept clear.
        foreach (var closet in plan.Rooms.Where(r => r.Role == RoomRole.Closet)) spots[closet.ClosetOf].Taken.Add((closet.ClosetDoor.X, closet.ClosetDoor.Y, 72));

        // The exit's switch on the wall facing the way in, on the cell's middle line (straight even in a cave).
        var exitRoom = plan.Rooms[plan.Path[^1]];
        var (mx, my) = Middle(plan, exitRoom);
        var (ex, ey) = entries.GetValueOrDefault(exitRoom.Index, (exitRoom.Box.X0, my));
        var eb = exitRoom.Box;
        plan.Exit = Math.Abs(ex - eb.X0) < 1 ? (eb.X1, my) : Math.Abs(ex - eb.X1) < 1 ? (eb.X0, my) : Math.Abs(ey - eb.Y0) < 1 ? (mx, eb.Y1) : (mx, eb.Y0);
        spots[exitRoom.Index].Taken.Add((plan.Exit.X, plan.Exit.Y, 96));

        // Teleporters: a pad in a dead-end side room, back to where its part of the map begins.
        if (s.Teleporters)
        {
            var wanted = s.Size is MapSize.Small or MapSize.Medium ? 1 : 2;
            var ends = plan.Rooms.Where(r => r.Role == RoomRole.Side && plan.Links.Count(l => l.A == r.Index || l.B == r.Index) == 1 && r.Box.Width >= 256 && r.Box.Height >= 256).ToList();
            rng.Shuffle(ends);
            foreach (var room in ends.Take(wanted))
            {
                var to = plan.Path.Select(i => plan.Rooms[i]).First(r => r.Zone == room.Zone);
                if (to.Index == room.Index) continue;
                var (cx, cy) = Middle(plan, room);
                var pad = new Box((int)cx - 64, (int)cy - 64, (int)cx + 64, (int)cy + 64);
                spots[room.Index].Taken.Add((cx, cy, 96));
                if (spots[to.Index].Find(32, rng, Middle(plan, to)) is not { } landing) continue;
                plan.Teleports.Add(new PlannedTeleport(room.Index, pad, to.Index, landing.X, landing.Y));
            }
        }

        // The start, facing the way on.
        var start = plan.Rooms[plan.Path[0]];
        var startSpot = Middle(plan, start);
        spots[start.Index].Taken.Add((startSpot.X, startSpot.Y, 48));
        var firstMouth = plan.Path.Count > 1 ? entries[plan.Path[1]] : (startSpot.X + 64, startSpot.Y);
        plan.Things.Add(new PlannedThing(ThingRole.Start, 1, startSpot.X, startSpot.Y, AngleTo(startSpot, firstMouth), Room: start.Index));

        // Keys in their rooms' middles.
        foreach (var keyRoom in plan.Rooms.Where(r => r.Key is not null))
        {
            // On its dais's middle when it has one, else near the room's middle.
            var at = keyRoom.Dais is not null ? Middle(plan, keyRoom) : spots[keyRoom.Index].Find(20, rng, Middle(plan, keyRoom)) ?? Middle(plan, keyRoom);
            if (keyRoom.Dais is not null) spots[keyRoom.Index].Taken.Add((at.X, at.Y, 40));
            plan.Things.Add(new PlannedThing(ThingRole.Key, keyRoom.Key!.Number, at.X, at.Y, 90, Room: keyRoom.Index));
        }

        // Weapons along the way in the game's order; another game's join them by how far along its own order each comes (its
        // second of five beside the base game's second of six), the first of each few left out on a small map.
        var own = palette.Weapons.Where(w => s.Doom2Things || !w.Doom2).ToList();
        var ownCount = Math.Min(own.Count, s.Size switch { MapSize.Small => 3, MapSize.Medium => 4, MapSize.Large => 5, _ => 6 });
        if (s.Bosses == 0 && s.Size != MapSize.Huge && ownCount == own.Count && ownCount > 2) ownCount--;
        var lent = s.WeaponsFrom.Where(f => f != s.Family).Select(FamilyPalette.For).OfType<FamilyPalette>().SelectMany(p => p.Weapons.Select((w, i) => (Weapon: w, At: (i + 1.0) / (p.Weapons.Count + 1) + 0.01))).ToList();
        var lentCount = Math.Min(lent.Count, s.Size switch { MapSize.Small => 1, MapSize.Medium => 2, MapSize.Large => 3, _ => 4 });
        var weapons = own.Take(ownCount).Select((w, i) => (Weapon: w, At: (i + 1.0) / (ownCount + 1))).Concat(lent.OrderBy(l => l.At).Where((_, i) => i % Math.Max(1, lent.Count / Math.Max(1, lentCount)) == 0).Take(lentCount))
            .OrderBy(w => w.At).Select(w => w.Weapon).ToList();
        var weaponCount = weapons.Count;
        var walkPath = plan.Path.Where(i => plan.Rooms[i].Role is RoomRole.Start or RoomRole.Path or RoomRole.Arena).ToList();
        var weaponAt = new Dictionary<int, int>();
        for (var w = 0; w < weaponCount; w++)
        {
            var index = Math.Clamp((int)Math.Round((w + 0.5) * (walkPath.Count - 1.0) / weaponCount), w == 0 ? 0 : 1, walkPath.Count - 1);
            if (w == 0 && walkPath.Count > 2) index = 1;
            var room = plan.Rooms[walkPath[index]];
            if (spots[room.Index].Find(24, rng) is not { } at) continue;
            var lentWeapon = weapons[w].Family != s.Family;
            plan.Things.Add(new PlannedThing(ThingRole.Weapon, weapons[w].Number, at.X, at.Y, 90, Room: room.Index, Family: lentWeapon ? weapons[w].Family : null));
            foreach (var other in lentWeapon ? [] : weapons[w].With)
            {
                // The other classes' weapons of the same tier beside it (Hexen: each class picks up its own).
                if (spots[room.Index].Find(20, rng, at) is { } beside) plan.Things.Add(new PlannedThing(ThingRole.Weapon, other, beside.X, beside.Y, 90, Room: room.Index));
            }

            weaponAt[w] = plan.Path.IndexOf(room.Index);
        }

        // Monsters: a budget of health per room by its part in the map, its place along the way and the challenge.
        var pool = (s.Monsters.Count > 0 ? s.Monsters : s.OwnMonsters()).Where(m => !m.Boss).OrderBy(m => m.Family, StringComparer.Ordinal).ThenBy(m => m.Class, StringComparer.Ordinal).ToList();
        if (pool.Count == 0) pool = [.. s.OwnMonsters().Where(m => !m.Boss)];
        var bosses = (s.Monsters.Count > 0 ? s.Monsters : s.OwnMonsters()).Where(m => m.Boss).OrderBy(m => m.Family, StringComparer.Ordinal).ThenBy(m => m.Class, StringComparer.Ordinal).ToList();
        if (bosses.Count == 0) bosses = [.. pool.OrderByDescending(m => m.Health).Take(1)];
        var c = Math.Clamp(s.Challenge, 0, 1);
        var factor = 0.35 + 2.15 * Math.Pow(c, 1.3);
        foreach (var room in plan.Rooms)
        {
            var baseline = room.Role switch
            {
                RoomRole.Path => 260.0,
                RoomRole.Side => 160,
                RoomRole.Key => 320,
                RoomRole.Arena => 900,
                RoomRole.Exit => 120,
                RoomRole.Closet => 160,
                _ => 0,
            };
            if (baseline == 0) continue;
            var budget = baseline * factor * (0.7 + 0.9 * room.Progress);
            var maxTier = room.Role == RoomRole.Arena ? 4 : Math.Clamp(1 + (int)Math.Floor(room.Progress * (2.2 + 2 * c) + c), 1, 4);

            // A closet's monsters face its door; they are small enough to stand in it, and wait for it to open.
            var closet = room.Role == RoomRole.Closet;
            var entry = closet ? room.ClosetDoor : entries.GetValueOrDefault(room.Index, (room.Box.CentreX, room.Box.CentreY));
            var fits = closet ? pool.Where(m => m.Radius <= 24).ToList() : pool;
            if (fits.Count == 0) continue;
            var ambush = s.Ambushes && room.Role is not (RoomRole.Arena or RoomRole.Closet) && rng.Chance(0.25);
            // The bosses first, at the far end of the arena, so the arena's crowd never leaves them no room: one that does not
            // fit gives way to the next boss that does, smallest last.
            if (room.Role == RoomRole.Arena)
            {
                var far = (X: 2 * room.Box.CentreX - entry.Item1, Y: 2 * room.Box.CentreY - entry.Item2);
                for (var b = 0; b < Math.Clamp(s.Bosses, 0, 3); b++)
                {
                    foreach (var kind in bosses.Skip(b % bosses.Count).Concat(bosses.Take(b % bosses.Count)).Concat(bosses.OrderBy(m => m.Radius)))
                    {
                        if (spots[room.Index].Find(kind.Radius, rng, far) is not { } at) continue;
                        plan.Things.Add(new PlannedThing(ThingRole.Boss, kind.Number, at.X, at.Y, AngleTo(at, entry), 0, false, kind, room.Index));
                        break;
                    }
                }
            }

            var placed = 0;
            for (var tries = 0; tries < 60 && budget > 0; tries++)
            {
                var tier = rng.Chance(0.4) ? maxTier : rng.Between(1, maxTier);
                var choices = fits.Where(m => m.Tier == tier).ToList();
                for (var t = tier - 1; choices.Count == 0 && t >= 1; t--) choices = [.. fits.Where(m => m.Tier == t)];
                if (choices.Count == 0) choices = fits;
                var kind = rng.Pick(choices);
                if (placed > 0 && kind.Health > budget * 1.5) continue;
                if (spots[room.Index].Find(kind.Radius, rng) is not { } at) continue;
                var skill = placed == 0 ? 0 : rng.NextDouble() switch { < 0.5 => 0, < 0.75 => 1, _ => 2 };
                plan.Things.Add(new PlannedThing(ThingRole.Monster, kind.Number, at.X, at.Y, AngleTo(at, entry), skill, ambush, kind, room.Index));
                budget -= kind.Health;
                placed++;
            }
        }

        Supplies(plan, palette, spots, weapons, weaponAt, rng);
        if (s.Decorations) Decorate(plan, palette, spots, rng);
        foreach (var room in plan.Rooms.Where(r => r.Dark && s.GZDoom))
        {
            if (spots[room.Index].Find(8, rng, (room.Box.CentreX, room.Box.CentreY)) is { } at) plan.Things.Add(new PlannedThing(ThingRole.Light, 0, at.X, at.Y, 0, Room: room.Index));
        }
    }

    /// <summary>The ammunition a weapon uses, from its own game's table.</summary>
    private static AmmoKind? AmmoFor(WeaponKind weapon) => FamilyPalette.For(weapon.Family)?.AmmoOf(weapon.Ammo);

    /// <summary>Ammunition and health for the monsters placed: enough damage for the hard difficulty's monsters with a margin the challenge shrinks, put where the fighting is.</summary>
    private static void Supplies(MapPlan plan, FamilyPalette palette, List<Spots> spots, List<WeaponKind> weapons, Dictionary<int, int> weaponAt, Seeded rng)
    {
        var s = plan.Settings;
        var c = Math.Clamp(s.Challenge, 0, 1);
        // A closet's monsters count with the room it opens into, where its supplies go.
        double HealthIn(int room) => plan.Things.Where(t => t.Room >= 0 && (t.Room == room || plan.Rooms[t.Room].ClosetOf == room) && t.Monster is not null).Sum(t => (double)t.Monster!.Health);
        var total = plan.Rooms.Sum(r => HealthIn(r.Index));
        var target = total * (1.6 - 0.45 * c);
        var have = palette.StartDamage + weaponAt.Keys.Sum(w => weapons[w].Gives * (AmmoFor(weapons[w])?.Damage ?? 0));
        var deficit = Math.Max(0, target - have);

        // Path position of a room: its own on the way, or its parent's for a room off it.
        int Position(PlannedRoom r)
        {
            var i = plan.Path.IndexOf(r.Index);
            if (i >= 0) return i;
            var parent = plan.Links.Where(l => !l.Loop && (l.A == r.Index || l.B == r.Index)).Select(l => l.A == r.Index ? l.B : l.A).FirstOrDefault(p => p < r.Index);
            return parent == r.Index ? 0 : Position(plan.Rooms[parent]);
        }

        var startAmmo = palette.Ammo[0];
        foreach (var room in plan.Rooms.Where(r => r.Role != RoomRole.Closet && HealthIn(r.Index) > 0).OrderBy(Position))
        {
            var share = total <= 0 ? 0 : deficit * HealthIn(room.Index) / total;
            var position = Position(room);
            var types = new List<AmmoKind> { startAmmo };
            foreach (var (w, at) in weaponAt.Where(p => p.Value <= position).OrderBy(p => p.Key))
            {
                if (AmmoFor(weapons[w]) is { } a && !types.Contains(a)) types.Add(a);
            }

            for (var guard = 0; share > 0 && guard < 30; guard++)
            {
                var ammo = types.Count > 1 && rng.Chance(0.6) ? types[^1] : rng.Pick(types);
                var big = share >= ammo.BigAmount * ammo.Damage * 0.8;
                if (spots[room.Index].Find(16, rng) is not { } at) break;
                plan.Things.Add(new PlannedThing(ThingRole.Ammo, big ? ammo.Big : ammo.Small, at.X, at.Y, 90, Room: room.Index, Family: ammo.Family != s.Family ? ammo.Family : null));
                share -= (big ? ammo.BigAmount : ammo.SmallAmount) * ammo.Damage;
            }

            // Health: about a tenth of the monsters' health, more at the gentle end.
            var heal = HealthIn(room.Index) * (0.14 - 0.08 * c);
            for (var guard = 0; heal > 4 && guard < 12; guard++)
            {
                var big = heal >= palette.Health.BigAmount * 0.8;
                if (spots[room.Index].Find(16, rng) is not { } at) break;
                plan.Things.Add(new PlannedThing(ThingRole.Health, big ? palette.Health.Big : palette.Health.Small, at.X, at.Y, 90, Room: room.Index));
                heal -= big ? palette.Health.BigAmount : palette.Health.SmallAmount;
            }
        }

        // Armour: light early on, heavy before the arena; and something worth finding in each secret.
        var walk = plan.Path.Select(i => plan.Rooms[i]).ToList();
        if (walk.Count > 2 && spots[walk[1].Index].Find(16, rng) is { } light) plan.Things.Add(new PlannedThing(ThingRole.Armor, palette.Armor.Light, light.X, light.Y, 90, Room: walk[1].Index));
        if (walk.FirstOrDefault(r => r.Role == RoomRole.Arena) is { } arena && walk.IndexOf(arena) > 0 && spots[walk[walk.IndexOf(arena) - 1].Index].Find(16, rng) is { } heavy)
        {
            plan.Things.Add(new PlannedThing(ThingRole.Armor, palette.Armor.Heavy, heavy.X, heavy.Y, 90, Room: walk[walk.IndexOf(arena) - 1].Index));
        }

        foreach (var secret in plan.Rooms.Where(r => r.Role == RoomRole.Secret))
        {
            // The secret's ammunition is for the base game's best weapon given so far.
            var ownBest = weaponAt.Keys.Where(w => weapons[w].Family == s.Family).DefaultIfEmpty(-1).Max();
            var best = ownBest >= 0 ? AmmoFor(weapons[ownBest]) : null;
            var gift = rng.Next(3) switch { 0 => palette.Health.Super, 1 => palette.Armor.Heavy, _ => best?.Big ?? palette.Health.Super };
            if (spots[secret.Index].Find(16, rng, (secret.Box.CentreX, secret.Box.CentreY)) is { } at) plan.Things.Add(new PlannedThing(gift == palette.Armor.Heavy ? ThingRole.Armor : gift == palette.Health.Super ? ThingRole.Health : ThingRole.Ammo, gift, at.X, at.Y, 90, Room: secret.Index));
        }
    }

    /// <summary>Decorations in the corners of rooms big enough for them: the game's lamps and columns inside, its trees outside.</summary>
    private static void Decorate(MapPlan plan, FamilyPalette palette, List<Spots> spots, Seeded rng)
    {
        foreach (var room in plan.Rooms.Where(r => r.Role is not (RoomRole.Secret or RoomRole.Closet) && r.Box.Width >= 256 && r.Box.Height >= 256))
        {
            var kinds = room.Outdoors && palette.OutdoorDecorations.Count > 0 ? palette.OutdoorDecorations : palette.Decorations;
            if (kinds.Count == 0 || !rng.Chance(0.7)) continue;
            var kind = rng.Pick(kinds);
            var b = room.Box;
            foreach (var (x, y) in new[] { (b.X0 + 48.0, b.Y0 + 48.0), (b.X1 - 48.0, b.Y0 + 48.0), (b.X0 + 48.0, b.Y1 - 48.0), (b.X1 - 48.0, b.Y1 - 48.0) })
            {
                if (!room.Holds(x, y, 24) || spots[room.Index].Taken.Any(t => Math.Abs(t.X - x) < t.R + 32 && Math.Abs(t.Y - y) < t.R + 32)) continue;
                spots[room.Index].Taken.Add((x, y, 24));
                plan.Things.Add(new PlannedThing(ThingRole.Decoration, kind, x, y, 90, Room: room.Index));
            }
        }
    }

    /// <summary>The middle of a corridor's mouth on a room's wall.</summary>
    public static (double X, double Y) Mouth(PlannedLink link, PlannedRoom room)
    {
        var b = link.Box;
        if (link.AlongX) return (Math.Abs(b.X0 - room.Box.X1) < 1 ? b.X0 : b.X1, b.CentreY);
        return (b.CentreX, Math.Abs(b.Y0 - room.Box.Y1) < 1 ? b.Y0 : b.Y1);
    }

    /// <summary>The angle from one point to another, to the nearest eighth of a turn, in degrees as maps keep them.</summary>
    private static int AngleTo((double X, double Y) from, (double X, double Y) to)
    {
        var degrees = Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;
        return ((int)Math.Round(degrees / 45) * 45 % 360 + 360) % 360;
    }
}
