using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Decorations made from pictures (Phase 7): sprites seen from every side standing on their bottom middle, a GIF's frames at its pace, height and blocking.</summary>
public sealed class PicturePropTests
{
    private static GameResources Freedoom2(Project project)
    {
        var wad = GameFiles.Require("freedoom2.wad");
        return GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
    }

    [Fact]
    public void AGifBecomesAnObjectPlayingItsFramesAtTheirOwnPace()
    {
        using var temp = new TempFolder();
        var gif = Path.Combine(temp.Path, "portal_loop.gif");
        File.WriteAllBytes(gif, PictureTests.Gif());
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var game = Freedoom2(project);

        var made = PictureProps.Import(project, game, gif);
        var thing = made.Actor.Thing;
        Assert.Equal(("PicturePortalLoop", "Portal loop", 2), (thing.ClassName, thing.Name, made.Frames));
        Assert.Equal((4.0, 4.0, 1.0), (thing.Radius, thing.Height, thing.ScaleX));
        var frame = ImageCodec.Decode(File.ReadAllBytes(Path.Combine(project.Folder, "sprites", $"{thing.Sprite}B0.png")), "frame");
        Assert.Equal((2, 4), (frame.OffsetX, frame.OffsetY));
        var script = File.ReadAllText(Path.Combine(project.Folder, "zscript", "wadsmith", "PicturePortalLoop.zs"));
        Assert.Contains($"\t\t{thing.Sprite} A 2;\n\t\t{thing.Sprite} B 4;\n\t\tLoop;\n", script, StringComparison.Ordinal);
        Assert.Contains("+SOLID;", script, StringComparison.Ordinal);
        Assert.Contains("#include \"zscript/wadsmith/PicturePortalLoop.zs\"", File.ReadAllText(Path.Combine(project.Folder, DukeBorrowing.ScriptFile)), StringComparison.Ordinal);
        Assert.NotNull(game.SpriteFor(made.Actor.Number));
    }

    [Fact]
    public void ATallPictureStandsAsTallAsAPersonAndItsInspectorChangesEveryOne()
    {
        using var temp = new TempFolder();
        var png = Path.Combine(temp.Path, "tall lamp.png");
        File.WriteAllBytes(png, ImageCodec.EncodePng(new RgbaImage(40, 128)));
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var game = Freedoom2(project);

        var made = PictureProps.Import(project, game, png);
        Assert.Equal((64.0, 0.5, 10.0), (made.Actor.Thing.Height, made.Actor.Thing.ScaleX, made.Actor.Thing.Radius));
        Assert.Contains($"{made.Actor.Thing.Sprite} A -1;\n\t\tStop;", File.ReadAllText(Path.Combine(project.Folder, "zscript", "wadsmith", "PictureTallLamp.zs")), StringComparison.Ordinal);

        PictureProps.Change(project, game, "PictureTallLamp", 32, false);
        var script = File.ReadAllText(Path.Combine(project.Folder, "zscript", "wadsmith", "PictureTallLamp.zs"));
        Assert.Contains("Height 32;", script, StringComparison.Ordinal);
        Assert.Contains("Scale 0.25;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("+SOLID", script, StringComparison.Ordinal);
        Assert.Equal((32.0, 0.25), (game.Things.Find(made.Actor.Number)!.Height, game.Things.Find(made.Actor.Number)!.ScaleX));
        Assert.Equal((32, false), (PictureProps.Of(project, made.Actor.Number)!.Value.Shape.Height, PictureProps.Of(project, made.Actor.Number)!.Value.Shape.Solid));

        // Opened again: the catalogue knows it at its new height, and MAPINFO places it by its number.
        Assert.Equal(32.0, Freedoom2(Project.Open(project.Folder)).Things.Find(made.Actor.Number)?.Height);
        Assert.Contains($"{made.Actor.Number} = PictureTallLamp", ProjectMapInfo.Text([], MapDefinitions.None, _ => [], project.CopiedNow()), StringComparison.Ordinal);
    }
}
