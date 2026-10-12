using WadSmith.App.ViewModels;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Exits: walls that end the map, found in any map and made with one button.</summary>
public sealed class ExitTests
{
    /// <summary>Doom's classic exit actions: switch, walk-over and shot, normal and secret.</summary>
    private static readonly HashSet<int> ClassicExits = [11, 51, 52, 124, 197, 198];

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void EveryClassicExitIsAnExitAndNothingElse(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var exits = 0;
        foreach (var lumps in MapLocator.Find(wad))
        {
            var classic = MapConversion.Read(lumps, "doom");
            var expected = Enumerable.Range(0, classic.Lines.Count).Where(i => ClassicExits.Contains(classic.Lines[i].Special)).ToHashSet();
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(classic, xlat, []));
            var found = map.Lines.Where(Exits.IsExit).Select(l => l.Slot).ToHashSet();
            Assert.True(expected.SetEquals(found), $"{fileName} {lumps.Name}: classic exits {string.Join(", ", expected.Order())}; recognised {string.Join(", ", found.Order())}");
            exits += found.Count;
        }

        Assert.True(exits > 0);
    }

    [Fact]
    public void ASolidWallBecomesTheExitSwitchAndBackExactly()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var before = Udmf(map.ToModel());
        var wall = map.Lines[0];
        Line? made = null;
        history.Record("Make the exit", e => made = Exits.Make(e, wall, false, Look.For("doom")));

        // The west wall is 512 long: the middle 64 of it is the switch, and the rest stays wall, its texture running on past the switch.
        var exit = Assert.IsType<Line>(made);
        Assert.True(Exits.IsExit(exit));
        Assert.Equal((Exits.Normal, "SW1EXIT", true), (exit.Special, exit.Front.Middle, exit.Flag("playeruse")));
        var west = map.Lines.Where(l => l.OnMap && l.Start.X == -256 && l.End.X == -256).OrderBy(l => l.Start.Y).ToList();
        Assert.Equal([(-256.0, -32.0, 0, "STARTAN3", 0.0), (-32.0, 32.0, Exits.Normal, "SW1EXIT", 0.0), (32.0, 256.0, 0, "STARTAN3", 288.0)], west.Select(l => (l.Start.Y, l.End.Y, l.Special, l.Front.Middle, l.Front.OffsetX)));
        Assert.Same(exit, west[1]);
        Assert.Equal("uses it", Exits.How(exit));
        history.Record("Change where the exit goes", e => Exits.GoTo(e, exit, true));
        Assert.Equal(Exits.Secret, exit.Special);
        history.Record("Take the exit away", e => Exits.Clear(e, exit));
        Assert.False(Exits.IsExit(exit));
        Assert.False(exit.Flag("playeruse"));
        history.Undo();
        history.Undo();
        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void TheExitSwitchGoesWhereTheWallWasClickedAndAShortWallIsAllSwitch()
    {
        // Clicked three quarters of the way up the west wall: the switch is the 64 round the click.
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        Line? made = null;
        history.Record("Make the exit", e => made = Exits.Make(e, map.Lines[0], false, Look.For("doom"), new MapPoint(-250, 200)));
        Assert.Equal((168.0, 232.0), (made!.Start.Y, made.End.Y));
        Assert.Equal(6, map.Lines.Count(l => l.OnMap));
        history.Undo();
        Assert.Equal(4, map.Lines.Count(l => l.OnMap));

        // Clicked at the very end: the switch stops at the corner.
        history.Record("Make the exit", e => made = Exits.Make(e, map.Lines[0], false, Look.For("doom"), new MapPoint(-256, 250)));
        Assert.Equal((192.0, 256.0), (made!.Start.Y, made.End.Y));
        Assert.Equal(5, map.Lines.Count(l => l.OnMap));

        // A wall not much wider than the switch picture is the switch whole.
        var (small, smallHistory, _) = MovingTests.Drawn((0, 0, 80, 128));
        var shortWall = small.Lines.First(l => l.OnMap && l.Length == 80);
        var walls = small.Lines.Count(l => l.OnMap);
        smallHistory.Record("Make the exit", e => made = Exits.Make(e, shortWall, false, Look.For("doom"), new MapPoint(10, 0)));
        Assert.Same(shortWall, made);
        Assert.Equal(walls, small.Lines.Count(l => l.OnMap));
        Assert.Equal("SW1EXIT", shortWall.Front.Middle);
    }

    [Fact]
    public void AWallBetweenRoomsEndsTheMapWhenWalkedAcross()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        history.Record("Make the exit", e => Exits.Make(e, shared, false, Look.For("doom")));
        Assert.True(shared.Flag("playercross"));
        Assert.Equal("walks across it", Exits.How(shared));
        Assert.Equal("-", shared.Front.Middle);
    }

    [Fact]
    public void TheInspectorMakesTheExitSwitchAndShowsItAsOne()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", _ => { });
        var inspector = new InspectorViewModel(session);
        var wall = session.Map.Lines[0];
        session.Selection.Set(wall);
        Assert.Equal("Solid wall", inspector.Heading);
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make it the exit switch").Command.Execute(null);
        Assert.Equal("Exit switch", inspector.Heading);
        Assert.Equal("Ends the map when the player uses it, going on to the next map.", inspector.Summary);

        // The selection moved to the piece that is the switch, 64 of the 512 wall, so the Inspector shows the switch itself.
        var piece = Assert.IsType<Line>(session.Selection.Items.Single());
        Assert.NotSame(wall, piece);
        Assert.Equal((64.0, 0, "STARTAN3"), (piece.Length, wall.Special, wall.Front.Middle));
        inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Goes to").Value = 1;
        Assert.Equal(Exits.Secret, piece.Special);
        Assert.Contains("the secret map", inspector.Summary);
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Not an exit").Command.Execute(null);
        Assert.Equal("Solid wall", inspector.Heading);
    }
}
