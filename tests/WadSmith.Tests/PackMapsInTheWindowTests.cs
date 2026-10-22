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
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, tile)));
                Ui.Until(() => tile.Maps.Count > 0, 10);
                Ui.Pump(0.3);

                // The maps are sets, each a card: the game's own first, then the pack beside it.
                var heading = window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.IsEffectivelyVisible && b.Text is { } c && c.StartsWith("Maps · ", StringComparison.Ordinal) && ReferenceEquals(b.DataContext, tile));
                Button Card(string title) => window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Classes.Contains("set") && b.DataContext is App.ViewModels.MapSetViewModel s && s.Title == title);
                var cards = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("set")).Select(b => ((App.ViewModels.MapSetViewModel)b.DataContext!).Title);
                said.Add($"{heading.Text}: {string.Join(", ", cards)}");

                // A card shows its maps; Back shows the cards again.
                Click(window, Card("Freedoom: Phase 2"));
                ListBoxItem[] Rows() => [.. window.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible && i.DataContext is App.ViewModels.SetMap)];
                said.Add($"own: {tile.ShownSetMaps.Count} maps, first {((App.ViewModels.SetMap)Rows()[0].DataContext!).Label}");
                Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content is "‹ Back"));
                said.Add($"back: {Rows().Length} rows, {window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("set"))} cards");

                // The search box finds a set by a map in it; in the set it shortens the maps; picking one starts editing it.
                var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.PlaceholderText == "Find a set or a map…");
                search.Text = "shre";
                Ui.Pump(0.2);
                said.Add($"found: {string.Join(", ", tile.ShownSets.Select(s => s.Title))}");
                Click(window, Card("shreds"));
                search.Text = "nothing like this";
                Ui.Pump(0.2);
                var none = Rows().Length;
                search.Text = "map03";
                Ui.Pump(0.2);
                said.Add($"filtered: {none} then {string.Join(", ", Rows().Select(r => ((App.ViewModels.SetMap)r.DataContext!).Label))}");
                // Picked as the user picks it: a click on its row in the list.
                Click(window, Rows().Single());
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
        Assert.Equal(["Maps · 2 sets, 33 maps: Freedoom: Phase 2, shreds", "own: 32 maps, first MAP01 · Hydroelectric Plant", "back: 0 rows, 2 cards", "found: shreds", "filtered: 0 then MAP03", "opened shreds MAP03 (MAP03): 1 room, add-on shreds.wad, game freedoom2, files freedoom2.wad + shreds.wad"], seen);
    }

    [Fact]
    public async Task AGameAndAPackArePlayedAsTheyAreFromARightClick()
    {
        // The engine is a stand-in that writes down what it was given: no game window opens.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script.");
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var games = Directory.CreateDirectory(Path.Combine(temp.Path, "games")).FullName;
        File.Copy(wad, Path.Combine(games, "freedoom2.wad"));
        var model = StarterRoom.Create("MAP03", "doom");
        var text = System.Text.Encoding.UTF8.GetBytes(UdmfText.WriteString(MapConversion.ToUdmf(model)));
        File.WriteAllBytes(Path.Combine(games, "shreds.wad"), WadWriter.Write(WadKind.Pwad, [("MAP03", ReadOnlyMemory<byte>.Empty), ("TEXTMAP", text), ("ENDMAP", ReadOnlyMemory<byte>.Empty)]));
        var written = Path.Combine(temp.Path, "engine-was-given.txt");
        var engine = Path.Combine(temp.Path, "engine.sh");
        File.WriteAllText(engine, $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{written}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), games);
            try
            {
                App.App.Services.Settings.Current.EnginePath = engine;
                var tile = vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2");
                var row = window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, tile));
                Click(window, row);
                Ui.Until(() => tile.Sets.Count == 2, 10);
                Ui.Pump(0.3);

                // Right-click, then Play in the menu that opens: the game in the list of games, then the pack's card.
                string Play(Control target)
                {
                    target.BringIntoView();
                    Ui.Pump(0.2);
                    var lines = File.Exists(written) ? File.ReadAllLines(written).Length : 0;
                    var holder = target.GetSelfAndVisualDescendants().OfType<Control>().First(c => c.ContextMenu is not null);
                    var menu = holder.ContextMenu!;
                    var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(middle, Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(middle, Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(middle, Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.None);
                    Ui.Pump(0.3);
                    var open = menu.IsOpen;
                    var item = menu.Items.OfType<MenuItem>().SingleOrDefault(m => m.IsVisible && m.Header is string h && h.StartsWith("Play ", StringComparison.Ordinal)) ?? throw new InvalidOperationException($"{holder.GetType().Name} open {open}, items: {string.Join(", ", menu.Items.OfType<MenuItem>().Select(m => $"{m.Header}/{m.IsVisible}"))}");
                    item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                    // A click on the item closes the menu; raised here as an event, it is closed as the click would.
                    menu.Close();
                    Ui.Pump(0.2);
                    Ui.Until(() => File.Exists(written) && File.ReadAllLines(written).Length > lines, 10);
                    return $"{item.Header} (menu open {open}): {File.ReadAllLines(written).Last().Replace(temp.Path + Path.DirectorySeparatorChar, "", StringComparison.Ordinal)}";
                }

                var said = new List<string> { Play(row) };
                var card = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Classes.Contains("set") && b.DataContext is App.ViewModels.MapSetViewModel { Title: "shreds" });
                said.Add(Play(card));
                said.Add(vm.Welcome.Status);
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["Play Freedoom: Phase 2 (menu open True): -iwad games/freedoom2.wad", "Play shreds (menu open True): -iwad games/freedoom2.wad -file games/shreds.wad", "Playing shreds in Your engine."], seen);
    }
}
