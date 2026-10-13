using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>The live checks (D20): every rule fires on its planted fault, a clean map shows none, and every fix leaves the map clean.</summary>
public sealed class ProblemTests(ITestOutputHelper output)
{
    internal static readonly Lazy<GameResources> Freedoom = new(() =>
    {
        var wad = GameFiles.Require("freedoom2.wad");
        var games = GameLocator.Scan([Path.GetDirectoryName(wad)!]);
        return GameResources.Load("freedoom2", null, games, GameFiles.Require("gzdoom.pk3")) ?? throw new InvalidOperationException("freedoom2 was not recognised");
    });

    /// <summary>An object as WadSmith places one: on every difficulty and in every game mode.</summary>
    private static Thing Placed(Thing t)
    {
        foreach (var flag in Editor.Smart.Things.Everywhere("doom")) t.SetFlag(flag, true);
        return t;
    }

    private static IReadOnlyList<Problem> Check(EditorMap map) => Problems.Check(MapGeometry.Build(map.Frame), Freedoom.Value, Look.For("doom"), "doom");

    /// <summary>The starter room with an exit switch near the north end of its west wall (a 64 piece of it): nothing to report.</summary>
    private static (EditorMap Map, History History) Clean()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        history.Record("Make the exit", e => Exits.Make(e, MapGeometry.Build(map.Frame), map.Lines[0], false, Look.For("doom"), new MapPoint(-256, 200)));
        return (map, history);
    }

    private static Problem Only(EditorMap map, string rule)
    {
        var found = Check(map);
        Assert.True(found.Count == 1 && found[0].Rule == rule, $"expected only {rule}, found: {string.Join("; ", found.Select(p => p.Rule + ": " + p.Sentence))}");
        return found[0];
    }

    private static void FixAndCheckClean(EditorMap map, History history, Problem problem)
    {
        Assert.NotNull(problem.Fix);
        history.Record(problem.FixLabel!, problem.Fix);
        Assert.Empty(Check(map));
    }

    /// <summary>
    /// Every rule runs over every map of the Doom-format games (as remixed: translated) without failing; what they find
    /// is printed. The games as shipped can be finished, so no key and no exit in them is out of reach (the control for those rules),
    /// and no door their players walk through opens too low; Freedoom has such doors (GZDoom opens Freedoom 2 MAP22's
    /// area 266 from 72 to 92, 20 high, as the rule says).
    /// </summary>
    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void EveryMapOfEveryGameCanBeChecked(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var counts = new SortedDictionary<string, int>();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var maps = 0;
        foreach (var lumps in MapLocator.Find(WadContainer.Open(GameFiles.Require(fileName))))
        {
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(MapConversion.Read(lumps, "doom"), xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            foreach (var p in Problems.Check(geometry, null, Look.For("doom"), "doom"))
            {
                counts[p.Rule] = counts.GetValueOrDefault(p.Rule) + 1;
                if (p.Rule.EndsWith("out-of-reach", StringComparison.Ordinal) || p.Rule.StartsWith("key-out-of-reach", StringComparison.Ordinal)) output.WriteLine($"{fileName} {lumps.Name}: {p.Sentence}");
            }

            // As a remix opens, with the map itself as the original: its own narrow ways and low openings are its author's.
            Assert.DoesNotContain(Problems.Check(geometry, null, Look.For("doom"), "doom", FitBaseline.Of(geometry)), p => p.Rule is "passage-too-narrow" or "passage-too-low");
            maps++;
        }

        output.WriteLine($"{fileName}: {maps} maps in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms (with reading); maps with each problem: {string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Assert.DoesNotContain(counts.Keys, k => k.StartsWith("key-out-of-reach", StringComparison.Ordinal));
        // The Progression graph (D29): every exit of the games as shipped is reached from the start.
        Assert.DoesNotContain("exit-out-of-reach", counts.Keys);
        if (!fileName.StartsWith("freedoom", StringComparison.OrdinalIgnoreCase)) Assert.DoesNotContain("door-too-low", counts.Keys);
    }

    [Fact]
    public void ACleanMapShowsNoProblems() => Assert.Empty(Check(Clean().Map));

    /// <summary>A room east of the starter room joined to it by a corridor <paramref name="width"/> wide.</summary>
    private static (EditorMap Map, History History) WithCorridor(double width)
    {
        var (map, history) = Clean();
        foreach (var (x0, y0, x1, y1) in new[] { (256.0, -width / 2, 384.0, width / 2), (384.0, -256.0, 640.0, 256.0) })
        {
            var geometry = MapGeometry.Build(map.Frame);
            history.Record("Draw", e => ShapeDrawing.Draw(e, geometry, [new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0)], Look.For("doom")));
        }

        return (map, history);
    }

    [Fact]
    public void ACorridorTooNarrowForThePlayerIsAProblemAndAWideOneIsNot()
    {
        var p = Only(WithCorridor(24).Map, "passage-too-narrow");
        Assert.Contains("24 across", p.Sentence);
        Assert.Equal(2, p.Show.Count);
        Assert.Empty(Check(WithCorridor(32).Map));
    }

    [Fact]
    public void TheMapsOwnNarrowWaysAreLeftOutButOneTheUserDrawsIsNot()
    {
        var (map, history) = WithCorridor(24);
        var original = MapGeometry.Build(map.Frame);
        var baseline = FitBaseline.Of(original);
        Assert.Empty(Problems.Check(original, Freedoom.Value, Look.For("doom"), "doom", baseline));
        Assert.Empty(Problems.Check(original, Freedoom.Value, Look.For("doom"), "doom", FitBaseline.Pending));

        // A second thin corridor, south of the first: only it is the user's.
        foreach (var (x0, y0, x1, y1) in new[] { (256.0, -200.0, 384.0, -184.0) })
        {
            var geometry = MapGeometry.Build(map.Frame);
            history.Record("Draw", e => ShapeDrawing.Draw(e, geometry, [new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0)], Look.For("doom")));
        }

        var found = Assert.Single(Problems.Check(MapGeometry.Build(map.Frame), Freedoom.Value, Look.For("doom"), "doom", baseline));
        Assert.Equal("passage-too-narrow", found.Rule);
        Assert.Contains("16 across", found.Sentence);
    }

    [Fact]
    public void APostBesideAWallIsMarkedOnThePlanButIsNoProblem()
    {
        // A raised post 16 from the starter room's west wall: bars are made so.
        var (map, history) = Clean();
        var geometry = MapGeometry.Build(map.Frame);
        var posts = new List<Sector>();
        history.Record("Post", e => posts.AddRange(ShapeDrawing.Draw(e, geometry, [new(-240, -8), new(-240, 8), new(-224, 8), new(-224, -8)], Look.For("doom"))));
        history.Record("Raise", e => e.Set(posts.Single(s => s.OnMap && MapGeometry.Build(map.Frame).WallBoundsAt(s.Slot) is { MaxX: -224 }), s => s.FloorHeight, (s, v) => s.FloorHeight = v, 128.0));
        var gap = Assert.Single(Fit.Gaps(MapGeometry.Build(map.Frame)));
        Assert.Equal(GapKind.Pinch, gap.Kind);
        Assert.Empty(Check(map));
    }

    [Fact]
    public void AnOpeningLowerThanThePlayerIsAProblemAndTheFixRaisesTheCeiling()
    {
        var (map, history) = WithCorridor(64);
        var corridor = map.Sectors.Single(s => s.OnMap && MapGeometry.Build(map.Frame).WallBoundsAt(s.Slot) is { MinX: 256, MaxX: 384 });
        history.Record("Low", e =>
        {
            e.Set(corridor, s => s.FloorHeight, (s, v) => s.FloorHeight = v, 16.0);
            e.Set(corridor, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, 72.0);
        });
        Assert.Empty(Check(map));
        history.Record("Lower", e => e.Set(map.Sectors[0], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, 64.0));
        var p = Only(map, "passage-too-low");
        Assert.Contains("only 48 high", p.Sentence);
        FixAndCheckClean(map, history, p);
        Assert.Equal(72.0, map.Sectors[0].CeilingHeight);
    }

    [Fact]
    public void NoPlayerStartIsAnErrorAndTheFixPutsOneInARoom()
    {
        var (map, history) = Clean();
        history.Record("Delete", e => e.Remove(map.Things[0]));
        var p = Only(map, "no-start");
        Assert.Equal(Severity.Error, p.Severity);
        FixAndCheckClean(map, history, p);
    }

    [Fact]
    public void TwoPlayerStartsAreSaidWithoutAFix()
    {
        var (map, history) = Clean();
        history.Record("Add", e => e.Add(Placed(new Thing { X = 100, Y = 100, Type = 1 })));
        var p = Only(map, "many-starts");
        Assert.Null(p.Fix);
        Assert.Equal(2, p.Show.Count);
    }

    [Fact]
    public void NoExitIsSaid()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        Only(map, "no-exit");
    }

    [Fact]
    public void ARoomWithAGapIsNotClosed()
    {
        var (map, history) = Clean();
        var wall = map.Lines[2];
        history.Record("Break", e =>
        {
            e.Remove(wall);
            e.Remove(wall.Front);
        });
        Assert.Contains(Check(map), p => p.Rule == "unclosed" && p.Show.Contains(map.Sectors[0]));
    }

    [Fact]
    public void AWallWithNoTextureIsFixedWithTheRoomsTexture()
    {
        var (map, history) = Clean();
        history.Record("Clear", e => e.Set(map.Lines[1].Front, s => s.Middle, (s, v) => s.Middle = v, "-"));
        var p = Only(map, "missing-texture");
        FixAndCheckClean(map, history, p);
        Assert.Equal("STARTAN3", map.Lines[1].Front.Middle);
    }

    [Fact]
    public void ATextureTheGameDoesNotHaveIsNamed()
    {
        var (map, history) = Clean();
        history.Record("Paint", e => e.Set(map.Lines[1].Front, s => s.Middle, (s, v) => s.Middle = v, "BRICK99"));
        Assert.Contains("BRICK99", Only(map, "unknown-texture").Sentence);
    }

    [Fact]
    public void AWallBetweenRoomsNotMarkedOpenIsFixed()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        history.Record("Start", e =>
        {
            e.Add(Placed(new Thing { X = 64, Y = 64, Type = 1 }));
            e.Flag(shared, "twosided", false);
            Exits.Make(e, MapGeometry.Build(map.Frame), map.Lines.First(l => l.OnMap && l.Back is null), false, Look.For("doom"));
        });
        FixAndCheckClean(map, history, Only(map, "two-sided"));
    }

    [Fact]
    public void AThingOutsideEveryRoomIsSaid()
    {
        var (map, history) = Clean();
        history.Record("Add", e => e.Add(Placed(new Thing { X = 5000, Y = 5000, Type = 3001 })));
        Only(map, "outside");
    }

    [Fact]
    public void AMonsterInAWallIsNudgedOut()
    {
        var (map, history) = Clean();

        // An imp (radius 20) 10 units from the south wall at y = -256.
        var imp = Placed(new Thing { X = 128, Y = -246, Type = 3001 });
        history.Record("Add", e => e.Add(imp));
        var p = Only(map, "stuck");
        Assert.Same(imp, Assert.Single(p.Show));
        FixAndCheckClean(map, history, p);
        Assert.True(imp.Y >= -256 + 20, $"the imp is at {imp.Y}");
    }

    [Fact]
    public void ATeleporterToNowhereIsSaid()
    {
        var (map, history) = Clean();
        history.Record("Teleport", e =>
        {
            e.Set(map.Lines[1], l => l.Special, (l, v) => l.Special = v, Teleports.Teleport);
            e.Set(map.Lines[1], l => l.Args[0], (l, v) => l.Args[0] = v, 99);
        });
        Only(map, "teleport-nowhere");
    }

    [Fact]
    public void ASwitchNamingATagNoAreaHasIsSaid()
    {
        var (map, history) = Clean();
        history.Record("Switch", e =>
        {
            e.Set(map.Lines[1], l => l.Special, (l, v) => l.Special = v, Doors.DoorRaise);
            e.Set(map.Lines[1], l => l.Args[0], (l, v) => l.Args[0] = v, 7);
            e.Flag(map.Lines[1], "playeruse", true);
        });
        var problem = Only(map, "tag-nowhere");
        Assert.Contains("tag 7", problem.Sentence);
        Assert.Same(map.Lines[1], Assert.Single(problem.Show));

        // Once an area has the tag, the switch works on it.
        history.Record("Tag", e => e.Set(map.Sectors[0], s => s.Tag, (s, v) => s.Tag = v, 7));
        Assert.Empty(Check(map));
    }

    /// <summary>A hall with the start and the exit, and a long room east of it behind a door locked with the red key.</summary>
    private static (EditorMap Map, History History, List<Sector> Rooms) LockedCloset()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 640, 160));
        var wall = map.Lines.Single(l => l.OnMap && l.Back is not null);
        Sector? door = null;
        history.Record("Make a door", e => door = Doors.Make(e, MapGeometry.Build(map.Frame), wall, Look.For("doom")).Door);
        var made = Doors.Recognise(MapGeometry.Build(map.Frame), door!)!;
        history.Record("Lock it", e => Doors.Change(e, made, made.Settings with { Lock = 129 }, false, false));
        history.Record("Start and exit", e =>
        {
            e.Add(Placed(new Thing { X = 128, Y = 128, Type = 1 }));
            Exits.Make(e, MapGeometry.Build(map.Frame), map.Lines.First(l => l.OnMap && l.Back is null && l.Front.Sector == rooms[0]), false, Look.For("doom"));
        });
        return (map, history, rooms);
    }

    [Fact]
    public void AKeyBehindTheDoorItOpensIsSaid()
    {
        var (map, history, _) = LockedCloset();
        var key = Placed(new Thing { X = 600, Y = 128, Type = 13 });
        history.Record("Key", e => e.Add(key));
        var problem = Only(map, "key-out-of-reach-red");
        Assert.Contains(key, problem.Show);

        // In the hall it is fine; so is a key just inside the door, picked up through it as the game does.
        history.Record("Move", e => e.Set(key, t => t.X, (t, v) => t.X = v, 64.0));
        Assert.Empty(Check(map));
        history.Record("Move", e => e.Set(key, t => t.X, (t, v) => t.X = v, 290.0));
        Assert.Empty(Check(map));
    }

    [Fact]
    public void ADoorBesideALowCeilingIsSaid()
    {
        // The door opens to 4 below the corridor's ceiling: 52 with the corridor 56 tall, too low for the player.
        var (map, history, rooms) = LockedCloset();
        history.Record("Key and low ceiling", e =>
        {
            e.Add(Placed(new Thing { X = 64, Y = 64, Type = 13 }));
            e.Set(rooms[1], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, 56.0);
        });
        var problem = Only(map, "door-too-low");
        Assert.Contains("52", problem.Sentence);

        // A ceiling an action raises in play does not count; nor does one high enough.
        var raise = ActionCatalog.Find("Ceiling_RaiseByValue")!.Number;
        history.Record("Raise it in play", e =>
        {
            e.Set(rooms[1], s => s.Tag, (s, v) => s.Tag = v, 9);
            e.Set(map.Lines[0], l => l.Special, (l, v) => l.Special = v, raise);
            e.Set(map.Lines[0], l => l.Args[0], (l, v) => l.Args[0] = v, 9);
            e.Flag(map.Lines[0], "playercross", true);
        });
        Assert.DoesNotContain(Check(map), p => p.Rule == "door-too-low");
        history.Undo();
        history.Record("Higher", e => e.Set(rooms[1], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, 64.0));
        Assert.Empty(Check(map));
    }

    [Fact]
    public void ADoorMetBeforeItsKeyOpensOnceTheKeyIsFound()
    {
        // The walk meets the red door first, then finds the red key in a room north of the hall.
        var (map, history, rooms) = LockedCloset();
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Side room", e => ShapeDrawing.Draw(e, geometry, [new(0, 256), new(0, 512), new(256, 512), new(256, 256)], Look.For("doom")));
        history.Record("Keys", e =>
        {
            e.Add(Placed(new Thing { X = 600, Y = 128, Type = 5 }));
            e.Add(Placed(new Thing { X = 128, Y = 480, Type = 13 }));
        });
        Assert.Empty(Check(map));
    }

    [Fact]
    public void AKeyBehindAnotherDoorWhoseKeyIsReachableIsFine()
    {
        var (map, history, _) = LockedCloset();
        history.Record("Keys", e =>
        {
            e.Add(Placed(new Thing { X = 600, Y = 128, Type = 5 }));
            e.Add(Placed(new Thing { X = 64, Y = 64, Type = 13 }));
        });
        Assert.Empty(Check(map));

        // Swap them round: the red key now waits behind its own door, and so the blue one with it.
        history.Undo();
        history.Record("Keys", e =>
        {
            e.Add(Placed(new Thing { X = 64, Y = 64, Type = 5 }));
            e.Add(Placed(new Thing { X = 600, Y = 128, Type = 13 }));
        });
        Only(map, "key-out-of-reach-red");
    }

    [Fact]
    public void DoorTracksThatSlideAreAnchored()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 96, 384, 160), (384, 0, 640, 256));
        var wall = map.Lines.Single(l => l.OnMap && l.Start.X == 256 && l.End.X == 256 && l.Back is not null);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a door", e => Doors.Make(e, geometry, wall, Look.For("doom")));
        history.Record("Start and exit", e =>
        {
            e.Add(Placed(new Thing { X = 128, Y = 128, Type = 1 }));
            Exits.Make(e, MapGeometry.Build(map.Frame), map.Lines.First(l => l.OnMap && l.Back is null && l.Front.Sector == rooms[0]), false, Look.For("doom"));
        });
        Assert.Empty(Check(map));
        var track = map.Lines.First(l => l.OnMap && l.Flag("dontpegbottom"));
        history.Record("Unanchor", e => e.Flag(track, "dontpegbottom", false));
        FixAndCheckClean(map, history, Only(map, "door-tracks"));
    }

    [Fact]
    public void ALockedDoorWithoutItsKeyIsSaidUntilTheKeyIsPlaced()
    {
        var (map, history) = Clean();
        history.Record("Lock", e =>
        {
            e.Set(map.Lines[2], l => l.Special, (l, v) => l.Special = v, Doors.DoorLockedRaise);
            e.Set(map.Lines[2], l => l.Args[3], (l, v) => l.Args[3] = v, 129);
        });
        Assert.Contains("red key", Only(map, "missing-key-red").Sentence);
        history.Record("Add", e => e.Add(Placed(new Thing { X = 0, Y = 64, Type = 38 })));
        Assert.Empty(Check(map));
    }
}
