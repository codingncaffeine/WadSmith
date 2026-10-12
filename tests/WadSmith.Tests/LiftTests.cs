using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Translation;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Lifts: found in any map from the actions that lower them, made from a room with one button.</summary>
public sealed class LiftTests(ITestOutputHelper output)
{
    /// <summary>Doom's classic lift actions: W1, S1, SR, WR and the blazing ones.</summary>
    private static readonly HashSet<int> ClassicLifts = [10, 21, 62, 88, 120, 121, 122, 123];

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void EveryClassicLiftIsRecognisedAndNothingElse(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var lifts = 0;
        foreach (var lumps in MapLocator.Find(wad))
        {
            var classic = MapConversion.Read(lumps, "doom");
            var expected = new HashSet<int>();
            foreach (var l in classic.Lines.Where(l => ClassicLifts.Contains(l.Special)))
            {
                var tag = Math.Max(l.Id, 0);
                if (tag == 0 && l.Back >= 0) expected.Add(classic.Sides[l.Back].Sector);
                for (var s = 0; s < classic.Sectors.Count; s++)
                {
                    if (tag != 0 && classic.Sectors[s].Id == tag) expected.Add(s);
                }
            }

            var map = EditorMap.FromModel(MapTranslation.ToZDoom(classic, xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            var found = map.Sectors.Where(s => Lifts.Recognise(geometry, s) is not null).Select(s => s.Slot).ToHashSet();
            Assert.True(expected.SetEquals(found), $"{fileName} {lumps.Name}: missed {string.Join(", ", expected.Except(found).Order())}; not lifts {string.Join(", ", found.Except(expected).Order())}");
            lifts += found.Count;
        }

        output.WriteLine($"{fileName}: {lifts} lifts, all recognised, nothing else");
        Assert.True(lifts > 0);
    }

    /// <summary>A low room (floor 0), a raised room (64) and another raised room beyond it (64), side by side.</summary>
    private static (EditorMap Map, History History, Sector Low, Sector Platform, Sector High) Rooms()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128), (256, 0, 384, 128));
        history.Record("Raise", e =>
        {
            e.Set(rooms[1], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64);
            e.Set(rooms[2], s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64);
        });
        return (map, history, rooms[0], rooms[1], rooms[2]);
    }

    [Fact]
    public void ARoomBesideALowerOneBecomesDoomsKindOfLift()
    {
        var (map, history, low, platform, high) = Rooms();
        var before = Udmf(map.ToModel());
        string? why = null;
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a lift", e => why = Lifts.Make(e, geometry, platform));
        Assert.Null(why);
        DrawingTests.AssertSound(map);
        Assert.Equal(1, platform.Tag);

        var lift = Lifts.Recognise(MapGeometry.Build(map.Frame), platform);
        Assert.NotNull(lift);
        Assert.Equal(0, lift.GoesDownTo);
        Assert.Equal(new LiftSettings(false), lift.Settings);
        Assert.True(lift.Monsters);
        Assert.Equal(["a switch", "a line walked over"], Triggers.Ways(lift.Triggers));

        // The switch faces the low room, where the player stands to use it; the line on the high side is walked across.
        var use = Assert.Single(lift.Triggers, l => l.Flag("playeruse"));
        Assert.Same(low, use.Front.Sector);
        Assert.Equal([1, 32, 105, 0, 0], use.Args);
        var cross = Assert.Single(lift.Triggers, l => l.Flag("playercross"));
        Assert.Contains(high, new[] { cross.Front.Sector, cross.Back!.Sector });

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void TheSwitchIsTurnedToFaceTheLowRoomWhenItFacedTheLift()
    {
        // The platform is drawn first, so the wall it shares with the low room faces the platform.
        var (map, history, rooms) = MovingTests.Drawn((128, 0, 256, 128), (0, 0, 128, 128));
        var (platform, low) = (rooms[0], rooms[1]);
        history.Record("Raise", e => e.Set(platform, s => s.FloorHeight, (s, v) => s.FloorHeight = v, 64));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        Assert.Same(platform, shared.Front.Sector);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a lift", e => Lifts.Make(e, geometry, platform));
        Assert.Same(low, shared.Front.Sector);
        Assert.True(shared.Flag("playeruse"));
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void ARoomWithNothingLowerBesideItCannotBeALift()
    {
        var (map, history, low, _, _) = Rooms();
        string? why = null;
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a lift", e => why = Lifts.Make(e, geometry, low));
        Assert.Contains("lower floor", why);
        Assert.Equal(0, low.Tag);
    }

    [Fact]
    public void ChangingALiftChangesEveryWallThatSetsItOff()
    {
        var (map, history, _, platform, _) = Rooms();
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a lift", e => Lifts.Make(e, geometry, platform));
        var lift = Lifts.Recognise(MapGeometry.Build(map.Frame), platform)!;
        history.Record("Change the lift", e => Lifts.Change(e, lift, new LiftSettings(true, 70), monsters: false));
        var changed = Lifts.Recognise(MapGeometry.Build(map.Frame), platform)!;
        Assert.Equal(new LiftSettings(true, 70), changed.Settings);
        Assert.False(changed.Monsters);
        Assert.All(changed.Triggers, l => Assert.Equal((64, 70), (l.Args[1], l.Args[2])));
    }
}
