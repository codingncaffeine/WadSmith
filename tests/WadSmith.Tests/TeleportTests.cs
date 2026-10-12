using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Translation;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Teleporters: walls that send to an arrival spot, found in any map and made with two clicks.</summary>
public sealed class TeleportTests(ITestOutputHelper output)
{
    /// <summary>Doom's classic teleport actions (walk-over, monsters only, switch, and Boom's silent ones).</summary>
    private static readonly HashSet<int> ClassicTeleports = [39, 97, 125, 126, 174, 195, 207, 208, 209, 210];

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    /// <summary>
    /// Teleporters that lead nowhere as the games shipped, confirmed in GZDoom by tools/engine/door-check.sh
    /// (2026-10-12): Doom II MAP12 wall 311 (no area has tag 13) and SIGIL E5M2 walls 1205–1208 (no arrival
    /// spot in the tag-15 area) send the player nowhere. TNT MAP27's four are monster-only lines to an area
    /// with no arrival spot. Every other teleporter finds its spot, among them SIGIL's whose spot stands on
    /// the tagged area's edge (the engine sends the player there).
    /// </summary>
    private static readonly Dictionary<string, int> Nowhere = new() { ["DOOM2.WAD"] = 1, ["SIGIL_v1_21.wad"] = 4, ["TNT.WAD"] = 4 };

    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void EveryClassicTeleporterIsOneAndFindsWhereItSends(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var (count, nowhere) = (0, 0);
        foreach (var lumps in MapLocator.Find(wad))
        {
            var classic = MapConversion.Read(lumps, "doom");
            var expected = Enumerable.Range(0, classic.Lines.Count).Where(i => ClassicTeleports.Contains(classic.Lines[i].Special)).ToHashSet();
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(classic, xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            var found = map.Lines.Where(Teleports.IsTeleport).Select(l => l.Slot).ToHashSet();
            Assert.True(expected.SetEquals(found), $"{fileName} {lumps.Name}: classic teleporters {string.Join(", ", expected.Order())}; recognised {string.Join(", ", found.Order())}");
            foreach (var l in map.Lines.Where(Teleports.IsTeleport))
            {
                var t = Teleports.Recognise(geometry, l)!;
                count++;
                if (t.Arrivals.Count == 0) nowhere++;
                else Assert.All(t.Arrivals, a => Assert.Contains(l, Teleports.SendersTo(geometry, a)));
            }
        }

        output.WriteLine($"{fileName}: {count} teleporter walls, {count - nowhere} lead somewhere, {nowhere} to no arrival spot");
        Assert.True(count > 0);
        Assert.Equal(Nowhere.GetValueOrDefault(fileName), nowhere);
    }

    /// <summary>A pad room in the middle of a room, and a second room apart to arrive in.</summary>
    private static (EditorMap Map, History History, Sector Pad, Sector Room, Sector Far) Rooms()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (96, 96, 160, 160), (512, 0, 768, 256));
        return (map, history, rooms[1], rooms[0], rooms[2]);
    }

    [Fact]
    public void APadSendsWhoeverStepsOnToTheArrivalSpot()
    {
        var (map, history, pad, room, far) = Rooms();
        var before = Udmf(map.ToModel());
        (Thing? Arrival, string? Why) made = (null, null);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a teleporter", e => made = Teleports.Make(e, geometry, pad, new MapPoint(640, 128), 180, "doom"));
        Assert.Null(made.Why);
        var arrival = Assert.IsType<Thing>(made.Arrival);
        Assert.Equal((Teleports.ArrivalType, 1, 180), (arrival.Type, arrival.Tid, arrival.Angle));
        DrawingTests.AssertSound(map);

        // The pad's four walls send, each facing out of the pad, so stepping on teleports and stepping off does not.
        var now = MapGeometry.Build(map.Frame);
        var senders = Teleports.SendersTo(now, arrival);
        Assert.Equal(4, senders.Count);
        Assert.All(senders, l => Assert.True(l.Front.Sector == room && l.Back!.Sector == pad && l.Flag("playercross") && l.Flag("repeatspecial")));
        Assert.All(senders, l => Assert.Same(arrival, Assert.Single(Teleports.Recognise(now, l)!.Arrivals)));

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void ARoomWithoutAWayOnOrAnArrivalOnThePadIsRefused()
    {
        var (map, history, pad, _, far) = Rooms();
        (Thing? Arrival, string? Why) made = (null, null);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a teleporter", e => made = Teleports.Make(e, geometry, far, new MapPoint(128, 40), 0, "doom"));
        Assert.Contains("a way onto it", made.Why);
        history.Record("Make a teleporter", e => made = Teleports.Make(e, geometry, pad, new MapPoint(128, 128), 0, "doom"));
        Assert.Contains("somewhere else", made.Why);
        Assert.Equal("Draw a room", history.UndoLabel);
    }
}
