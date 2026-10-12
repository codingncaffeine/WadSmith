using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>
/// Floor and ceiling textures turned, moved and scaled by an area's UDMF fields, as GZDoom draws them (measured 2026-10-12:
/// the capture tool's editor-flatcal pose against GZDoom's own pictures from above differs 4.4–4.9 per channel on all five
/// test floors, and 25–28 when a plain floor is held against a turned, moved or scaled one).
/// </summary>
public sealed class FlatTransformTests
{
    [Fact]
    public void APlainFloorLinesUpWithTheMapsGrid()
    {
        Assert.Equal((10.0, -20.0), FlatTransform.Plain.Texel(10, 20));
        Assert.True(FlatTransform.Of([], ceiling: false).IsPlain);
    }

    [Fact]
    public void ThirtyDegreesTurnTheTextureClockwiseAsSeenFromAbove()
    {
        // The texture's rows (along u) run east-south-east: clockwise from east, as GZDoom shows rotationfloor 30.
        var t = new FlatTransform(0, 0, 30, 1, 1);
        var (u0, v0) = t.Texel(0, 0);
        var (u1, v1) = t.Texel(1, 0);
        Assert.Equal(Math.Cos(-30 * Math.PI / 180), u1 - u0, 9);
        Assert.Equal(Math.Sin(-30 * Math.PI / 180), v1 - v0, 9);
    }

    [Fact]
    public void PanningMovesItAndScalingAboveOneMakesItSmaller()
    {
        Assert.Equal((16.0, 40.0), new FlatTransform(16, 40, 0, 1, 1).Texel(0, 0));
        Assert.Equal((128.0, 32.0), new FlatTransform(0, 0, 0, 2, 0.5).Texel(64, -64));
    }

    [Fact]
    public void TheFieldsAreReadFromAnAreasFieldsFloorAndCeilingApart()
    {
        KeyValuePair<string, UdmfValue>[] extra = [new("rotationfloor", UdmfValue.Float(45)), new("xpanningceiling", UdmfValue.Int(8)), new("yscalefloor", UdmfValue.Float(2))];
        Assert.Equal(new FlatTransform(0, 0, 45, 1, 2), FlatTransform.Of(extra, ceiling: false));
        Assert.Equal(new FlatTransform(8, 0, 0, 1, 1), FlatTransform.Of(extra, ceiling: true));
    }

    [Theory]
    [InlineData(0, 0, 0, 1, 1)]
    [InlineData(30, 16, 40, 1, 1)]
    [InlineData(-75, 3, -9, 2, 0.5)]
    public void ThePlansShaderTakesEachTexelBackToTheMapPointThatShowsIt(double rotation, double xpan, double ypan, double xscale, double yscale)
    {
        var t = new FlatTransform(xpan, ypan, rotation, xscale, yscale);
        var back = App.Canvas.CanvasSnapshot.ToMap(t);
        foreach (var (x, y) in new[] { (0.0, 0.0), (100.0, -37.0), (-250.0, 512.0) })
        {
            var (u, v) = t.Texel(x, y);
            var p = back.MapPoint((float)u, (float)v);
            Assert.Equal(x, p.X, 2);
            Assert.Equal(y, p.Y, 2);
        }
    }

    [Fact]
    public void TheThreeDSceneUsesTheAreasTransformForItsFloor()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256));
        history.Record("Turn", e => e.Field(rooms[0], "rotationfloor", UdmfValue.Float(90)));
        var scene = MapScene.Build(MapGeometry.Build(map.Frame), _ => (64, 64));
        var floor = scene.Batches.Single(b => b.Kind == SurfaceKind.Floor);
        var t = new FlatTransform(0, 0, 90, 1, 1);
        for (var i = 0; i < floor.Vertices.Count; i += SceneBatch.Stride)
        {
            var (u, v) = t.Texel(floor.Vertices[i], floor.Vertices[i + 1]);
            Assert.Equal(u, floor.Vertices[i + 3], 3);
            Assert.Equal(v, floor.Vertices[i + 4], 3);
        }
    }
}
