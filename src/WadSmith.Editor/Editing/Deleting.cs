using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Deleting what is selected so the map stays whole: an object goes; a room goes with the objects
/// standing in it, and the walls it shared become its neighbours' solid walls, keeping their look; a
/// wall between two rooms goes and the rooms become one (the first's settings, but a piece of wall closed
/// from floor to ceiling always gives way to the room beside it); a corner where two
/// walls meet goes and they become one straight wall. What deleting would leave open (a room's outer
/// wall, a corner where three walls meet) stays, with a sentence saying why. A face counts as its wall.
/// </summary>
public static class Deleting
{
    /// <summary>
    /// Deletes <paramref name="selection"/> through <paramref name="edit"/>; returns the sentences for
    /// what stayed (empty when all of it went). Works near the selection only.
    /// </summary>
    public static IReadOnlyList<string> Delete(StructureEdit edit, MapGeometry geometry, IReadOnlyList<Element> selection, Look look)
    {
        if (geometry.Version != edit.Map.Version) throw new InvalidOperationException("The map's shapes are behind the map; delete once they have caught up.");
        var said = new List<string>();
        var loose = new HashSet<Vertex>(ReferenceEqualityComparer.Instance);

        // Walls whose ends this delete moved (straightened ones): the shapes do not know their new ends.
        var rerouted = new List<Line>();
        List<Line> WallsAt(Vertex v) => [.. geometry.LinesIn(v.X, v.Y, v.X, v.Y).Concat(rerouted).Distinct().Where(l => l.OnMap && (l.Start == v || l.End == v))];

        foreach (var t in selection.OfType<Thing>()) edit.Remove(t);

        foreach (var room in selection.OfType<Sector>().Where(s => s.OnMap))
        {
            foreach (var l in WallsOf(geometry, room))
            {
                var (front, back) = (l.Front.Sector == room, l.Back?.Sector == room);
                if (l.Back is null || (front && back))
                {
                    RemoveWall(edit, l, loose);
                }
                else if (front)
                {
                    // The neighbour is behind: the wall turns round to face it, and is its solid wall now.
                    var (start, end, old, keep) = (l.Start, l.End, l.Front, l.Back);
                    edit.Set(l, x => x.Start, (x, v) => x.Start = v, end);
                    edit.Set(l, x => x.End, (x, v) => x.End = v, start);
                    edit.Set(l, x => x.Front, (x, v) => x.Front = v, keep);
                    edit.Set(l, x => x.Back, (x, v) => x.Back = v, null);
                    edit.Remove(old);
                    Solidify(edit, l, look);
                }
                else
                {
                    var old = l.Back;
                    edit.Set(l, x => x.Back, (x, v) => x.Back = v, null);
                    edit.Remove(old);
                    Solidify(edit, l, look);
                }
            }

            foreach (var t in ThingsIn(geometry, room)) edit.Remove(t);
            edit.Remove(room);
        }

        foreach (var l in selection.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>().Distinct().Where(l => l.OnMap).ToList())
        {
            if (l.Back is null)
            {
                said.Add("A wall with a room on one side only stays: deleting it would leave the room open. Delete the room, or drag the wall where you want it.");
                continue;
            }

            // A piece of wall (an area closed from floor to ceiling) gives way to the room beside it, never the room to it: deleting
            // one of its walls opens it up, and the room keeps its floor and ceiling.
            var (keep, gone) = (l.Front.Sector, l.Back.Sector);
            if (keep.FloorHeight >= keep.CeilingHeight && gone.FloorHeight < gone.CeilingHeight) (keep, gone) = (gone, keep);
            RemoveWall(edit, l, loose);
            if (ReferenceEquals(keep, gone) || !gone.OnMap) continue;

            // The two rooms become one: every face of the second joins the first, and walls left with the
            // same room on both sides (the rest of the wall between them) go too.
            foreach (var w in WallsOf(geometry, gone))
            {
                if (w.Front.Sector == gone) edit.Set(w.Front, s => s.Sector, (s, v) => s.Sector = v, keep);
                if (w.Back is { } b && b.Sector == gone) edit.Set(b, s => s.Sector, (s, v) => s.Sector = v, keep);
                if (w.OnMap && w.Back is not null && w.Front.Sector == w.Back.Sector && w.Front.Sector == keep) RemoveWall(edit, w, loose);
            }

            edit.Remove(gone);
        }

        foreach (var v in selection.OfType<Vertex>().Where(v => v.OnMap))
        {
            var walls = WallsAt(v);
            if (walls.Count == 0)
            {
                edit.Remove(v);
                continue;
            }

            if (walls.Count != 2)
            {
                said.Add(walls.Count == 1
                    ? "A corner at the end of a single wall stays: delete the wall instead."
                    : "A corner where three or more walls meet stays: delete one of the walls first.");
                continue;
            }

            if (Straighten(edit, v, walls[0], walls[1])) rerouted.Add(walls[0]);
            else said.Add("A corner between walls that face different rooms stays: deleting it would mix the rooms up.");
        }

        // Corners no wall uses any more go too.
        foreach (var v in loose)
        {
            if (v.OnMap && WallsAt(v).Count == 0) edit.Remove(v);
        }

        return said.Distinct().ToList();
    }

