using System.IO.Compression;
using WadSmith.Core.Engines;

namespace WadSmith.Tests;

/// <summary>Play From Here: WadSmith's add-on and the setting that names the spot (measured in GZDoom: "WADSMITH FROM 512 0").</summary>
public sealed class PlayFromTests
{
    [Fact]
    public void TheAddOnHoldsItsHandlerItsSettingAndWhereToFindIt()
    {
        using var temp = new TempFolder();
        var path = PlayFrom.Write(temp.Path);
        using var zip = ZipFile.OpenRead(path);
        string Text(string name)
        {
            using var r = new StreamReader(zip.GetEntry(name)!.Open());
            return r.ReadToEnd();
        }

        Assert.Contains("class WadSmithPlayFrom : StaticEventHandler", Text("zscript.txt"), StringComparison.Ordinal);
        Assert.Equal("server noarchive string wadsmith_from = \"\";\n", Text("cvarinfo.txt"));
        Assert.Contains("AddEventHandlers = \"WadSmithPlayFrom\"", Text("mapinfo.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain('\r', Text("zscript.txt"));
    }

    [Fact]
    public void TheAddOnIsWrittenOnlyWhenItChanges()
    {
        // A game still running from the last Play holds the add-on open, and Windows refuses to replace an open file.
        using var temp = new TempFolder();
        var path = PlayFrom.Write(temp.Path);
        var first = File.ReadAllBytes(path);
        var written = File.GetLastWriteTimeUtc(path);
        using (File.OpenRead(path))
        {
            Assert.Equal(path, PlayFrom.Write(temp.Path));
        }

        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        // Its entries carry a fixed date, not the clock's, so the add-on is the same bytes on another day too.
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.All(zip.Entries, e => Assert.True(e.LastWriteTime < DateTimeOffset.Now.AddDays(-1), e.FullName));
        }

        // One left by an older WadSmith is replaced.
        File.WriteAllBytes(path, [1, 2, 3]);
        PlayFrom.Write(temp.Path);
        Assert.Equal(first, File.ReadAllBytes(path));
    }

    [Fact]
    public void PlayingFromASpotAddsTheAddOnAndTheSettingBeforeTheMap()
    {
        var request = new PlayRequest("/g/DOOM2.WAD", ["/p"], "MAP01", 3, "/d/c.ini", "/d/l.log", (512, -0.5, 180), "/d/engine/wadsmith-play.pk3");
        var (_, args) = PlayCommand.For(new EngineInstall("GZDoom", "/usr/bin/gzdoom", "native"), request);
        var at = args.ToList().IndexOf("+set");
        Assert.Equal(["-file", "/d/engine/wadsmith-play.pk3", "+set", "wadsmith_from", "512 -0.5 180"], args.Skip(at - 2).Take(5));
        Assert.True(at < args.ToList().IndexOf("+map"));

        // A plain Play has neither.
        var (_, plain) = PlayCommand.For(new EngineInstall("GZDoom", "/usr/bin/gzdoom", "native"), request with { From = null });
        Assert.DoesNotContain("+set", plain);
    }
}
