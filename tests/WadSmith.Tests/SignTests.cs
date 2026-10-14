using SkiaSharp;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Signs on walls (Phase 7): words laid out as large as fit, in the game's own letters or a computer font, on the wall or a plate.</summary>
public sealed class SignTests
{
    [Fact]
    public void WordsTakeTheNumberOfLinesThatDrawsThemLargest()
    {
        var area = new SKRect(0, 0, 400, 100);
        float Width(string t) => t.Length * 10;
        var (wide, wideScale) = SignPictures.Layout("EXIT", Width, 10, area);
        Assert.Equal(["EXIT"], wide);
        var (tall, _) = SignPictures.Layout("THE BLUE KEY OPENS THE VAULT BEYOND THE BRIDGE", Width, 10, new SKRect(0, 0, 100, 100));
        Assert.True(tall.Count > 1, string.Join("/", tall));
        Assert.Equal("THE BLUE KEY OPENS THE VAULT BEYOND THE BRIDGE", string.Join(' ', tall));
        Assert.Equal(100f / 12, wideScale);
    }

    [Fact]
    public void TheGamesOwnLettersAreDrawnInTheChosenColourOnThePlate()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;

        int Coloured(WadSmith.Core.Graphics.RgbaImage image, Func<byte, byte, byte, bool> isIt)
        {
            var n = 0;
            for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++) { var c = image.Get(x, y); if (isIt(c.R, c.G, c.B)) n++; }
            return n;
        }

        var green = SignPictures.Render(new SignSettings("EXIT", SignLettering.Game, 0x40D040, SignBack.Dark), 128, 64, null, game);
        Assert.Equal((512, 256), (green.Width, green.Height));
        Assert.True(Coloured(green, (r, g, b) => g > 100 && g > 2 * r && g > 2 * b) > 500, "no green letters on the dark plate");
        Assert.Equal(0, Coloured(green, (r, g, b) => r > 150 && g < 100));

        var plain = SignPictures.Render(new SignSettings("Exit", SignLettering.Plain, 0xE03020, SignBack.Light), 128, 64, null, game);
        Assert.True(Coloured(plain, (r, g, b) => r > 150 && g < 100 && b < 100) > 500, "no red letters on the light plate");

        var made = SignPictures.Write(project, game, SignSettings.New, 128, 64, "STARTAN3");
        Assert.Equal("SIGN0001", made.Texture);
        Assert.True(game.Textures.Has("SIGN0001"));
        Assert.Equal(made, SignPictures.Of(project.CopiedNow(), "SIGN0001"));
        Assert.Equal("SIGN0002", SignPictures.Write(project, game, SignSettings.New with { Text = "Go | on" }, 128, 64, "STARTAN3").Texture);
        Assert.Equal("Go | on", SignPictures.Of(project.CopiedNow(), "SIGN0002")?.Settings.Text);
    }
}
