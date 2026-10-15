using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>
/// Through the real window, as the user went (2026-10-14): a friend's WAD put in a folder of its own and opened with Open a
/// project…, a file opened with Open a map file…, a pack moved while WadSmith runs (listed again where it went, unasked),
/// and a pack gone from where it was found (said so, where the user is looking).
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class FriendsMapInTheWindowTests
{
    private static void Click(Window window, Control target)
    {
        target.BringIntoView();
        Ui.Pump(0.2);
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not in the window.");
        window.MouseMove(middle, Avalonia.Input.RawInputModifiers.None);
        window.MouseDown(middle, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        window.MouseUp(middle, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    /// <summary>Given WADSMITH_FRIEND_SHOT, the window as it shows now, for eyes.</summary>
    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WADSMITH_FRIEND_SHOT") is not { Length: > 0 } shots) return;
        Ui.Pump(0.3);
        using var frame = HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
        frame.Save(Path.Combine(shots, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static Button Shown(Window window, string words) => window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == words);

    /// <summary>A map pack: a WAD holding a lit room for each slot, as a friend's file would.</summary>
    private static void Pack(string path, params string[] slots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lumps = new List<(string, ReadOnlyMemory<byte>)>();
        foreach (var slot in slots)
        {
            var text = System.Text.Encoding.UTF8.GetBytes(UdmfText.WriteString(MapConversion.ToUdmf(StarterRoom.Create(slot, "doom"))));
            lumps.AddRange([(slot, ReadOnlyMemory<byte>.Empty), ("TEXTMAP", text), ("ENDMAP", ReadOnlyMemory<byte>.Empty)]);
        }

        File.WriteAllBytes(path, WadWriter.Write(WadKind.Pwad, lumps));
    }

    /// <summary>A games folder with Freedoom: Phase 2 and the engine's data file (CI has no engine installed).</summary>
    private static string Games(TempFolder temp)
    {
        var games = Directory.CreateDirectory(Path.Combine(temp.Path, "games")).FullName;
        File.Copy(GameFiles.Require("freedoom2.wad"), Path.Combine(games, "freedoom2.wad"));
        File.Copy(GameFiles.Require("gzdoom.pk3"), Path.Combine(games, "gzdoom.pk3"));
        return games;
    }

    private static string StatusBar(Window window) =>
        string.Join(" ", window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 } s && s.Contains("never changed", StringComparison.Ordinal)).Select(t => t.Text));

    [Fact]
    public async Task AFriendsWadInAFolderOpensWithOpenAProjectAndAnEmptyFolderIsSaidSo()
    {
        using var temp = new TempFolder();
        var games = Games(temp);
        var relay = Path.Combine(temp.Path, "Doom Maps", "Relay");
        Pack(Path.Combine(relay, "RELAY.WAD"), "MAP05");
        var empty = Directory.CreateDirectory(Path.Combine(temp.Path, "Doom Maps", "Empty")).FullName;
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), games);
            var said = new List<string>();
            try
            {
                vm.Welcome.SelectedGame = vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2");
                // A folder with no map files in it: the notice says what a project is and what to do, where the user looks.
                vm.Welcome.PickFolder = _ => Task.FromResult<string?>(empty);
                Click(window, Shown(window, "Open a project…"));
                Ui.Until(() => vm.Welcome.Notice.Length > 0);
                var strip = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NoticeStrip");
                Shot(window, "notice.png");
                said.Add($"notice shown {strip.IsEffectivelyVisible}: {vm.Welcome.Notice.Split(':')[0]}");
                Click(window, Shown(window, "OK"));
                said.Add($"put away {!strip.IsEffectivelyVisible}");

                // The friend's WAD in a folder of its own: its one map opens, and the editor says what it opened.
                vm.Welcome.PickFolder = _ => Task.FromResult<string?>(relay);
                Click(window, Shown(window, "Open a project…"));
                Ui.Until(() => vm.Page is EditorViewModel);
                Ui.Pump(0.5);
                var editor = (EditorViewModel)vm.Page!;
                said.Add($"opened {editor.Session.Slot} of {string.Join(", ", editor.Session.Project.Info.AddOns.Select(Path.GetFileName))}; status bar says {StatusBar(window).Length > 0}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(["notice shown True: Empty is not a WadSmith project, and holds no map files. A project is a folder WadSmith makes, with wadsmith.json in it", "put away True", "opened MAP05 of RELAY.WAD; status bar says True"], seen);
    }

    [Fact]
    public async Task AFileWithSeveralMapsOffersThemAndOnePickedOpens()
    {
        using var temp = new TempFolder();
        var games = Games(temp);
        var friend = Path.Combine(temp.Path, "Downloads", "episode.wad");
        Pack(friend, "MAP01", "MAP02", "MAP03");
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), games);
            var said = new List<string>();
            try
            {
                vm.Welcome.SelectedGame = vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2");
                vm.Welcome.PickFile = _ => Task.FromResult<string?>(friend);
                Click(window, Shown(window, "Open a map file…"));
                Ui.Until(() => vm.Welcome.Offered.Count > 0);
                Shot(window, "offered.png");
                said.Add(vm.Welcome.OfferedTitle);
                Click(window, Shown(window, "episode.wad › MAP02"));
                Ui.Until(() => vm.Page is EditorViewModel);
                var editor = (EditorViewModel)vm.Page!;
                said.Add($"opened {editor.Session.Slot}; offer put away {vm.Welcome.Offered.Count == 0}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(["episode.wad: 3 maps for Freedoom: Phase 2. Pick one to edit it; the file itself is never changed.", "opened MAP02; offer put away True"], seen);
    }

    [Fact]
    public async Task APackMovedWithinAGameFolderIsListedWhereItWentUnasked()
    {
        using var temp = new TempFolder();
        var games = Games(temp);
        var before = Path.Combine(games, "from a friend", "shreds.wad");
        var after = Path.Combine(games, "Doom Maps", "Shreds", "shreds.wad");
        Pack(before, "MAP03");
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), games);
            var said = new List<string>();
            try
            {
                var tile = vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2");
                vm.Welcome.SelectedGame = tile;
                tile.PackFilter = "shreds";
                Ui.Pump(0.3);
                string Where() => string.Join(", ", tile.PackMaps.Select(p => Path.GetRelativePath(games, p.Pack.Path)));
                said.Add($"listed at {Where()}");

                // Moved in a file manager while WadSmith runs: the folder's watch sees it, and the tile is brought up to date in
                // place (the same tile, its search kept) once things are quiet: never sooner than the quiet wait.
                Directory.CreateDirectory(Path.GetDirectoryName(after)!);
                var moved = DateTime.UtcNow;
                File.Move(before, after);
                var found = Ui.Until(() => tile.PackMaps.Any(p => p.Pack.Path == after), 20);
                var took = DateTime.UtcNow - moved;
                said.Add($"found again {found}, after the quiet wait {took >= App.GameFolderWatcher.Quiet}, listed at {Where()}");
                said.Add($"same tile {ReferenceEquals(vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2"), tile)}, search kept '{tile.PackFilter}', shown {tile.ShownPackMaps.Count}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(
        [
            $"listed at {Path.Combine("from a friend", "shreds.wad")}",
            $"found again True, after the quiet wait True, listed at {Path.Combine("Doom Maps", "Shreds", "shreds.wad")}",
            "same tile True, search kept 'shreds', shown 1",
        ], seen);
    }

    [Fact]
    public async Task APackGoneFromWhereItWasFoundIsSaidSoAndLookedForAgain()
    {
        using var temp = new TempFolder();
        var games = Games(temp);

        // Beside the games folder, found by the look around it but not watched: only a click finds it gone.
        var gone = Path.Combine(temp.Path, "elsewhere", "relay.wad");
        Pack(gone, "MAP07");
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), games);
            var said = new List<string>();
            try
            {
                var tile = vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2");
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, tile)));
                var choice = tile.PackMaps.Single(p => p.Pack.Path == gone);
                File.Delete(gone);
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, choice)));
                Ui.Until(() => vm.Welcome.Notice.Length > 0);
                said.Add($"still on the start screen {vm.Page is WelcomeViewModel}; notice names the file and where it was {vm.Welcome.Notice.StartsWith($"relay.wad is no longer in {Path.Combine(temp.Path, "elsewhere")}: ", StringComparison.Ordinal)}");
                Ui.Until(() => tile.PackMaps.All(p => p.Pack.Path != gone));
                said.Add($"listed no more {tile.PackMaps.All(p => p.Pack.Path != gone)}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(["still on the start screen True; notice names the file and where it was True", "listed no more True"], seen);
    }
}
