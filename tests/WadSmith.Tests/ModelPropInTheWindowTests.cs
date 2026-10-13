using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// A 3D model brought in as the user goes (Phase 11): the Objects shelf's "Add a 3D model…" button, the file chosen, and the
/// prop waiting under Your models with its baked picture; a model file dropped on the map is brought in and placed there.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class ModelPropInTheWindowTests
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
    public async Task TheShelfsButtonBringsAModelInAndItWaitsUnderYourModels()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var model = ModelPropTests.WriteBox(Path.Combine(temp.Path, "source"));
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.ShowObjects = true;
                Ui.Pump(0.5);
                // The system's file dialog is the one step not driven here: it answers with the model's path.
                var asked = 0;
                editor.PickModel = () =>
                {
                    asked++;
                    return Task.FromResult<string?>(model);
                };

                var shelf = editor.ObjectShelf;
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add a 3D model…"));
                for (var i = 0; i < 400 && !shelf.Tiles.Any(t => t.Group == ShelfViewModel.ModelGroup); i++) Ui.Pump(0.05);

                var groups = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, shelf.Groups));
                groups.SelectedIndex = shelf.Groups.ToList().IndexOf(ShelfViewModel.ModelGroup);
                Ui.Pump(0.3);
                var tiles = string.Join(",", shelf.Tiles.Select(t => $"{t.Label}:{t.Picture is not null}"));

                // Dropped on the map from the file manager: brought in again as its own prop and placed where it was let go.
                var before = editor.Session.Map.Things.Count;
                editor.Session.ImportModel(model, new MapPoint(64, 64));
                for (var i = 0; i < 400 && editor.Session.Map.Things.Count == before; i++) Ui.Pump(0.05);
                var placed = editor.Session.Map.Things.Skip(before).Select(t => $"{t.Type}@{t.X},{t.Y}");
                return $"asked {asked}; {tiles}; placed {string.Join(",", placed)}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal($"asked 1; Crate box:True; placed {WadSmith.Editor.Editing.ActorBorrowing.FirstNumber + 1}@64,64", said);
    }

    [Fact]
    public async Task TheInspectorTurnsTheModelAndTheEditorShowsItTurned()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var model = ModelPropTests.WriteBox(Path.Combine(temp.Path, "source"));
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                var before = session.Map.Things.Count;
                session.ImportModel(model, new MapPoint(64, 64));
                for (var i = 0; i < 400 && session.Map.Things.Count == before; i++) Ui.Pump(0.05);
                var prop = session.Map.Things.Last();
                session.Selection.Set(prop);
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                string Front() => session.Resources!.SpriteFor(prop.Type) is { } s && s.Get(s.Width / 2, s.Height / 2).R > 0 ? "red" : "not red";
                var was = Front();

                // As the user goes: the Model faces box, a quarter turn left.
                var row = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Model faces");
                var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, row));
                box.SelectedIndex = 1;
                for (var i = 0; i < 400 && WadSmith.Editor.Editing.ModelProps.TurnOf(session.Project.CopiedNow(), WadSmith.Editor.Editing.ModelProps.Of(session.Project, prop.Type)!) != 90; i++) Ui.Pump(0.05);
                Ui.Pump(0.5);
                return $"{was} → {Front()}; row shows {row.Value}; status '{editor.Status}'";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("red → not red; row shows 1; status 'The model is turned; every one placed turns with it, in the editor and in GZDoom.'", said);
    }

    [Fact]
    public async Task TheTextureShelfsButtonBringsAMaterialInAsATexture()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        var image = new WadSmith.Core.Graphics.RgbaImage(4, 4);
        foreach (var name in new[] { "Tiles012_1K-PNG_Color.png", "Tiles012_1K-PNG_NormalGL.png" }) File.WriteAllBytes(Path.Combine(source, name), WadSmith.Core.Graphics.ImageCodec.EncodePng(image));
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.ShowTextures = true;
                Ui.Pump(0.5);
                var shelf = editor.TextureShelf;
                for (var i = 0; i < 400 && shelf.Tiles.Count == 0; i++) Ui.Pump(0.05);
                editor.PickMaterial = () => Task.FromResult<string?>(Path.Combine(source, "Tiles012_1K-PNG_NormalGL.png"));
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add a material…"));
                for (var i = 0; i < 400 && !shelf.Tiles.Any(t => t.Label == "TILES012"); i++) Ui.Pump(0.05);
                var tile = shelf.Tiles.FirstOrDefault(t => t.Label == "TILES012");
                return $"{tile?.Label}:{tile?.Group}:{tile?.Picture is not null}; status '{editor.Status}'";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("TILES012:Walls:True; status 'The material is the texture TILES012, with a normal map: under Textures, ready for walls, doors and floors. GZDoom lights it by its maps under dynamic lights.'", said);
    }
}
