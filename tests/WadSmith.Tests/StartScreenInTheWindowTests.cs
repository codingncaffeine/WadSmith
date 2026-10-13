using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;

namespace WadSmith.Tests;

/// <summary>
/// The start screen as a game library: a game picked in the list shows its picture and the ways to start a map, the pick
/// is there again at the next start, the four start tiles fit side by side in the smallest window, and Make me a map is
/// the one that stands out.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class StartScreenInTheWindowTests
{
    private static readonly string[] Tiles = ["Make me a map", "New map", "Remix a map", "Guided first map"];

    private static void Click(Window window, Control target)
    {
        // As the user would: scrolled into view first, then pressed where it shows.
        target.BringIntoView();
        Ui.Pump(0.2);
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not in the window.");
        window.MouseMove(middle, RawInputModifiers.None);
        window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    // What the start screen shows: the chosen game's name on its banner, and each start tile with where it sits in the window.
    private static (string Shown, List<(string Name, Rect Where, IBrush? Fill, bool Enabled)> Tiles) Seen(MainWindow window, MainViewModel vm)
    {
        var detail = window.GetVisualDescendants().OfType<ContentControl>().Single(c => c is not ListBoxItem && c.IsEffectivelyVisible && c.Content is GameTileViewModel);
        var tiles = detail.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && Tiles.Contains(AutomationProperties.GetName(b)))
            .Select(b => (AutomationProperties.GetName(b)!, new Rect(b.TranslatePoint(default, window)!.Value, b.Bounds.Size), b.Background, b.IsEnabled)).ToList();
        return (((GameTileViewModel)detail.Content!).Title + (ReferenceEquals(vm.Welcome.SelectedGame, detail.Content) ? "" : " (not the selection)"), tiles);
    }

    [Fact]
    public async Task APickedGameShowsItsStartTilesAndIsPickedAgainAtTheNextStart()
    {
        var one = GameFiles.Require("freedoom1.wad");
        var two = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var games = Directory.CreateDirectory(Path.Combine(temp.Path, "games")).FullName;
        File.Copy(one, Path.Combine(games, "freedoom1.wad"));
        File.Copy(two, Path.Combine(games, "freedoom2.wad"));
        var profile = Path.Combine(temp.Path, "profile");
        var seen = await Ui.Run(() =>
        {
            var said = new List<string>();
            var (window, vm) = Ui.Start(profile, games);
            try
            {
                // The smallest window the app allows: the four tiles still sit side by side, whole, inside it.
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Ui.Pump(0.5);
                var (shown, tiles) = Seen(window, vm);
                said.Add($"at start: {shown}; {string.Join(", ", tiles.Select(t => t.Name))}");
                var inside = tiles.All(t => t.Where.X >= 0 && t.Where.Right <= window.Bounds.Width + 0.5);
                var apart = tiles.Zip(tiles.Skip(1)).All(p => p.First.Where.Right <= p.Second.Where.X && Math.Abs(p.First.Where.Y - p.Second.Where.Y) < 0.5);
                said.Add($"inside the window {inside}, side by side {apart}");

                // Make me a map stands out: a fill of its own, none of the other tiles shares it.
                var make = tiles.Single(t => t.Name == "Make me a map");
                said.Add($"make me a map enabled {make.Enabled}, a fill of its own {tiles.Where(t => t.Name != make.Name).All(t => !Equals(t.Fill, make.Fill))}");

                // The other game, picked in the list.
                var other = vm.Welcome.Games.Single(g => !ReferenceEquals(g, vm.Welcome.SelectedGame));
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, other)));
                said.Add($"picked: {Seen(window, vm).Shown}");
            }
            finally
            {
                window.Close();
            }

            // The next start opens on the game picked last.
            (window, vm) = Ui.Start(profile, games);
            try
            {
                said.Add($"next start: {Seen(window, vm).Shown}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(
        [
            "at start: Freedoom: Phase 1; Make me a map, New map, Remix a map, Guided first map",
            "inside the window True, side by side True",
            "make me a map enabled True, a fill of its own True",
            "picked: Freedoom: Phase 2",
            "next start: Freedoom: Phase 2",
        ], seen);
    }
}
