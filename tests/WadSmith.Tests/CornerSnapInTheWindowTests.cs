using Avalonia.VisualTree;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// A corner clicked a few units short of a room's corner, zoomed in, goes onto that corner — not beside it on the wall,
/// which left a sliver of room between two rooms in a user's map (2026-10-12).
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class CornerSnapInTheWindowTests
{
    [Fact]
    public async Task AClickJustShortOfARoomsCornerLandsOnTheCorner()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();

                // Zoomed in on the starter room's north-east corner (256, 256): about six pixels a unit.
                canvas.FitAround(new MapPoint(256, 256), 100);
                Ui.Pump(0.3);
                var said = new List<string>();
                foreach (var (x, y) in new[] { (256.4, 252.1), (256.0, 249.0), (252.0, 255.6) })
                {
                    var at = canvas.SnapCorner(canvas.ToScreen(new MapPoint(x, y)));
                    said.Add($"({x}, {y}) -> ({at.X}, {at.Y})");
                }

                return said;
            }
            finally
            {
                window.Close();
            }
        });

        // Short of the corner along either wall, or just off it: the corner itself. Further down the east wall: on the wall, on the grid.
        Assert.Equal(["(256.4, 252.1) -> (256, 256)", "(256, 249) -> (256, 248)", "(252, 255.6) -> (256, 256)"], seen);
    }
}
