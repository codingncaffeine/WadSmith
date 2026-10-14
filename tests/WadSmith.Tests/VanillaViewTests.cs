using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Classic;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// The 1993 renderer's tables counted for a view (6b.4): visplanes, wall pieces, sprites and openings, on maps made to load
/// them a known amount — a plain room, and a hall cut across into strips each lit its own level, so every strip's floor and
/// ceiling is a plane of its own.
/// </summary>
public sealed class VanillaViewTests(ITestOutputHelper output)
{
    /// <summary>
    /// A hall along x of <paramref name="strips"/> rooms <paramref name="width"/> wide and 256 deep, side by side, joined by
    /// open walls; room i lit <paramref name="light"/>(i). One-sided walls round it show STARTAN3; the player starts in the
    /// first room's middle facing along the hall (east), with <paramref name="monsters"/> imps in the last room.
    /// </summary>
    internal static ClassicMap Hall(int strips, Func<int, short> light, int width = 32, int monsters = 0)
    {
        var m = new ClassicMap { Name = "MAP01", Format = MapFormat.Doom };
        for (var i = 0; i <= strips; i++)
        {
            m.Vertices.Add(new ClassicVertex((short)(i * width), 0));
            m.Vertices.Add(new ClassicVertex((short)(i * width), 256));
        }

        for (var i = 0; i < strips; i++) m.Sectors.Add(new ClassicSector(0, 128, "FLOOR4_8", "CEIL3_5", light(i), 0, 0));
        ushort Side(int sector, string middle)
        {
            m.Sides.Add(new ClassicSide(0, 0, "-", "-", middle, (ushort)sector));
            return (ushort)(m.Sides.Count - 1);
        }

        ushort Low(int i) => (ushort)(2 * i);
        ushort High(int i) => (ushort)(2 * i + 1);
        for (var i = 0; i < strips; i++)
        {
            // South wall right to left and north wall left to right, so each faces into its room (the front is on the right).
            m.Lines.Add(new ClassicLine(Low(i + 1), Low(i), 1, 0, 0, Side(i, "STARTAN3"), ClassicLine.NoSide));
            m.Lines.Add(new ClassicLine(High(i), High(i + 1), 1, 0, 0, Side(i, "STARTAN3"), ClassicLine.NoSide));
        }

        m.Lines.Add(new ClassicLine(Low(0), High(0), 1, 0, 0, Side(0, "STARTAN3"), ClassicLine.NoSide));
        m.Lines.Add(new ClassicLine(High(strips), Low(strips), 1, 0, 0, Side(strips - 1, "STARTAN3"), ClassicLine.NoSide));
        for (var i = 1; i < strips; i++)
        {
            // North to south: the front faces west, into room i − 1; two-sided (flag 4).
            m.Lines.Add(new ClassicLine(High(i), Low(i), 4, 0, 0, Side(i - 1, "-"), Side(i, "-")));
        }

        m.Things.Add(new ClassicThing((short)(width / 2), 128, 0, 1, 7));
        for (var i = 0; i < monsters; i++) m.Things.Add(new ClassicThing((short)((strips - 1) * width + width / 2), (short)(8 + i * 240 / Math.Max(1, monsters)), 180, 3001, 7));
        NodeBuilder.Build(m);
        return m;
    }

    [Fact]
    public void APlainRoomHoldsItsFloorItsCeilingAndItsWalls()
    {
        var view = new VanillaView(Hall(1, _ => 160, width: 512));
        var load = view.Count(16, 128, 0);
        Assert.Equal(2, load.Visplanes);
        Assert.InRange(load.WallPieces, 1, 3);
        Assert.Equal(0, load.Sprites);
    }

    [Fact]
    public void EveryStripLitItsOwnLevelIsAFloorAndACeilingOfItsOwn()
    {
        // Ninety-six strips: two planes each, all in view down the hall.
        var dark = Hall(96, i => (short)(255 - 2 * i));
        var load = new VanillaView(dark).Count(16, 128, 0);
        output.WriteLine($"96 strips: {load}");
        Assert.InRange(load.Visplanes, 2 * 96 - 4, 2 * 96 + 8);
        Assert.True(load.Visplanes > VanillaView.MostVisplanes);

        // Lit alike, the strips share one floor and one ceiling, and the open walls between them are not drawn at all.
        var alike = new VanillaView(Hall(96, _ => 160)).Count(16, 128, 0);
        Assert.Equal(2, alike.Visplanes);

        // Forty strips stay well inside the table; facing away from the hall the strips are not seen.
        Assert.InRange(new VanillaView(Hall(40, i => (short)(255 - 2 * i))).Count(16, 128, 0).Visplanes, 2 * 40 - 4, VanillaView.MostVisplanes - 1);
        Assert.InRange(new VanillaView(dark).Count(16, 128, 180).Visplanes, 1, 4);
    }

