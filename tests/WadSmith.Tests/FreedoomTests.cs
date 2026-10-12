using System.IO.Compression;
using System.Security.Cryptography;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

public sealed class FreedoomTests
{
    private static string Zip(TempFolder temp, params (string Path, string Text)[] entries)
    {
        var path = temp.File("download.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(archive.CreateEntry(name).Open());
                w.Write(text);
            }
        }

        return path;
    }

    private static string HashOf(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    [Fact]
    public void ADownloadIsCheckedBeforeAnythingIsUnpacked()
    {
        using var temp = new TempFolder();
        var zip = Zip(temp, ("freedoom-0.13.0/freedoom1.wad", "IWAD one"), ("freedoom-0.13.0/freedoom2.wad", "IWAD two"), ("freedoom-0.13.0/README.html", "about"));
        var games = Path.Combine(temp.Path, "games");

        var refused = Assert.Throws<InvalidDataException>(() => Freedoom.Unpack(zip, games, Freedoom.Sha256));
        Assert.Contains("not Freedoom", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(games));

        Freedoom.Unpack(zip, games, HashOf(zip));
        Assert.Equal("IWAD one", File.ReadAllText(Path.Combine(games, "freedoom1.wad")));
        Assert.Equal("IWAD two", File.ReadAllText(Path.Combine(games, "freedoom2.wad")));
        Assert.Equal(2, Directory.GetFiles(games).Length);
    }

    [Fact]
    public void ADownloadMissingAGameIsRefusedWhole()
    {
        using var temp = new TempFolder();
        var zip = Zip(temp, ("freedoom-0.13.0/freedoom1.wad", "IWAD one"));
        var games = Path.Combine(temp.Path, "games");
        Assert.Throws<InvalidDataException>(() => Freedoom.Unpack(zip, games, HashOf(zip)));
        Assert.False(File.Exists(Path.Combine(games, "freedoom1.wad")));
    }
}
