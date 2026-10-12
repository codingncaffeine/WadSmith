using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// What was copied: rooms with their walls, corners and the things standing in them, and things on
/// their own, kept off the map with their places measured from the middle of what was copied.
/// </summary>
public sealed class MapClip
{
    internal List<Vertex> Vertices { get; } = [];

    internal List<Sector> Sectors { get; } = [];

    internal List<Line> Lines { get; } = [];

    internal List<Thing> Things { get; } = [];

    public bool IsEmpty => Sectors.Count == 0 && Things.Count == 0;

    /// <summary>How big the copy is, for placing it on the grid.</summary>
    public (double Width, double Height) Size { get; internal set; }

    /// <summary>Where the middle of what was copied stood, so pastes can move it in whole floor-texture steps.</summary>
    public MapPoint Origin { get; internal set; }

    /// <summary>The game (and project) it was copied from, so a paste into another can carry its pictures along; null when unknown.</summary>
    public GameResources? From { get; set; }

    /// <summary>Every picture the copy uses: wall textures, and floors and ceilings (<c>Flat</c>), once each.</summary>
    public IReadOnlyList<(string Name, bool Flat)> Pictures()
    {
        var found = new HashSet<(string, bool)>();
        foreach (var s in Sectors)
        {
            found.Add((s.FloorTexture.ToUpperInvariant(), true));
            found.Add((s.CeilingTexture.ToUpperInvariant(), true));
        }

        foreach (var l in Lines)
        {
            foreach (var side in new[] { l.Front, l.Back })
            {
                if (side is null) continue;
                foreach (var t in new[] { side.Top, side.Middle, side.Bottom }) found.Add((t.ToUpperInvariant(), false));
            }
        }

        return [.. found.Where(p => p.Item1 is not ("" or "-"))];
    }

    /// <summary>What was copied, in words: "2 rooms and 3 things".</summary>
    public string Describe()
    {
        static string Count(int n, string one, string many) => n == 1 ? one : $"{n} {many}";
        var parts = new List<string>();
        if (Sectors.Count > 0) parts.Add(Count(Sectors.Count, "a room", "rooms"));
        if (Things.Count > 0) parts.Add(Count(Things.Count, "a thing", "things"));
        return string.Join(" and ", parts);
    }
}

/// <summary>Copying and pasting parts of a map, within a map or into another one.</summary>
public static class Copying
{
    /// <summary>What was copied last, for every open map (Ctrl+V pastes it into whichever map is open).</summary>
    public static MapClip? Clipboard { get; set; }

    /// <summary>
    /// Where a paste near <paramref name="wanted"/> goes: the copy moves from where it was copied in steps of
    /// 64, so its floors and ceilings stay lined up with the world's grid as they were.
    /// </summary>
    public static MapPoint PlaceNear(MapClip clip, MapPoint wanted) =>
        new(clip.Origin.X + Math.Round((wanted.X - clip.Origin.X) / 64) * 64, clip.Origin.Y + Math.Round((wanted.Y - clip.Origin.Y) / 64) * 64);

    /// <summary>
    /// Copies the selected rooms (with every wall and corner they have and the things standing in them,
    /// except player starts: a map has one) and things (a start selected itself is copied). A wall shared
    /// with a room that is not copied becomes the copy's solid wall. Reads the map's own elements near the
    /// selection: for the UI thread.
    /// </summary>
    public static MapClip Copy(MapGeometry geometry, IReadOnlyList<Element> selection)
    {
        var clip = new MapClip();
        var rooms = selection.OfType<Sector>().Where(s => s.OnMap).ToHashSet();
        var things = selection.OfType<Thing>().Where(t => t.OnMap).ToList();
        var walls = new List<Line>();
        foreach (var room in rooms)
        {
            if (geometry.WallBoundsAt(room.Slot) is not { } b) continue;
            foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                if (l.OnMap && (rooms.Contains(l.Front.Sector) || (l.Back is { } back && rooms.Contains(back.Sector))) && !walls.Contains(l)) walls.Add(l);
            }

            foreach (var i in geometry.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
            {
                if (geometry.Frame.Things[i].Element is { OnMap: true, Type: not (1 or 2 or 3 or 4) } t && ReferenceEquals(geometry.SectorAt(new MapPoint(t.X, t.Y)), room) && !things.Contains(t)) things.Add(t);
            }
        }

        if (walls.Count == 0 && things.Count == 0) return clip;

        // Everything is kept relative to the middle of what was copied.
        var xs = walls.SelectMany(l => new[] { l.Start.X, l.End.X }).Concat(things.Select(t => t.X)).ToList();
        var ys = walls.SelectMany(l => new[] { l.Start.Y, l.End.Y }).Concat(things.Select(t => t.Y)).ToList();
        var (cx, cy) = ((xs.Min() + xs.Max()) / 2, (ys.Min() + ys.Max()) / 2);
        clip.Size = (xs.Max() - xs.Min(), ys.Max() - ys.Min());
        clip.Origin = new MapPoint(cx, cy);

        var vertices = new Dictionary<Vertex, Vertex>(ReferenceEqualityComparer.Instance);
        var sectors = new Dictionary<Sector, Sector>(ReferenceEqualityComparer.Instance);
        Vertex V(Vertex v)
        {
            if (!vertices.TryGetValue(v, out var c))
            {
                vertices[v] = c = new Vertex { X = v.X - cx, Y = v.Y - cy };
                foreach (var (k, value) in v.Extra) c.Extra[k] = value;
                clip.Vertices.Add(c);
            }

            return c;
        }

        Sector S(Sector s)
        {
            if (!sectors.TryGetValue(s, out var c))
            {
                sectors[s] = c = new Sector { FloorHeight = s.FloorHeight, CeilingHeight = s.CeilingHeight, FloorTexture = s.FloorTexture, CeilingTexture = s.CeilingTexture, Light = s.Light, Special = s.Special, Tag = s.Tag };
                foreach (var (k, value) in s.Extra) c.Extra[k] = value;
                clip.Sectors.Add(c);
            }

            return c;
        }

        foreach (var room in rooms) S(room);
        foreach (var l in walls)
        {
            var keepFront = rooms.Contains(l.Front.Sector);
            var keepBack = l.Back is { } b && rooms.Contains(b.Sector);

            // Only the back's room is copied: the copy runs the other way, so that face is its front.
            var (start, end, front, back) = keepFront ? (l.Start, l.End, l.Front, keepBack ? l.Back : null) : (l.End, l.Start, l.Back!, null);
            var copy = new Line(V(start), V(end), Side(front, S(front.Sector))) { Special = l.Special, LineId = l.LineId };
            l.Args.CopyTo(copy.Args, 0);
            foreach (var (k, value) in l.Extra) copy.Extra[k] = value;
            if (back is not null)
            {
                copy.Back = Side(back, S(back.Sector));
            }
            else if (l.Back is not null)
            {
                // A wall between a copied room and one left behind: the copy's solid wall, keeping its look.
                var face = copy.Front;
                if (face.Middle is "-" or "") face.Middle = face.Top is not ("-" or "") ? face.Top : face.Bottom is not ("-" or "") ? face.Bottom : "-";
                copy.SetFlag("twosided", false);
                copy.SetFlag("blocking", true);
            }

            clip.Lines.Add(copy);
        }

        foreach (var t in things)
        {
            var c = new Thing { X = t.X - cx, Y = t.Y - cy, Height = t.Height, Angle = t.Angle, Type = t.Type, Special = t.Special, Tid = t.Tid };
            t.Args.CopyTo(c.Args, 0);
            foreach (var (k, value) in t.Extra) c.Extra[k] = value;
            clip.Things.Add(c);
        }

        return clip;

        static Side Side(Side from, Sector sector)
        {
            var s = new Side(sector) { OffsetX = from.OffsetX, OffsetY = from.OffsetY, Top = from.Top, Middle = from.Middle, Bottom = from.Bottom };
            foreach (var (k, value) in from.Extra) s.Extra[k] = value;
            return s;
        }
    }

