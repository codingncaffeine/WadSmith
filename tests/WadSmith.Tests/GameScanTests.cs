using System.IO.Compression;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

/// <summary>
/// Finding the games the way a player keeps them: inside archives, in the folder beside the one they picked, in the
/// folders their engine already searches; and saying what a folder held when no game was in it.
/// </summary>
public sealed class GameScanTests
{
    /// <summary>A small map pack: one classic map and nothing else.</summary>
    private static byte[] MapPack() => WadWriter.Write(WadKind.Pwad, [("MAP03", ReadOnlyMemory<byte>.Empty), ("THINGS", ReadOnlyMemory<byte>.Empty), ("LINEDEFS", ReadOnlyMemory<byte>.Empty), ("SIDEDEFS", ReadOnlyMemory<byte>.Empty), ("VERTEXES", ReadOnlyMemory<byte>.Empty), ("SECTORS", ReadOnlyMemory<byte>.Empty)]);

    private static string Zip(string path, params (string Entry, string File)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, file) in entries) zip.CreateEntryFromFile(file, entry);
        return path;
    }

    [Fact]
    public void ABaseGameInsideAZipIsFoundAndTakenOutForTheEngine()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "downloads")).FullName;
        var zip = Zip(Path.Combine(folder, "my-doom-backup.zip"), ("games/freedoom2.wad", wad));
        var extracted = Path.Combine(temp.Path, "extracted");

        var report = GameLocator.Report([new ScanTarget(folder, 2)], cancel: TestContext.Current.CancellationToken);
        var inside = Assert.Single(report.Games);
        Assert.Equal(("freedoom2", zip, "games/freedoom2.wad", false), (inside.Game.Id, inside.Path, inside.Inside, inside.Usable));
        var here = Assert.Single(report.Folders);
        Assert.Equal((0, 1), (here.Wads, here.Archives));
        Assert.Empty(Directory.Exists(extracted) ? Directory.GetFiles(extracted, "*", SearchOption.AllDirectories) : []);

        // Taken out when it is to be used: once; a second time, the copy there is kept.
        var usable = GameLocator.Usable(report.Games, extracted);
        var game = Assert.Single(usable);
        Assert.Equal((Path.Combine(extracted, "my-doom-backup", "freedoom2.wad"), true), (game.Path, game.Usable));
        Assert.Equal(new FileInfo(wad).Length, new FileInfo(game.Path).Length);
        var written = File.GetLastWriteTimeUtc(game.Path);
        Thread.Sleep(20);
        Assert.Equal(game.Path, Assert.Single(GameLocator.Usable(report.Games, extracted)).Path);
        Assert.Equal(written, File.GetLastWriteTimeUtc(game.Path));

        // A loose copy of the same game comes first and nothing is taken out for it.
        var loose = Path.Combine(folder, "loose.wad");
        File.Copy(wad, loose);
        var both = GameLocator.Report([new ScanTarget(folder, 2)], cancel: TestContext.Current.CancellationToken);
        Assert.Equal([loose, zip], both.Games.Select(g => g.Path));
        Assert.Equal([loose], GameLocator.Usable(both.Games, Path.Combine(temp.Path, "elsewhere")).Select(g => g.Path));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "elsewhere")));
    }

    [Fact]
    public void WhatAnArchiveHeldIsRememberedUntilItChanges()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "downloads")).FullName;
        Zip(Path.Combine(folder, "backup.zip"), ("freedoom2.wad", wad));
        var pack = Path.Combine(temp.Path, "pack.wad");
        File.WriteAllBytes(pack, MapPack());
        var packZip = Zip(Path.Combine(folder, "pack.zip"), ("pack.wad", pack));
        var cacheFile = Path.Combine(temp.Path, "cache", "game-scan.json");
        var heard = new List<string>();
        var progress = new SynchronousProgress(heard.Add);

        var cache = ArchiveCache.Load(cacheFile);
        var first = GameLocator.Report([new ScanTarget(folder, 2)], cache, progress, TestContext.Current.CancellationToken);
        Assert.Equal((0, 2), (cache.Hits, cache.Count));
        Assert.Equal(["Looking inside backup.zip…", "Looking inside pack.zip…"], heard.Order(StringComparer.Ordinal));
        cache.Save(cacheFile);

        // The same archives again: answered from memory, nothing opened, the same answers.
        heard.Clear();
        var again = ArchiveCache.Load(cacheFile);
        var second = GameLocator.Report([new ScanTarget(folder, 2)], again, progress, TestContext.Current.CancellationToken);
        Assert.Equal((2, 2), (again.Hits, again.Count));
        Assert.Empty(heard);
        Assert.Equal(first.Games.Select(g => (g.Game.Id, g.Path, g.Inside, g.Size, g.Iwad)), second.Games.Select(g => (g.Game.Id, g.Path, g.Inside, g.Size, g.Iwad)));
        Assert.Equal(first.MapFiles.Select(m => (m.Name, string.Join(",", m.Maps))), second.MapFiles.Select(m => (m.Name, string.Join(",", m.Maps))));

        // A changed archive is read again.
        File.SetLastWriteTimeUtc(packZip, DateTime.UtcNow.AddMinutes(1));
        heard.Clear();
        GameLocator.Report([new ScanTarget(folder, 2)], again, progress, TestContext.Current.CancellationToken);
        Assert.Equal(["Looking inside pack.zip…"], heard);
    }

    [Fact]
    public void ArchivesInAGameLibraryAreNotRead()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var library = Directory.CreateDirectory(Path.Combine(temp.Path, "steamapps", "common", "SomeGame")).FullName;
        Zip(Path.Combine(library, "assets.zip"), ("freedoom2.wad", wad));
        File.Copy(wad, Path.Combine(library, "loose.wad"));
        var report = GameLocator.Report([new ScanTarget(temp.Path, 4, ReadArchives: false)], cancel: TestContext.Current.CancellationToken);
        var here = Assert.Single(report.Folders);
        Assert.Equal((1, 0), (here.Wads, here.Archives));
        Assert.Equal([Path.Combine(library, "loose.wad")], report.Games.Select(g => g.Path));
    }

    /// <summary>A progress sink that reports on the caller's thread, so a test sees every message at once.</summary>
    private sealed class SynchronousProgress(Action<string> heard) : IProgress<string>
    {
        public void Report(string value) => heard(value);
    }

    [Fact]
    public void AMapPackIsReportedAndNotMistakenForAGameLooseOrZipped()
    {
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "maps")).FullName;
        var pack = Path.Combine(folder, "mypack.wad");
        File.WriteAllBytes(pack, MapPack());
        Zip(Path.Combine(folder, "mypack.zip"), ("mypack/mypack.wad", pack), ("mypack/readme.txt", pack));

        var report = GameLocator.Report([new ScanTarget(folder, 2)], cancel: TestContext.Current.CancellationToken);
        Assert.Empty(report.Games);
        var here = Assert.Single(report.Folders);
        Assert.Equal((1, 1), (here.Wads, here.Archives));
        Assert.Equal(["mypack.wad", "mypack.zip › mypack/mypack.wad"], here.MapFiles.Select(m => m.Name).Order());
        Assert.All(here.MapFiles, m => Assert.Equal(["MAP03"], m.Maps));
    }

    [Fact]
    public void TheFolderAboveTheOneAPlayerPickedIsLookedIn()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var port = Directory.CreateDirectory(Path.Combine(temp.Path, "games", "port")).FullName;
        File.Copy(wad, Path.Combine(port, "freedoom2.wad"));
        var picked = Directory.CreateDirectory(Path.Combine(port, "DOOM WADs")).FullName;
        File.WriteAllBytes(Path.Combine(picked, "shreds.wad"), MapPack());

        var around = GameLocator.Around(picked);
        Assert.Equal([picked, port, Path.Combine(temp.Path, "games")], around.Select(t => t.Folder));
        Assert.Equal([4, 2, 1], around.Select(t => t.Depth));
        Assert.DoesNotContain(GameLocator.Around("/tmp"), t => t.Folder == "/");

        var report = GameLocator.Report([new ScanTarget(picked, 4, Added: true)], cancel: TestContext.Current.CancellationToken);
        var game = Assert.Single(report.Games);
        Assert.Equal(Path.Combine(port, "freedoom2.wad"), game.Path);
        Assert.Equal(3, report.Folders.Count);
        Assert.Equal(("DOOM WADs", 1, 0), (Path.GetFileName(report.Folders[0].Folder), report.Folders[0].Wads, report.Folders[0].Games.Count));
        Assert.Equal(("port", 1), (Path.GetFileName(report.Folders[1].Folder), report.Folders[1].Games.Count));
        Assert.Single(report.Folders[0].MapFiles);
    }

    [Fact]
    public void TheEnginesOwnSearchFoldersAreRead()
    {
        using var temp = new TempFolder();
        var home = Directory.CreateDirectory(Path.Combine(temp.Path, "home")).FullName;
        Directory.CreateDirectory(Path.Combine(home, ".config", "gzdoom"));
        File.WriteAllText(Path.Combine(home, ".config", "gzdoom", "gzdoom.ini"), """
            [IWADSearch.Directories]
            Path=.
            Path=$DOOMWADDIR
            Path=$HOME/mywads
            Path=$PROGDIR
            Path=/usr/share/games/doom

            [FileSearch.Directories]
            Path=$HOME/pwads

            [SoundfontSearch.Directories]
            Path=$HOME/soundfonts
            """);
        string? Env(string name) => name == "HOME" ? home : null;
        Assert.Equal([Path.Combine(home, "mywads"), "/usr/share/games/doom", Path.Combine(home, "pwads")], GameLocator.EngineSearchFolders(home, Env));

        // Only the ones that exist reach the standard list.
        Directory.CreateDirectory(Path.Combine(home, "pwads"));
        var standard = GameLocator.StandardFolders(Env);
        Assert.Contains(Path.Combine(home, "pwads"), standard);
        Assert.DoesNotContain(Path.Combine(home, "mywads"), standard);
    }

    [Fact]
    public void MapsAreTheNamesBeforeThingsOrTextmap() =>
        Assert.Equal(["MAP01", "MAP02"], GameLocator.MapsIn(["MAP01", "THINGS", "LINEDEFS", "MAP02", "TEXTMAP", "ENDMAP", "TEXTURE1", "THINGS"]));
}
