using WadSmith.Core.Containers;
using WadSmith.Core.Exporting;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// The project's own pictures carried into a classic WAD (Phase 12): walls as patches in the game's PNAMES and TEXTURE1
/// (the game's own textures kept, in order), floors between FF_START and FF_END for every port but vanilla.
/// </summary>
public sealed class PictureCarryTests
{
    private static byte[] Png(int w, int h, (byte R, byte G, byte B) colour, bool holes = false)
    {
        var img = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) img.Set(x, y, colour.R, colour.G, colour.B, (byte)(holes && x < w / 2 ? 0 : 255));
        }

        return ImageCodec.EncodePng(img);
    }

    private static (Project Project, string Engine, BaseGame Game) Setup(TempFolder temp)
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        var iwad = GameFiles.Require("freedoom2.wad");
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "freedoom2");
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room 0 0 256 256\nroom 256 96 384 160\nstart 128 128\ndoor 256 128", Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        var model = map.ToModel();
        model.Sides[model.Lines.First(l => l.Back < 0).Front].Middle = "MYWALL";
        model.Sides[model.Lines.Last(l => l.Back < 0).Front].Middle = "MYGRATE";
        model.Sectors[0].FloorTexture = "MYFLAT";
        model.Sectors[1].CeilingTexture = "BIGFLAT";
        project.SaveMap(model);
        project.WriteFile("textures/MYWALL.png", Png(64, 128, (255, 0, 0)));
        project.WriteFile("textures/MYGRATE.png", Png(32, 64, (0, 0, 255), holes: true));
        project.WriteFile("flats/MYFLAT.png", Png(64, 64, (0, 255, 0)));
        project.WriteFile("flats/BIGFLAT.png", Png(128, 128, (255, 255, 0)));
        return (project, engine, new BaseGame(new ResourceStack([ResourceContainer.Open(iwad)])));
    }

    [Fact]
    public void WallsAndFloorsAreCarriedInTheGamesColoursAndTheGamesOwnTexturesStay()
    {
        using var temp = new TempFolder();
        var (project, engine, game) = Setup(temp);
        var file = Path.Combine(temp.Path, "boom.wad");
        var report = ProjectExport.Write(project, "boom", file, engine, game);
        Assert.DoesNotContain(report.Losses, l => l.Key.Contains("picture", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.StartsWith("2 wall and 2 floor pictures carried", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.StartsWith("1 floor pictures resized", StringComparison.Ordinal));

        var wad = WadContainer.Open(file);
        byte[] Lump(string name) => wad.Entries.Single(e => e.Name == name).Read();
        var gamePnames = TextureLump.ReadPatchNames(game.Lump("PNAMES")).Select(n => n.Text).ToList();
        var pnames = TextureLump.ReadPatchNames(Lump("PNAMES")).Select(n => n.Text).ToList();
        Assert.Equal(gamePnames, pnames.Take(gamePnames.Count));
        Assert.Equal(gamePnames.Count + 2, pnames.Count);
        var gameTextures = TextureLump.Read(game.Lump("TEXTURE1"), TextureLump.ReadPatchNames(game.Lump("PNAMES")), "game").Textures.Select(t => t.Name).ToList();
        var textures = TextureLump.Read(Lump("TEXTURE1"), TextureLump.ReadPatchNames(Lump("PNAMES")), "export").Textures;
        Assert.Equal(gameTextures, textures.Take(gameTextures.Count).Select(t => t.Name));
        var mine = textures.Single(t => t.Name == "MYWALL");
        Assert.Equal((64, 128), (mine.Width, mine.Height));

        // The patch is the picture in the game's palette: red becomes the palette's nearest red; the grate keeps its holes.
        var palette = game.Palette!;
        var patch = DoomPicture.Decode(Lump(mine.Patches.Single().Patch));
        Assert.Equal(palette.Nearest(255, 0, 0), patch.Indices[0]);
        var grate = DoomPicture.Decode(Lump(textures.Single(t => t.Name == "MYGRATE").Patches.Single().Patch));
        Assert.False(grate.Opaque[0]);
        Assert.True(grate.Opaque[31]);

        // Floors between FF_START and FF_END, 64×64 each.
        var names = wad.Entries.Select(e => e.Name).ToList();
        var start = names.IndexOf("FF_START");
        Assert.True(start >= 0 && names.IndexOf("MYFLAT") > start && names.IndexOf("BIGFLAT") > start && names.IndexOf("FF_END") > names.IndexOf("BIGFLAT"));
        Assert.Equal(4096, Lump("BIGFLAT").Length);
        Assert.Equal(palette.Nearest(0, 255, 0), Lump("MYFLAT")[0]);
    }

    [Fact]
    public void AMapsOwnMusicPlaysUnderTheSlotsSongWhereThePortCannotNameIt()
    {
        using var temp = new TempFolder();
        var (project, engine, game) = Setup(temp);
        project.UpdateMap("MAP01", m => m with { Music = "D_SHAWN" });
        var file = Path.Combine(temp.Path, "vanilla.wad");
        var report = ProjectExport.Write(project, "vanilla", file, engine, game);
        Assert.DoesNotContain(report.Losses, l => l.Key == "map:info");
        Assert.Contains(report.Notes, n => n == "MAP01: its music D_SHAWN written as D_RUNNIN, the music the slot plays.");
        // Freedoom's songs are MIDI: carried, with the one engine that cannot play them named.
        Assert.Contains(report.Notes, n => n.StartsWith("MAP01: its music D_SHAWN is MIDI", StringComparison.Ordinal));
        Assert.Equal(game.Lump("D_SHAWN"), WadContainer.Open(file).Entries.Single(e => e.Name == "D_RUNNIN").Read());
    }

    [Fact]
    public void VanillaTakesTheWallsAndSaysItsGameStopsOverTheFloors()
    {
        using var temp = new TempFolder();
        var (project, engine, game) = Setup(temp);
        var file = Path.Combine(temp.Path, "vanilla.wad");
        var report = ProjectExport.Write(project, "vanilla", file, engine, game);
        var floors = Assert.Single(report.Losses, l => l.Key == "room:no-picture");
        Assert.True(floors.Breaks);
        Assert.Contains("MYFLAT", floors.Detail, StringComparison.Ordinal);
        Assert.Contains("BIGFLAT", floors.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Losses, l => l.Key == "side:no-picture");
        Assert.DoesNotContain(WadContainer.Open(file).Entries, e => e.Name == "FF_START");
    }
}
