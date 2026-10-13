using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Skies from 360° pictures (Phase 11): the cube's faces look where GZDoom shows them, HDR photographs are read, the project gets a skybox.</summary>
public sealed class SkyTests
{
    /// <summary>A panorama coloured by where it looks: north red, east green, south blue, west yellow; the top sixth white, the bottom sixth black.</summary>
    internal static RgbaImage Compass(int width = 256)
    {
        var image = new RgbaImage(width, width / 2);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // u = 0.5 is north; a quarter to the right is east.
                var quarter = (int)Math.Floor(((double)x / width - 0.5 + 0.125 + 1) * 4) % 4;
                var (r, g, b) = quarter switch { 0 => (255, 0, 0), 1 => (0, 255, 0), 2 => (0, 0, 255), _ => (255, 255, 0) };
                if (y < image.Height / 6) (r, g, b) = (255, 255, 255);
                if (y >= image.Height * 5 / 6) (r, g, b) = (0, 0, 0);
                image.Set(x, y, (byte)r, (byte)g, (byte)b);
            }
        }

        return image;
    }

    [Fact]
    public void EachFaceLooksWhereGZDoomShowsIt()
    {
        static (int, int, int) Round((double X, double Y, double Z) d) => ((int)Math.Round(d.X), (int)Math.Round(d.Y), (int)Math.Round(d.Z));
        Assert.Equal((0, 1, 0), Round(Panorama.Direction(0, 0.5, 0.5)));
        Assert.Equal((1, 0, 0), Round(Panorama.Direction(1, 0.5, 0.5)));
        Assert.Equal((0, -1, 0), Round(Panorama.Direction(2, 0.5, 0.5)));
        Assert.Equal((-1, 0, 0), Round(Panorama.Direction(3, 0.5, 0.5)));
        Assert.Equal((0, 0, 1), Round(Panorama.Direction(4, 0.5, 0.5)));
        Assert.Equal((0, 0, -1), Round(Panorama.Direction(5, 0.5, 0.5)));
        // Facing north, the left edge is toward the west and the top edge up.
        Assert.True(Panorama.Direction(0, 0, 0.5).X < 0);
        Assert.True(Panorama.Direction(0, 0.5, 0).Z > 0);
        // Facing east, the left edge is toward the north.
        Assert.True(Panorama.Direction(1, 0, 0.5).Y > 0);
    }

    [Fact]
    public void ACompassPanoramaLandsOnTheRightFaces()
    {
        var faces = Panorama.ToCube(Compass(), 32);
        (byte, byte, byte) Middle(int face) { var (r, g, b, _) = faces[face].Get(16, 16); return (r, g, b); }
        Assert.Equal((255, 0, 0), Middle(0));
        Assert.Equal((0, 255, 0), Middle(1));
        Assert.Equal((0, 0, 255), Middle(2));
        Assert.Equal((255, 255, 0), Middle(3));
        Assert.Equal((255, 255, 255), Middle(4));
        Assert.Equal((0, 0, 0), Middle(5));
    }

    /// <summary>A Radiance HDR: the header, then rows flat (2 pixels) or run-length (8 pixels wide, each channel a run).</summary>
    internal static byte[] Hdr(bool packed)
    {
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        var width = packed ? 8 : 2;
        w.Write(System.Text.Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X {width}\n"));
        if (!packed)
        {
            w.Write(new byte[] { 128, 64, 32, 129, 0, 0, 0, 0 });
        }
        else
        {
            w.Write(new byte[] { 2, 2, 0, 8 });
            foreach (var value in new byte[] { 128, 64, 32, 129 }) w.Write(new byte[] { 128 + 8, value });
        }

        return file.ToArray();
    }

    [Fact]
    public void AnHdrPhotographIsReadFlatOrPacked()
    {
        var flat = RadianceHdr.Decode(Hdr(false));
        Assert.Equal((2, 1), (flat.Width, flat.Height));
        var (r, g, b, _) = flat.Get(0, 0);
        Assert.True(r > g && g > b && b > 0, $"{r},{g},{b}");
        Assert.Equal((0, 0, 0, 255), flat.Get(1, 0));
        var packed = RadianceHdr.Decode(Hdr(true));
        Assert.Equal(8, packed.Width);
        Assert.Equal(packed.Get(0, 0), packed.Get(7, 0));
        Assert.Throws<WadSmith.Core.CorruptDataException>(() => RadianceHdr.Decode(Hdr(true)[..^3]));
    }

    [Fact]
    public void APanoramaBecomesASkyboxTheMapCanPick()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "meadow.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(Compass(512)));
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var made = Skies.Make(project, game, path);
        Assert.Equal(("SKYB01", 128), (made.Name, made.Size));
        Assert.Empty(made.Notes);
        foreach (var face in "NESWTB") Assert.True(game.Textures.Has($"SKB01{face}"), $"face {face}");
        var gldefs = File.ReadAllText(Path.Combine(project.Folder, Skies.DefinitionFile));
        Assert.Contains("skybox SKYB01\n{\n\tSKB01N\n\tSKB01E\n\tSKB01S\n\tSKB01W\n\tSKB01T\n\tSKB01B\n}", gldefs, StringComparison.Ordinal);
        Assert.Contains("SKYB01", game.Skies);
        Assert.DoesNotContain(game.Skies, s => s.StartsWith("SKB", StringComparison.Ordinal));
        Assert.Equal("SKYB02", Skies.Make(project, game, path).Name);
        // Opened again, the project's skies are offered.
        var again = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        Assert.Contains("SKYB02", again.Skies);

        // A picture not twice as wide as tall says the sky may look stretched.
        var square = Path.Combine(temp.Path, "square.png");
        File.WriteAllBytes(square, ImageCodec.EncodePng(new RgbaImage(64, 64)));
        Assert.Contains(Skies.Make(project, game, square).Notes, n => n.Contains("stretched", StringComparison.Ordinal));
    }
}
