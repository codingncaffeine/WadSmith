using Avalonia.VisualTree;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// A sliding door in the real editor (D62): the Door tool's door, made to slide aside from the Inspector, stands in its doorway
/// on the plan (gold) and in the 3D view, its Inspector says so, and Undo brings the rising door back. With WADSMITH_DOOR_SHOT
/// set to a folder, the window is kept there as door-slides.png and door-swings.png.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class PolyDoorInTheWindowTests
{
    [Fact]
    public async Task ADoorMadeToSlideInTheInspectorStandsInItsDoorway()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                editor.Layout = WadSmith.Editor.App.MapLayout.Split;
                var corners = session.Map.Vertices.Where(v => v.OnMap).ToList();
                var (x1, y0, y1) = (corners.Max(v => v.X), corners.Min(v => v.Y), corners.Max(v => v.Y));
                session.DrawRoom([new(x1, y0), new(x1 + 256, y0), new(x1 + 256, y1), new(x1, y1)]);
                Ui.Pump(0.5);
                session.MakeDoor(session.Map.Lines.Single(l => l.OnMap && l.Back is not null));
                Ui.Pump(0.8);
                var area = session.Selection.Items.OfType<Sector>().Single();
                var said = new List<string> { $"made: {editor.Inspector.Heading}, opens {Choice(editor, "Opens")}" };

                void Shot(string name)
                {
                    if (Environment.GetEnvironmentVariable("WADSMITH_DOOR_SHOT") is not { Length: > 0 } shots) return;
                    var view = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                    view.FitTo([area]);
                    view.ZoomBy(0.35);
                    Ui.Pump(1.5);
                    using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
                    frame.Save(Path.Combine(shots, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                Row(editor, "Opens").Value = Row(editor, "Opens").Options.ToList().IndexOf("Slides aside");
                Ui.Pump(1.0);
                var door = PolyDoors.Recognise(session.Geometry, area, session.Engine);
                said.Add($"slides: {door?.Kind}, {door?.Halves.Count} polyobject, standing in the doorway: {Standing(session.Geometry, area)}; Inspector {editor.Inspector.Heading}: {editor.Inspector.Summary.Split(' ')[0]}, rows {string.Join(", ", editor.Inspector.Rows.Select(r => r.Label).Where(l => l is "Opens" or "Two halves that part" or "Slides toward" or "Locked with"))}");
                Shot("door-slides.png");

                Row(editor, "Opens").Value = Row(editor, "Opens").Options.ToList().IndexOf("Swings");
                Ui.Pump(1.0);
                var swinging = PolyDoors.Recognise(session.Geometry, area, session.Engine);
                var shown = session.Resources!.Textures.FindWall(swinging!.Own.First().Front.Middle)!;
                said.Add($"swings: {swinging.Kind}, a door picture {shown.Width} × {shown.Height}");
                Shot("door-swings.png");

                session.History.Undo();
                session.History.Undo();
                Ui.Pump(1.0);
                said.Add($"undone twice: {Doors.Recognise(session.Geometry, area)?.Kind}, {session.Geometry.Polyobjects(session.Engine).Count} polyobjects");

                // Ctrl+K has the same: on the selected door, the kinds it can become.
                session.Selection.Set(area);
                Ui.Pump(0.3);
                var kinds = editor.Commands().Where(c => c.Name.StartsWith("Make the door ", StringComparison.Ordinal) && c.Available).Select(c => c.Name).ToList();
                editor.Commands().Single(c => c.Name == "Make the door swing").Run();
                Ui.Pump(1.0);
                said.Add($"palette offers {string.Join(", ", kinds)}; after its swing: {PolyDoors.Recognise(session.Geometry, area, session.Engine)?.Kind}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(
        [
            "made: Door, opens Upward",
            "slides: Slides, 1 polyobject, standing in the doorway: True; Inspector Door: Slides, rows Opens, Two halves that part, Slides toward, Locked with",
            "swings: Swings, a door picture 64 × 128",
            "undone twice: Rises, 0 polyobjects",
            "palette offers Make the door slide aside, Make the door swing; after its swing: Swings",
        ], seen);
    }

    [Fact]
    public async Task TheGuidesDoorsChapterShowsTheKindsOfDoor()
    {
        // The step after a door's look teaches the kinds of door, with their picture on the card; the steps before do not show it.
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                editor.StartGuideCommand.Execute("doors");
                Ui.Pump(0.5);
                var guide = editor.Guide!;
                var diagram = window.GetVisualDescendants().OfType<App.Views.DoorKindsDiagram>().Single();
                var said = new List<string> { $"{guide.Title}: picture {diagram.IsEffectivelyVisible}" };
                guide.SkipCommand.Execute(null);
                guide.SkipCommand.Execute(null);
                Ui.Pump(0.5);
                said.Add($"{guide.Title}: picture {diagram.IsEffectivelyVisible}, {diagram.Bounds.Width:0} × {diagram.Bounds.Height:0}");
                if (Environment.GetEnvironmentVariable("WADSMITH_DOOR_SHOT") is { Length: > 0 } shots)
                {
                    using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
                    frame.Save(Path.Combine(shots, "guide-door-kinds.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                return said;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Make a door: picture False", seen[0]);
        Assert.StartsWith("Make it slide or swing: picture True, ", seen[1], StringComparison.Ordinal);
        Assert.DoesNotContain(" 0 × ", seen[1], StringComparison.Ordinal);
    }

    private static App.ViewModels.ChoiceRow Row(App.ViewModels.EditorViewModel editor, string label) => editor.Inspector.Rows.OfType<App.ViewModels.ChoiceRow>().Single(r => r.Label == label);

    private static string Choice(App.ViewModels.EditorViewModel editor, string label) => Row(editor, label) is var row ? row.Options[row.Value] : "";

    /// <summary>Whether every polyobject of the map stands, as the map starts, in the doorway (its middle there).</summary>
    private static bool Standing(MapGeometry g, Sector doorway)
    {
        var f = g.Frame;
        var polys = g.Polyobjects("doom");
        return polys.Count > 0 && polys.All(p =>
        {
            var corners = p.Lines.SelectMany(slot => new[] { f.Lines[slot].V1, f.Lines[slot].V2 }).Distinct().Select(v => f.Vertices[v]).ToList();
            return ReferenceEquals(g.SectorAt(new MapPoint(corners.Average(v => v.X) + p.Dx, corners.Average(v => v.Y) + p.Dy)), doorway);
        });
    }
}
