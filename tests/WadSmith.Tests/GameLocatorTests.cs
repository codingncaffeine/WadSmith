using WadSmith.Core.Containers;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

public sealed class GameLocatorTests
{
    private static void WriteGame(string path, WadKind kind)
    {
        var lumps = new[] { "MAP01", "THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SECTORS", "PLAYPAL", "COLORMAP", "PLAYA1", "STBAR" }.Select(n => (n, (ReadOnlyMemory<byte>)new byte[] { 1, 2, 3, 4 }));
        File.WriteAllBytes(path, WadWriter.Write(kind, lumps));
    }

    /// <summary>
    /// Two copies of one game, found in either order: the one whose header says IWAD comes first, since GZDoom refuses the
    /// other as the base game (the 2024 re-release ships Doom II with a PWAD header).
    /// </summary>
    [Fact]
    public void ACopyWithAnIwadHeaderComesBeforeOneWithAPwadHeader()
    {
        using var temp = new TempFolder();
        WriteGame(temp.File("a/DOOM2.WAD"), WadKind.Pwad);
        WriteGame(temp.File("b/DOOM2.WAD"), WadKind.Iwad);
        var found = GameLocator.Scan([Path.Combine(temp.Path, "a"), Path.Combine(temp.Path, "b")], cancel: TestContext.Current.CancellationToken);
        Assert.Equal(2, found.Count);
        Assert.True(found[0].Iwad);
        Assert.EndsWith(Path.Combine("b", "DOOM2.WAD"), found[0].Path, StringComparison.Ordinal);
        Assert.False(found[1].Iwad);
        Assert.All(found, g => Assert.Equal("doom2", g.Game.Id));
        Assert.Equal([found[0].Path], GameLocator.FilesFor(found, "doom2"));
        Assert.Empty(GameLocator.FilesFor(found, "heretic"));
    }
}
