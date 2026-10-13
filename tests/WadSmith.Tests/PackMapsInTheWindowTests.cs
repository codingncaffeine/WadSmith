using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.VisualTree;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>A map pack beside the games is listed on the tile of the game it is for, and one click opens its map with the pack loaded (D52).</summary>
[Collection(TimingCollection.Name)]
public sealed class PackMapsInTheWindowTests
{
    private static void Click(Window window, Control target)
    {
        // As the user would: scrolled into view first, then pressed where it shows.
        target.BringIntoView();
        Ui.Pump(0.2);
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not in the window.");
        window.MouseMove(middle, Avalonia.Input.RawInputModifiers.None);
        window.MouseDown(middle, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        window.MouseUp(middle, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    [Fact]
    public async Task APackBesideTheGameIsOnItsTileAndOpensWithThePack()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var games = Directory.CreateDirectory(Path.Combine(temp.Path, "games")).FullName;
        File.Copy(wad, Path.Combine(games, "freedoom2.wad"));
        // Editing a map from a pack reads the engine's rules: its data file goes beside the games, as CI has no GZDoom installed.
        File.Copy(GameFiles.Require("gzdoom.pk3"), Path.Combine(games, "gzdoom.pk3"));
        var model = StarterRoom.Create("MAP03", "doom");
        var text = System.Text.Encoding.UTF8.GetBytes(UdmfText.WriteString(MapConversion.ToUdmf(model)));
        var pack = Path.Combine(games, "shreds.wad");
        File.WriteAllBytes(pack, WadWriter.Write(WadKind.Pwad, [("MAP03", ReadOnlyMemory<byte>.Empty), ("TEXTMAP", text), ("ENDMAP", ReadOnlyMemory<byte>.Empty)]));
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), games);
            try
            {
                var said = new List<string>();
                var tile = vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2");
                var choice = tile.PackMaps.Single(p => p.Pack.Name == "shreds.wad");
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, tile)));
                var heading = window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.IsEffectivelyVisible && b.Text is { } c && c.StartsWith("Maps found near your games", StringComparison.Ordinal) && ReferenceEquals(b.DataContext, tile));
                said.Add($"{heading.Text}: {choice.Label}");

                // The search box shortens the list; picking a map in it starts editing it.
                var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.PlaceholderText == "Find a pack or a map…");
                search.Text = "nothing like this";
                Ui.Pump(0.2);
                var none = tile.ShownPackMaps.Count;
                search.Text = "shre";
                Ui.Pump(0.2);
                said.Add($"filtered: {none} then {tile.ShownPackMaps.Count}, {tile.ShownPackMaps.FirstOrDefault()?.Label}");
                // Picked as the user picks it: a click on its row in the list.
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, choice)));
                for (var i = 0; i < 100 && vm.Page is not App.ViewModels.EditorViewModel; i++) Ui.Pump(0.05);
                Ui.Pump(1.0);
                var editor = (App.ViewModels.EditorViewModel)vm.Page!;
                var session = editor.Session;
                said.Add($"opened {session.Project.Info.Name} ({session.Slot}): {session.Map.Sectors.Count(s => s.OnMap)} room, add-on {string.Join(", ", session.Project.Info.AddOns.Select(Path.GetFileName))}, game {session.Resources?.Game.Id}, files {string.Join(" + ", session.Resources?.Files.Select(Path.GetFileName) ?? [])}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["Maps found near your games · 1: shreds.wad › MAP03", "filtered: 0 then 1, shreds.wad › MAP03", "opened shreds MAP03 (MAP03): 1 room, add-on shreds.wad, game freedoom2, files freedoom2.wad + shreds.wad"], seen);
    }
}
