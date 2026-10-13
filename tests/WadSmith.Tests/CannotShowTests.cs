using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// What a game's own data cannot show is not offered and is called out: Doom II's super shotgun in a Doom map showed an
/// error in the game (the user's first map, 2026-10-12).
/// </summary>
public sealed class CannotShowTests
{
    private static GameResources Load(string id, string file)
    {
        var path = GameFiles.Require(file);
        return GameResources.Load(id, null, [new FoundGame(GameCatalog.Get(id)!, path, new FileInfo(path).Length)], GameFiles.Require("gzdoom.pk3"))!;
    }

    [Fact]
    public void DoomCannotShowDoomIIsSuperShotgunAndProblemsSaysSoWithAFix()
    {
        var doom = Load("doom", "DOOM.WAD");
        Assert.True(doom.CanShow(2001));
        Assert.False(doom.CanShow(82));
        Assert.True(doom.CanShow(9108));
        Assert.True(doom.CanShow(9001));
        Assert.Equal("Marine with a rocket launcher", doom.Things.Find(9108)!.Name);

        var model = StarterRoom.Create("E1M1", "doom");
        var gun = new MapThing { X = 64, Y = 64, Type = 82 };
        foreach (var f in new[] { "skill1", "skill2", "skill3", "skill4", "skill5", "single", "coop", "dm" }) gun.SetFlag(f, true);
        model.Things.Add(gun);
        var map = EditorMap.FromModel(model);
        var history = new History(map);
        var problem = Assert.Single(Problems.Check(MapGeometry.Build(map.Frame), doom, Look.For("doom"), "doom"), p => p.Rule == "cannot-show");
        Assert.Contains("is not in Doom: the game has no picture for it, so the game shows an error there.", problem.Sentence);
        Assert.Equal("Take it out", problem.FixLabel);
        Assert.Single(problem.Show);

        history.Record(problem.FixLabel!, problem.Fix!);
        Assert.Single(map.Things, t => t.OnMap);
        Assert.DoesNotContain(Problems.Check(MapGeometry.Build(map.Frame), doom, Look.For("doom"), "doom"), p => p.Rule == "cannot-show");
    }

    [Fact]
    public void FreedoomShowsItsOwnThings()
    {
        var freedoom = Load("freedoom1", "freedoom1.wad");
        Assert.True(freedoom.CanShow(1));
        Assert.True(freedoom.CanShow(2001));
        Assert.True(freedoom.CanShow(3001));
        Assert.False(freedoom.CanShow(31337));
    }
}
