using System.IO.Compression;
using WadSmith.Core.Engines;

namespace WadSmith.Tests;

/// <summary>The game's view (D41): the add-on that stands the player at the spot, and the capture with a stand-in engine.</summary>
public sealed class EngineViewTests
{
    [Fact]
    public void TheAddOnStandsThePlayerAtTheSpotAndIsWrittenOnce()
    {
        using var temp = new TempFolder();
        var path = EngineView.WriteAddOn(temp.Path);
        var first = File.GetLastWriteTimeUtc(path);
        using (var zip = ZipFile.OpenRead(path))
        {
            string Text(string name)
            {
                using var r = new StreamReader(zip.GetEntry(name)!.Open());
                return r.ReadToEnd();
            }

            Assert.Contains("class WadSmithView : StaticEventHandler", Text("zscript.txt"), StringComparison.Ordinal);
            Assert.Contains("mo.pitch = parts.Size() >= 5", Text("zscript.txt"), StringComparison.Ordinal);
            Assert.Contains("noarchive string wsview", Text("cvarinfo.txt"), StringComparison.Ordinal);
            Assert.Contains("AddEventHandlers = \"WadSmithView\"", Text("mapinfo.txt"), StringComparison.Ordinal);
        }

        Assert.Equal(path, EngineView.WriteAddOn(temp.Path));
        Assert.Equal(first, File.GetLastWriteTimeUtc(path));
        Assert.Equal(" 10 -20 90", new ViewSpot(10, -20, 90).Text);
        Assert.Equal(" 10 -20 90 41 -12.5", new ViewSpot(10, -20, 90, 41, -12.5).Text);
    }

    private static string FakeEngine(TempFolder temp, string body)
    {
        var script = temp.File("fake/gzdoom");
        File.WriteAllText(script, "#!/usr/bin/env bash\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [Fact]
    public async Task TheCaptureGetsTheEnginesScreenshotAndStopsTheEngine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script.");
        using var temp = new TempFolder();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAAAoCAAAAABrpaM1AAAAJ0lEQVRIx2OsZ6AMMFGof9SAUQNGDRg1YNSAUQNGDRg1YNSAQWkAAKobAM+E5kdKAAAAAElFTkSuQmCC");
        File.WriteAllBytes(temp.File("fake/shot.png"), png);
        var script = FakeEngine(temp, """
            prev2=""; prev=""; SHOTS=""
            for a in "$@"; do
              [ "$prev2" = "+set" ] && [ "$prev" = "screenshot_dir" ] && SHOTS="$a"
              prev2="$prev"; prev="$a"
            done
            printf '%s\n' "$@" > "$(dirname "$0")/args.txt"
            echo $$ > "$(dirname "$0")/pid.txt"
            sleep 0.3
            cp "$(dirname "$0")/shot.png" "$SHOTS/Screenshot_Doom_0001.png"
            sleep 60
            """);
        var request = new PlayRequest("/g/DOOM2.WAD", ["/p"], "MAP01", 3, temp.File("play.ini"), "/d/l.log");
        File.WriteAllText(request.Config, "[GlobalSettings]\n");
        var result = await EngineView.CaptureAsync(new EngineInstall("Stand-in", script, "custom"), request, new ViewSpot(64, -128, 90, 0, -10), 64, 40, temp.Path, null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(png, File.ReadAllBytes(result));
        var args = File.ReadAllLines(temp.File("fake/args.txt"));
        var set = Array.IndexOf(args, "wsview");
        Assert.Equal(" 64 -128 90 0 -10", args[set + 1]);
        Assert.Contains("-nosound", args);
        Assert.Equal("1", args[Array.IndexOf(args, "+vid_preferbackend") + 1]);
        Assert.Contains(args, a => a.EndsWith("wadsmith-view.pk3", StringComparison.Ordinal));
        Assert.Equal("64", args[Array.IndexOf(args, "-width") + 1]);
        Assert.True(File.Exists(temp.File("engine/view/view.ini")), "the engine gets its own copy of the settings");

        // The engine was stopped although it meant to run on.
        var pid = int.Parse(File.ReadAllText(temp.File("fake/pid.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists($"/proc/{pid}") && !File.ReadAllText($"/proc/{pid}/stat").Contains(" Z ", StringComparison.Ordinal), "the stand-in engine is still running");
    }

    [Fact]
    public async Task AnEngineThatDrawsNothingIsReportedInWords()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script.");
        using var temp = new TempFolder();
        var script = FakeEngine(temp, "echo 'Script error, \"x\" line 1' >&2\nexit 3\n");
        var request = new PlayRequest("/g/DOOM2.WAD", ["/p"], "MAP01", 3, temp.File("play.ini"), "/d/l.log");
        var ex = await Assert.ThrowsAsync<EngineViewException>(() => EngineView.CaptureAsync(new EngineInstall("Stand-in", script, "custom"), request, new ViewSpot(0, 0, 0), 64, 40, temp.Path, null, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Contains("stopped before it drew the view", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Script error", ex.Message, StringComparison.Ordinal);
    }
}
