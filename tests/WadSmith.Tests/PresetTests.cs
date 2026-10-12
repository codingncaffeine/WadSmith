using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>Presets over numbers (D27): room heights and brightness by name beside the Inspector's numbers.</summary>
public sealed class PresetTests
{
    private static (EditorSession Session, TempFolder Temp) Open(MapModel m)
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(m);
        return (EditorSession.Open(project, "MAP01", _ => { }), temp);
    }

    private static string Lit(NumberRow row) => string.Join(" ", row.Presets.Where(p => p.IsCurrent).Select(p => p.Name).DefaultIfEmpty("-"));

    [Fact]
    public void ARoomsHeightAndBrightnessAreSetByNameAndUndoneInOneStep()
    {
        // The west room is 0..128 at 160; the east one 32..96 at 200.
        var (session, temp) = Open(SceneTests.TwoRooms(32, 96));
        using var _ = temp;
        using var __ = session;
        var inspector = new InspectorViewModel(session);
        var west = session.Map.Sectors[0];
        session.Selection.Set(west);
        NumberRow Row(string label) => inspector.Rows.OfType<NumberRow>().Single(r => r.Label == label);
        Assert.Equal(["Low", "Normal", "Tall", "Huge"], Row("Ceiling height").Presets.Select(p => p.Name));
        Assert.Equal(["Dark", "Dim", "Normal", "Bright"], Row("Brightness").Presets.Select(p => p.Name));
        Assert.Equal(("Normal", "-"), (Lit(Row("Ceiling height")), Lit(Row("Brightness"))));

        Row("Ceiling height").Presets.Single(p => p.Name == "Tall").IsCurrent = true;
        Assert.Equal((0.0, 192.0), (west.FloorHeight, west.CeilingHeight));
        Assert.Equal("Make the room tall", session.History.UndoLabel);
        Assert.Equal("Tall", Lit(Row("Ceiling height")));
        Assert.Equal(192, Row("Ceiling height").Value);

        Row("Brightness").Presets.Single(p => p.Name == "Dark").IsCurrent = true;
        Assert.Equal(96, west.Light);
        Assert.Equal("Dark", Lit(Row("Brightness")));

        // Choosing the lit one again changes nothing and it stays lit (a toggle button unchecks itself when clicked).
        Row("Brightness").Presets.Single(p => p.Name == "Dark").IsCurrent = false;
        Assert.Equal(96, west.Light);
        Assert.Equal("Dark", Lit(Row("Brightness")));
        Assert.Equal("Make the room dark", session.History.UndoLabel);

        // A height the presets do not name lights none of them; typing the number still works.
        Row("Ceiling height").Value = 200;
        Assert.Equal("-", Lit(Row("Ceiling height")));

        session.History.Undo();
        session.History.Undo();
        session.History.Undo();
        Assert.Equal((128.0, 160), (west.CeilingHeight, west.Light));
        Assert.Equal("Normal", Lit(Row("Ceiling height")));
    }

    [Fact]
    public void SeveralRoomsTakeTheHeightAboveTheirOwnFloors()
    {
        var (session, temp) = Open(SceneTests.TwoRooms(32, 96));
        using var _ = temp;
        using var __ = session;
        var inspector = new InspectorViewModel(session);
        var (west, east) = (session.Map.Sectors[0], session.Map.Sectors[1]);
        session.Selection.Set(west, east);
        NumberRow Row(string label) => inspector.Rows.OfType<NumberRow>().Single(r => r.Label == label);
        Assert.Equal(("-", "they differ: 64 to 128"), (Lit(Row("Room heights")), Row("Room heights").HintText));

        Row("Room heights").Presets.Single(p => p.Name == "Huge").IsCurrent = true;
        Assert.Equal((320.0, 352.0), (west.CeilingHeight, east.CeilingHeight));
        Assert.Equal("Huge", Lit(Row("Room heights")));
        Row("Brightness of all").Presets.Single(p => p.Name == "Bright").IsCurrent = true;
        Assert.Equal((255, 255), (west.Light, east.Light));

        session.History.Undo();
        session.History.Undo();
        Assert.Equal((128.0, 96.0, 160, 200), (west.CeilingHeight, east.CeilingHeight, west.Light, east.Light));
    }
}

/// <summary>Through the real window: the presets show under the numbers and a click on one sets the room.</summary>
[Collection(TimingCollection.Name)]
public sealed class PresetsInTheWindowTests
{
    [Fact]
    public async Task ClickingTallUnderTheCeilingHeightMakesTheRoomTall()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var room = editor.Session.Map.Sectors[0];
                editor.Session.Selection.Set(room);
                Ui.Pump(0.3);
                ToggleButton Segment(string name) => window.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(b => b.IsEffectivelyVisible && b.DataContext is PresetChoice p && p.Name == name)
                    .First(b => b.FindAncestorOfType<ItemsControl>()?.DataContext is NumberRow { Label: "Ceiling height" });
                var before = $"{room.CeilingHeight - room.FloorHeight} {Segment("Normal").IsChecked} {Segment("Tall").IsChecked}";
                var tall = Segment("Tall");
                tall.BringIntoView();
                Ui.Pump(0.2);
                var at = tall.TranslatePoint(new Point(tall.Bounds.Width / 2, tall.Bounds.Height / 2), window)!.Value;
                window.MouseMove(at, RawInputModifiers.None);
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                return $"{before} | {room.CeilingHeight - room.FloorHeight} {Segment("Normal").IsChecked} {Segment("Tall").IsChecked} {editor.Session.History.UndoLabel}";
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal("128 True False | 192 False True Make the room tall", seen);
    }
}
