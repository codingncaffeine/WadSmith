using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Library;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// The free sources in the real window, as the user goes (D61): Realm667 chosen on the Objects shelf's chooser, an object
/// dragged from it onto the plan and brought in; the texture packs chosen on the Textures shelf, a pack opened by pressing
/// its tile and one of its textures dragged onto a wall; Blasphemer offered while it is not found. The store is seeded with
/// what a fetch would have kept (the tests use no internet).
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class LibraryInTheWindowTests
{
    private static readonly JsonSerializerOptions Json = new();

    /// <summary>What the store keeps after a list was fetched and an entry downloaded: the list, the files, the entry.</summary>
    private static void Seed(string store, string list, LibraryItem item, byte[] wad)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(store, list))!);
        File.WriteAllBytes(Path.Combine(store, list), JsonSerializer.SerializeToUtf8Bytes(new List<LibraryItem> { item }, Json));
        var dir = Path.Combine(store, item.Source, "files", item.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "thing.wad"), wad);
        File.WriteAllText(Path.Combine(dir, "files.txt"), "thing.wad\n");
        File.WriteAllText(Path.Combine(dir, "item.json"), JsonSerializer.Serialize(item, Json));
    }

    private static LibraryItem Entry(string source, string id, string title, LibraryKind kind, string info) =>
        new(source, id, title, kind, kind == LibraryKind.Object ? "Decorations" : "Texture packs", "Doom", "doom", null, new Uri($"https://example.invalid/{id}"), new Uri($"https://example.invalid/page/{id}"), "Submitted: Someone", "A test entry.", info);

    private static byte[] Crate()
    {
        var picture = new PalettedImage(8, 8) { OffsetX = 4, OffsetY = 8 };
        Array.Fill(picture.Indices, (byte)176);
        Array.Fill(picture.Opaque, true);
        return WadWriter.Write(WadKind.Pwad,
        [
            ("SS_START", ReadOnlyMemory<byte>.Empty), ("TCRTA0", DoomPicture.Encode(picture)), ("SS_END", ReadOnlyMemory<byte>.Empty),
            ("DECORATE", Encoding.UTF8.GetBytes("actor WindowCrate { Radius 12 Height 24 +SOLID States { Spawn: TCRT A -1 Stop } }")),
        ]);
    }

    private static byte[] Pack()
    {
        var picture = new RgbaImage(64, 64);
        for (var i = 0; i < picture.Pixels.Length; i += 4) (picture.Pixels[i], picture.Pixels[i + 1], picture.Pixels[i + 2], picture.Pixels[i + 3]) = ((byte)200, (byte)40, (byte)40, (byte)255);
        return WadWriter.Write(WadKind.Pwad, [("TX_START", ReadOnlyMemory<byte>.Empty), ("WINPACK1", ImageCodec.EncodePng(picture)), ("TX_END", ReadOnlyMemory<byte>.Empty)]);
    }

    internal static void Drop(MainWindow window, MapCanvas canvas, MapPoint at, ShelfDrop drop)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ShelfDrop.Format, drop));
        var point = canvas.TranslatePoint(canvas.ToScreen(at), window)!.Value;
        foreach (var kind in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
        {
            window.DragDrop(point, kind, data, DragDropEffects.Copy, RawInputModifiers.None);
            Ui.Pump(0.1);
        }
    }

    private static void Press(MainWindow window, ShelfTile tile)
    {
        var border = window.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, tile) && b.Width == 92);
        border.BringIntoView();
        Ui.Pump(0.2);
        var middle = border.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height / 2), window)!.Value;
        window.MouseMove(middle, RawInputModifiers.None);
        window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.2);
    }

    private static ComboBox Chooser(MainWindow window, ShelfViewModel shelf) =>
        window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, shelf.Games));

    [Fact]
    public async Task ARealm667ObjectIsDraggedInAndATexturePacksPictureGoesOnAWall()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var store = WadSmith.App.App.Services.Library.Folder;
                Seed(store, Path.Combine("realm667", "prop-stop_technical.json"), Entry("realm667", "r667-901", "Window Crate", LibraryKind.Object, "Summon: WindowCrate"), Crate());
                Seed(store, Path.Combine("idgames", "packs.json"), Entry("idgames", "idgames-7", "Window Pack", LibraryKind.TexturePack, "File: graphics/winpack.zip"), Pack());
                var editor = Ui.NewMap(vm);
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var lines = new List<string>();

                // Objects: Realm667 on the chooser, its tile, dragged onto the plan.
                editor.ShowObjects = true;
                var objects = editor.ObjectShelf;
                for (var i = 0; i < 400 && objects.Tiles.Count == 0; i++) Ui.Pump(0.05);
                var chooser = Chooser(window, objects);
                lines.Add("objects offer " + string.Join(", ", objects.Games.Where(g => objects.Sources.Any(s => s.Name == g))));
                chooser.SelectedIndex = objects.Games.IndexOf(LibrarySources.Realm667Name);
                for (var i = 0; i < 400 && !objects.Tiles.Any(t => t.Label == "Window Crate"); i++) Ui.Pump(0.05);
                var crate = objects.Tiles.Single(t => t.Label == "Window Crate");
                lines.Add($"{crate.Label} in {crate.Group}, badge {crate.Badge ?? "none"}");
                var before = editor.Session.Map.Things.Count;
                Drop(window, canvas, new MapPoint(64, 64), crate.Drop);
                for (var i = 0; i < 400 && editor.Session.Map.Things.Count == before; i++) Ui.Pump(0.05);
                var placed = editor.Session.Map.Things.Skip(before).Select(t => $"{editor.Session.Resources!.Things.Find(t.Type)?.ClassName}@{t.X},{t.Y}");
                lines.Add("placed " + string.Join(",", placed));
                lines.Add($"files {File.Exists(Path.Combine(editor.Session.Project.Folder, "DECORATE.r667-901"))} {File.Exists(Path.Combine(editor.Session.Project.Folder, "credits", "r667-901.txt"))}");

                // Textures: the packs on the chooser, the pack opened by pressing its tile, its texture dropped on a wall.
                editor.ShowTextures = true;
                var textures = editor.TextureShelf;
                for (var i = 0; i < 400 && textures.Tiles.Count == 0; i++) Ui.Pump(0.05);
                Chooser(window, textures).SelectedIndex = textures.Games.IndexOf(LibrarySources.PacksName);
                for (var i = 0; i < 400 && !textures.Tiles.Any(t => t.Label == "Window Pack"); i++) Ui.Pump(0.05);
                var pack = textures.Tiles.Single(t => t.Label == "Window Pack");
                lines.Add($"{pack.Label}, badge {pack.Badge}");
                Press(window, pack);
                for (var i = 0; i < 400 && !textures.Tiles.Any(t => t.Label == "WINPACK1"); i++) Ui.Pump(0.05);
                lines.Add($"chooser shows {Chooser(window, textures).SelectedItem}; tiles {string.Join(",", textures.Tiles.Select(t => t.Label))}");
                var south = editor.Session.Map.Lines.First(l => l.OnMap && l.Start.Y == l.End.Y && l.Start.Y == editor.Session.Map.Lines.Where(x => x.OnMap).Min(x => x.Start.Y));
                Drop(window, canvas, new MapPoint((south.Start.X + south.End.X) / 2, south.Start.Y + 2), textures.Tiles.Single(t => t.Label == "WINPACK1").Drop);
                for (var i = 0; i < 400 && south.Front.Middle != "WINPACK1"; i++) Ui.Pump(0.05);
                lines.Add($"wall {south.Front.Middle}; pack credits {File.Exists(Path.Combine(editor.Session.Project.Folder, "credits", "idgames-7.txt"))}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal($"""
            objects offer {LibrarySources.Realm667Name}, Blasphemer (free)
            Window Crate in Decorations, badge none
            placed WindowCrate@64,64
            files True True
            Window Pack, badge Open
            chooser shows Window Pack; tiles WINPACK1
            wall WINPACK1; pack credits True
            """.Replace("\r", "", StringComparison.Ordinal), said);
    }
}
