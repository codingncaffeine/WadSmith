using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>Objects from another game carried into a project (D55): what they need, what is copied, and that the map then names and shows them.</summary>
public sealed class CarriedActorTests
{
    private static ThingCatalog Family(string family) => ThingCatalog.Load(ResourceContainer.Open(GameFiles.Require("gzdoom.pk3")), family);

    private static GameResources Load(string id, string file, string? project = null)
    {
        var path = GameFiles.Require(file);
        return GameResources.Load(id, project, [new FoundGame(GameCatalog.Get(id)!, path, new FileInfo(path).Length)], GameFiles.Require("gzdoom.pk3"))!;
    }

    [Fact]
    public void AnObjectsNeedsAreItsSpritesWhatItSpawnsAndItsSounds()
    {
        // From the engine's own definitions alone: Heretic's Gargoyle Leader fires its fireball, and makes its own sounds;
        // the walk stops at the engine's base classes, so the common stock (puffs, blood, teleport fog) does not come along.
        var heretic = Family("heretic");
        var leader = heretic.FindClass("HereticImpLeader")!;
        var needs = heretic.Needs(leader.ClassName);
        Assert.Contains("HereticImpBall", needs.Classes);
        Assert.Contains("HereticImp", needs.Classes);
        Assert.Contains("IMPX", needs.OwnSprites);
        Assert.Contains("FX10", needs.Sprites);
        Assert.Contains("himp/sight", needs.Sounds);
        Assert.DoesNotContain("Actor", needs.Classes);
        Assert.DoesNotContain("BulletPuff", needs.Classes);
        Assert.DoesNotContain("GOTO", needs.Sprites);

        // A torch needs only its own frames.
        var torch = heretic.FindClass("SerpentTorch")!;
        Assert.Equal(["SRTC"], heretic.Needs(torch.ClassName).OwnSprites);
    }

    [Fact]
    public async Task ATorchFromHereticIsCarriedIntoADoomProjectAndTheMapNamesAndShowsIt()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var doom = Load("doom2", "DOOM2.WAD", project.Folder);
        var heretic = Load("heretic", "HERETIC.WAD");
        var torch = heretic.Things.FindClass("SerpentTorch")!;
        Assert.Null(ActorBorrowing.WhyNot(doom, heretic, torch));
        Assert.Null(doom.Things.FindClass("SerpentTorch"));

        var carried = ActorBorrowing.Carry(project, doom, heretic, torch);
        Assert.Equal((ActorBorrowing.FirstNumber, true), (carried.Number, carried.New));
        Assert.True(carried.Sprites > 0);
        Assert.True(File.Exists(Path.Combine(project.Folder, "sprites", "SRTCA0.png")));
        Assert.True(doom.CanShow(carried.Number));
        Assert.NotNull(doom.SpriteFor(carried.Number));

        // The project's MAPINFO gives the map the number; a second use finds the first carry.
        ProjectMapInfo.Refresh(project, doom.Game, doom.Files, engine);
        Assert.Contains($"{ActorBorrowing.FirstNumber} = SerpentTorch", await File.ReadAllTextAsync(Path.Combine(project.Folder, ProjectMapInfo.FileName), TestContext.Current.CancellationToken));
        Assert.False(ActorBorrowing.Carry(project, doom, heretic, torch).New);

        // Opened again, the project's game has the torch, and Problems does not call it out.
        var again = Load("doom2", "DOOM2.WAD", project.Folder);
        Assert.True(again.CanShow(ActorBorrowing.FirstNumber));
        var map = StarterRoom.Create("MAP01", "doom");
        map.Things.Add(new MapThing { X = 0, Y = 0, Type = ActorBorrowing.FirstNumber });
        var editor = WadSmith.Editor.Model.EditorMap.FromModel(map);
        Assert.DoesNotContain(Problems.Check(MapGeometry.Build(editor.Frame), again, Look.For("doom"), "doom"), p => p.Rule == "cannot-show");
    }

    [Fact]
    public void AnObjectWhosePicturesShareANameWithThisGamesWaitsForALaterUpdate()
    {
        // Hexen's Fighter boss draws as the player (PLAY), which Doom II has too.
        var doom = Load("doom2", "DOOM2.WAD");
        var hexen = Load("hexen", "HEXEN.WAD");
        Assert.Contains("PLAY", ActorBorrowing.WhyNot(doom, hexen, hexen.Things.FindClass("FighterBoss")!));
        Assert.Null(ActorBorrowing.WhyNot(doom, hexen, hexen.Things.FindClass("ZWingedStatue")!));
    }
}