    /// <summary>
    /// Pastes a copy with its middle at <paramref name="centre"/> through <paramref name="edit"/>, then joins
    /// it to the map as a dragged room is joined (corners merged, walls split and shared). Returns the
    /// pasted rooms and things, and how many of its walls cross walls that were there. <paramref name="rename"/> gives the new
    /// names of pictures copied in under another name (a paste from another game); <paramref name="keep"/> chooses which of
    /// the copy's objects to paste (null: all).
    /// </summary>
    public static (IReadOnlyList<Element> Pasted, int Crossings) Paste(StructureEdit edit, MapGeometry before, MapClip clip, MapPoint centre, Look look, IReadOnlyDictionary<string, string>? rename = null, Func<Thing, bool>? keep = null)
    {
        string Name(string n) => rename is not null && rename.TryGetValue(n.ToUpperInvariant(), out var to) ? to : n;
        var vertices = new Dictionary<Vertex, Vertex>(ReferenceEqualityComparer.Instance);
        var sectors = new Dictionary<Sector, Sector>(ReferenceEqualityComparer.Instance);
        foreach (var v in clip.Vertices)
        {
            var c = new Vertex { X = v.X + centre.X, Y = v.Y + centre.Y };
            foreach (var (k, value) in v.Extra) c.Extra[k] = value;
            vertices[v] = edit.Add(c);
        }

        foreach (var s in clip.Sectors)
        {
            var c = new Sector { FloorHeight = s.FloorHeight, CeilingHeight = s.CeilingHeight, FloorTexture = Name(s.FloorTexture), CeilingTexture = Name(s.CeilingTexture), Light = s.Light, Special = s.Special, Tag = s.Tag };
            foreach (var (k, value) in s.Extra) c.Extra[k] = value;
            sectors[s] = edit.Add(c);
        }

        var lines = new List<Line>();
        foreach (var l in clip.Lines)
        {
            var line = new Line(vertices[l.Start], vertices[l.End], edit.Add(SideCopy(l.Front))) { Special = l.Special, LineId = l.LineId };
            if (l.Back is { } b) line.Back = edit.Add(SideCopy(b));
            l.Args.CopyTo(line.Args, 0);
            foreach (var (k, value) in l.Extra) line.Extra[k] = value;
            lines.Add(edit.Add(line));
        }

        var pasted = new List<Element>(sectors.Values);
        foreach (var t in clip.Things)
        {
            if (keep is not null && !keep(t)) continue;
            var c = new Thing { X = t.X + centre.X, Y = t.Y + centre.Y, Height = t.Height, Angle = t.Angle, Type = t.Type, Special = t.Special, Tid = t.Tid };
            t.Args.CopyTo(c.Args, 0);
            foreach (var (k, value) in t.Extra) c.Extra[k] = value;
            pasted.Add(edit.Add(c));
        }

        var crossings = lines.Count == 0 ? 0 : Joining.AfterMove(edit, MoveSet.Of(before, [.. vertices.Values], lines), look);
        return (pasted, crossings);

        Side SideCopy(Side from)
        {
            var s = new Side(sectors[from.Sector]) { OffsetX = from.OffsetX, OffsetY = from.OffsetY, Top = Name(from.Top), Middle = Name(from.Middle), Bottom = Name(from.Bottom) };
            foreach (var (k, value) in from.Extra) s.Extra[k] = value;
            return s;
        }
    }
}
