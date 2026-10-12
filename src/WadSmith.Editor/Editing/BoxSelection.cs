using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>What a click or a box picks with the Select tool: anything, or one kind only (Doom Builder's modes).</summary>
public enum PickOnly
{
    Anything,
    Corners,
    Walls,
    Rooms,
    Objects,
}

/// <summary>
/// What a box drawn over the plan selects: the biggest whole things inside it, so a box round some
/// rooms picks the rooms (which carry their walls, corners and things when moved or deleted), a box
/// round part of a room picks the walls and corners in it, and objects inside are picked too.
/// </summary>
public static class BoxSelection
{
    /// <summary>
    /// Rooms whose every wall lies inside the box, then walls inside it that are not a picked room's,
    /// then corners inside it that are not on a picked wall, then objects. Reads only the geometry's
    /// frame: call it on a worker (a box can hold the whole map). Picking one kind only, every one of that kind inside
    /// is picked (walls and rooms whole), whatever holds it.
    /// </summary>
    public static IReadOnlyList<Element> Pick(MapGeometry geometry, double minX, double minY, double maxX, double maxY, PickOnly only = PickOnly.Anything)
    {
        var f = geometry.Frame;
        bool Inside(double x, double y) => x >= minX && x <= maxX && y >= minY && y <= maxY;

        var rooms = new HashSet<int>();
        foreach (var s in geometry.SectorSlotsIn(minX, minY, maxX, maxY))
        {
            if (geometry.WallBoundsAt(s) is { } b && b.MinX >= minX && b.MinY >= minY && b.MaxX <= maxX && b.MaxY <= maxY) rooms.Add(s);
        }

        int SectorOf(int side) => side >= 0 && side < f.Sides.Count ? f.Sides[side].Sector : -1;
        var walls = new List<int>();
        var covered = new HashSet<int>();
        foreach (var l in geometry.LineSlotsIn(minX, minY, maxX, maxY))
        {
            var lf = f.Lines[l];
            if (!Inside(f.Vertices[lf.V1].X, f.Vertices[lf.V1].Y) || !Inside(f.Vertices[lf.V2].X, f.Vertices[lf.V2].Y)) continue;
            covered.Add(lf.V1);
            covered.Add(lf.V2);
            if (only == PickOnly.Walls || (!rooms.Contains(SectorOf(lf.Front)) && !rooms.Contains(SectorOf(lf.Back)))) walls.Add(l);
        }

        if (only == PickOnly.Corners) covered.Clear();
        var picked = new List<Element>();
        if (only is PickOnly.Anything or PickOnly.Rooms) picked.AddRange(rooms.Order().Select(s => (Element)f.Sectors[s].Element!));
        if (only is PickOnly.Anything or PickOnly.Walls) picked.AddRange(walls.Order().Select(l => (Element)f.Lines[l].Element!));
        if (only is PickOnly.Anything or PickOnly.Corners) picked.AddRange(geometry.VertexSlotsIn(minX, minY, maxX, maxY).Where(v => !covered.Contains(v)).Order().Select(v => (Element)f.Vertices[v].Element!));
        if (only is PickOnly.Anything or PickOnly.Objects) picked.AddRange(geometry.ThingSlotsIn(minX, minY, maxX, maxY).Order().Select(t => (Element)f.Things[t].Element!));
        return picked;
    }
}
