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
                project = Path.GetFileName(added.Session.Project.Folder.TrimEnd('/'));
                var (request, why) = PlayRequests.For(added.Session.Project, added.Session.Slot, App.App.Services!.Games!, "config.ini", "log.txt", 3);
                said.Add($"play: {why ?? string.Join(" then ", request!.Files.Select(f => Path.GetFileName(f.TrimEnd('/'))))}");

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
