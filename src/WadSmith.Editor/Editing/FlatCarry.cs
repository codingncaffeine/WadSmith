using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Editor.Editing;

/// <summary>
/// The floor and ceiling textures that go with rooms moved, turned, resized or mirrored whole (§3.4: "textures re-align"):
/// each room's texture fields as the drag started, set again for every step from that start, so a step undone or merged is
/// exact. Rooms only partly moved (a corner dragged) keep their textures where they are, lined up with the map.
/// </summary>
internal sealed class FlatCarry
{
    private static readonly string[] Fields = ["xpanning", "ypanning", "rotation", "xscale", "yscale"];
    private readonly (Sector Room, KeyValuePair<string, UdmfValue>[] Start, (int, int)? FloorSize, (int, int)? CeilingSize)[] _rooms;

    private FlatCarry((Sector, KeyValuePair<string, UdmfValue>[], (int, int)?, (int, int)?)[] rooms) => _rooms = rooms;

    public IEnumerable<Element> Rooms => _rooms.Select(r => (Element)r.Room);

    /// <summary>
    /// The rooms a move carries whole: every wall of theirs runs between corners that move. Reads the map: make it on the UI
    /// thread, with <paramref name="flatSize"/> giving a flat's size by name (null when unknown).
    /// </summary>
    public static FlatCarry? Of(MoveSet set, Func<string, (int Width, int Height)?>? flatSize)
    {
        if (set.Vertices.Count == 0) return null;
        var moving = set.Vertices.ToHashSet(ReferenceEqualityComparer.Instance);
        var candidates = set.Lines.Where(l => l.OnMap).SelectMany(l => l.Back is { } b ? new[] { l.Front.Sector, b.Sector } : [l.Front.Sector]).Distinct().ToList();
        var carried = new List<(Sector, KeyValuePair<string, UdmfValue>[], (int, int)?, (int, int)?)>();
        foreach (var room in candidates)
        {
            if (set.Geometry.WallBoundsAt(room.Slot) is not { } b) continue;
            var walls = set.Geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back?.Sector, room))).ToList();
            if (walls.Count == 0 || !walls.All(l => moving.Contains(l.Start) && moving.Contains(l.End))) continue;
            carried.Add((room, room.Extra.ToArray(), flatSize?.Invoke(room.FloorTexture), flatSize?.Invoke(room.CeilingTexture)));
        }

        return carried.Count == 0 ? null : new FlatCarry([.. carried]);
    }

    /// <summary>Sets every carried room's texture fields for <paramref name="p"/> from where they started (identity: as they were).</summary>
    public void Place(Placement p)
    {
        foreach (var (room, start, floorSize, ceilingSize) in _rooms)
        {
            if (p == Placement.Identity)
            {
                Restore(room, start);
                continue;
            }

            Set(room, "floor", FlatTransform.Of(start, false).Through(p, floorSize));
            Set(room, "ceiling", FlatTransform.Of(start, true).Through(p, ceilingSize));
        }
    }

    private static void Set(Sector room, string part, FlatTransform t)
    {
        double[] values = [t.XPan, t.YPan, t.Rotation, t.XScale, t.YScale];
        double[] plain = [0, 0, 0, 1, 1];
        for (var i = 0; i < Fields.Length; i++)
        {
            var name = Fields[i] + part;
            if (Math.Abs(values[i] - plain[i]) < 1e-9) room.Extra.Remove(name);
            else room.Extra[name] = UdmfValue.Float(values[i]);
        }
    }

    private static void Restore(Sector room, KeyValuePair<string, UdmfValue>[] fields)
    {
        foreach (var name in room.Extra.Select(f => f.Key).ToList()) room.Extra.Remove(name);
        foreach (var (k, v) in fields) room.Extra[k] = v;
    }
}
