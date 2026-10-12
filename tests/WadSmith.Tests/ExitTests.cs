using WadSmith.App.ViewModels;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
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
        history.Record("Make the exit", e => Exits.Make(e, wall, false, Look.For("doom")));
        Assert.True(Exits.IsExit(wall));
        Assert.Equal((Exits.Normal, "SW1EXIT", true), (wall.Special, wall.Front.Middle, wall.Flag("playeruse")));
        Assert.Equal("uses it", Exits.How(wall));
        history.Record("Change where the exit goes", e => Exits.GoTo(e, wall, true));
        Assert.Equal(Exits.Secret, wall.Special);
        history.Record("Take the exit away", e => Exits.Clear(e, wall));
        Assert.False(Exits.IsExit(wall));
        Assert.False(wall.Flag("playeruse"));
        history.Undo();
        history.Undo();
        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
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
        inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Goes to").Value = 1;
        Assert.Equal(Exits.Secret, wall.Special);
        Assert.Contains("the secret map", inspector.Summary);
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Not an exit").Command.Execute(null);
        Assert.Equal("Solid wall", inspector.Heading);
    }
}
