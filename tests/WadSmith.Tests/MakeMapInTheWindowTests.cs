using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Generate;

namespace WadSmith.Tests;

/// <summary>
/// "Make me a map" as the user goes (D50): the game card's button opens the page; segments and the seed change the plan the
/// preview shows; Make it puts a project named after the seed in the made-maps folder (the default, then one chosen in
/// Preferences), opens it in the editor, and lists it on the game's card.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class MakeMapInTheWindowTests
{
    private static void Click(MainWindow window, Visual target)
    {
        // As the user would: scrolled into view first, then pressed where it shows.
        if (target is Control control) control.BringIntoView();
        Ui.Pump(0.2);
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not in the window.");
        window.MouseMove(middle, RawInputModifiers.None);
        window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    // The game picked in the list of games, then its Make me a map tile, as the user goes.
    private static void MakeMapFor(MainWindow window, GameTileViewModel tile)
    {
        Click(window, Shown<ListBoxItem>(window, i => ReferenceEquals(i.DataContext, tile)));
        Click(window, Shown<Button>(window, b => ReferenceEquals(b.DataContext, tile) && AutomationProperties.GetName(b) == "Make me a map"));
    }

    private static T Shown<T>(MainWindow window, Func<T, bool> which)
        where T : Visual => window.GetVisualDescendants().OfType<T>().First(v => v.IsEffectivelyVisible && which(v));

    [Fact]
    public async Task TheCardsButtonMakesAMapThatOpensAndIsListedOnTheCard()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var log = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var said = new List<string>();
            try
            {
                var tile = vm.Welcome.Games.First(t => t.Found.Game.Id == "freedoom2");
                // The card set to vanilla Doom: the page says a made map plays in GZDoom.
                tile.TargetIndex = WadSmith.Core.Projects.Targets.All.ToList().FindIndex(t => t.Id == WadSmith.Core.Projects.Targets.Vanilla);
                MakeMapFor(window, tile);
                var page = (MakeMapViewModel)vm.Page;
                said.Add($"page for {page.GameTitle}, base row {page.Sources[0].Title}, plays in {page.Target}, note {page.TargetNote.Length > 0}");

                // Large, three locked doors, two bosses, by their segments; the seed typed.
                Click(window, Shown<ToggleButton>(window, b => ReferenceEquals(b.DataContext, page.Size.Items[2])));
                Click(window, Shown<ToggleButton>(window, b => ReferenceEquals(b.DataContext, page.Keys.Items[3])));
                Click(window, Shown<ToggleButton>(window, b => ReferenceEquals(b.DataContext, page.Bosses.Items[2])));
                Shown<TextBox>(window, t => t.Text == page.Seed).Text = "bleak-depot";
                for (var i = 0; i < 200 && !page.Facts.StartsWith("Bleak Depot", StringComparison.Ordinal); i++) Ui.Pump(0.05);
                said.Add(page.Facts.Split(" · about")[0]);
                said.Add($"plan bosses {page.Plan!.Things.Count(t => t.Role == ThingRole.Boss)}, preview checked {page.Verdict.Contains("Checked:", StringComparison.Ordinal)}");

                Click(window, Shown<Button>(window, b => Equals(b.Content, "Make it")));
                for (var i = 0; i < 600 && vm.Page is not EditorViewModel; i++) Ui.Pump(0.05);
                var editor = (EditorViewModel)vm.Page;
                var folder = editor.Session.Project.Folder;
                Ui.Pump(1.0);
                var walk = Progression.Walk(editor.Session.Geometry, "doom");
                said.Add($"opened {Path.GetFileName(folder)} in {Path.GetFileName(Path.GetDirectoryName(folder))}, exit reached {walk.ExitReached}, keys {walk.Steps.Count(s => s.Kind == "key")}, settings kept {File.Exists(Path.Combine(folder, MadeProjects.SettingsFile))}, project plays in {editor.Session.Project.Info.Target}");

                // Ctrl+K's "Make another like it": the page again with this map's choices and a new seed; back to the start screen,
                // the map is on its game's card.
                editor.Commands().Single(c => c.Name == "Make another like it").Run();
                for (var i = 0; i < 100 && vm.Page is not MakeMapViewModel; i++) Ui.Pump(0.05);
                var again = (MakeMapViewModel)vm.Page;
                said.Add($"another like it: size {again.Size.Index}, keys {again.Keys.Index}, bosses {again.Bosses.Index}, new seed {again.Seed != "bleak-depot"}");
                Click(window, Shown<Button>(window, b => Equals(b.Content, "‹ Your games")));
                Ui.Wait(vm.Welcome.RefreshProjectsAsync());
                for (var i = 0; i < 100 && tile.Projects.Count == 0; i++) Ui.Pump(0.05);
                said.Add($"card: {string.Join(", ", tile.Projects.Select(p => p.Name))}");

                // A folder chosen in Preferences takes the next one, and the card lists both.
                var chosen = Path.Combine(temp.Path, "My made maps");
                App.App.Services!.Settings.Current.MadeMapsFolder = chosen;
                MakeMapFor(window, tile);
                var second = (MakeMapViewModel)vm.Page;
                Shown<TextBox>(window, t => t.Text == second.Seed).Text = "grim-keep";
                for (var i = 0; i < 200 && !second.Facts.StartsWith("Grim Keep", StringComparison.Ordinal); i++) Ui.Pump(0.05);
                Click(window, Shown<Button>(window, b => Equals(b.Content, "Make it")));
                for (var i = 0; i < 600 && vm.Page is not EditorViewModel; i++) Ui.Pump(0.05);
                said.Add($"second in {Path.GetDirectoryName(((EditorViewModel)vm.Page).Session.Project.Folder) == chosen}");
                Ui.Wait(vm.CloseEditorAsync());
                for (var i = 0; i < 100 && tile.Projects.Count < 2; i++) Ui.Pump(0.05);
                said.Add($"card: {string.Join(", ", tile.Projects.Select(p => p.Name).Order(StringComparer.Ordinal))}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(
        [
            "page for Freedoom: Phase 2, base row Freedoom: Phase 2, plays in gzdoom, note True",
            "Bleak Depot · 24 rooms · 3 keys · 3 secrets · 5 traps",
            "plan bosses 2, preview checked True",
            "opened Bleak Depot in Made maps, exit reached True, keys 3, settings kept True, project plays in gzdoom",
            "another like it: size 2, keys 3, bosses 2, new seed True",
            "card: Bleak Depot",
            "second in True",
            "card: Bleak Depot, Grim Keep",
        ], log);
    }

    /// <summary>An episode as the user makes one: the Maps segment, Make it, the first map open; the slot's button lists the three maps and opens the second.</summary>
    [Fact]
    public async Task AnEpisodeOpensItsFirstMapAndTheSlotButtonOpensTheOthers()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var log = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var said = new List<string>();
            try
            {
                var tile = vm.Welcome.Games.First(t => t.Found.Game.Id == "freedoom2");
                MakeMapFor(window, tile);
                var page = (MakeMapViewModel)vm.Page;
                Click(window, Shown<ToggleButton>(window, b => ReferenceEquals(b.DataContext, page.Maps.Items[1])));
                Shown<TextBox>(window, t => t.Text == page.Seed).Text = "eerie-works";
                for (var i = 0; i < 200 && !page.Facts.StartsWith("Eerie Works · map 1 of 3", StringComparison.Ordinal); i++) Ui.Pump(0.05);
                said.Add(page.Facts.Split(" · ")[1]);

                Click(window, Shown<Button>(window, b => Equals(b.Content, "Make it")));
                for (var i = 0; i < 600 && vm.Page is not EditorViewModel; i++) Ui.Pump(0.05);
                var editor = (EditorViewModel)vm.Page;
                Ui.Pump(0.5);
                said.Add($"opened {editor.Session.Slot}, maps {string.Join(", ", editor.ProjectMaps.Select(m => m.Slot))}, last ends {editor.Session.Project.Info.Maps[^1].Next}");
                var chooser = Shown<Button>(window, b => Equals(b.Content, "MAP01 ▾"));
                Click(window, chooser);
                editor.ProjectMaps.Single(m => m.Slot == "MAP02").Open.Execute(null);
                for (var i = 0; i < 400 && (vm.Page as EditorViewModel)?.Session.Slot != "MAP02"; i++) Ui.Pump(0.05);
                var second = (EditorViewModel)vm.Page;
                Ui.Pump(0.5);
                said.Add($"switched to {second.Session.Slot} \"{second.Session.Project.Info.Maps[1].Title}\", rooms {second.Session.Map.Sectors.Count(x => x.OnMap) > 10}, first map's session closed {editor.Session.IsDisposed}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(3, log.Count);
        Assert.Equal("map 1 of 3", log[0]);
        Assert.Equal("opened MAP01, maps MAP01, MAP02, MAP03, last ends EndTitle", log[1]);
        Assert.Matches("^switched to MAP02 \"[A-Z][a-z]+ [A-Z][a-z]+\", rooms True, first map's session closed True$", log[2]);
    }
}
