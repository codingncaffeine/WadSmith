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
/// A 3D model brought in as the user goes (Phase 11): the Objects shelf's "Add a 3D model or picture…" button, the file chosen, and the
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
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add a 3D model or picture…"));
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
                // The record is written on the worker before the window hears of it: wait for what the window says.
                Ui.Until(() => editor.Status.StartsWith("The model is turned", StringComparison.Ordinal));
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
    public async Task QuakesModelsAreOnTheObjectsShelfAndOneIsPlaced()
    {
        var doom2 = GameFiles.Require("DOOM2.WAD");
        GameFiles.Require("PAK0.PAK");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(doom2)!);
            try
            {
                var tile = vm.Welcome.Games.Single(g => g.Found.Game.Id == "doom2");
                Ui.Wait(tile.NewMap!.ExecuteAsync(tile));
                Ui.Pump(1.0);
                var editor = (EditorViewModel)vm.Page!;
                editor.ShowObjects = true;
                var shelf = editor.ObjectShelf;
                for (var i = 0; i < 600 && !shelf.Games.Any(g => g.StartsWith("Quake", StringComparison.Ordinal)); i++) Ui.Pump(0.05);

                // As the user goes: Quake in the shelf's game box.
                var chooser = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, shelf.Games));
                chooser.SelectedIndex = shelf.Games.ToList().FindIndex(g => g.StartsWith("Quake", StringComparison.Ordinal));
                Ui.Pump(0.5);
                var armour = shelf.Tiles.FirstOrDefault(t => t.Drop.QuakeModel == "progs/armor.mdl");
                var pictured = shelf.Tiles.Count(t => t.Picture is not null);

                // Dropped on the plan (the drop handler's own call).
                var session = editor.Session;
                var before = session.Map.Things.Count;
                session.PlaceQuakeModel(armour!.Drop.From!, armour.Drop.QuakeModel!, new MapPoint(64, 64));
                for (var i = 0; i < 400 && session.Map.Things.Count == before; i++) Ui.Pump(0.05);
                var placed = session.Map.Things.Skip(before).Select(t => session.Resources!.Things.Find(t.Type)?.Name);
                return $"{armour.Label} on '{chooser.SelectedItem}', {pictured} of {shelf.Tiles.Count} pictured; placed {string.Join(",", placed)}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Matches(@"^Armor on 'Quake', (\d+) of \1 pictured; placed Armor$", said);
    }

    [Fact]
    public async Task TheInspectorMakesASkyAndTheMapTakesIt()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var picture = Path.Combine(temp.Path, "meadow.png");
        File.WriteAllBytes(picture, WadSmith.Core.Graphics.ImageCodec.EncodePng(SkyTests.Compass(256)));
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.Session.Selection.Clear();
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                editor.Inspector.PickPanorama = () => Task.FromResult<string?>(picture);
                var row = editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make a sky from a 360° picture…");
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, row)));
                for (var i = 0; i < 400 && editor.Session.MapEntry.Sky != "SKYB01"; i++) Ui.Pump(0.05);
                Ui.Pump(0.5);
                var sky = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Sky");
                return $"map sky {editor.Session.MapEntry.Sky}; box shows {sky.Options[sky.Value]}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("map sky SKYB01; box shows SKYB01", said);
    }

    [Fact]
    public async Task TheInspectorBringsAWolfensteinLevelInAsANewMap()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var (head, maps) = GridMapTests.Wolf();
        var mapHead = Path.Combine(temp.Path, "MAPHEAD.WL6");
        File.WriteAllBytes(mapHead, head);
        File.WriteAllBytes(Path.Combine(temp.Path, "GAMEMAPS.WL6"), maps);
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.Session.Selection.Clear();
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                editor.Inspector.PickGrid = () => Task.FromResult<string?>(mapHead);
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is ActionRow { Label: "Bring in a Wolfenstein 3D level…" }));
                for (var i = 0; i < 200 && !editor.Inspector.Rows.OfType<ChoiceRow>().Any(r => r.Label == "Wolfenstein 3D level"); i++) Ui.Pump(0.05);
                var levels = string.Join(",", editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Wolfenstein 3D level").Options);
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is ActionRow { Label: "Make this level a map of the project" }));
                for (var i = 0; i < 400 && editor.ProjectMaps.Count < 2; i++) Ui.Pump(0.05);
                return $"levels {levels}; maps {string.Join(",", editor.ProjectMaps.Select(m => m.Label))}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Matches("^levels Test; maps (E1M1|MAP01),(E1M2|MAP02) · Test$", said);
    }

    [Fact]
    public async Task AnAnimatedGifIsOneTileOnTheShelf()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var gif = Path.Combine(temp.Path, "portal_loop.gif");
        File.WriteAllBytes(gif, PictureTests.Gif());
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.ShowTextures = true;
                var shelf = editor.TextureShelf;
                for (var i = 0; i < 400 && shelf.Tiles.Count == 0; i++) Ui.Pump(0.05);
                editor.PickMaterial = () => Task.FromResult<string?>(gif);
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add a picture or material…"));
                for (var i = 0; i < 400 && !shelf.Tiles.Any(t => t.Group == ShelfViewModel.PictureGroup); i++) Ui.Pump(0.05);
                var mine = shelf.Tiles.Where(t => t.Group == ShelfViewModel.PictureGroup).Select(t => $"{t.Label}={t.Drop.Texture}");
                var frames = shelf.Tiles.Count(t => t.Drop.Texture is { } n && n.StartsWith("PORTA", StringComparison.Ordinal));
                return $"{string.Join(",", mine)}; tiles of its frames {frames}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("portal_loop=PORTA000; tiles of its frames 1", said);
    }

    /// <summary>
    /// A GIF on a room's floor plays on the plan, as the user sees it: brought in by the shelf's button, its tile dropped
    /// inside the new map's room, then the window's pictures show the room's middle changing again and again (the GIF loops
    /// every six tics, 171 ms). A plan that is not drawn again as time passes, or draws one frame only, changes it at most once.
    /// </summary>
    [Fact]
    public async Task AGifOnARoomsFloorPlaysOnThePlan()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var gif = Path.Combine(temp.Path, "portal_loop.gif");
        File.WriteAllBytes(gif, PictureTests.Gif());
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.ShowTextures = true;
                var shelf = editor.TextureShelf;
                for (var i = 0; i < 400 && shelf.Tiles.Count == 0; i++) Ui.Pump(0.05);
                editor.PickMaterial = () => Task.FromResult<string?>(gif);
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add a picture or material…"));
                for (var i = 0; i < 400 && !shelf.Tiles.Any(t => t.Group == ShelfViewModel.PictureGroup); i++) Ui.Pump(0.05);
                var tile = shelf.Tiles.First(t => t.Group == ShelfViewModel.PictureGroup);

                var session = editor.Session!;
                var room = session.Geometry.Frame.Sectors.First(s => s.Element is not null && session.Geometry.LoopsAt(s.Element.Slot).Count > 0);
                var corners = session.Geometry.LoopsAt(room.Element!.Slot).SelectMany(l => l).ToArray();
                var middle = new MapPoint((corners.Min(p => p.X) + corners.Max(p => p.X)) / 2, (corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 2);
                var canvas = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First(c => c.IsEffectivelyVisible);
                LibraryInTheWindowTests.Drop(window, canvas, middle, tile.Drop);
                for (var i = 0; i < 200 && session.Geometry.AreaAt(middle)?.FloorTexture != tile.Drop.Texture; i++) Ui.Pump(0.05);
                Ui.Pump(0.5);

                // A block of 24 × 24 pixels round the middle, summed: at the zoom of any plan it holds at least one whole tile of
                // the 4 × 4 picture, so the second frame's corner shows in it wherever the tiles fall on the screen (one pixel can
                // sit on a texel no frame changes). The plan plays when the block changes again and again: a picture that arrives
                // late changes it once. Pictures are taken until then, for up to half a minute, not for a fixed while.
                var at = canvas.TranslatePoint(canvas.ToScreen(middle), window)!.Value;
                long? last = null;
                var (changes, pictures) = (0, 0);
                Ui.Until(() =>
                {
                    using var frame = window.CaptureRenderedFrame()!;
                    using var pixels = frame.Lock();
                    var scale = frame.PixelSize.Width / window.Bounds.Width;
                    var (cx, cy) = ((int)(at.X * scale), (int)(at.Y * scale));
                    long sum = 0;
                    for (var y = cy - 12; y < cy + 12; y++)
                    {
                        for (var x = cx - 12; x < cx + 12; x++)
                        {
                            var word = System.Runtime.InteropServices.Marshal.ReadInt32(pixels.Address + y * pixels.RowBytes + x * 4);
                            sum += (word & 255) + ((word >> 8) & 255) * 1000 + ((word >> 16) & 255) * 1000000L;
                        }
                    }

                    if (last is { } before && before != sum) changes++;
                    (last, pictures) = (sum, pictures + 1);
                    return changes >= 3;
                });

                var zoom = (canvas.ToScreen(new MapPoint(middle.X + 64, middle.Y)).X - canvas.ToScreen(middle).X) / 64;
                return $"floor {session.Geometry.AreaAt(middle)?.FloorTexture}; "
                    + (changes >= 3 ? "the plan plays it" : $"the middle changed {changes} times in {pictures} pictures (zoom {zoom:0.###}, moving floors in the picture {canvas.PlaysMovingFloors})");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("floor PORTA000; the plan plays it", said);
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
                Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Add a picture or material…"));
                for (var i = 0; i < 400 && !shelf.Tiles.Any(t => t.Drop.Texture == "TILES012"); i++) Ui.Pump(0.05);
                var tile = shelf.Tiles.FirstOrDefault(t => t.Drop.Texture == "TILES012");
                return $"{tile?.Label}:{tile?.Group}:{tile?.Picture is not null}; status '{editor.Status}'";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Tiles012 1K PNG:Your pictures:True; status 'The material is the texture TILES012, with a normal map: under Textures, ready for walls, doors and floors. GZDoom lights it by its maps under dynamic lights.'", said);
    }
}
