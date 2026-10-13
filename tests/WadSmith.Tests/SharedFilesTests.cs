using WadSmith.Core;

namespace WadSmith.Tests;

/// <summary>
/// A file another program holds open (on Windows: a virus scanner the moment a map is saved) can still be read and
/// replaced. Linux has no such locks, so there these pass on any code; the Windows leg of CI is where they bite.
/// </summary>
public sealed class SharedFilesTests
{
    [Fact]
    public void AFileOpenForWritingElsewhereIsStillRead()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "MAP01.wad");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        // The control: a plain read is refused on Windows while the other handle can write.
        if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.ReadAllBytes(path));
        Assert.Equal([1, 2, 3], SharedFiles.ReadAllBytes(path));
        Assert.Equal("\u0001\u0002\u0003", SharedFiles.ReadAllText(path));
    }

    [Fact]
    public async Task AReadWaitsForAFileHeldShutForAMoment()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "project.json");
        File.WriteAllText(path, "{}");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.ReadAllText(path));

        var letGo = LetGoSoon(held);
        Assert.Equal("{}", SharedFiles.ReadAllText(path));
        await letGo;
    }

    [Fact]
    public async Task AFileHeldForAMomentIsStillReplaced()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "MAP01.wad");
        var saving = path + ".saving";
        File.WriteAllBytes(path, [1]);
        File.WriteAllBytes(saving, [2]);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (OperatingSystem.IsWindows()) Assert.ThrowsAny<Exception>(() => File.Move(saving, path, overwrite: true));

        var letGo = LetGoSoon(held);
        SharedFiles.Replace(saving, path);
        await letGo;
        Assert.Equal([2], File.ReadAllBytes(path));
        Assert.False(File.Exists(saving));
    }

    // Its own thread, so the pool being busy cannot keep the file held past the waits.
    private static Task LetGoSoon(FileStream held) => Task.Factory.StartNew(() =>
    {
        Thread.Sleep(150);
        held.Dispose();
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
