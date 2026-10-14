using System.Collections.Concurrent;
using WadSmith.App.ViewModels;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// Doors in every game's own terms (Phase 9, D62): a door in Heretic, Hexen or Strife offers the game's own door pictures and
/// keys in the Inspector as a Doom door does, and says its lock in the game's words; a Doom door keeps them under any look.
/// </summary>
public sealed class OtherGamesDoorsTests
{
    [Fact]
    public void EachGameLocksItsDoorsWithItsOwnKeys()
    {
        Assert.Equal([(0, "Nothing"), (129, "Red key"), (130, "Blue key"), (131, "Yellow key")], Keys.Locks("doom"));
        Assert.Equal([(0, "Nothing"), (129, "Green key"), (130, "Blue key"), (131, "Yellow key")], Keys.Locks("heretic"));
        var hexen = Keys.Locks("hexen");
        Assert.Equal(12, hexen.Count);
        Assert.Equal([(1, "Steel key"), (11, "Castle key")], [hexen[1], hexen[11]]);
        var strife = Keys.Locks("strife");
        Assert.Equal(30, strife.Count);
        Assert.Contains((4, "ID badge"), strife);
        Assert.Contains((17, "Brass key"), strife);
        Assert.Equal("Steel key", Keys.NameOfLock(1, "hexen"));
        Assert.Null(Keys.NameOfLock(0, "hexen"));
        Assert.Null(Keys.NameOfLock(7, "doom"));
    }