    [Fact]
    public void ObjectsInARoomTheViewReachesAreSpritesWhereverTheyStand()
    {
        var load = new VanillaView(Hall(8, _ => 160, monsters: 20)).Count(16, 128, 0);
        Assert.Equal(20, load.Sprites);
        Assert.Equal(0, new VanillaView(Hall(8, _ => 160, monsters: 20)).Count(16, 128, 180).Sprites);
    }

    [Fact]
    public void TheSweepFindsTheOverflowingViewInTheRoomItIsSeenFrom()
    {
        var found = VanillaLimits.Find(Hall(96, i => (short)(255 - 2 * i)), cancel: TestContext.Current.CancellationToken);
        var visplanes = found.Where(o => o.Table == VanillaTable.Visplanes).ToList();
        Assert.NotEmpty(visplanes);
        // From the first room, looking east down the hall, every strip is in sight.
        var worst = visplanes[0];
        output.WriteLine($"worst: {worst}");
        Assert.InRange(worst.Count, 180, 200);
        Assert.Equal(0, worst.Degrees);
        Assert.True(worst.X < 96);
        Assert.Empty(VanillaLimits.Find(Hall(40, i => (short)(255 - 2 * i)), cancel: TestContext.Current.CancellationToken));
    }

    /// <summary>The sweep over every map of a free game with its own nodes: what it finds and how long it takes (read in the log).</summary>
    [Theory]
    [InlineData("freedoom1.wad")]
    [InlineData("freedoom2.wad")]
    public void EveryMapOfAFreeGameIsSwept(string fileName)
    {
        var wad = WadSmith.Core.Containers.WadContainer.Open(GameFiles.Require(fileName));
        var total = System.Diagnostics.Stopwatch.StartNew();
        foreach (var map in MapLocator.Find(wad))
        {
            var classic = ClassicMap.Read(map);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var found = VanillaLimits.Find(classic, cancel: TestContext.Current.CancellationToken);
            output.WriteLine($"{map.Name}: {classic.Sectors.Count} rooms, {started.ElapsedMilliseconds} ms; {string.Join(", ", found.GroupBy(o => o.Table).Select(g => $"{g.Key} in {g.Count()} rooms, worst {g.Max(o => o.Count)}"))}");
        }

        output.WriteLine($"all: {total.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// In a project that plays in vanilla, Problems says where a view overflows and shows the rooms it is seen from; a
    /// limit-removing project is not told, nor a hall with fewer strips.
    /// </summary>
    [Fact]
    public void AVanillaProjectsProblemsShowTheRoomTheOverflowingViewIsSeenFrom()
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        (Problem[] Found, EditorMap Map) Check(ClassicMap hall, string target)
        {
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(MapConversion.FromClassic(hall, "doom"), xlat, []));
            return ([.. Problems.Check(MapGeometry.Build(map.Frame), null, Look.For("doom"), "doom", null, new ClassicRules("doom", xlat, target))], map);
        }

        var dark = Hall(96, i => (short)(255 - 2 * i));
        var (found, map) = Check(dark, Targets.Vanilla);
        var visplanes = Assert.Single(found, p => p.Rule == "visplanes");
        output.WriteLine(visplanes.Sentence);
        Assert.Contains("of its 128", visplanes.Sentence, StringComparison.Ordinal);
        Assert.Contains(map.Sectors[0], visplanes.Show);
        Assert.DoesNotContain(Check(dark, Targets.LimitRemoving).Found, p => p.Rule == "visplanes");
        Assert.DoesNotContain(Check(Hall(40, i => (short)(255 - 2 * i)), Targets.Vanilla).Found, p => p.Rule == "visplanes");
    }
}
