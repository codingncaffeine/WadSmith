using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ThreeD;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Model;
using static WadSmith.Tests.DoorPartsInTheWindowTests;

namespace WadSmith.Tests;

/// <summary>
/// The user's report (2026-10-15): walls and floors picked in the 3D view (a click, then Shift+click) are what a texture let go on
/// one of them, or Delete, changes, so a texture need not go on the whole wall nor Delete take a whole wall.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class PickedPanelsInTheWindowTests
{
    [Fact]
    public async Task PanelsPickedWithShiftInThe3DViewTakeATextureOrGoWithDelete()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                editor.Layout = MapLayout.Split;
                editor.CameraMode = CameraMode.Orbit;
                Ui.Pump(1.5);
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var view = window.GetVisualDescendants().OfType<View3D>().First(v => v.IsEffectivelyVisible);
                var start = session.Map.Sectors.Single(s => s.OnMap);
                view.LookAt(AreaMiddle(session, start), start.FloorHeight, 640);
                Ui.Pump(0.5);
                var camera = view.CurrentCamera;
                var (west, east, wall) = TwoRooms(session, l => Facing(l, camera.X, camera.Y));
                editor.Tool = EditorTool.Door;
                canvas.FitAround(Middle(wall), 768);
                Ui.Pump(0.3);
                ClickPlan(window, canvas, Middle(wall));
                if (!Ui.Until(() => DoorOn(session) is not null)) throw new InvalidOperationException("The Door tool made no door: " + editor.Status);
                editor.Tool = EditorTool.Select;
                var said = new List<string>();

                // The two pieces of wall beside the door, as the room the camera looks from sees them: one clicked, one Shift+clicked.
                var panels = FacesToward(session, west);
                var height = (west.FloorHeight + west.CeilingHeight) / 2;
                view.LookAt(Middle(wall), height, 640);
                Ui.Until(() => view.SceneVersion >= session.Map.Version, 10);
                Ui.Pump(0.5);
                (double, double, double) At(Side f) => (Middle(f.Line!).X, Middle(f.Line!).Y, height);
                Click3D(window, view, At(panels[0]));
                Ui.Until(() => session.Selection.Count == 1, 10);
                Click3D(window, view, At(panels[1]), RawInputModifiers.Shift);
                Ui.Until(() => session.Selection.Count == 2, 10);
                said.Add($"picked: {string.Join(", ", session.Selection.Items.Select(e => panels.Contains(e) ? "a piece of wall beside the door" : e.GetType().Name))}");

                // A texture let go on one of them goes on both, and on nothing else; they stay picked.
                var used = session.Map.Sides.Where(s => s.OnMap).SelectMany(s => new[] { s.Top, s.Middle, s.Bottom }).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var texture = session.Resources!.Textures.Walls.First(t => t.Width >= 64 && t.Height >= 64 && !used.Contains(t.Name) && t.Name != "-").Name;
                var others = session.Map.Sides.Where(s => s.OnMap && !panels.Contains(s)).Select(s => (s, Looks(s))).ToList();
                Drop3D(window, view, At(panels[1]), new ShelfDrop(texture, 0, texture));
                Ui.Until(() => panels.All(p => Looks(p).Contains(texture, StringComparison.Ordinal)), 10);
                said.Add($"a texture on one: {panels.Count(p => Looks(p).Contains(texture, StringComparison.Ordinal))} of the 2 show it, {others.Count(o => Looks(o.s) != o.Item2)} other faces changed, {session.Selection.Count} still picked");

                // Floors the same way: the two rooms' floors, a floor picture let go on one.
                var flat = session.Resources.Textures.Flats.First(t => !t.Name.StartsWith("F_SKY", StringComparison.OrdinalIgnoreCase) && t.Name != west.FloorTexture && t.Name != east.FloorTexture && t.Name != "-").Name;
                var westFloor = (AreaMiddle(session, west).X, AreaMiddle(session, west).Y, west.FloorHeight);
                var eastFloor = FarFloor(session, east, wall);
                Click3D(window, view, westFloor);
                Ui.Until(() => session.Selection.Items is [Sector], 10);
                Click3D(window, view, eastFloor, RawInputModifiers.Shift);
                Ui.Until(() => session.Selection.Count == 2, 10);
                var ceilings = (west.CeilingTexture, east.CeilingTexture);
                Drop3D(window, view, westFloor, new ShelfDrop(flat, 0, flat, Flat: true));
                Ui.Until(() => west.FloorTexture == flat && east.FloorTexture == flat, 10);
                said.Add($"a floor picture on one: {(west.FloorTexture == flat ? "west" : "")} {(east.FloorTexture == flat ? "east" : "")} floors show it, ceilings {((west.CeilingTexture, east.CeilingTexture) == ceilings ? "kept" : "changed")}");

                // One piece of wall picked and Delete: that piece goes and the rooms meet there; the room stays as it was.
                var (floor, ceiling) = (west.FloorHeight, west.CeilingHeight);
                var slabsBefore = Slabs(session).Count;
                Click3D(window, view, At(panels[0]));
                Ui.Until(() => session.Selection.Items is [Side], 10);
                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Ui.Until(() => Slabs(session).Count < slabsBefore && session.Geometry.Version == session.Map.Version, 10);
                said.Add($"a piece deleted: {slabsBefore - Slabs(session).Count} piece of wall gone, the west room {(west.OnMap && west.FloorHeight == floor && west.CeilingHeight == ceiling ? "as it was" : $"now {west.FloorHeight}/{west.CeilingHeight}")}, {(session.Map.Lines.Any(l => l.OnMap && l.Back is not null && ((ReferenceEquals(l.Front.Sector, west) && ReferenceEquals(l.Back.Sector, east)) || (ReferenceEquals(l.Front.Sector, east) && ReferenceEquals(l.Back.Sector, west)))) ? "the rooms meet there" : "the rooms do not meet")}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, seen));
        Assert.Equal(
        [
            "picked: a piece of wall beside the door, a piece of wall beside the door",
            "a texture on one: 2 of the 2 show it, 0 other faces changed, 2 still picked",
            "a floor picture on one: west east floors show it, ceilings kept",
            "a piece deleted: 1 piece of wall gone, the west room as it was, the rooms meet there",
        ], seen);
    }

    /// <summary>A point of a room's floor far from the wall, which the camera sees over it.</summary>
    private static (double, double, double) FarFloor(EditorSession session, Sector room, Line wall)
    {
        var middle = AreaMiddle(session, room);
        var (wx, wy) = (Middle(wall).X, Middle(wall).Y);
        return (wx + (middle.X - wx) * 1.6, wy + (middle.Y - wy) * 1.6, room.FloorHeight);
    }

    private static void Drop3D(MainWindow window, View3D view, (double X, double Y, double Z) at, ShelfDrop drop)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ShelfDrop.Format, drop));
        var point = Screen(window, view, at);
        foreach (var kind in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
        {
            window.DragDrop(point, kind, data, DragDropEffects.Copy, RawInputModifiers.None);
            Ui.Pump(0.15);
        }
    }
}
