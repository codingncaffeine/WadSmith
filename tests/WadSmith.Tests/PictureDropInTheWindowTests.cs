using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.Core.Graphics;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// Pictures of the user's own put straight on the map (Phase 7, §3.6), as the user goes: a picture file let go in a room
/// from the file manager is brought in and covers its floor; a picture on the clipboard pasted with Ctrl+V goes on the
/// selected room's floor.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class PictureDropInTheWindowTests
{
    [Fact]
    public async Task APictureDroppedOrPastedGoesOnTheRoomsFloor()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var brick = Path.Combine(temp.Path, "brick.png");
        var picture = new RgbaImage(64, 64);
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) picture.Set(x, y, 200, 40, 40);
        File.WriteAllBytes(brick, ImageCodec.EncodePng(picture));
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var room = session.Map.Sectors.First(s => s.OnMap);
                var corners = session.Geometry.LoopsAt(room.Slot).SelectMany(l => l).ToArray();
                var middle = new MapPoint((corners.Min(p => p.X) + corners.Max(p => p.X)) / 2, (corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 2);
                var lines = new List<string> { $"floor {room.FloorTexture}" };

                SoundInTheWindowTests.DropFile(window, canvas, middle, brick);
                for (var i = 0; i < 200 && room.FloorTexture != "BRICK"; i++) Ui.Pump(0.05);
                lines.Add($"dropped: floor {room.FloorTexture}");

                // A picture copied elsewhere: red-green, 32 × 32.
                var copied = new WriteableBitmap(new Avalonia.PixelSize(32, 32), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Opaque);
                using (var pixels = copied.Lock())
                {
                    for (var i = 0; i < 32 * 32; i++) System.Runtime.InteropServices.Marshal.WriteInt32(pixels.Address + i * 4, unchecked((int)0xFF00C080));
                }

                Ui.Wait(Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(window.Clipboard!, copied));
                session.Selection.Set(room);
                canvas.Focus();
                Ui.Pump(0.2);
                window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
                for (var i = 0; i < 200 && room.FloorTexture == "BRICK"; i++) Ui.Pump(0.05);
                lines.Add($"pasted: floor {room.FloorTexture}; a texture {session.Resources!.Textures.Has(room.FloorTexture)}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("floor FLOOR4_8\ndropped: floor BRICK\npasted: floor PASTED; a texture True", said);
    }
}
