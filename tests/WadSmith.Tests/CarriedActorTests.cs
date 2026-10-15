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
    public void AMonsterComesWithWhatItFiresAndTheSoundsItMakes()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var doom = Load("doom2", "DOOM2.WAD", project.Folder);
        var heretic = Load("heretic", "HERETIC.WAD");
        var leader = heretic.Things.FindClass("HereticImpLeader")!;
        Assert.Equal(ThingCategory.Monster, leader.Category);

        var carried = ActorBorrowing.Carry(project, doom, heretic, leader);
        Assert.True(File.Exists(Path.Combine(project.Folder, "sprites", "IMPXA1.png")));
        Assert.Contains(Directory.GetFiles(Path.Combine(project.Folder, "sprites")), f => Path.GetFileName(f).StartsWith("FX10", StringComparison.Ordinal));
        Assert.True(carried.Sounds > 0);
        var table = File.ReadAllText(Path.Combine(project.Folder, ActorBorrowing.SoundFile));
        Assert.Contains("himp/sight", table);
        Assert.True(doom.CanShow(carried.Number));
    }

    /// <summary>
    /// Two monsters that share pictures (Heretic's golem and its ghost both draw MUMM): the second is carried after the first,
    /// in the same project and when the project is opened again; the pictures the first brought are not a clash with Doom II.
    /// </summary>
    [Fact]
    public void AMonsterSharingPicturesWithOneCarriedBeforeComesToo()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var doom = Load("doom2", "DOOM2.WAD", project.Folder);
        var heretic = Load("heretic", "HERETIC.WAD");
        var golem = ActorBorrowing.Carry(project, doom, heretic, heretic.Things.FindClass("Mummy")!);
        Assert.True(golem.New);
        var ghost = heretic.Things.FindClass("MummyGhost")!;
        Assert.Null(ActorBorrowing.WhyNot(doom, heretic, ghost));
        Assert.True(ActorBorrowing.Carry(project, doom, heretic, ghost).New);
        var again = Load("doom2", "DOOM2.WAD", project.Folder);
        Assert.Null(ActorBorrowing.WhyNot(again, heretic, heretic.Things.FindClass("MummyLeaderGhost")!));

        // A sprite the game itself has still clashes: Hexen's Fighter boss draws PLAY, as Doom II's player does.
        var hexen = Load("hexen", "HEXEN.WAD");
        Assert.Contains("PLAY", ActorBorrowing.WhyNot(again, hexen, hexen.Things.FindClass("FighterBoss")!));
    }

    /// <summary>Doom II's Hell Knight in a Doom map: its own pictures come, the Baron's fireball it throws is Doom's own, and it shows.</summary>
    [Fact]
    public void ADoomTwoMonsterComesIntoADoomMapWithWhatDoomLacks()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom");
        project.SaveMap(StarterRoom.Create("E1M1", "doom"));
        var doom = Load("doom", "DOOM.WAD", project.Folder);
        var doom2 = Load("doom2", "DOOM2.WAD");
        var knight = doom2.Things.FindClass("HellKnight")!;
        Assert.Null(ActorBorrowing.WhyNot(doom, doom2, knight));
        var carried = ActorBorrowing.Carry(project, doom, doom2, knight);
        var sprites = Directory.GetFiles(Path.Combine(project.Folder, "sprites")).Select(Path.GetFileName).ToList();
        Assert.Contains(sprites, f => f!.StartsWith("BOS2", StringComparison.Ordinal));
        Assert.DoesNotContain(sprites, f => f!.StartsWith("BAL7", StringComparison.Ordinal));
        Assert.True(doom.CanShow(carried.Number));
    }

    [Fact]
    public void AWeaponComesWithItsAmmunitionAndItsSlot()
    {
        // The slots, from the engine's own player classes.
        Assert.Equal(4, Family("heretic").SlotOf("Blaster"));
        Assert.Equal(2, Family("hexen").SlotOf("FWeapAxe"));
        Assert.Equal(3, Family("strife").SlotOf("AssaultGun"));

        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var doom = Load("doom2", "DOOM2.WAD", project.Folder);
        var heretic = Load("heretic", "HERETIC.WAD");
        var blaster = heretic.Things.FindClass("Blaster")!;
        var needs = heretic.Things.Needs("Blaster");
        Assert.Contains("BlasterAmmo", needs.Classes);
        Assert.Contains("BlasterPowered", needs.Classes);

        ActorBorrowing.Carry(project, doom, heretic, blaster);
        Assert.True(File.Exists(Path.Combine(project.Folder, "sprites", "BLSRA0.png")));
        Assert.Contains("addslotdefault 4 Blaster", File.ReadAllText(Path.Combine(project.Folder, ActorBorrowing.SlotFile)));
    }

    /// <summary>
    /// The engine renames some of Heretic's, Hexen's and Strife's own sprites as it loads them, and their objects draw the new
    /// names: Strife's stalker is SPID in STRIFE1.WAD and STLK in the game. It comes into a Doom II map under STLK, so it shows
    /// in the game ("has no frames" before), and leaves SPID to Doom II's spider mastermind.
    /// </summary>
    [Fact]
    public void AStalkerFromStrifeComesWithItsPicturesUnderTheNamesTheEngineGivesThem()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var doom = Load("doom2", "DOOM2.WAD", project.Folder);
        var strife = Load("strife", "STRIFE1.WAD");
        Assert.NotEmpty(strife.Sprites.Frames("STLK"));
        Assert.Empty(strife.Sprites.Frames("SPID"));

        var stalker = strife.Things.FindClass("Stalker")!;
        Assert.Null(ActorBorrowing.WhyNot(doom, strife, stalker));
        var carried = ActorBorrowing.Carry(project, doom, strife, stalker);
        var sprites = Directory.GetFiles(Path.Combine(project.Folder, "sprites")).Select(Path.GetFileName).ToList();
        Assert.Contains(sprites, f => f!.StartsWith("STLK", StringComparison.Ordinal));
        Assert.DoesNotContain(sprites, f => f!.StartsWith("SPID", StringComparison.Ordinal));
        Assert.True(doom.CanShow(carried.Number));
        Assert.All(doom.Sprites.Frames("SPID"), f => Assert.False(doom.IsProjectFile(f.Entry)));

        // Heretic's Iron Lich is HEAD in its file and LICH in the game; Doom's cacodemon keeps HEAD, so either comes into the other's game.
        var heretic = Load("heretic", "HERETIC.WAD");
        Assert.NotEmpty(heretic.Sprites.Frames("LICH"));
        Assert.Empty(heretic.Sprites.Frames("HEAD"));
        ActorBorrowing.Carry(project, doom, heretic, heretic.Things.FindClass("Ironlich")!);
        Assert.Contains(Directory.GetFiles(Path.Combine(project.Folder, "sprites")), f => Path.GetFileName(f).StartsWith("LICH", StringComparison.Ordinal));
        Assert.Null(ActorBorrowing.WhyNot(heretic, doom, doom.Things.FindClass("Cacodemon")!));
        Assert.Null(ActorBorrowing.WhyNot(strife, doom, doom.Things.FindClass("SpiderMastermind")!));
    }

    /// <summary>
    /// Hexen's own file names most of its monsters' sounds (its SNDINFO: BishopSight is SYAB2D), in no table of the engine's:
    /// a Hexen monster comes into a Doom II map with them, where it was silent before.
    /// </summary>
    [Fact]
    public void AHexenMonsterComesWithTheSoundsHexensOwnTableNames()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var doom = Load("doom2", "DOOM2.WAD", project.Folder);
        var hexen = Load("hexen", "HEXEN.WAD");
        Assert.NotNull(hexen.Things.Sounds.LumpFor("BishopSight"));
        var bishop = hexen.Things.FindClass("Bishop")!;
        Assert.NotNull(bishop.Sound);

        var carried = ActorBorrowing.Carry(project, doom, hexen, bishop);
        Assert.True(carried.Sounds > 0);
        Assert.Contains("BishopSight", File.ReadAllText(Path.Combine(project.Folder, ActorBorrowing.SoundFile)));
        var lump = hexen.Things.Sounds.LumpFor("BishopSight")!.ToUpperInvariant();
        Assert.True(File.Exists(Path.Combine(project.Folder, "sounds", $"{lump}.lmp")));
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
