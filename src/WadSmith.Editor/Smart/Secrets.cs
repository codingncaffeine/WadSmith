using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// A secret found in the map (D21): an area that counts toward the map's secrets when the player steps into
/// it, the walls into it, and the doors that lead into it.
/// </summary>
/// <param name="Area">The secret area.</param>
/// <param name="Entrances">Its walls to other areas, and the walls that open the doors leading into it from elsewhere.</param>
/// <param name="Doors">The doors that lead into it.</param>
/// <param name="Hidden">Whether every entrance is drawn as a solid wall on the in-game map.</param>
public sealed record Secret(Sector Area, IReadOnlyList<Line> Entrances, IReadOnlyList<Door> Doors, bool Hidden);

/// <summary>
/// Secrets: an area with the secret flag (GZDoom's 1024 bit in the type; translated Doom maps keep it there).
/// Its entrances get the secret line flag, which draws them as solid walls on the in-game map, and a door into
/// it can be made to look like the wall beside it, as Doom's own secret doors do.
/// </summary>
public static class Secrets
{
    /// <summary>The bit in an area's type that makes it a secret.</summary>
    public const int Flag = 1024;

    public static bool IsSecret(Sector s) => (s.Special & Flag) != 0 || s.Flag("secret");

    /// <summary>The secret <paramref name="area"/> is, or null.</summary>
    public static Secret? Recognise(MapGeometry geometry, Sector area)
    {
        if (!area.OnMap || !IsSecret(area)) return null;
        var entrances = new List<Line>();
        var doors = new List<Door>();
        foreach (var l in WallsOf(geometry, area))
        {
            if (l.Back is null || Other(l, area) is not { } beside || ReferenceEquals(beside, area)) continue;
            entrances.Add(l);

            // A door beside it leads in: its walls to the other rooms are entrances too.
            if (Smart.Doors.Recognise(geometry, beside) is not { } door || doors.Any(d => ReferenceEquals(d.Area, door.Area))) continue;
            doors.Add(door);
            entrances.AddRange(Outside(geometry, door, area).Where(w => !entrances.Contains(w)));
        }

        return new Secret(area, entrances, doors, entrances.Count > 0 && entrances.All(l => l.Flag("secret")));
    }

    /// <summary>
    /// Makes <paramref name="area"/> a secret through <paramref name="edit"/>: it counts when the player steps in,
    /// and its entrances (with the walls that open a door leading in) are drawn as solid walls on the in-game map.
    /// </summary>
    public static void Make(StructureEdit edit, MapGeometry before, Sector area)
    {
        if (!IsSecret(area)) edit.Set(area, s => s.Special, (s, v) => s.Special = v, area.Special | Flag);
        foreach (var l in Entrances(before, area)) edit.Flag(l, "secret", true);
    }

    /// <summary>Makes <paramref name="area"/> an ordinary area again: it no longer counts, and its entrances show on the in-game map.</summary>
    public static void Clear(StructureEdit edit, MapGeometry before, Sector area)
    {
        edit.Set(area, s => s.Special, (s, v) => s.Special = v, area.Special & ~Flag);
        if (area.Flag("secret")) edit.Flag(area, "secret", false);
        foreach (var l in Entrances(before, area)) edit.Flag(l, "secret", false);
    }

    /// <summary>
    /// Makes a door leading into a secret look like the wall beside it from outside, as Doom's secret doors do:
    /// each wall that opens it from another room takes the texture of the wall it stands in line with, lined up
    /// with it across and down (the door's texture hangs from its ceiling, which rests on its floor when shut).
    /// </summary>
    public static void HideDoor(StructureEdit edit, MapGeometry before, Door door, Sector secret)
    {
        foreach (var face in Outside(before, door, secret))
        {
            var side = ReferenceEquals(face.Back?.Sector, door.Area) ? face.Front : face.Back!;
            if (InLine(before, face, side.Sector) is not { } wall) continue;
            var (texture, offsetX) = wall.Texture;
            var room = side.Sector;
            edit.Set(side, s => s.Top, (s, v) => s.Top = v, texture);
            edit.Set(side, s => s.OffsetX, (s, v) => s.OffsetX = v, offsetX);
            edit.Set(side, s => s.OffsetY, (s, v) => s.OffsetY = v, wall.OffsetY + room.CeilingHeight - door.Area.FloorHeight);
            edit.Flag(face, "secret", true);
        }
    }

    /// <summary>The walls that open a door from rooms other than the secret.</summary>
    private static IEnumerable<Line> Outside(MapGeometry geometry, Door door, Sector secret) =>
        door.Openers.Where(l => l.OnMap && l.Back is not null && (ReferenceEquals(l.Front.Sector, door.Area) || ReferenceEquals(l.Back.Sector, door.Area)) && !ReferenceEquals(Other(l, door.Area), secret));

    private static List<Line> Entrances(MapGeometry geometry, Sector area)
    {
        var found = new List<Line>();
        foreach (var l in WallsOf(geometry, area))
        {
            if (l.Back is null || Other(l, area) is not { } beside || ReferenceEquals(beside, area)) continue;
            found.Add(l);
            if (Smart.Doors.Recognise(geometry, beside) is { } door) found.AddRange(Outside(geometry, door, area).Where(w => !found.Contains(w)));
        }

        return found;
    }

    /// <summary>
    /// The one-sided wall of <paramref name="room"/> running on from either end of <paramref name="face"/> in a
    /// straight line, with its texture and where that texture would be across at the face's start.
    /// </summary>
    private static ((string Name, double OffsetX) Texture, double OffsetY)? InLine(MapGeometry geometry, Line face, Sector room)
    {
        // The face as the room sees it: running with the room on its right.
        var (a, b) = ReferenceEquals(face.Front.Sector, room) ? (face.Start, face.End) : (face.End, face.Start);
        var length = face.Length;
        foreach (var l in geometry.LinesIn(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)))
        {
            if (!l.OnMap || l.Back is not null || !ReferenceEquals(l.Front.Sector, room) || !Doors.Parallel(l, face) || l.Front.Middle is "-" or "") continue;

            // Before the face (ends where it starts) or after it (starts where it ends), facing the same way.
            if (ReferenceEquals(l.End, a)) return ((l.Front.Middle, l.Front.OffsetX + l.Length), l.Front.OffsetY);
            if (ReferenceEquals(l.Start, b)) return ((l.Front.Middle, l.Front.OffsetX - length), l.Front.OffsetY);
        }

        return null;
    }

    private static Sector? Other(Line l, Sector area) => ReferenceEquals(l.Front.Sector, area) ? l.Back?.Sector : l.Front.Sector;

    private static List<Line> WallsOf(MapGeometry geometry, Sector area)
    {
        if (geometry.WallBoundsAt(area.Slot) is not { } b) return [];
        return [.. geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area)))];
    }
}