    [Fact]
    public void ADoorSaysItsLockInItsGamesWords()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var area = map.Sectors.First(s => s.OnMap);
        string Said(int lockNumber, string family) => Explaining.Describe(new Door(area, [], new DoorSettings(false, false, lockNumber), false, 0), family);
        Assert.Contains(", with the red key;", Said(129, "doom"), StringComparison.Ordinal);
        Assert.Contains(", with the green key;", Said(129, "heretic"), StringComparison.Ordinal);
        Assert.Contains(", with the steel key;", Said(1, "hexen"), StringComparison.Ordinal);
        Assert.Contains(", with the ID badge;", Said(4, "strife"), StringComparison.Ordinal);
        Assert.Contains(", with the governor's key;", Said(2, "strife"), StringComparison.Ordinal);
        Assert.Contains(", with lock 7;", Said(7, "doom"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditScriptLocksADoorWithTheGamesKeyAndPlacesIt()
    {
        var map = EditorMap.FromModel(new Core.Maps.MapModel { Name = "MAP01", Namespace = "zdoom" });
        var steps = EditScript.Run(map, "room 0 0 256 256\nroom 256 96 384 160\ndoor 256 128\nlock 264 128 steel\nkey steel 64 64", Look.For("hexen"), "hexen");
        Assert.All(steps, s => Assert.True(s.Done, s.Said));
        var geometry = WadSmith.Editor.Geometry.MapGeometry.Build(map.Frame);
        var door = map.Sectors.Select(s => Doors.Recognise(geometry, s)).Single(d => d is not null)!;
        Assert.Equal(1, door.Settings.Lock);
        Assert.Contains(map.Things, t => t.OnMap && t.Type == 8030);
    }

    /// <summary>A project of <paramref name="gameId"/> whose MAP01 holds two rooms with a door between them, opened with the game's resources.</summary>
    private static (TempFolder Temp, EditorSession Session, ConcurrentQueue<Action> Ui, Sector Door) OpenWithDoor(string gameId, string file)
    {
        var temp = new TempFolder();
        var game = GameCatalog.Get(gameId)!;
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", gameId);
        var map = EditorMap.FromModel(new Core.Maps.MapModel { Name = "MAP01", Namespace = "zdoom" });
        var steps = EditScript.Run(map, "room 0 0 256 256\nroom 256 96 384 160\ndoor 256 128\nstart 64 64", Look.For(game.Engine), game.Engine);
        Assert.All(steps, s => Assert.True(s.Done, s.Said));
        project.SaveMap(map.Frame.ToModel());
        var path = GameFiles.Require(file);
        var resources = GameResources.Load(gameId, project.Folder, [new FoundGame(game, path, new FileInfo(path).Length)], GameFiles.Find("gzdoom.pk3"))!;
        var ui = new ConcurrentQueue<Action>();
        var session = EditorSession.Open(project, "MAP01", new SessionHooks(ui.Enqueue, resources));
        var door = session.Map.Sectors.Single(s => s.OnMap && Doors.Recognise(WadSmith.Editor.Geometry.MapGeometry.Build(session.Map.Frame), s) is not null);
        return (temp, session, ui, door);
    }

    private static async Task Settle(EditorSession session, ConcurrentQueue<Action> ui)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        do
        {
            while (ui.TryDequeue(out var a)) a();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        while ((session.Geometry.Version != session.Map.Version || !ui.IsEmpty) && DateTime.UtcNow < until);
    }

    /// <summary>What a door's Inspector offers: its heading, its pictures and its locks.</summary>
    private static async Task<(string Heading, IReadOnlyList<string> Looks, IReadOnlyList<string> Locks, string Summary)> InspectDoor(EditorSession session, ConcurrentQueue<Action> ui, Sector door)
    {
        await Settle(session, ui);
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(door);
        await Settle(session, ui);
        var looks = inspector.Rows.OfType<PictureChoiceRow>().SingleOrDefault(r => r.Label == "Looks like")?.Choices.Select(c => c.Name).ToList() ?? [];
        var locks = inspector.Rows.OfType<ChoiceRow>().SingleOrDefault(r => r.Label == "Locked with")?.Options ?? [];
        return (inspector.Heading, looks, locks, inspector.Summary);
    }

    [Fact]
    public async Task ADoomDoorKeepsItsPicturesAndKeysWhateverTheLook()
    {
        // The rows were shown only under Doom's default look: with a named look chosen they went (and in every other game).
        var (temp, session, ui, door) = OpenWithDoor("freedoom2", "freedoom2.wad");
        using var _ = temp;
        using var __ = session;
        session.ChooseLook(Look.Named("doom").First(n => n.Look != Look.For("doom")).Look);
        var (heading, looks, locks, _) = await InspectDoor(session, ui, door);
        Assert.Equal("Door", heading);
        Assert.Contains("BIGDOOR2", looks);
        Assert.Equal(["Nothing", "Red key", "Blue key", "Yellow key"], locks);
    }

    [Theory]
    [InlineData("heretic", "HERETIC.WAD", "DOORSTON", "Green key")]
    [InlineData("hexen", "HEXEN.WAD", "D_WD10", "Steel key")]
    [InlineData("strife", "STRIFE1.WAD", "DORML01", "ID badge")]
    public async Task ADoorInAnotherGameOffersThatGamesPicturesAndKeys(string gameId, string file, string picture, string key)
    {
        var (temp, session, ui, door) = OpenWithDoor(gameId, file);
        using var _ = temp;
        using var __ = session;
        var (heading, looks, locks, _) = await InspectDoor(session, ui, door);
        Assert.Equal("Door", heading);
        Assert.Contains(picture, looks);
        Assert.Equal("Nothing", locks[0]);
        Assert.Contains(key, locks);

        // Locked from the Inspector with the game's key, the door says so in the game's words.
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(door);
        await Settle(session, ui);
        var row = inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Locked with");
        row.Value = row.Options.ToList().IndexOf(key);
        await Settle(session, ui);
        Assert.Equal(Keys.Locks(GameCatalog.Get(gameId)!.Engine).Single(k => k.Name == key).Lock, Doors.Recognise(session.Geometry, door)!.Settings.Lock);
        Assert.Contains($"with the {(char.IsUpper(key[1]) ? key : char.ToLowerInvariant(key[0]) + key[1..])};", inspector.Summary, StringComparison.Ordinal);
    }
}
