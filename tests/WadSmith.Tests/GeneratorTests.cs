using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Generate;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// "Make me a map" (D50): every map the generator makes is built line for line by the editor's tools, keeps the shape rules,
/// has no Problems, and can be finished — the Progression walk (the editor's own, not the generator's graph) reaches the
/// exit with every key before its door; the same seed makes the same map; the settings change what is made; and the
/// palette is held to the engine's own numbers.
/// </summary>
public sealed class GeneratorTests
{
    private static GeneratorSettings For(string family, string seed, MapSize size = MapSize.Medium, int keys = 2, int bosses = 1, MapStyle style = MapStyle.Base) => new()
    {
        Style = style,
        GameId = family switch { "heretic" => "heretic", "hexen" => "hexen", "strife" => "strife", _ => "doom2" },
        Family = family,
        Slot = family == "heretic" ? "E1M1" : "MAP01",
        Seed = seed,
        Size = size,
        Keys = keys,
        Bosses = bosses,
        Outdoors = OutdoorShare.Some,
    };

    private static string Text(MadeMap made) => System.Text.Encoding.UTF8.GetString(MapConversion.ToUdmfLumps(made.Map.Frame.ToModel()).First(l => l.Name == "TEXTMAP").Data);

    /// <summary>What is wrong with a made map, in sentences; empty when it is sound, clean and can be finished.</summary>
    private static List<string> Faults(MadeMap made, GameResources? resources = null)
    {
        var s = made.Plan.Settings;
        var faults = made.Failed.Select(f => $"line {f.Line} ({f.Text}): {f.Said}").ToList();
        faults.AddRange(Invariants.Check(made.Map));
        var g = MapGeometry.Build(made.Map.Frame);
        faults.AddRange(Problems.Check(g, resources, Look.For(s.Family), s.Family).Select(p => $"{p.Rule}: {p.Sentence}"));
        var walk = Progression.Walk(g, s.Family);
        if (!walk.ExitReached) faults.Add("the exit cannot be reached");
        var held = new HashSet<string>();
        foreach (var step in walk.Steps)
        {
            if (step.Kind == "key") held.Add(step.Colour!);
            else if (step.Kind == "door" && !held.Contains(step.Colour!)) faults.Add($"the {step.Colour} door comes before its key");
        }

        var locked = made.Plan.Links.Count(l => l.Key is not null);
        if (walk.Steps.Count(x => x.Kind == "door") != locked) faults.Add($"the walk opens {walk.Steps.Count(x => x.Kind == "door")} locked doors of {locked}");
        return faults;
    }

    [Theory]
    [InlineData("doom", MapSize.Small, MapStyle.Base, 30)]
    [InlineData("doom", MapSize.Medium, MapStyle.Base, 30)]
    [InlineData("doom", MapSize.Large, MapStyle.Base, 20)]
    [InlineData("doom", MapSize.Huge, MapStyle.Base, 10)]
    [InlineData("doom", MapSize.Medium, MapStyle.Caves, 20)]
    [InlineData("doom", MapSize.Medium, MapStyle.Castle, 20)]
    [InlineData("doom", MapSize.Large, MapStyle.Mixed, 15)]
    [InlineData("doom", MapSize.Huge, MapStyle.Mixed, 8)]
    [InlineData("heretic", MapSize.Medium, MapStyle.Mixed, 15)]
    [InlineData("hexen", MapSize.Medium, MapStyle.Mixed, 15)]
    [InlineData("strife", MapSize.Medium, MapStyle.Mixed, 15)]
    public void EveryMadeMapIsSoundCleanAndCanBeFinished(string family, MapSize size, MapStyle style, int seeds)
    {
        var faults = new List<string>();
        for (var i = 0; i < seeds; i++)
        {
            var made = MapMaker.Make(For(family, $"test-{i}", size, keys: i % 4, bosses: i % 4, style) with { Outdoors = (OutdoorShare)(i % 3) });
            faults.AddRange(Faults(made).Select(f => $"seed test-{i}: {f}"));
        }

        Assert.Empty(faults);
    }

