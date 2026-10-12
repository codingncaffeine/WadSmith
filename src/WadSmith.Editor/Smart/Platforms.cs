using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>What a platform is: a solid slab to stand on, water to swim in, or a slab the player passes through (GZDoom's 3D floor types 1, 2 and 3).</summary>
public enum PlatformKind
{
    Solid = 1,
    Water = 2,
    Passable = 3,
}

/// <summary>
/// A platform (or pool) in a room: GZDoom's 3D floor (Sector_Set3DFloor, 160), a slab between two heights that the
/// engine takes from a control room drawn outside the map. Measured in GZDoom: the slab's top shows the control room's
/// ceiling picture and its underside the control room's floor picture; the control wall's picture is on the slab's sides.
/// </summary>
public sealed record Platform(Sector Room, Sector Control, Line ControlLine)
{
    /// <summary>What it is, from the special's type argument (read live, so a change shows at once).</summary>
    public PlatformKind Kind => (PlatformKind)(ControlLine.Args[1] & 3);

    public double Bottom => Control.FloorHeight;

    public double Top => Control.CeilingHeight;

    /// <summary>How opaque it is: 255 solid-looking, 0 invisible (the special's alpha argument).</summary>
    public int Alpha => ControlLine.Args[3];

    /// <summary>Its sides' picture: the control wall's middle texture.</summary>
    public string Sides => ControlLine.Front.Middle;
}

public static class Platforms
{
    public const int Special = PlatformFrame.Special;

    /// <summary>The field that marks a control room WadSmith drew for a platform.</summary>
    public const string Field = "user_wadsmith_platform";

    /// <summary>GZDoom's type flag that draws a slab's faces from inside it too (water seen from below its surface).</summary>
    private const int RenderInside = 4;

    /// <summary>The platforms in <paramref name="room"/>, from the control walls naming its tag.</summary>
    public static IReadOnlyList<Platform> Of(MapGeometry geometry, Sector room)
    {
        var f = geometry.Frame;
        var found = new List<Platform>();
        foreach (var p in geometry.PlatformsIn(room.Slot))
        {
            if (f.Sectors[p.Control].Element is { } control && f.Lines[p.Line].Element is { } line) found.Add(new Platform(room, control, line));
        }

        return found;
    }

    /// <summary>The platform <paramref name="sector"/> controls, when it is a control room; null for an ordinary room.</summary>
    public static Platform? ControlOf(MapGeometry geometry, Sector sector)
    {
        var f = geometry.Frame;
        foreach (var p in geometry.Platforms)
        {
            if (p.Control != sector.Slot) continue;
            if (f.Sectors[p.Room].Element is { } room && f.Lines[p.Line].Element is { } line) return new Platform(room, sector, line);
        }

        return null;
    }

    /// <summary>
    /// Adds a platform to <paramref name="room"/> from <paramref name="bottom"/> to <paramref name="top"/>: a 64-square
    /// control room drawn north-west of the map, outside everything, with the room's floor picture on both faces (water:
    /// the game's water, see-through) and the room's wall picture on the sides; the room gets a tag when it has none.
    /// Returns the control room.
    /// </summary>
    public static Sector Add(StructureEdit e, MapGeometry before, Sector room, double bottom, double top, PlatformKind kind, Look look)
    {
        var tag = room.Tag != 0 ? room.Tag : before.HighestTag + 1;
        if (room.Tag == 0) e.Set(room, s => s.Tag, (s, v) => s.Tag = v, tag);
        var (x0, _, _, y1) = before.Extent();

        // A free 64-square north-west of the map, further west for each control room already there.
        var x = Math.Floor((x0 - 192) / 64) * 64;
        var y = Math.Ceiling((y1 + 128) / 64) * 64;
        while (before.LinesIn(x - 8, y - 8, x + 72, y + 72).Count > 0 || before.SectorAt(new MapPoint(x + 32, y + 32)) is not null) x -= 128;
        var control = ShapeDrawing.Draw(e, before, [new(x, y), new(x, y + 64), new(x + 64, y + 64), new(x + 64, y)], look).Single();
        var flat = kind == PlatformKind.Water ? look.Water : room.FloorTexture;
        e.Set(control, s => s.FloorHeight, (s, v) => s.FloorHeight = v, bottom);
        e.Set(control, s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, top);
        e.Set(control, s => s.FloorTexture, (s, v) => s.FloorTexture = v, flat);
        e.Set(control, s => s.CeilingTexture, (s, v) => s.CeilingTexture = v, flat);
        e.Set(control, s => s.Light, (s, v) => s.Light = v, room.Light);
        e.Field(control, Field, UdmfValue.Bool(true));

        // The square's own walls (all new, so among the edit's touched elements: no whole-map search).
        var walls = e.Touched.OfType<Line>().Where(l => l.OnMap && ReferenceEquals(l.Front.Sector, control)).Distinct().ToList();
        var controlWall = walls.OrderBy(l => l.Start.X + l.Start.Y).First();
        var sides = kind == PlatformKind.Water ? look.Wall : FirstWallTexture(before, room) ?? look.Wall;
        foreach (var l in walls) e.Set(l.Front, s => s.Middle, (s, v) => s.Middle = v, sides);
        e.Set(controlWall, l => l.Special, (l, v) => l.Special = v, Special);
        int[] args = [tag & 255, Type(kind), 0, kind == PlatformKind.Water ? 128 : 255, tag >> 8];
        for (var i = 0; i < args.Length; i++)
        {
            var k = i;
            e.Set(controlWall, l => l.Args[k], (l, v) => l.Args[k] = v, args[k]);
        }

        return control;
    }

    /// <summary>The special's type argument for a kind: water is also drawn from inside, so its surface shows from below.</summary>
    private static int Type(PlatformKind kind) => kind == PlatformKind.Water ? (int)kind | RenderInside : (int)kind;

    /// <summary>The picture on the room's first wall, for the sides of a platform in it.</summary>
    private static string? FirstWallTexture(MapGeometry geometry, Sector room)
    {
        if (geometry.WallBoundsAt(room.Slot) is not { } b) return null;
        foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY))
        {
            if (!l.OnMap) continue;
            var face = ReferenceEquals(l.Front.Sector, room) ? l.Front : l.Back is { } back && ReferenceEquals(back.Sector, room) ? back : null;
            if (face is not null && face.Middle is not ("-" or "")) return face.Middle;
        }

        return null;
    }

    /// <summary>Takes a platform away: its control room goes (the room keeps its tag).</summary>
    public static void Remove(StructureEdit e, MapGeometry geometry, Platform platform, Look look) => Deleting.Delete(e, geometry, [platform.Control], look);

    /// <summary>Makes a platform solid, water or passable: its type, how see-through it is, and water's picture.</summary>
    public static void SetKind(StructureEdit e, Platform platform, PlatformKind kind, Look look)
    {
        var line = platform.ControlLine;
        e.Set(line, l => l.Args[1], (l, v) => l.Args[1] = v, (line.Args[1] & ~(3 | RenderInside)) | Type(kind));
        if (kind == PlatformKind.Water && platform.Alpha == 255) e.Set(line, l => l.Args[3], (l, v) => l.Args[3] = v, 128);
        if (kind != PlatformKind.Water && platform.Kind == PlatformKind.Water) e.Set(line, l => l.Args[3], (l, v) => l.Args[3] = v, 255);
        if (kind == PlatformKind.Water)
        {
            e.Set(platform.Control, s => s.FloorTexture, (s, v) => s.FloorTexture = v, look.Water);
            e.Set(platform.Control, s => s.CeilingTexture, (s, v) => s.CeilingTexture = v, look.Water);
        }
    }

    /// <summary>Sets how opaque a platform is (255 solid-looking, 0 invisible).</summary>
    public static void SetAlpha(StructureEdit e, Platform platform, int alpha) =>
        e.Set(platform.ControlLine, l => l.Args[3], (l, v) => l.Args[3] = v, Math.Clamp(alpha, 0, 255));
}
