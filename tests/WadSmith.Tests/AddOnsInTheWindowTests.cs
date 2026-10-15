using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.Core.Engines;
using WadSmith.Core.Graphics;

namespace WadSmith.Tests;

/// <summary>
/// The user's own files loaded with a project (D65, Ultimate Doom Builder's resources), through the Inspector with nothing
/// selected: a folder added shows its pictures on the Textures shelf and loads before the project in Play; taken out, it is
/// gone again; a game's own WAD is refused with where its pictures are instead.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class AddOnsInTheWindowTests
{
    private static byte[] Png(int w, int h, (byte R, byte G, byte B) colour)
    {
        var img = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) img.Set(x, y, colour.R, colour.G, colour.B, 255);
        }

        return ImageCodec.EncodePng(img);
    }

    /// <summary>
    /// The monsters an add-on defines (D65 B2): a folder with a DECORATE monster, its sprite and a language name, added in the
    /// Inspector, puts the monster on the Objects shelf under its kind and name, and let go on the plan it stands there with its
    /// own picture.
    /// </summary>
    [Fact]
    public async Task AFolderOfMonstersPutsThemOnTheObjectsShelfAndOnThePlan()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var pack = Path.Combine(temp.Path, "my-monsters");
        Directory.CreateDirectory(Path.Combine(pack, "sprites"));
        File.WriteAllText(Path.Combine(pack, "decorate.txt"), "actor Ghoul 20002\n{\n  Monster\n  Health 80\n  Radius 20\n  Height 56\n  Tag \"$GHOUL_TAG\"\n  States\n  {\n  Spawn:\n    GHUL A 10 A_Look\n    Loop\n  }\n}\n");
        File.WriteAllText(Path.Combine(pack, "language.txt"), "[enu default]\nGHOUL_TAG = \"Hungry ghoul\";\n");
        File.WriteAllBytes(Path.Combine(pack, "sprites", "GHULA0.png"), Png(40, 56, (40, 200, 40)));
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.PickAddOn = _ => Task.FromResult<string?>(pack);
                editor.ShowInspector = true;
                editor.Session.Selection.Clear();
                Ui.Pump(0.3);
                Ui.Until(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content as string == "Add a folder…"), 10);
                var button = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "Add a folder…");
                button.BringIntoView();
                Ui.Pump(0.2);
                var middle = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(middle, RawInputModifiers.None);
                window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
                Assert.True(Ui.Until(() => vm.Page is EditorViewModel now && !ReferenceEquals(now, editor), 30), "The map did not open again.");
                Ui.Pump(0.5);
                var added = (EditorViewModel)vm.Page!;
                var said = new List<string>();

                // The Objects shelf: the monster by its name, among the monsters.
                added.ShowObjects = true;
                Assert.True(Ui.Until(() => added.ObjectShelf.Tiles.Count > 0, 30), "The Objects shelf did not fill.");
                added.ObjectShelf.Search = "ghoul";
                Ui.Pump(0.2);
                var tile = added.ObjectShelf.Tiles.FirstOrDefault(t => t.Drop.Thing == 20002);
                said.Add(tile is null ? $"shelf: no ghoul ({string.Join(", ", added.ObjectShelf.Tiles.Select(t => t.Label))})" : $"shelf: {tile.Label} under {tile.Group}");

                // Let go on the plan: it stands there, drawn with its own picture.
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First(c => c.IsEffectivelyVisible);
                if (tile is not null) LibraryInTheWindowTests.Drop(window, canvas, new Editor.Geometry.MapPoint(0, 64), tile.Drop);
                Ui.Until(() => added.Session.Map.Things.Any(t => t.OnMap && t.Type == 20002), 10);
                var resources = added.Session.Resources!;
                said.Add($"plan: {added.Session.Map.Things.Count(t => t.OnMap && t.Type == 20002)} placed, picture {(resources.SpriteFor(20002) is { } sprite ? $"{sprite.Width}x{sprite.Height}" : "none")}, shown {resources.CanShow(20002)}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["shelf: Hungry ghoul under Monsters", "plan: 1 placed, picture 40x56, shown True"], seen);
    }

    [Fact]
    public async Task AFolderAddedInTheInspectorBringsItsPicturesAndPlaysUntilTakenOut()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var pack = Path.Combine(temp.Path, "my-textures");
        Directory.CreateDirectory(Path.Combine(pack, "textures"));
        File.WriteAllBytes(Path.Combine(pack, "textures", "ADDONTEX.png"), Png(64, 64, (200, 40, 40)));
        string? project = null;
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                EditorViewModel Now() => (EditorViewModel)vm.Page!;
                void Press(string label)
                {
                    Ui.Until(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content as string == label), 10);
                    var button = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == label);
                    button.BringIntoView();
                    Ui.Pump(0.2);
                    var middle = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    // The pointer has rested where the last click left it while the map opened again, so a tooltip can stand
                    // over this button: it goes over the plan first (the tooltip closes), then to the button and clicks.
                    var plan = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First(c => c.IsEffectivelyVisible);
                    window.MouseMove(plan.TranslatePoint(new Point(plan.Bounds.Width / 2, plan.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
                    Ui.Pump(0.2);
                    window.MouseMove(middle, RawInputModifiers.None);
                    window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
                    Ui.Pump(0.1);
                }

                EditorViewModel Reopened(EditorViewModel before)
                {
                    Assert.True(Ui.Until(() => vm.Page is EditorViewModel now && !ReferenceEquals(now, before), 30), "The map did not open again.");
                    Ui.Pump(0.5);
                    return Now();
                }

                string Shelf(EditorViewModel e)
                {
                    e.ShowTextures = true;
                    Assert.True(Ui.Until(() => e.TextureShelf.Tiles.Count > 0, 30), "The Textures shelf did not fill.");
                    e.TextureShelf.Search = "addontex";
                    Ui.Pump(0.1);
                    var found = e.TextureShelf.Tiles.Select(t => t.Label).ToList();
                    e.TextureShelf.Search = "";
                    e.ShowInspector = true;
                    Ui.Pump(0.2);
                    return found.Count == 0 ? "no ADDONTEX" : string.Join(",", found);
                }

                var said = new List<string>();
                editor.PickAddOn = _ => Task.FromResult<string?>(pack);
                editor.ShowInspector = true;
                editor.Session.Selection.Clear();
                Ui.Pump(0.3);
                Press("Add a folder…");
                var added = Reopened(editor);
                said.Add($"added: add-ons [{string.Join(", ", added.Session.Project.Info.AddOns.Select(Path.GetFileName))}], shelf {Shelf(added)}");

                // Play loads it before the project, after the game.
                project = Path.GetFileName(Path.TrimEndingDirectorySeparator(added.Session.Project.Folder));
                var (request, why) = PlayRequests.For(added.Session.Project, added.Session.Slot, App.App.Services!.Games!, "config.ini", "log.txt", 3);
                said.Add($"play: {why ?? string.Join(" then ", request!.Files.Select(f => Path.GetFileName(Path.TrimEndingDirectorySeparator(f))))}");

                // Taken out in the Inspector: the map opens again without it.
                Press("Take out my-textures");
                var without = Reopened(added);
                said.Add($"taken out: add-ons [{string.Join(", ", without.Session.Project.Info.AddOns)}], shelf {Shelf(without)}");

                // A game's own WAD is not an add-on: refused, the map stays open.
                without.PickAddOn = _ => Task.FromResult<string?>(wad);
                Press("Add a WAD or PK3…");
                Ui.Until(() => without.Status.Contains("is a game", StringComparison.Ordinal), 10);
                said.Add($"a game: {(ReferenceEquals(vm.Page, without) ? "the map stays open" : "the map was closed")}, \"{without.Status}\"");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal([
            "added: add-ons [my-textures], shelf ADDONTEX",
            $"play: my-textures then {project}",
            "taken out: add-ons [], shelf no ADDONTEX",
            "a game: the map stays open, \"freedoom2.wad is a game, not an add-on: its pictures are on the Textures shelf under the game chooser, copied in when you use them.\"",
        ], seen);
    }
}
