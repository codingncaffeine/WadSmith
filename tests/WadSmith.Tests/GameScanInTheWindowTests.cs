using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.VisualTree;
using WadSmith.Core.Containers;

namespace WadSmith.Tests;

/// <summary>The start screen with games where a player keeps them: above the folder they picked, or not there at all, said plainly.</summary>
[Collection(TimingCollection.Name)]
public sealed class GameScanInTheWindowTests
{
    private static byte[] MapPack() => WadWriter.Write(WadKind.Pwad, [("MAP03", ReadOnlyMemory<byte>.Empty), ("THINGS", ReadOnlyMemory<byte>.Empty), ("LINEDEFS", ReadOnlyMemory<byte>.Empty), ("SIDEDEFS", ReadOnlyMemory<byte>.Empty), ("VERTEXES", ReadOnlyMemory<byte>.Empty), ("SECTORS", ReadOnlyMemory<byte>.Empty)]);

    [Fact]
    public async Task AGameAboveThePickedFolderIsFoundAndTheStatusSaysWhere()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var port = Directory.CreateDirectory(Path.Combine(temp.Path, "port")).FullName;
        File.Copy(wad, Path.Combine(port, "freedoom2.wad"));
        var picked = Directory.CreateDirectory(Path.Combine(port, "DOOM WADs")).FullName;
        File.WriteAllBytes(Path.Combine(picked, "shreds.wad"), MapPack());
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), picked);
            try
            {
                return $"{vm.Welcome.Games.Count} | {vm.Welcome.Status} | no games {vm.Welcome.NoGames}";
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal("1 | Found 1 game. It is in port, above the folder you added (DOOM WADs). | no games False", seen);
    }

    [Fact]
    public async Task TheSecondStartRemembersWhatTheArchivesHeld()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "downloads")).FullName;
        using (var zip = ZipFile.Open(Path.Combine(folder, "backup.zip"), ZipArchiveMode.Create)) zip.CreateEntryFromFile(wad, "freedoom2.wad");
        var pack = Path.Combine(temp.Path, "pack.wad");
        File.WriteAllBytes(pack, MapPack());
        using (var zip = ZipFile.Open(Path.Combine(folder, "pack.zip"), ZipArchiveMode.Create)) zip.CreateEntryFromFile(pack, "pack.wad");
        var profile = Path.Combine(temp.Path, "profile");
        var seen = await Ui.Run(() =>
        {
            var said = new List<string>();
            for (var start = 1; start <= 2; start++)
            {
                var (window, vm) = Ui.Start(profile, folder);
                try
                {
                    // Other tests' folders may sit beside this one and be looked in too: what matters is that the second start remembers every archive the first read.
                    var (remembered, known) = vm.Welcome.ScanMemory;
                    var memory = known < 2 ? $"too few known ({known})" : remembered == 0 ? "none remembered" : remembered == known ? "all remembered" : $"{remembered} of {known} remembered";
                    said.Add($"start {start}: {vm.Welcome.Games.Count} game, {memory}, plays {Path.GetFileName(Path.GetDirectoryName(App.App.Services!.Games![0].Path))}/{Path.GetFileName(App.App.Services.Games[0].Path)}");
                }
                finally
                {
                    window.Close();
                }
            }

            return said;
        });
        Assert.Equal(["start 1: 1 game, none remembered, plays backup/freedoom2.wad", "start 2: 1 game, all remembered, plays backup/freedoom2.wad"], seen);
    }

    [Fact]
    public async Task AFolderWithOnlyMapPacksIsDescribedAndAGameFileCanBePicked()
    {
        using var temp = new TempFolder();
        var maps = Directory.CreateDirectory(Path.Combine(temp.Path, "maps")).FullName;
        var pack = Path.Combine(maps, "mypack.wad");
        File.WriteAllBytes(pack, MapPack());
        using (var zip = ZipFile.Open(Path.Combine(maps, "pack.zip"), ZipArchiveMode.Create)) zip.CreateEntryFromFile(pack, "pack/inner.wad");
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), maps);
            try
            {
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).Where(c => c is "Pick a game file…" or "Get Freedoom" or "Add a folder…").Distinct().Order(StringComparer.Ordinal);
                return $"{vm.Welcome.Games.Count} | {vm.Welcome.NoGames} {vm.Welcome.FoldersAdded} | {vm.Welcome.NoGamesHeading} | {vm.Welcome.NoGamesText.Split('\n')[0]} | {string.Join(", ", buttons)}";
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal($"0 | True True | No base game in the folders you added | {maps}: 1 WAD and 1 archive looked at, 2 map files (mypack.wad, pack.zip › pack/inner.wad); no base game. | Add a folder…, Get Freedoom, Pick a game file…", seen);
    }
}
