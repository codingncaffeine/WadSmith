using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>PBR material sets (Phase 11): found by their names as the free libraries give them, read as GZDoom reads them, brought in as textures.</summary>
public sealed class MaterialTests
{
    private static string Write(string folder, string name, byte r, byte g, byte b)
    {
        Directory.CreateDirectory(folder);
        var image = new RgbaImage(4, 4);
        for (var i = 0; i < 16; i++) image.Set(i % 4, i / 4, r, g, b);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, ImageCodec.EncodePng(image));
        return path;
    }

    [Fact]
    public void AnAmbientCgSetIsFoundFromAnyOfItsPictures()
    {
        using var temp = new TempFolder();
        Write(temp.Path, "Bricks076_1K-PNG_Color.png", 200, 80, 60);
        Write(temp.Path, "Bricks076_1K-PNG_NormalDX.png", 128, 0, 255);
        var gl = Write(temp.Path, "Bricks076_1K-PNG_NormalGL.png", 128, 255, 255);
        Write(temp.Path, "Bricks076_1K-PNG_Roughness.png", 90, 90, 90);
        Write(temp.Path, "Bricks076_1K-PNG_AmbientOcclusion.png", 30, 30, 30);
        Write(temp.Path, "Bricks076_1K-PNG_Displacement.png", 1, 1, 1);
        Write(temp.Path, "Stone020_1K-PNG_Color.png", 1, 1, 1);

        var set = PbrSet.Of(Path.Combine(temp.Path, "Bricks076_1K-PNG_Roughness.png"))!;
        Assert.Equal("Bricks076 1K PNG", set.Name);
        Assert.Equal([PbrLayer.Colour, PbrLayer.Normal, PbrLayer.Roughness, PbrLayer.Occlusion], set.Layers.Keys.Order());
        // The GL normal map wins over the DirectX one, read as it is.
        Assert.Equal(gl, set.Layers[PbrLayer.Normal].File);
        Assert.Equal((128, 255, 255, 255), PbrSet.Read(set)[PbrLayer.Normal].Get(0, 0));
        // A colour alone is no set; a picture with no layer word is none either.
        Assert.Null(PbrSet.Of(Path.Combine(temp.Path, "Stone020_1K-PNG_Color.png")));
        Assert.Null(PbrSet.Of(Write(temp.Path, "plain.png", 1, 1, 1)));
    }

    [Fact]
    public void APolyHavenSetsDirectXNormalIsTurnedGreenUpAndItsArmIsSplit()
    {
        using var temp = new TempFolder();
        Write(temp.Path, "brick_wall_001_diff_1k.png", 200, 80, 60);
        Write(temp.Path, "brick_wall_001_nor_dx_1k.png", 128, 40, 255);
        var arm = Write(temp.Path, "brick_wall_001_arm_1k.png", 10, 120, 240);
        var set = PbrSet.Of(arm)!;
        Assert.Equal("brick wall 001", set.Name);
        var pictures = PbrSet.Read(set);
        Assert.Equal((128, 215, 255, 255), pictures[PbrLayer.Normal].Get(0, 0));
        Assert.Equal((10, 10, 10, 255), pictures[PbrLayer.Occlusion].Get(0, 0));
        Assert.Equal((120, 120, 120, 255), pictures[PbrLayer.Roughness].Get(0, 0));
        Assert.Equal((240, 240, 240, 255), pictures[PbrLayer.Metallic].Get(0, 0));
    }

    [Fact]
    public void AMaterialBecomesATextureForWallsAndFloorsWithItsMapsWhereGZDoomLooks()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source");
        var colour = Write(source, "Bricks076_1K-PNG_Color.png", 200, 80, 60);
        Write(source, "Bricks076_1K-PNG_NormalGL.png", 128, 128, 255);
        Write(source, "Bricks076_1K-PNG_Metalness.png", 0, 0, 0);
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var made = Materials.Import(project, game, colour);
        Assert.Equal("BRICKS07", made.Texture);
        Assert.Equal("a normal map and metalness", Materials.Words(made.Layers));
        Assert.True(File.Exists(Path.Combine(project.Folder, "textures", "BRICKS07.png")));
        Assert.True(File.Exists(Path.Combine(project.Folder, "materials", "normalmaps", "BRICKS07.png")));
        Assert.True(File.Exists(Path.Combine(project.Folder, "materials", "metallic", "BRICKS07.png")));
        Assert.Equal((200, 80, 60, 255), game.Textures.GetWall("BRICKS07")!.Get(0, 0));
        Assert.NotNull(game.Textures.GetFlat("BRICKS07"));
        // A second one of the same name gets another.
        Assert.NotEqual("BRICKS07", Materials.Import(project, game, colour).Texture);
    }
}
