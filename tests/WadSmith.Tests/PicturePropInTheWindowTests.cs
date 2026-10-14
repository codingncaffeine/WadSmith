using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Core.Graphics;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>A picture brought in as a decoration (Phase 7), as the user goes: the Objects shelf's button, its tile under Your pictures dragged onto the map, its Inspector's Blocks the way.</summary>
[Collection(TimingCollection.Name)]
public sealed class PicturePropInTheWindowTests
{
    private static void Click(MainWindow window, Visual target)
    {
        if (target is Control control) control.BringIntoView();
        Ui.Pump(0.2);
        var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Not in the window.");
        window.MouseMove(middle, RawInputModifiers.None);
        window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    [Fact]
    public async Task APictureBecomesADecorationOnTheShelfOnTheMapAndInItsInspector()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var png = Path.Combine(temp.Path, "tall lamp.png");
        File.WriteAllBytes(png, ImageCodec.EncodePng(new RgbaImage(40, 128)));
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                editor.ShowObjects = true;
                var shelf = editor.ObjectShelf;
                for (var i = 0; i < 400 && shelf.Tiles.Count == 0; i++) Ui.Pump(0.05);
                editor.PickModel = () => Task.FromResult<string?>(png);
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add a 3D model or picture…"));
                for (var i = 0; i < 400 && !shelf.Tiles.Any(t => t.Group == ShelfViewModel.PictureGroup); i++) Ui.Pump(0.05);
                var tile = shelf.Tiles.Single(t => t.Group == ShelfViewModel.PictureGroup);
                var lines = new List<string> { $"tile {tile.Label} pictured {tile.Picture is not null}" };

                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var room = session.Map.Sectors.First(s => s.OnMap);
                var corners = session.Geometry.LoopsAt(room.Slot).SelectMany(l => l).ToArray();
                var middle = new MapPoint(Math.Round((corners.Min(p => p.X) + corners.Max(p => p.X)) / 128) * 64, Math.Round((corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 128) * 64);
                var before = session.Map.Things.Count;
                LibraryInTheWindowTests.Drop(window, canvas, middle, tile.Drop);
                for (var i = 0; i < 200 && session.Map.Things.Count == before; i++) Ui.Pump(0.05);
                var placed = session.Map.Things.Last();
                lines.Add($"placed {session.Resources!.Things.Find(placed.Type)?.ClassName}");

                session.Selection.Set(placed);
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                var row = editor.Inspector.Rows.OfType<FlagRow>().Single(r => r.Label == "Blocks the way");
                var box = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, row));
                lines.Add($"blocks {box.IsChecked}");
                Click(window, box);
                for (var i = 0; i < 200 && PictureProps.Of(session.Project, placed.Type)?.Shape.Solid != false; i++) Ui.Pump(0.05);
                // The class is written on the worker before the window hears of it: wait for what the window says.
                Ui.Until(() => editor.Status.StartsWith("Every ", StringComparison.Ordinal));
                lines.Add($"blocks now {PictureProps.Of(session.Project, placed.Type)?.Shape.Solid}; status '{editor.Status}'");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("tile Tall lamp pictured True\nplaced PictureTallLamp\nblocks True\nblocks now False; status 'Every Tall lamp stands 64 units tall, and players and monsters pass through it, in the editor and in GZDoom.'", said);
    }
}