    [Fact]
    public void TheSameSeedMakesTheSameMapAndAnotherSeedAnother()
    {
        var a = Text(MapMaker.Make(For("doom", "misty-cavern")));
        var b = Text(MapMaker.Make(For("doom", "Misty Cavern")));
        var c = Text(MapMaker.Make(For("doom", "rusty-foundry")));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void TheSettingsChangeWhatIsMade()
    {
        foreach (var keys in new[] { 0, 1, 2, 3 })
        {
            var plan = Planner.Plan(For("doom", "grim-keep", MapSize.Large, keys, bosses: keys));
            Assert.Equal(keys, plan.Links.Count(l => l.Kind == LinkKind.Locked));
            Assert.Equal(keys, plan.Things.Count(t => t.Role == ThingRole.Key));
            Assert.Equal(keys, plan.Things.Count(t => t.Role == ThingRole.Boss));
            Assert.Equal(keys > 0, plan.Rooms.Any(r => r.Role == RoomRole.Arena));
        }

        foreach (var size in Enum.GetValues<MapSize>()) Assert.Equal(GeneratorSettings.RoomsFor(size), Planner.Plan(For("doom", "grim-keep", size, keys: 1)).Rooms.Count(r => r.Role != RoomRole.Closet));
        var without = Planner.Plan(For("doom", "grim-keep", MapSize.Huge) with { Secrets = false, Stairs = false, Lifts = false, Teleporters = false, DarkPlaces = false, Ambushes = false, Decorations = false, Traps = false });
        Assert.DoesNotContain(without.Rooms, r => r.Role is RoomRole.Secret or RoomRole.Closet || r.Dark);
        Assert.DoesNotContain(without.Links, l => l.Kind is LinkKind.Stairs or LinkKind.Lift);
        Assert.Empty(without.Teleports);
        Assert.DoesNotContain(without.Things, t => t.Ambush || t.Role is ThingRole.Decoration or ThingRole.Light);
        var with = Planner.Plan(For("doom", "grim-keep", MapSize.Huge));
        Assert.Contains(with.Rooms, r => r.Role == RoomRole.Secret);
        Assert.Contains(with.Links, l => l.Kind is LinkKind.Stairs or LinkKind.Lift);
        Assert.NotEmpty(with.Teleports);
        Assert.Contains(with.Rooms, r => r.Role == RoomRole.Closet);

        // The styles: caves cut their corners, castle halls stand pillars; the same seed keeps the same way through.
        var caves = Planner.Plan(For("doom", "grim-keep", MapSize.Huge, style: MapStyle.Caves));
        var castle = Planner.Plan(For("doom", "grim-keep", MapSize.Huge, style: MapStyle.Castle));
        Assert.Contains(caves.Rooms, r => r.Cuts != default);
        Assert.DoesNotContain(caves.Rooms, r => r.Pillars.Count > 0);
        Assert.Contains(castle.Rooms, r => r.Pillars.Count > 0);
        Assert.DoesNotContain(castle.Rooms, r => r.Cuts != default);
        Assert.Equal(with.Path.Select(i => with.Rooms[i].Cell), caves.Path.Select(i => caves.Rooms[i].Cell));

        // The challenge: more monsters, fewer spare supplies.
        var gentle = Planner.Plan(For("doom", "grim-keep") with { Challenge = 0 });
        var brutal = Planner.Plan(For("doom", "grim-keep") with { Challenge = 1 });
        static double Health(MapPlan p) => p.Things.Where(t => t.Role == ThingRole.Monster).Sum(t => (double)t.Monster!.Health);
        Assert.True(Health(brutal) > 3 * Health(gentle), $"{Health(brutal)} against {Health(gentle)}");
        Assert.True(brutal.MonsterCounts.Hard > gentle.MonsterCounts.Hard);
        Assert.Equal(gentle.Rooms.Select(r => r.Box), brutal.Rooms.Select(r => r.Box));
    }

    /// <summary>Each closet's door opens from a line the player walks over on the way into its room, and only the room's way in does it.</summary>
    [Fact]
    public void EveryClosetOpensWhenThePlayerWalksIntoItsRoom()
    {
        var closets = 0;
        for (var i = 0; i < 12; i++)
        {
            var made = MapMaker.Make(For("doom", $"closet-{i}", MapSize.Large, style: (MapStyle)(i % 4)));
            var g = Editor.Geometry.MapGeometry.Build(made.Map.Frame);
            foreach (var closet in made.Plan.Rooms.Where(r => r.Role == RoomRole.Closet))
            {
                closets++;
                var (dx, dy) = closet.ClosetDoor;
                var inside = closet.Box.Contains(dx, dy + 8) ? (dx, dy + 8) : closet.Box.Contains(dx, dy - 8) ? (dx, dy - 8) : closet.Box.Contains(dx + 8, dy) ? (dx + 8, dy) : (dx - 8, dy);
                var door = g.SectorAt(new(inside.Item1, inside.Item2));
                Assert.NotNull(door);
                Assert.NotNull(Editor.Smart.Doors.Recognise(g, door!));
                var trigger = g.LineNear(new(closet.Trigger.X, closet.Trigger.Y), 4)!;
                Assert.Equal((Editor.Smart.Doors.DoorOpen, door!.Tag, true), (trigger.Special, trigger.Args[0], trigger.Flag("playercross")));
                Assert.NotEqual(0, door.Tag);
            }
        }

        Assert.True(closets > 5, $"only {closets} closets in 12 large maps");
    }

    /// <summary>Every boss asked for stands in the arena, in every size and style, even at the top challenge's crowd (the arena once filled before the second boss came, and the Spider Mastermind had no room).</summary>
    [Fact]
    public void EveryBossAskedForIsInTheArena()
    {
        var missing = new List<string>();
        foreach (var size in Enum.GetValues<MapSize>())
        {
            foreach (var style in Enum.GetValues<MapStyle>())
            {
                for (var i = 0; i < 6; i++)
                {
                    var bosses = 1 + i % 3;
                    var plan = Planner.Plan(For("doom", $"boss-{i}", size, bosses: bosses, style: style) with { Challenge = 1 });
                    var placed = plan.Things.Count(t => t.Role == ThingRole.Boss);
                    if (placed != bosses) missing.Add($"{size} {style} boss-{i}: {placed} of {bosses}");
                }
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// Another game's weapons join the base game's along the way, in an order that mixes them, with their own ammunition;
    /// carried in by the numbers the project gives them, and left out when they cannot come.
    /// </summary>
    [Fact]
    public void AnotherGamesWeaponsComeWithTheirAmmunition()
    {
        var plan = Planner.Plan(For("doom", "grim-keep", MapSize.Huge) with { WeaponsFrom = ["heretic"] });
        var lentWeapons = plan.Things.Where(t => t.Role == ThingRole.Weapon && t.Family == "heretic").ToList();
        var heretic = FamilyPalette.For("heretic")!;
        Assert.InRange(lentWeapons.Count, 1, 4);
        Assert.All(lentWeapons, w => Assert.Contains(heretic.Weapons, k => k.Number == w.Number));
        var ammo = plan.Things.Where(t => t.Role == ThingRole.Ammo && t.Family == "heretic").ToList();
        Assert.NotEmpty(ammo);
        Assert.All(ammo, a => Assert.Contains(heretic.Ammo, k => k.Small == a.Number || k.Big == a.Number));
        Assert.Contains(plan.Things, t => t.Role == ThingRole.Weapon && t.Family is null);

        // Built with the project's numbers they are on the map; with none, they are left out and the map is still sound.
        var carried = MapMaker.Build(plan, new Lent(Items: (family, number) => family == "heretic" ? 21000 + number : null));
        Assert.Empty(Faults(carried));
        Assert.Equal(lentWeapons.Count + ammo.Count, carried.Map.Things.Count(t => t.OnMap && t.Type > 21000));
        var without = MapMaker.Build(plan);
        Assert.Empty(Faults(without));
        Assert.DoesNotContain(without.Map.Things, t => t.OnMap && t.Type > 21000);
    }

    /// <summary>A secret's door is flush with its room's wall and looks like it from the room: the room's wall picture on its face, marked secret (the automap shows it as wall).</summary>
    [Fact]
    public void EverySecretDoorLooksLikeTheWallBesideIt()
    {
        var secrets = 0;
        for (var i = 0; i < 10; i++)
        {
            var made = MapMaker.Make(For("doom", $"secret-{i}", MapSize.Huge, style: (MapStyle)(i % 4)));
            var g = Editor.Geometry.MapGeometry.Build(made.Map.Frame);
            foreach (var link in made.Plan.Links.Where(l => made.Plan.Rooms[l.A].Role == RoomRole.Secret || made.Plan.Rooms[l.B].Role == RoomRole.Secret))
            {
                secrets++;
                var room = made.Plan.Rooms[link.DoorAt];
                Assert.NotEqual(RoomRole.Secret, room.Role);
                var (mx, my) = Planner.Mouth(link, room);
                var face = g.LineNear(new(mx, my), 2)!;
                var (cx, cy) = Planner.Middle(made.Plan, room);
                var inRoom = g.SectorAt(new(cx, cy));
                var side = ReferenceEquals(face.Front.Sector, inRoom) ? face.Front : face.Back!;
                Assert.True(face.Flag("secret"), $"seed secret-{i}: the door's face at ({mx}, {my}) is not marked secret");
                Assert.Equal((room.Look?.Look ?? Look.For("doom")).Wall, side.Top);
            }
        }

        Assert.True(secrets >= 30, $"only {secrets} secrets in 10 huge maps");
    }

    /// <summary>Pools come with the hazards and go without them; each is sunk 16 into its room's floor with its picture, and a hurting one hurts by GZDoom's own fields.</summary>
    [Fact]
    public void PoolsAreSunkWithTheirPictureAndTheirDamage()
    {
        Assert.DoesNotContain(Planner.Plan(For("doom", "grim-keep", MapSize.Huge) with { Hazards = false }).Rooms, r => r.Pools.Count > 0);
        var hurting = 0;
        foreach (var family in new[] { "doom", "heretic", "hexen", "strife" })
        {
            var made = MapMaker.Make(For(family, "grim-keep", MapSize.Huge, style: MapStyle.Castle));
            var g = Editor.Geometry.MapGeometry.Build(made.Map.Frame);
            var pools = made.Plan.Rooms.SelectMany(r => r.Pools.Select(p => (Room: r, Pool: p))).ToList();
            Assert.NotEmpty(pools);
            foreach (var (room, (box, flat, damage)) in pools)
            {
                var area = g.SectorAt(new(box.CentreX, box.CentreY))!;
                Assert.Equal((room.Floor - 16, flat), (area.FloorHeight, area.FloorTexture));
                var amount = area.Extra.TryGet("damageamount", out var v) ? v.AsInt() : 0;
                Assert.Equal(damage, amount);
                if (damage > 0) hurting++;
            }
        }

        Assert.True(hurting > 0, "no pool hurts in four huge maps");
    }

    /// <summary>Every game's pool pictures are its own: each is a floor picture of the game (where the game is here).</summary>
    [Theory]
    [InlineData("doom", "doom2", "DOOM2.WAD")]
    [InlineData("doom", "freedoom2", "freedoom2.wad")]
    [InlineData("heretic", "heretic", "HERETIC.WAD")]
    [InlineData("hexen", "hexen", "HEXEN.WAD")]
    [InlineData("strife", "strife", "STRIFE1.WAD")]
    public void EveryPoolPictureIsTheGamesOwn(string family, string id, string file)
    {
        var path = GameFiles.Require(file);
        var game = GameResources.Load(id, null, [new FoundGame(GameCatalog.Get(id)!, path, new FileInfo(path).Length)], GameFiles.Require("gzdoom.pk3"))!;
        Assert.Empty(Hazards.Of(family).Where(h => game.Textures.FindFlat(h.Flat) is null).Select(h => h.Flat));
    }

    /// <summary>A dais is a step up from its room, and a key room's key stands on its dais.</summary>
    [Fact]
    public void KeysStandOnADaisAStepUp()
    {
        var onDais = 0;
        for (var i = 0; i < 8; i++)
        {
            var made = MapMaker.Make(For("doom", $"dais-{i}", MapSize.Large, keys: 3, style: (MapStyle)(i % 4)));
            var g = Editor.Geometry.MapGeometry.Build(made.Map.Frame);
            foreach (var room in made.Plan.Rooms.Where(r => r.Dais is not null))
            {
                var d = room.Dais!.Value;
                Assert.Equal(room.Floor + 24, g.SectorAt(new(d.CentreX, d.CentreY))!.FloorHeight);
                if (room.Role != RoomRole.Key) continue;
                var key = made.Plan.Things.Single(t => t.Role == ThingRole.Key && t.Room == room.Index);
                Assert.True(d.Contains(key.X, key.Y), $"seed dais-{i}: the key at ({key.X}, {key.Y}) is off the dais {d}");
                onDais++;
            }
        }

        Assert.True(onDais >= 8, $"only {onDais} keys on a dais");
    }

    /// <summary>Every look a made map's rooms can have is drawn with pictures its own game has (Duke Nukem 3D's tiles stand for its floors too).</summary>
    [Theory]
    [InlineData("doom", "doom2", "DOOM2.WAD")]
    [InlineData("heretic", "heretic", "HERETIC.WAD")]
    [InlineData("hexen", "hexen", "HEXEN.WAD")]
    [InlineData("strife", "strife", "STRIFE1.WAD")]
    [InlineData("build", "duke3d", "DUKE3D.GRP")]
    public void EveryLookIsDrawnWithItsGamesOwnPictures(string family, string id, string file)
    {
        var path = GameFiles.Require(file);
        var game = GameResources.Load(id, null, [new FoundGame(GameCatalog.Get(id)!, path, new FileInfo(path).Length)], GameFiles.Require("gzdoom.pk3"))!;
        var looks = GeneratorSettings.OwnLooks(family);
        Assert.NotEmpty(looks);
        var missing = looks.SelectMany(l => new[] { (l.Name, l.Look.Wall, false), (l.Name, l.Look.Floor, true), (l.Name, l.Look.Ceiling, true) })
            .Where(p => !GeneratorSettings.IsSky(p.Item2) && (p.Item3 ? game.Textures.GetFlat(p.Item2) ?? game.Textures.GetWall(p.Item2) : game.Textures.GetWall(p.Item2)) is null)
            .Select(p => $"{p.Name}: {p.Item2}").ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryDifficultyHasMonstersAndTheHardOnesMore()
    {
        var plan = Planner.Plan(For("doom", "grim-keep", MapSize.Large));
        var (easy, normal, hard) = plan.MonsterCounts;
        Assert.True(easy > 0 && easy < normal && normal < hard, $"{easy}/{normal}/{hard}");
    }

    [Fact]
    public void NoMonsterStandsInAWallOrOnAnother()
    {
        for (var i = 0; i < 20; i++)
        {
            var plan = Planner.Plan(For("doom", $"crowd-{i}", MapSize.Large, bosses: 3, style: (MapStyle)(i % 4)) with { Challenge = 1 });
            var monsters = plan.Things.Where(t => t.Monster is not null).ToList();
            foreach (var m in monsters)
            {
                var room = plan.Rooms[m.Room];
                Assert.True(room.Holds(m.X, m.Y, m.Monster!.Radius), $"{m.Monster.Class} at ({m.X}, {m.Y}) reaches out of its room {room.Box} (cut {room.Cuts}, {room.Pillars.Count} pillars)");
            }

            foreach (var (a, b) in monsters.SelectMany((a, i) => monsters.Skip(i + 1).Select(b => (a, b))))
            {
                var gap = a.Monster!.Radius + b.Monster!.Radius;
                Assert.False(Math.Abs(a.X - b.X) < gap && Math.Abs(a.Y - b.Y) < gap, $"{a.Monster.Class} and {b.Monster.Class} overlap at ({a.X}, {a.Y}) and ({b.X}, {b.Y})");
            }
        }
    }

    [Fact]
    public void ThePaletteHoldsTheEnginesOwnNumbers()
    {
        var engine = ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"));
        var faults = new List<string>();
        foreach (var family in FamilyPalette.Families)
        {
            var catalog = ThingCatalog.Load(engine, family);
            var p = FamilyPalette.For(family)!;
            void Expect(int number, ThingCategory category, string? className = null)
            {
                if (catalog.Find(number) is not { } t) faults.Add($"{family}: nothing is number {number} ({className})");
                else if (className is not null && !string.Equals(t.ClassName, className, StringComparison.OrdinalIgnoreCase)) faults.Add($"{family}: {number} is {t.ClassName}, not {className}");
                else if (t.Category != category) faults.Add($"{family}: {number} ({t.ClassName}) is a {t.Category}, not a {category}");
            }

            foreach (var m in p.Monsters)
            {
                Expect(m.Number, ThingCategory.Monster, m.Class);
                if (catalog.Find(m.Number) is { } t && (t.Health != m.Health || t.Radius != m.Radius)) faults.Add($"{family}: {m.Class} has health {t.Health} and radius {t.Radius} in the engine, {m.Health} and {m.Radius} in the palette");
            }

            foreach (var w in p.Weapons)
            {
                Expect(w.Number, ThingCategory.Weapon, w.Class);
                if (catalog.Find(w.Number) is { } t && (t.AmmoType != w.Ammo || t.AmmoGive != w.Gives)) faults.Add($"{family}: {w.Class} gives {t.AmmoGive} {t.AmmoType} in the engine, {w.Gives} {w.Ammo} in the palette");
                foreach (var other in w.With) Expect(other, ThingCategory.Weapon);
            }

            foreach (var a in p.Ammo)
            {
                foreach (var (number, amount) in new[] { (a.Small, a.SmallAmount), (a.Big, a.BigAmount) })
                {
                    Expect(number, ThingCategory.Ammo);
                    if (catalog.Find(number) is { } t && t.Amount != amount) faults.Add($"{family}: {t.ClassName} gives {t.Amount} in the engine, {amount} in the palette");
                }
            }

            foreach (var number in new[] { p.Health.Small, p.Health.Big, p.Health.Super }) Expect(number, ThingCategory.Health);
            foreach (var number in new[] { p.Armor.Light, p.Armor.Heavy }) Expect(number, ThingCategory.Armor);
            foreach (var k in p.Keys) Expect(k.Number, ThingCategory.Key);
            foreach (var number in p.Decorations.Concat(p.OutdoorDecorations)) Expect(number, ThingCategory.Decoration);
        }

        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    [Fact]
    public void TheBalanceMeterSaysThereIsEnoughAmmunitionAtEveryChallenge()
    {
        var things = ProblemTests.Freedoom.Value.Things;
        var faults = new List<string>();
        foreach (var challenge in new[] { 0.0, 0.5, 1.0 })
        {
            for (var i = 0; i < 15; i++)
            {
                var made = MapMaker.Make(For("doom", $"balance-{i}", (MapSize)(i % 4), bosses: i % 3) with { Challenge = challenge });
                var hard = Balance.Read(made.Map.Things, things, "doom")!.Single(r => r.Difficulty == "Hard");
                if (hard.Ratio < 1) faults.Add($"challenge {challenge}, seed balance-{i}: {hard.Sentence}");
            }
        }

        Assert.Empty(faults);
    }

    [Fact]
    public void AMadeMapHasNoProblemsWithTheGamesOwnPicturesAndObjects()
    {
        var resources = ProblemTests.Freedoom.Value;
        var faults = new List<string>();
        for (var i = 0; i < 10; i++) faults.AddRange(Faults(MapMaker.Make(For("doom", $"pictures-{i}", MapSize.Large) with { Outdoors = OutdoorShare.Lots }), resources).Select(f => $"seed pictures-{i}: {f}"));
        Assert.Empty(faults);
    }

    /// <summary>The music: the seed picks the same track from the same games every time; another game's track is copied in and named in the MAPINFO, the base game's own is only named.</summary>
    [Fact]
    public void TheSeedPicksATrackAndAnotherGamesIsCopiedIn()
    {
        (string, string)[] pool = [("doom2", "D_RUNNIN"), ("doom2", "D_STALKS"), ("heretic", "MUS_E1M1"), ("heretic", "MUS_E1M2")];
        Assert.Equal(MadeMusic.Pick("misty-cavern", pool), MadeMusic.Pick("Misty Cavern", [.. pool.Reverse()]));
        Assert.Null(MadeMusic.Pick("misty-cavern", []));
        Assert.True(Enumerable.Range(0, 20).Select(i => MadeMusic.Pick($"seed-{i}", pool)).Distinct().Count() > 1);

        var engine = GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        Load(out var doom, out var heretic, temp, engine, out var project);
        Assert.Equal("MUS_E1M1", MadeMusic.Use(project, doom, heretic, "MUS_E1M1", "MAP01", engine));
        Assert.True(File.Exists(Path.Combine(project.Folder, "music", "MUS_E1M1.lmp")));
        Assert.Contains("music = \"MUS_E1M1\"", File.ReadAllText(Path.Combine(project.Folder, WadSmith.Core.Projects.ProjectMapInfo.FileName)));
        Assert.Equal("D_RUNNIN", MadeMusic.Use(project, doom, doom, "D_RUNNIN", "MAP01", engine));
        Assert.False(File.Exists(Path.Combine(project.Folder, "music", "D_RUNNIN.lmp")));
        Assert.Equal("D_RUNNIN", project.Info.Maps.Single().Music);
    }

    private static void Load(out GameResources doom, out GameResources heretic, TempFolder temp, string engine, out WadSmith.Core.Projects.Project project)
    {
        project = WadSmith.Core.Projects.Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var doomWad = GameFiles.Require("DOOM2.WAD");
        var hereticWad = GameFiles.Require("HERETIC.WAD");
        doom = GameResources.Load("doom2", project.Folder, [new FoundGame(GameCatalog.Get("doom2")!, doomWad, new FileInfo(doomWad).Length)], engine)!;
        heretic = GameResources.Load("heretic", null, [new FoundGame(GameCatalog.Get("heretic")!, hereticWad, new FileInfo(hereticWad).Length)], engine)!;
    }

    /// <summary>An episode: maps in a row from the first slot, each with its own seed and title, the challenge rising, the bosses in the last (one halfway through five or more).</summary>
    [Fact]
    public void AnEpisodeRisesInChallengeWithItsBossesLast()
    {
        var five = Episodes.Of(For("doom", "grim-keep", bosses: 2) with { Maps = 5, Challenge = 0.5 });
        Assert.Equal(["MAP01", "MAP02", "MAP03", "MAP04", "MAP05"], five.Select(m => m.Slot));
        Assert.Equal(["grim-keep", "grim-keep-2", "grim-keep-3", "grim-keep-4", "grim-keep-5"], five.Select(m => m.Seed));
        Assert.Equal([0, 0, 1, 0, 2], five.Select(m => m.Bosses));
        Assert.True(five.Zip(five.Skip(1)).All(p => p.First.Challenge < p.Second.Challenge));
        Assert.Equal(["E1M1", "E1M2", "E1M3"], Episodes.Of(For("heretic", "grim-keep") with { Maps = 3 }).Select(m => m.Slot));
        Assert.Single(Episodes.Of(For("doom", "grim-keep")));
        var settings = For("doom", "grim-keep") with { Maps = 3 };
        Assert.Equal(3, Enumerable.Range(0, 3).Select(i => Episodes.TitleOf(settings, i)).Distinct().Count());
        Assert.Equal("Grim Keep", Episodes.TitleOf(settings, 0));

        using var temp = new TempFolder();
        var (project, made) = MadeProjects.Create(Path.Combine(temp.Path, "Grim Keep"), settings);
        Assert.Equal(["MAP01", "MAP02", "MAP03"], project.Info.Maps.Select(m => m.Slot));
        Assert.Equal([null, null, WadSmith.Core.Projects.ProjectMapInfo.EndToTitle], project.Info.Maps.Select(m => m.Next));
        Assert.All(made, m => Assert.Empty(Faults(m)));
        Assert.True(made[2].Plan.MonsterCounts.Hard > 0);
    }

    [Fact]
    public void MakeItWritesAProjectThatKeepsItsSettings()
    {
        using var temp = new TempFolder();
        var settings = For("doom", "bleak-depot", MapSize.Small) with { Monsters = [.. FamilyPalette.For("heretic")!.Monsters.Take(2)] };
        var (project, all) = MadeProjects.Create(Path.Combine(temp.Path, "Bleak Depot"), settings, prepare: (_, _) => new Lent(m => 20000 + m.Number));
        var made = all.Single();
        Assert.Empty(made.Failed);
        Assert.Equal("Bleak Depot", project.Info.Name);
        Assert.Equal("MAP01", project.Info.Maps.Single().Slot);
        Assert.Contains(made.Map.Things, t => t.OnMap && t.Type > 20000);
        var back = SettingsJson.Read(File.ReadAllText(Path.Combine(project.Folder, MadeProjects.SettingsFile)))!;
        Assert.Equal(SettingsJson.Write(settings), SettingsJson.Write(back));
        Assert.Equal(settings.Monsters, back.Monsters);
    }
}
