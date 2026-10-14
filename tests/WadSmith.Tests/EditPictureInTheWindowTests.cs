using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ViewModels;
using WadSmith.Core.Graphics;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// A picture of the user's own edited in another program (D22, Phase 7), as the user goes: the room's Inspector action hands the
/// picture's file to the computer's program for pictures; when the file is saved there, the plan shows the new picture.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class EditPictureInTheWindowTests
{
    private static RgbaImage Plain(byte r, byte g, byte b)
    {
        var picture = new RgbaImage(64, 64);
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) picture.Set(x, y, r, g, b);
        return picture;
    }

    [Fact]
    public async Task APictureSavedInAnotherProgramShowsOnThePlan()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var brick = Path.Combine(temp.Path, "brick.png");
        File.WriteAllBytes(brick, ImageCodec.EncodePng(Plain(200, 40, 40)));
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
                SoundInTheWindowTests.DropFile(window, canvas, middle, brick);
                for (var i = 0; i < 200 && room.FloorTexture != "BRICK"; i++) Ui.Pump(0.05);

                string Seen()
                {
                    Ui.Pump(0.3);
                    using var frame = window.CaptureRenderedFrame()!;
                    using var pixels = frame.Lock();
                    var at = canvas.TranslatePoint(canvas.ToScreen(middle), window)!.Value;
                    var scale = frame.PixelSize.Width / window.Bounds.Width;
                    var word = System.Runtime.InteropServices.Marshal.ReadInt32(pixels.Address + (int)(at.Y * scale) * pixels.RowBytes + (int)(at.X * scale) * 4);
                    var (low, high) = (word & 255, (word >> 16) & 255);
                    var (r, b) = pixels.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? (high, low) : (low, high);
                    return r > 2 * b ? "red" : b > 2 * r ? "blue" : $"other {word:X8}";
                }

                var lines = new List<string> { $"floor {room.FloorTexture} seen {Seen()}" };
                string? opened = null;
                editor.OpenInOtherProgram = path =>
                {
                    opened = path;
                    return Task.FromResult(true);
                };
                session.Selection.Set(room);
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                var edit = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is ActionRow { Label: "Edit the floor's picture in another program" });
                edit.BringIntoView();
                Ui.Pump(0.2);
                var at = edit.TranslatePoint(new Point(edit.Bounds.Width / 2, edit.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                lines.Add($"opened {Path.GetFileName(opened)}");

                // The other program saves a blue picture over it.
                File.WriteAllBytes(opened!, ImageCodec.EncodePng(Plain(40, 40, 200)));
                for (var i = 0; i < 200 && !editor.Status.Contains("was saved", StringComparison.Ordinal); i++) Ui.Pump(0.05);
                var status = editor.Status;
                session.Selection.Clear();
                lines.Add($"status '{status}'; seen {Seen()}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("floor BRICK seen red\nopened BRICK.png\nstatus 'BRICK.png was saved: BRICK shows the new picture.'; seen blue", said);
    }
}