    /// <summary>
    /// Makes the two walls at a corner one wall from the far end of the first to the far end of the
    /// second, when they face the same rooms on the same sides; the first wall's settings stay.
    /// </summary>
    internal static bool Straighten(StructureEdit edit, Vertex v, Line a, Line b)
    {
        // Along the chain far(a) → v → far(b): is each wall running that way?
        var aForward = a.End == v;
        var bForward = b.Start == v;
        var farB = bForward ? b.End : b.Start;
        if (farB == (aForward ? a.Start : a.End)) return false;
        var same = aForward == bForward;
        var (bFront, bBack) = same ? (b.Front.Sector, b.Back?.Sector) : (b.Back?.Sector, (Sector?)b.Front.Sector);
        if (!ReferenceEquals(a.Front.Sector, bFront) || !ReferenceEquals(a.Back?.Sector, bBack)) return false;
        if (aForward) edit.Set(a, x => x.End, (x, w) => x.End = w, farB);
        else edit.Set(a, x => x.Start, (x, w) => x.Start = w, farB);
        edit.Remove(b);
        edit.Remove(b.Front);
        if (b.Back is { } back) edit.Remove(back);
        edit.Remove(v);
        return true;
    }

    /// <summary>A wall left with a room on one side only: it blocks, and shows a solid texture (its upper or lower if it had one).</summary>
    private static void Solidify(StructureEdit edit, Line l, Look look)
    {
        var side = l.Front;
        if (side.Middle is "-" or "")
        {
            var texture = side.Top is not ("-" or "") ? side.Top : side.Bottom is not ("-" or "") ? side.Bottom : look.Wall;
            edit.Set(side, s => s.Middle, (s, v) => s.Middle = v, texture);
        }

        edit.Flag(l, "twosided", false);
        edit.Flag(l, "blocking", true);
    }

    internal static void RemoveWall(StructureEdit edit, Line l, HashSet<Vertex> loose)
    {
        if (!l.OnMap) return;
        edit.Remove(l);
        edit.Remove(l.Front);
        if (l.Back is { } b) edit.Remove(b);
        loose.Add(l.Start);
        loose.Add(l.End);
    }

    /// <summary>The walls with a face in the room, from the shapes (live values decide).</summary>
    internal static List<Line> WallsOf(MapGeometry geometry, Sector room)
    {
        if (geometry.WallBoundsAt(room.Slot) is not { } b) return [];
        return [.. geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (l.Front.Sector == room || l.Back?.Sector == room))];
    }

    private static List<Thing> ThingsIn(MapGeometry geometry, Sector room)
    {
        if (geometry.WallBoundsAt(room.Slot) is not { } b) return [];
        var f = geometry.Frame;
        return [.. geometry.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(i => ReferenceEquals(geometry.SectorAt(new MapPoint(f.Things[i].X, f.Things[i].Y)), room)).Select(i => f.Things[i].Element!)];
    }
}
