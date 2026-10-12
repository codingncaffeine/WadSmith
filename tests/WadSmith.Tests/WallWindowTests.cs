using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Windows: made through a thick wall or in a shared one, recognised as what they are.</summary>
public sealed class WallWindowTests(ITestOutputHelper output)
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static (Sector? Window, string? Why) Make(EditorMap map, History history, Line wall, double from, double to)
    {
        (Sector?, string?) result = (null, null);
        var geometry = MapGeometry.Build(map.Frame);
        history.Record("Make a window", e => result = WallWindows.Make(e, geometry, wall, from, to, Look));
        return result;
    }

    /// <summary>The wall of <paramref name="room"/> running up and down at <paramref name="x"/>.</summary>
    private static Line WallAt(EditorMap map, Sector room, double x) => map.Lines.Single(l => l.OnMap && l.Start.X == x && l.End.X == x && (l.Front.Sector == room || l.Back?.Sector == room));

    private static (double, double) Span(EditorMap map, Sector area)
    {
        var ys = map.Lines.Where(l => l.OnMap && (l.Front.Sector == area || l.Back?.Sector == area)).SelectMany(l => new[] { l.Start.Y, l.End.Y }).ToList();
        return (ys.Min(), ys.Max());
    }

    [Fact]
    public void AWindowThroughAThickWallOpensOntoTheRoomBehind()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (144, 0, 272, 128));
        var before = Udmf(map.ToModel());
        var wall = WallAt(map, rooms[0], 128);
        var texture = wall.Front.Middle;
        var (window, why) = Make(map, history, wall, 32, 96);
        Assert.Null(why);
        Assert.NotNull(window);
        DrawingTests.AssertSound(map);
        Assert.Equal((40.0, 104.0), (window.FloorHeight, window.CeilingHeight));
        Assert.Equal((32.0, 96.0), Span(map, window));

        var found = WallWindows.Recognise(MapGeometry.Build(map.Frame), window);
        Assert.NotNull(found);
        Assert.Equal(new HashSet<Sector>(rooms), new HashSet<Sector>(found.Rooms));
        Assert.Equal(2, found.Faces.Count);
        Assert.False(found.Blocks);

        // The wall's texture shows above and below the opening, and on its sides, its rows running on from the
        // room's walls: the parts above and below pegged to the room's ceiling, the sides lowered by as much as
        // the window's top is below it.
        Assert.All(found.Faces, l => Assert.Equal((texture, texture, "-"), (l.Front.Top, l.Front.Bottom, l.Front.Middle)));
        Assert.All(found.Faces, l => Assert.True(l.Flag("dontpegtop") && l.Flag("dontpegbottom")));
        var sides = map.Lines.Where(l => l.OnMap && l.Back is null && l.Front.Sector == window).ToList();
        Assert.Equal(2, sides.Count);
        Assert.All(sides, l => Assert.Equal((texture, 24.0), (l.Front.Middle, l.Front.OffsetY)));
        Assert.Null(WallWindows.Recognise(MapGeometry.Build(map.Frame), rooms[0]));

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void AClickMakesAWindowOfTheUsualWidthCentredOnIt()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 256), (160, 0, 288, 256));
        var wall = WallAt(map, rooms[1], 160);
        var along = wall.Start.Y == 0 ? 100.0 : 256 - 100.0;
        var (window, _) = Make(map, history, wall, along, along);
        Assert.NotNull(window);
        Assert.Equal((68.0, 132.0), Span(map, window));
        DrawingTests.AssertSound(map);

        // Near the end of the wall it stays on the wall.
        (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 256), (160, 0, 288, 256));
        var (corner, _) = Make(map, history, WallAt(map, rooms[0], 128), 0, 0);
        Assert.NotNull(corner);
        Assert.Equal(64, Span(map, corner).Item2 - Span(map, corner).Item1);
        DrawingTests.AssertSound(map);
    }

    [Fact]
    public void NoWindowWhereThereIsNoRoomBehindOrSomethingIsInTheWay()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (400, 0, 528, 128));
        var before = Udmf(map.ToModel());
        var (window, why) = Make(map, history, WallAt(map, rooms[0], 128), 32, 96);
        Assert.Null(window);
        Assert.Contains("none within 128", why);

        // A pillar room standing in the gap, part of the way along.
        (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (192, 0, 320, 128), (144, 48, 160, 80));
        (window, why) = Make(map, history, WallAt(map, rooms[0], 128), 16, 112);
        Assert.Null(window);
        Assert.NotNull(why);
        Assert.False(history.CanRedo);
        Assert.NotEqual(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void AWindowInASharedWallMakesItAThinWallClosedButForTheWindow()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 384, 128));
        var before = Udmf(map.ToModel());
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        var (window, why) = Make(map, history, shared, 48, 80);
        Assert.Null(why);
        Assert.NotNull(window);
        DrawingTests.AssertSound(map);
        Assert.Equal((40.0, 104.0), (window.FloorHeight, window.CeilingHeight));
        Assert.Equal((48.0, 80.0), Span(map, window));

        // Three pieces in the bigger room's side: the window and two closed ones.
        var pieces = map.Sectors.Where(s => s.OnMap && !rooms.Contains(s)).ToList();
        Assert.Equal(3, pieces.Count);
        Assert.All(pieces.Where(s => s != window), s => Assert.Equal((40.0, 40.0), (s.FloorHeight, s.CeilingHeight)));
        var shapes = MapGeometry.Build(map.Frame);
        Assert.All(pieces, s => Assert.True(shapes.WallBoundsAt(s.Slot) is { MinX: 128, MaxX: 144 }));
        Assert.Equal(new HashSet<Sector>(rooms), new HashSet<Sector>(WallWindows.Recognise(shapes, window)!.Rooms));
        Assert.All(pieces.Where(s => s != window), s => Assert.Null(WallWindows.Recognise(shapes, s)));

        // The window's sides beside the closed pieces show the room's texture rows too.
        var jambs = map.Lines.Where(l => l.OnMap && l.Back is { } jb && pieces.Contains(l.Front.Sector) && pieces.Contains(jb.Sector)).ToList();
        Assert.Equal(2, jambs.Count);
        Assert.All(jambs, l => Assert.Equal(24.0, (l.Front.Sector == window ? l.Front : l.Back!).OffsetY));
        Assert.All(jambs, l => Assert.True(l.Flag("dontpegtop")));

        // The rooms meet only through the window now.
        Assert.DoesNotContain(map.Lines, l => l.OnMap && l.Back is { } b && new HashSet<Sector> { l.Front.Sector, b.Sector }.SetEquals(rooms));

        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void AWindowAsWideAsASharedWallIsOnePiece()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 64), (128, 0, 256, 64));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        var (window, _) = Make(map, history, shared, 0, 64);
        Assert.NotNull(window);
        DrawingTests.AssertSound(map);
        Assert.Single(map.Sectors, s => s.OnMap && !rooms.Contains(s));
        Assert.NotNull(WallWindows.Recognise(MapGeometry.Build(map.Frame), window));
    }

    [Fact]
    public void WhatIsAndIsNotAWindow()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (144, 0, 272, 128));
        var (window, _) = Make(map, history, WallAt(map, rooms[0], 128), 32, 96);
        Assert.NotNull(window);
        WallWindow? Seen() => WallWindows.Recognise(MapGeometry.Build(map.Frame), window);

        // A sill the player steps onto with room to stand is a passage, not a window...
        window.FloorHeight = 16;
        Assert.Null(Seen());

        // ...unless its walls block, or the opening is too low to stand in.
        foreach (var face in map.Lines.Where(l => l.OnMap && l.Back?.Sector == window || l.Back is not null && l.Front.Sector == window)) face.SetFlag("blocking", true);
        Assert.True(Seen()!.Blocks);
        foreach (var face in map.Lines.Where(l => l.OnMap && l.Back is not null)) face.SetFlag("blocking", false);
        window.CeilingHeight = 64;
        Assert.NotNull(Seen());

        // No top below a room's ceiling (a low wall in the open), or closed: not a window.
        window.CeilingHeight = 128;
        window.FloorHeight = 40;
        Assert.Null(Seen());
        window.CeilingHeight = 40;
        Assert.Null(Seen());
    }

    /// <summary>
    /// The windows recognised in the games as they shipped are not doors or lifts. GZDoom (tools/engine/window-check.sh,
    /// 2026-10-12) confirms all 204 in DOOM.WAD and DOOM2.WAD are windows: sight passes through each and the player
    /// does not walk in, from both rooms for 191 and from the one room the player can stand in for 13; areas of
    /// E1M1 that are not windows (doors, stairs, rooms) fail the same check.
    /// </summary>
    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void WindowsInTheOriginalGamesAreNotDoorsOrLifts(string fileName)
    {
        var xlat = Core.Translation.XlatTranslator.Load(new Core.Containers.ResourceStack([Core.Containers.ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var wad = Core.Containers.WadContainer.Open(GameFiles.Require(fileName));
        var total = 0;
        foreach (var lumps in MapLocator.Find(wad))
        {
            var map = EditorMap.FromModel(Core.Translation.MapTranslation.ToZDoom(MapConversion.Read(lumps, "doom"), xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            var found = map.Sectors.Select(s => WallWindows.Recognise(geometry, s)).OfType<WallWindow>().ToList();
            Assert.All(found, w => Assert.True(Doors.Recognise(geometry, w.Area) is null && Lifts.Recognise(geometry, w.Area) is null, $"{lumps.Name} area {w.Area.Slot} is a window and a door or lift"));
            total += found.Count;
            output.WriteLine($"WINDOWS {fileName} {lumps.Name} {string.Join(" ", found.Select(w => w.Area.Slot))}");
            output.WriteLine($"{lumps.Name}: {found.Count} — {string.Join("; ", found.Take(8).Select(w => $"{w.Area.Slot} sill {w.Sill:0} top {w.Top:0} rooms {w.Rooms[0].Slot}/{w.Rooms[1].Slot}{(w.Blocks ? " blocks" : "")}"))}");
        }

        output.WriteLine($"{fileName}: {total} windows");
    }
}
