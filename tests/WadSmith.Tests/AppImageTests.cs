using System.Diagnostics;
using WadSmith.Core.Engines;

namespace WadSmith.Tests;

/// <summary>
/// An AppImage's data file (UZDoom's Linux download, D64) is taken out by its own runtime, asked with no screen and no
/// session, once until the AppImage changes; a file that is no AppImage is never run.
/// </summary>
public sealed class AppImageTests
{
    private static readonly TimeSpan Allowed = TimeSpan.FromSeconds(5);

    [Fact]
    public void AFileThatIsNoAppImageIsNeverRun()
    {
        using var temp = new TempFolder();
        var script = Path.Combine(temp.Path, "uzdoom.AppImage");
        File.WriteAllText(script, "#!/bin/sh\necho started > started.txt\n");
        var runs = 0;
        Assert.False(AppImage.IsAppImage(script));
        Assert.Null(AppImage.DataFile(script, "uzdoom", Path.Combine(temp.Path, "cache"), Allowed, (_, _) => ++runs > 0));
        Assert.Equal(0, runs);
    }

    [Fact]
    public void TheRuntimeIsAskedForTheDataFileWithNoScreenAndWhatItGivesIsKeptUntilTheAppImageChanges()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "Linux-UZDoom-Release-x86_64.AppImage");
        EngineLocatorTests.PlantAppImage(image, startable: true);
        var cache = Path.Combine(temp.Path, "cache");
        var asked = new List<string>();
        bool Runtime(ProcessStartInfo start, TimeSpan timeout)
        {
            asked.Add(string.Join(" ", start.ArgumentList));
            Assert.Equal(image, start.FileName);
            Assert.StartsWith(cache, start.WorkingDirectory, StringComparison.Ordinal);
            foreach (var variable in new[] { "DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "APPIMAGE_EXTRACT_AND_RUN", "TARGET_APPIMAGE" })
            {
                Assert.False(start.Environment.ContainsKey(variable), variable);
            }

            // As the runtime does: what matches the pattern, unpacked under squashfs-root in its working folder.
            if (start.ArgumentList[1] != "usr/share/games/*/?zdoom.pk3") return true;
            var pk3 = Path.Combine(start.WorkingDirectory, "squashfs-root/usr/share/games/uzdoom/uzdoom.pk3");
            Directory.CreateDirectory(Path.GetDirectoryName(pk3)!);
            File.WriteAllText(pk3, $"UZDoom's data {asked.Count}");
            return true;
        }

        var data = AppImage.DataFile(image, "uzdoom", cache, Allowed, Runtime);
        Assert.NotNull(data);
        Assert.Equal(("uzdoom.pk3", "UZDoom's data 1"), (Path.GetFileName(data), File.ReadAllText(data)));
        Assert.Equal(["--appimage-extract usr/share/games/*/?zdoom.pk3"], asked);
        Assert.Equal([Path.GetDirectoryName(data)!], Directory.GetDirectories(cache));

        // Kept: asked again, the runtime is not run.
        Assert.Equal(data, AppImage.DataFile(image, "uzdoom", cache, Allowed, Runtime));
        Assert.Single(asked);

        // Another AppImage in its place (another time or size) is unpacked again.
        File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal("UZDoom's data 2", File.ReadAllText(AppImage.DataFile(image, "uzdoom", cache, Allowed, Runtime)!));
    }

    [Fact]
    public void ARuntimeThatFailsOrGivesNothingLeavesNoDataFile()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "uzdoom.AppImage");
        EngineLocatorTests.PlantAppImage(image, startable: true);
        var cache = Path.Combine(temp.Path, "cache");
        var tried = 0;
        Assert.Null(AppImage.DataFile(image, "uzdoom", cache, Allowed, (_, _) => ++tried < 0));
        Assert.Equal(3, tried);
        Assert.Null(AppImage.DataFile(image, "uzdoom", cache, Allowed, (_, _) => true));
        Assert.Empty(Directory.Exists(cache) ? Directory.GetFileSystemEntries(cache) : []);
    }

    /// <summary>
    /// The real thing, when WS_REAL_APPIMAGE names a UZDoom AppImage: its runtime gives the data file. Run it on a private
    /// screen and bus (xvfb-run -a dbus-run-session); the runtime opens no window either way.
    /// </summary>
    [Fact]
    public void ARealUZDoomAppImageGivesItsDataFile()
    {
        var real = Environment.GetEnvironmentVariable("WS_REAL_APPIMAGE");
        Assert.SkipWhen(string.IsNullOrEmpty(real) || !File.Exists(real), "WS_REAL_APPIMAGE names no AppImage.");
        using var temp = new TempFolder();
        var watch = Stopwatch.StartNew();
        var data = AppImage.DataFile(real!, "uzdoom", temp.Path, TimeSpan.FromSeconds(60));
        TestContext.Current.SendDiagnosticMessage($"taken out in {watch.ElapsedMilliseconds} ms: {data}");
        Assert.NotNull(data);
        Assert.Equal("uzdoom.pk3", Path.GetFileName(data));
        using var zip = System.IO.Compression.ZipFile.OpenRead(data);
        Assert.NotNull(zip.GetEntry("zscript.txt"));
    }
}
