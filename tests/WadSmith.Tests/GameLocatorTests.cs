using WadSmith.Core.Containers;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

public sealed class GameLocatorTests
{
    /// <summary>One map, and the pictures a game is known by: its palette, light tables, player and status bar. A game holds the family's sounds too.</summary>
    private static IEnumerable<(string, ReadOnlyMemory<byte>)> Lumps(bool sounds) =>
        new[] { "MAP01", "THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SECTORS", "PLAYPAL", "COLORMAP", "PLAYA1", "STBAR" }.Concat(sounds ? GameCatalog.DoomSounds : []).Select(n => (n, (ReadOnlyMemory<byte>)new byte[] { 1, 2, 3, 4 }));

    private static void WriteGame(string path, WadKind kind) => File.WriteAllBytes(path, WadWriter.Write(kind, Lumps(sounds: true)));

    /// <summary>
    /// A map pack with a palette of its own brings every picture drawn in it, the player's and the status bar's among them.
    /// It is still a pack, listed with its maps; the game is the file that holds the game's sounds as well, though the pack
    /// is found first and both headers say PWAD (the 2024 re-release's Doom II does).
    /// </summary>
    [Fact]
    public void AMapPackWithAPaletteOfItsOwnIsAPackNotTheGame()
    {
        using var temp = new TempFolder();
        var pack = temp.File("a/megawad.wad");
        File.WriteAllBytes(pack, WadWriter.Write(WadKind.Pwad, Lumps(sounds: false)));
        WriteGame(temp.File("b/DOOM2.WAD"), WadKind.Pwad);

        var report = GameLocator.Report([new ScanTarget(Path.Combine(temp.Path, "a"), 1), new ScanTarget(Path.Combine(temp.Path, "b"), 1)], cancel: TestContext.Current.CancellationToken);
        var game = Assert.Single(report.Games);
        Assert.Equal(("doom2", Path.Combine(temp.Path, "b", "DOOM2.WAD")), (game.Game.Id, game.Path));
        var found = Assert.Single(report.MapFiles);
        Assert.Equal((pack, "MAP01"), (found.Path, string.Join(",", found.Maps)));

        // All but one of the sounds is not the game either: it holds every one.
        var nearly = temp.File("c/nearly.wad");
        File.WriteAllBytes(nearly, WadWriter.Write(WadKind.Pwad, Lumps(sounds: true).Where(l => l.Item1 != GameCatalog.DoomSounds[^1])));
        Assert.Empty(GameLocator.Scan([Path.Combine(temp.Path, "c")], cancel: TestContext.Current.CancellationToken));
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

    [Fact]
    public void GZDoomsDataIsFoundBesideTheGamesWhenNotInstalled()
    {
        using var temp = new TempFolder();
        var beside = Path.Combine(temp.Path, "gzdoom.pk3");
        File.WriteAllBytes(beside, [0x50, 0x4B, 0x05, 0x06]);
        var installed = Core.Games.EngineData.Find();
        // An installed engine comes first; without one, the copy beside the games is used (CI has no GZDoom installed).
        Assert.Equal(installed ?? beside, Core.Games.EngineData.Find(["", Path.Combine(temp.Path, "nothing"), temp.Path]));
        Assert.Equal(installed, Core.Games.EngineData.Find([Path.Combine(temp.Path, "nothing")]));
    }
}
