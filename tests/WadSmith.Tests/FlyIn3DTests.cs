using Avalonia.VisualTree;
using WadSmith.App.ThreeD;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>Through the real window: the 3D view opens flying at the player start, as Doom Builder's visual mode does.</summary>
[Collection(TimingCollection.Name)]
public sealed class FlyIn3DTests
{
    [Fact]
    public async Task The3DViewOpensFlyingAtThePlayerStartsEyesAndPlayFromHereStartsThere()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            var said = new List<string>();
            try
            {
                var editor = Ui.NewMap(vm);
                Ui.Pump(1.5);
                var view = window.GetVisualDescendants().OfType<View3D>().First(v => v.IsEffectivelyVisible);
                var start = editor.Session.Map.Things.First(t => t.OnMap && t.Type == 1);
                var floor = editor.Session.Geometry.AreaAt(new(start.X, start.Y))!.Value.FloorHeight;
                var camera = view.CurrentCamera;
                said.Add($"mode {editor.CameraMode}, Fly pressed {editor.IsFly}");
                said.Add($"at the start {Math.Abs(camera.X - start.X) < 0.5 && Math.Abs(camera.Y - start.Y) < 0.5}, eyes {camera.Z - floor:0}, facing {camera.Yaw - start.Angle:0}, level {camera.Pitch:0}");
                said.Add($"the 3D view took the keys {view.IsKeyboardFocusWithin}");
                var spot = editor.PlaySpot!();
                said.Add($"play from here at the start {Math.Abs(spot.X - start.X) < 0.5 && Math.Abs(spot.Y - start.Y) < 0.5}");

                // Orbit turns round the whole map; back in Fly the camera stands where the orbit camera stood.
                editor.CameraMode = CameraMode.Orbit;
                Ui.Pump(0.3);
                var orbit = view.CurrentCamera;
                editor.CameraMode = CameraMode.Fly;
                Ui.Pump(0.3);
                var back = view.CurrentCamera;
                said.Add($"fly from orbit {Math.Abs(back.X - orbit.X) < 0.5 && Math.Abs(back.Y - orbit.Y) < 0.5 && Math.Abs(back.Z - orbit.Z) < 0.5}");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(
        [
            "mode Fly, Fly pressed True",
            $"at the start True, eyes {Walker.EyeHeight:0}, facing 0, level 0",
            "the 3D view took the keys False",
            "play from here at the start True",
            "fly from orbit True",
        ], seen);
    }
}
