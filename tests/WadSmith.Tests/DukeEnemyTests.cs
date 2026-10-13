using WadSmith.Core.Build;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Duke Nukem 3D's enemies with a Doom monster's behaviour (D48, D55): read from the game's own scripts, carried into a Doom II project.</summary>
public sealed class DukeEnemyTests
{
    [Fact]
    public void ARoleIsReadFromAnActionsName()
    {
        Assert.Equal(DukeRole.Walk, DukeEnemies.RoleOf("WALKING"));
        Assert.Equal(DukeRole.Attack, DukeEnemies.RoleOf("SHOOT"));
        Assert.Equal(DukeRole.Pain, DukeEnemies.RoleOf("FLINTCH"));
        Assert.Equal(DukeRole.Dead, DukeEnemies.RoleOf("LYINGDEAD"));
        Assert.Null(DukeEnemies.RoleOf("FROZEN"));
    }

    [Fact]
    public void ThePigCopsFramesComeFromTheGamesOwnScript()
    {
        var art = BuildArtContainer.Open(GameFiles.Require("DUKE3D.GRP"));
        Assert.Equal(["LIZTROOP", "PIGCOP", "LIZMAN", "OCTABRAIN", "COMMANDER", "BOSS1"], art.Enemies.Select(e => e.Key));
        var pig = art.Enemies.Single(e => e.Key == "PIGCOP");

        // action APIGSTAND 30 1 5, APIGWALK 0 4 5, APIGSHOOT 30 2 5, APIGHIT 55 1 1, APIGDYING 55 5 1, APIGDEAD 60 1 1 — from tile 2000.
        Assert.Equal([(2030, 5)], pig.Of(DukeRole.Stand).Select(f => (f.Tile, f.Views)));
        Assert.Equal([2000, 2005, 2010, 2015], pig.Of(DukeRole.Walk).Select(f => f.Tile));
        Assert.Equal([2030, 2035], pig.Of(DukeRole.Attack).Select(f => f.Tile));
        Assert.Equal([2055], pig.Of(DukeRole.Pain).Select(f => f.Tile));
        Assert.Equal([2055, 2056, 2057, 2058, 2059], pig.Of(DukeRole.Dying).Select(f => f.Tile));
        Assert.Equal([2060], pig.Of(DukeRole.Dead).Select(f => f.Tile));
        Assert.All(art.Enemies, e => Assert.NotNull(art.Art.Tile(e.PictureTile)));
    }

    [Fact]
    public void APigCopBehavingLikeAShotgunGuyIsDeclaredInTheProjectAndShown()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        var grp = GameFiles.Require("DUKE3D.GRP");
        var doom2 = GameFiles.Require("DOOM2.WAD");
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var games = new[] { new FoundGame(GameCatalog.Get("doom2")!, doom2, new FileInfo(doom2).Length) };
        var doom = GameResources.Load("doom2", project.Folder, games, engine)!;
        var art = BuildArtContainer.Open(grp);
        var pig = art.Enemies.Single(e => e.Key == "PIGCOP");

        var carried = DukeBorrowing.Carry(project, doom, art, pig, "ShotgunGuy");
        Assert.True(carried.New);
        Assert.Equal("DukePigcopAsShotgunGuy", carried.Thing.ClassName);
        Assert.Equal(ThingCategory.Monster, carried.Thing.Category);

        // Five views as rotations, the middle three mirrored as Doom pairs them (2–8, 3–7, 4–6); a dying frame from one view;
        // the standing frame, which is also the first attack frame, written once.
        var sprites = Directory.GetFiles(Path.Combine(project.Folder, "sprites")).Select(Path.GetFileNameWithoutExtension).ToHashSet();
        Assert.Contains("DPIGA1", sprites);
        Assert.Contains("DPIGA2A8", sprites);
        Assert.Contains("DPIGA4A6", sprites);
        Assert.Contains("DPIGA5", sprites);
        Assert.Contains(sprites, s => s!.Length == 6 && s.EndsWith('0'));
        Assert.DoesNotContain(sprites, s => s!.Length == 8 && s[5] + s[7] - 2 * '0' != 10);

        // The class: the Shotgun guy's states on Duke's frames, its own mind and attack kept.
        var script = File.ReadAllText(Path.Combine(project.Folder, "zscript", "wadsmith", "DukePigcopAsShotgunGuy.zs"));
        Assert.Contains("class DukePigcopAsShotgunGuy : ShotgunGuy", script);
        Assert.Contains("A_SposAttackUseAtkSound", script);
        Assert.Contains("DPIG ", script);
        Assert.DoesNotContain("SPOS ", script);
        Assert.DoesNotContain("GenericCrush", script);
        Assert.Contains("version \"4.10\"", File.ReadAllText(Path.Combine(project.Folder, DukeBorrowing.ScriptFile)));

        // The map names it, the editor shows it, and it is still there when the project is opened again.
        ProjectMapInfo.Refresh(project, doom.Game, doom.Files, engine);
        Assert.Contains($"{carried.Number} = DukePigcopAsShotgunGuy", File.ReadAllText(Path.Combine(project.Folder, ProjectMapInfo.FileName)));
        Assert.NotNull(doom.SpriteFor(carried.Number));
        var again = GameResources.Load("doom2", project.Folder, games, engine)!;
        Assert.True(again.CanShow(carried.Number));
        Assert.Equal(carried.Thing.Name, again.Things.Find(carried.Number)!.Name);
        Assert.False(DukeBorrowing.Carry(project, again, art, pig, "ShotgunGuy").New);

        // The placed object says what it is; another behaviour is another class, and the first stays.
        Assert.Equal(("PIGCOP", "ShotgunGuy", grp), DukeBorrowing.Of(project, carried.Number));
        var imp = DukeBorrowing.Carry(project, again, art, pig, "DoomImp");
        Assert.NotEqual(carried.Number, imp.Number);
        Assert.Equal(("PIGCOP", "DoomImp", grp), DukeBorrowing.Of(project, imp.Number));
        Assert.Contains("A_TroopAttack", File.ReadAllText(Path.Combine(project.Folder, "zscript", "wadsmith", "DukePigcopAsDoomImp.zs")));
        Assert.Equal(2, File.ReadAllText(Path.Combine(project.Folder, DukeBorrowing.ScriptFile)).Split("#include").Length - 1);
        Assert.Null(DukeBorrowing.Of(project, 3001));
    }
}
