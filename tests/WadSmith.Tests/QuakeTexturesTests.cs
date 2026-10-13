using System.Buffers.Binary;
using System.Text;
using WadSmith.Core.Games;
using WadSmith.Core.Quake;

namespace WadSmith.Tests;

/// <summary>
/// Quake's textures (Phase 8b.6): read from a PAK file's levels through its palette, each under a short name GZDoom keeps
/// whole; skies and liquids left out; the game found by the scan from its first PAK file; and, where Quake is installed,
/// every texture of the full game read with names of their own.
/// </summary>
public sealed class QuakeTexturesTests
{
    /// <summary>A PAK holding a palette and one level whose textures are <paramref name="textures"/> (name, width, height, colour index).</summary>
    private static string MakePak(string folder, params (string Name, int Width, int Height, byte Index)[] textures)
    {
        // The level: version 29, fifteen lumps; lump 2 holds the textures.
        var miptex = new MemoryStream();
        var offsets = new List<int>();
        var head = 4 + textures.Length * 4;
        var body = new MemoryStream();
        foreach (var (name, width, height, index) in textures)
        {
            offsets.Add(head + (int)body.Length);
            var entry = new byte[40 + width * height];
            Encoding.ASCII.GetBytes(name).CopyTo(entry, 0);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), width);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(20), height);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(24), 40);
            entry.AsSpan(40).Fill(index);
            body.Write(entry);
        }

        var count = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, textures.Length);
        miptex.Write(count);
        foreach (var o in offsets)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, o);
            miptex.Write(b);
        }

        miptex.Write(body.ToArray());
        var bsp = new byte[4 + 15 * 8 + miptex.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bsp, 29);
        BinaryPrimitives.WriteInt32LittleEndian(bsp.AsSpan(4 + 2 * 8), 4 + 15 * 8);
        BinaryPrimitives.WriteInt32LittleEndian(bsp.AsSpan(4 + 2 * 8 + 4), (int)miptex.Length);
        miptex.ToArray().CopyTo(bsp, 4 + 15 * 8);

        var palette = new byte[768];
        for (var i = 0; i < 256; i++) (palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]) = ((byte)i, (byte)(255 - i), 7);
        (string Name, byte[] Data)[] files = [("gfx/palette.lmp", palette), ("maps/e1m1.bsp", bsp)];
        var pak = new MemoryStream();
        pak.Write(new byte[12]);
        var table = new MemoryStream();
        foreach (var (name, data) in files)
        {
            var entry = new byte[64];
            Encoding.ASCII.GetBytes(name).CopyTo(entry, 0);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(56), (int)pak.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(60), data.Length);
            pak.Write(data);
            table.Write(entry);
        }

        var tableAt = (int)pak.Length;
        pak.Write(table.ToArray());
        var bytes = pak.ToArray();
        "PACK"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), tableAt);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), (int)table.Length);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "PAK0.PAK");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ALevelsTexturesAreReadThroughThePaletteUnderShortNames()
    {
        using var temp = new TempFolder();
        var path = MakePak(Path.Combine(temp.Path, "id1"), ("metal1_1", 64, 32, 10), ("sky4", 256, 128, 1), ("*water0", 64, 64, 2), ("brick", 16, 16, 200));
        var container = QuakeTextureContainer.Open(path);
        var textures = container.Textures.Textures;
        Assert.Equal(["brick", "metal1_1"], textures.Select(t => t.Original));
        Assert.Equal("QBRICK", textures[0].Name);
        Assert.Equal(QuakeTextures.ShortName("metal1_1"), textures[1].Name);
        Assert.Equal(8, textures[1].Name.Length);
        var picture = container.Textures.Picture(textures[1]);
        Assert.Equal((64, 32), (picture.Width, picture.Height));
        Assert.Equal(new byte[] { 10, 245, 7, 255 }, picture.Pixels[..4]);
        Assert.Contains(container.Entries, e => e.Name == textures[1].Name && e.Namespace == Core.Containers.ResourceNamespace.Textures);

        // The scan finds the game from its first PAK file.
        var games = GameLocator.Scan([temp.Path], cancel: TestContext.Current.CancellationToken);
        Assert.Contains(games, g => g.Game.Id == "quake" && g.Path == path);
    }

    [Fact]
    public void ShortNamesAreTheSameEverywhereAndKeepToEightLetters()
    {
        Assert.Equal("QSKY4", QuakeTextures.ShortName("sky4"));
        Assert.Equal("QTECH1_1", QuakeTextures.ShortName("tech1_1"));
        Assert.Equal(QuakeTextures.ShortName("metal1_1"), QuakeTextures.ShortName("METAL1_1"));
        Assert.NotEqual(QuakeTextures.ShortName("metal1_1"), QuakeTextures.ShortName("metal1_2"));
        Assert.All(new[] { "metal1_1", "+0basebtn", "wizmet1_2", "azfloor1_1" }, n => Assert.True(QuakeTextures.ShortName(n).Length <= 8));
    }

    [Fact]
    public void TheFullGamesTexturesAllHaveNamesOfTheirOwn()
    {
        var path = GameFiles.Require("PAK0.PAK");
        var textures = QuakeTextureContainer.Open(path).Textures.Textures;
        Assert.True(textures.Count > 300, $"only {textures.Count} textures");
        Assert.Equal(textures.Count, textures.Select(t => t.Name).Distinct().Count());
        Assert.All(textures, t => Assert.True(t.Name.Length <= 8 && t.Name.StartsWith('Q'), t.Name));
        Assert.DoesNotContain(textures, t => t.Original.StartsWith('*') || t.Original.StartsWith("sky", StringComparison.OrdinalIgnoreCase));
    }
}
