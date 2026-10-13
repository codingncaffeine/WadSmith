using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>A picture fitted to a wall or a room (D15): GZDoom's UDMF scale written, and the 3D view drawing one copy across.</summary>
public sealed class FittingTests
{
    private static EditorSession Open(TempFolder temp)
    {
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        return EditorSession.Open(project, "MAP01", _ => { });
    }

    private static (int, int)? Picture(string name) => name == "BIGPIC" ? (384, 256) : null;

    [Fact]
    public void AFittedWallShowsThePictureOnceAcrossItsFace()
    {
        using var temp = new TempFolder();
        var session = Open(temp);
        var side = session.Map.Lines.First(l => l.OnMap && l.Back is null).Front;
        var line = side.Line!;
        var length = Math.Sqrt(Math.Pow(line.End.X - line.Start.X, 2) + Math.Pow(line.End.Y - line.Start.Y, 2));
        var tall = side.Sector.CeilingHeight - side.Sector.FloorHeight;
        session.History.Record("picture", e =>
        {
            e.Set(side, s => s.Middle, (s, v) => s.Middle = v, "BIGPIC");
            e.Set(side, s => s.OffsetX, (s, v) => s.OffsetX = v, 24.0);
        });
        string? why = "unset";
        session.History.Record("fit", e => why = Fitting.FitWall(e, side, Picture));
        Assert.Null(why);
        Assert.True(side.Extra.TryGet("scalex_mid", out var sx) && Math.Abs(sx.AsFloat() - 384 / length) < 1e-5);
        Assert.True(side.Extra.TryGet("offsetx_mid", out var ox) && ox.AsFloat() == -24);

        // The 3D view: the face's corners read the picture from 0 to 384 across and 0 to 256 down.
        var scene = MapScene.Build(MapGeometry.Build(session.Map.Frame), name => name == "BIGPIC" ? (384, 256) : (64, 64));
        var batch = scene.Batches.Single(b => b.Texture == "BIGPIC");
        var u = Enumerable.Range(0, batch.VertexCount).Select(i => batch.Vertices[i * SceneBatch.Stride + 3]).ToList();
        var v = Enumerable.Range(0, batch.VertexCount).Select(i => batch.Vertices[i * SceneBatch.Stride + 4]).ToList();
        Assert.Equal((0, 384), ((double)Math.Round(u.Min(), 3), (double)Math.Round(u.Max(), 3)));
        Assert.Equal((0, 256), ((double)Math.Round(v.Min(), 3), (double)Math.Round(v.Max(), 3)));
        Assert.True(tall > 0);

        // One undo puts the wall as it was.
        session.History.Undo();
        Assert.False(side.Extra.Contains("scalex_mid"));
    }

    [Fact]
    public void AFittedFloorPutsThePicturesCornersOnTheRoomsAndItsTopNorth()
    {
        using var temp = new TempFolder();
        var session = Open(temp);
        var room = session.Map.Sectors.First(s => s.OnMap);
        session.History.Record("picture", e => e.Set(room, s => s.FloorTexture, (s, v) => s.FloorTexture = v, "BIGPIC"));
        var walls = session.Map.Lines.Where(l => l.OnMap).ToList();
        string? why = "unset";
        session.History.Record("fit", e => why = Fitting.FitFlat(e, room, walls, false, Picture));
        Assert.Null(why);
        var points = walls.Where(l => l.Front.Sector == room || l.Back?.Sector == room).SelectMany(l => new[] { l.Start, l.End }).ToList();
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        var transform = FlatTransform.Of([.. room.Extra], ceiling: false);
        var (u0, v0) = transform.Texel(minX, maxY);
        var (u1, v1) = transform.Texel(maxX, minY);
        Assert.Equal((0.0, 0.0), (Math.Round(u0, 3), Math.Round(v0, 3)));
        Assert.Equal((384.0, 256.0), (Math.Round(u1, 3), Math.Round(v1, 3)));
        string? ceiling = null;
        session.History.Record("fit the ceiling", e => ceiling = Fitting.FitFlat(e, room, walls, true, Picture));
        Assert.Contains("no picture", ceiling, StringComparison.Ordinal);
    }
}
