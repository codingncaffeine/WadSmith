using WadSmith.Core.Games;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// A switch found in the map (D21): a wall the player uses that sets off something elsewhere, the areas
/// it names by their tag, and what it does to them.
/// </summary>
/// <param name="Wall">The switch.</param>
/// <param name="Targets">The areas with the tag its action names (none for actions on things or scripts).</param>
/// <param name="Does">What it does, in the Inspector's words: "opens a door", "lowers a lift", or the action's name.</param>
/// <param name="Repeats">Whether it works again and again.</param>
public sealed record Switch(Line Wall, IReadOnlyList<Sector> Targets, string Does, bool Repeats);

/// <summary>
/// Switches: walls with an action the player uses that work on areas elsewhere by tag (not a door's or lift's
/// own walls, which work on the area behind them, nor exits or teleporters, which have their own views). A
/// door or lift gets a switch by a click on a solid wall: a piece of it as wide as the switch picture becomes
/// the switch, and the door or lift gets a tag of its own if it has none.
/// </summary>
public static class Switches
{
    /// <summary>The width of the piece of wall a switch takes when its picture's size is unknown (most switch pictures are 64 wide).</summary>
    public const double Width = 64;

    /// <summary>How deep the alcove of a recessed switch is.</summary>
    public const double RecessDepth = 8;

    private static readonly string[] AreaActions = ["Door_", "Plat_", "Floor_", "Ceiling_", "FloorAndCeiling_", "Stairs_", "Light_", "Generic_", "Pillar_", "Elevator_", "Sector_"];

    /// <summary>Whether an action works on the areas with the tag in its first argument (doors, lifts, floors, lights…).</summary>
    public static bool NamesAreas(int special) => ActionCatalog.Get(special) is { } a && AreaActions.Any(p => a.Name.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The switch <paramref name="wall"/> is, or null.</summary>
    public static Switch? Recognise(MapGeometry geometry, Line wall)
    {
        if (!wall.OnMap || wall.Special == 0 || !wall.Flag("playeruse") || Exits.IsExit(wall) || Teleports.IsTeleport(wall) || wall.Args[0] == 0) return null;
        var f = geometry.Frame;
        var targets = NamesAreas(wall.Special) ? geometry.SectorsTagged(wall.Args[0]).Select(i => f.Sectors[i].Element).OfType<Sector>().Where(s => s.OnMap).ToList() : [];
        var does = wall.Special switch
        {
            Doors.DoorRaise or Doors.DoorOpen or Doors.DoorLockedRaise or Doors.DoorAnimated => targets.Count > 1 ? "opens doors" : "opens a door",
            Lifts.DownWaitUpStay or Lifts.DownWaitUpStayLip => targets.Count > 1 ? "lowers lifts" : "lowers a lift",
            _ => ActionCatalog.Get(wall.Special)?.Name ?? $"action {wall.Special}",
        };
        return new Switch(wall, targets, does, wall.Flag("repeatspecial"));
    }

    /// <summary>
    /// The switch a part of the map belongs to — the wall carrying it (a switch, or an exit switch), a face of that wall, or the
    /// alcove it sits in (its area, or a wall round it) — or null: a click in the 3D view lands on a face, and the switch is what
    /// the user means (Delete takes it away).
    /// </summary>
    public static Line? SwitchOf(MapGeometry geometry, Element element) => element switch
    {
        Side { Line: { } wall } => SwitchOf(geometry, wall),
        Line { OnMap: true } wall when Carries(geometry, wall) => wall,
        Line { OnMap: true } wall => AlcoveOf(geometry, wall.Front.Sector)?.Switch ?? (wall.Back is { } back ? AlcoveOf(geometry, back.Sector)?.Switch : null),
        Sector { OnMap: true } area => AlcoveOf(geometry, area)?.Switch,
        _ => null,
    };

    /// <summary>Whether a wall carries a switch the player uses: one that works on something elsewhere, or an exit switch.</summary>
    private static bool Carries(MapGeometry geometry, Line wall) => Recognise(geometry, wall) is not null || (Exits.IsExit(wall) && wall.Flag("playeruse"));

    /// <summary>
    /// The alcove a switch sits in (as <see cref="Place"/> makes it): an area opening onto one room by one wall, its mouth, its
    /// other walls solid, the one in line with the mouth no deeper than <see cref="RecessDepth"/> carrying a switch; null otherwise.
    /// </summary>
    private static (Line Switch, Line Mouth, Sector Room)? AlcoveOf(MapGeometry geometry, Sector area)
    {
        if (!area.OnMap || area.CeilingHeight <= area.FloorHeight || geometry.WallBoundsAt(area.Slot) is not { } b || Math.Min(b.MaxX - b.MinX, b.MaxY - b.MinY) > 256) return null;
        var walls = geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, area) || ReferenceEquals(l.Back?.Sector, area))).ToList();
        if (walls.Count > 8 || walls.Count(l => l.Back is not null) != 1) return null;
        var mouth = walls.Single(l => l.Back is not null);
        var room = ReferenceEquals(mouth.Front.Sector, area) ? mouth.Back!.Sector : mouth.Front.Sector;
        var (dx, dy) = ((mouth.End.X - mouth.Start.X) / mouth.Length, (mouth.End.Y - mouth.Start.Y) / mouth.Length);
        double Apart(Line l) => Math.Abs((l.Start.X - mouth.Start.X) * dy - (l.Start.Y - mouth.Start.Y) * dx);
        return walls.FirstOrDefault(l => l.Back is null && Doors.Parallel(l, mouth) && Apart(l) <= RecessDepth + 0.5 && Carries(geometry, l)) is { } wall ? (wall, mouth, room) : null;
    }

    /// <summary>
    /// Takes a switch away through <paramref name="edit"/>, leaving the wall it was set into as the wall beside it (Delete; the
    /// user's report on doors, 2026-10-15): an alcove closes up (its walls, its area and what stands in it go, and its mouth is
    /// the room's wall again); a switch on a flat piece of wall loses its action. The wall left takes the texture of the wall
    /// beside it, and walls left in one line facing the same room become one wall. <paramref name="geometry"/> shows the map as
    /// it was before this edit.
    /// </summary>
    public static void Remove(StructureEdit edit, MapGeometry geometry, Line wall, Look look)
    {
        if (!wall.OnMap) return;
        var loose = new HashSet<Vertex>(ReferenceEqualityComparer.Instance);
        var face = wall;
        var room = wall.Front.Sector;
        if (wall.Back is null && AlcoveOf(geometry, wall.Front.Sector) is { } alcove)
        {
            var area = wall.Front.Sector;
            room = alcove.Room;
            face = alcove.Mouth;
            if (geometry.WallBoundsAt(area.Slot) is { } b)
            {
                var f = geometry.Frame;
                foreach (var t in geometry.ThingSlotsIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(i => ReferenceEquals(geometry.SectorAt(new MapPoint(f.Things[i].X, f.Things[i].Y)), area)).Select(i => f.Things[i].Element!)) edit.Remove(t);
                foreach (var l in geometry.LinesIn(b.MinX, b.MinY, b.MaxX, b.MaxY).Where(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, area)).ToList()) Deleting.RemoveWall(edit, l, loose);
            }

            // The mouth turns to face the room, and is its solid wall again.
            if (ReferenceEquals(face.Front.Sector, area)) Walls.Turn(edit, face);
            var gone = face.Back!;
            edit.Set(face, x => x.Back, (x, v) => x.Back = v, null);
            edit.Remove(gone);
            edit.Flag(face, "twosided", false);
            edit.Flag(face, "blocking", true);
            edit.Remove(area);
        }

        edit.Set(face, x => x.Special, (x, v) => x.Special = v, 0);
        for (var i = 0; i < 5; i++)
        {
            var k = i;
            edit.Set(face, x => x.Args[k], (x, v) => x.Args[k] = v, 0);
        }

        edit.Flag(face, "playeruse", false);
        edit.Flag(face, "repeatspecial", false);

        // A solid wall takes the picture of the wall beside it along the same line, facing the same room.
        if (face.Back is null)
        {
            var beside = new[] { face.Start, face.End }.SelectMany(v => geometry.LinesIn(v.X, v.Y, v.X, v.Y).Where(l => l.OnMap && !ReferenceEquals(l, face) && l.Back is null && (l.Start == v || l.End == v) && ReferenceEquals(l.Front.Sector, room) && Doors.Parallel(l, face) && !Carries(geometry, l))).FirstOrDefault();
            edit.Set(face.Front, x => x.Middle, (x, v) => x.Middle = v, beside?.Front.Middle is { } shown and not ("-" or "") ? shown : look.Wall);
            edit.Set(face.Front, x => x.Top, (x, v) => x.Top = v, "-");
            edit.Set(face.Front, x => x.Bottom, (x, v) => x.Bottom = v, "-");
            edit.Set(face.Front, x => x.OffsetY, (x, v) => x.OffsetY = v, beside?.Front.OffsetY ?? 0);
            edit.Flag(face, "dontpegtop", false);
            edit.Flag(face, "dontpegbottom", beside?.Flag("dontpegbottom") ?? false);
        }

        // Walls left in one line facing the same room become one wall; corners no wall uses go.
        var rerouted = new List<Line>();
        List<Line> WallsAt(Vertex v) => [.. geometry.LinesIn(v.X, v.Y, v.X, v.Y).Concat(rerouted).Concat([face]).Distinct().Where(l => l.OnMap && (l.Start == v || l.End == v))];
        foreach (var v in new[] { face.Start, face.End }.Concat(loose).Distinct().ToList())
        {
            if (!v.OnMap) continue;
            var at = WallsAt(v);
            if (at.Count == 0) edit.Remove(v);
            else if (at.Count == 2 && Doors.Parallel(at[0], at[1]) && Deleting.Straighten(edit, v, at[0], at[1])) rerouted.Add(at[0]);
        }
    }

    /// <summary>The switches that work on <paramref name="area"/>: walls elsewhere used by the player naming its tag.</summary>
    public static IReadOnlyList<Line> Of(MapGeometry geometry, Sector area)
    {
        if (area.Tag == 0) return [];
        var f = geometry.Frame;
        return [.. geometry.LinesNaming(area.Tag).Select(i => f.Lines[i].Element).OfType<Line>().Where(l => NamesAreas(l.Special) && Recognise(geometry, l) is not null)];
    }

    /// <summary>
    /// The piece of <paramref name="wall"/> a switch takes, cut out through <paramref name="edit"/>: <paramref name="width"/>
    /// wide round <paramref name="at"/> (the wall's middle when null), kept on the wall; the whole wall when it is not
    /// much wider than the switch. The wall's texture runs on across the pieces either side.
    /// </summary>
    public static Line Piece(StructureEdit edit, Line wall, MapPoint? at, double width = Width)
    {
        var length = wall.Length;
        if (length <= width + 16) return wall;
        var (dx, dy) = ((wall.End.X - wall.Start.X) / length, (wall.End.Y - wall.Start.Y) / length);
        var t = at is { } p ? (p.X - wall.Start.X) * dx + (p.Y - wall.Start.Y) * dy : length / 2;
        var from = Math.Clamp(Math.Round(t - width / 2), 0, length - width);
        Vertex Corner(double d) => edit.Add(new Vertex { X = wall.Start.X + dx * d, Y = wall.Start.Y + dy * d });
        if (from + width < length - 1e-6) Walls.Split(edit, wall, Corner(from + width));
        return from > 1e-6 ? Walls.Split(edit, wall, Corner(from)) : wall;
    }

    /// <summary>
    /// Puts the switch picture <paramref name="texture"/> on <paramref name="wall"/> round <paramref name="at"/> and returns
    /// the wall that carries it, through <paramref name="edit"/>: a piece as wide as the picture (<see cref="Width"/> when
    /// <paramref name="sizeOf"/> does not know it). When the picture is shorter than the room, the piece becomes the mouth
    /// of an alcove <see cref="RecessDepth"/> deep and as tall as the picture, with the picture on its back wall and the
    /// room's wall above it, as Doom's own switches sit — unless there is no room for the alcove behind the wall, when
    /// the picture goes on the flat piece. <paramref name="before"/> must show the map as it is, untouched by this edit.
    /// </summary>
    public static Line Place(StructureEdit edit, MapGeometry before, Line wall, MapPoint? at, Look look, string texture, Func<string, (int Width, int Height)?>? sizeOf)
    {
        var size = sizeOf?.Invoke(texture);
        var width = size is { Width: >= 8 } s ? Math.Min(s.Width, wall.Length) : Math.Min(Width, wall.Length);
        var room = wall.Front.Sector;
        if (size is { Height: > 0 } tall && tall.Height < room.CeilingHeight - room.FloorHeight && Recess(edit, before, wall, at, width, tall.Height, look, texture) is { } recessed) return recessed;

        var piece = Piece(edit, wall, at, width);
        edit.Set(piece.Front, x => x.Middle, (x, v) => x.Middle = v, texture);
        edit.Set(piece.Front, x => x.OffsetX, (x, v) => x.OffsetX = v, 0);
        edit.Set(piece.Front, x => x.OffsetY, (x, v) => x.OffsetY = v, 0);
        return piece;
    }

    /// <summary>
    /// An alcove behind <paramref name="wall"/>, round <paramref name="at"/>: <paramref name="width"/> wide, <see cref="RecessDepth"/>
    /// deep, <paramref name="height"/> tall, with <paramref name="texture"/> on its back wall; null when something lies behind the wall there.
    /// </summary>
    private static Line? Recess(StructureEdit edit, MapGeometry before, Line wall, MapPoint? at, double width, double height, Look look, string texture)
    {
        var length = wall.Length;
        var (dx, dy) = ((wall.End.X - wall.Start.X) / length, (wall.End.Y - wall.Start.Y) / length);
        var t = at is { } p ? (p.X - wall.Start.X) * dx + (p.Y - wall.Start.Y) * dy : length / 2;
        var from = Math.Clamp(Math.Round(t - width / 2), 0, length - width);

        // Outward: away from the room, which lies on the wall's right.
        var (nx, ny) = (-dy, dx);
        MapPoint At(double along, double outward = 0) => new(wall.Start.X + dx * along + nx * outward, wall.Start.Y + dy * along + ny * outward);
        MapPoint[] strip = [At(from), At(from + width), At(from + width, RecessDepth), At(from, RecessDepth)];
        if (!Doors.Clear(before, wall, strip)) return null;

        var made = ShapeDrawing.Draw(edit, before, strip, look);
        if (made.Count != 1) throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The switch's alcove did not come out as one area: {made.Count}."));
        var alcove = made[0];
        alcove.CeilingHeight = alcove.FloorHeight + height;

        // Its lintel shows only under a ceiling of its own: the sky beside the sky would show through the wall above it.
        alcove.CeilingTexture = Look.SolidUnder(alcove.CeilingTexture, alcove.FloorTexture);

        Line? back = null;
        var touched = edit.Touched.Select(e => e switch { Line w => w, Side { Line: { } w } => w, _ => null }).OfType<Line>().Distinct().Where(l => l.OnMap).ToList();
        foreach (var l in touched.Where(l => ReferenceEquals(l.Front.Sector, alcove) || ReferenceEquals(l.Back?.Sector, alcove)))
        {
            if (l.Back is not null)
            {
                // The mouth: the room's wall above the alcove, hung from the ceiling like the wall beside it.
                var face = ReferenceEquals(l.Front.Sector, alcove) ? l.Back : l.Front;
                edit.Set(face, x => x.Top, (x, v) => x.Top = v, look.Wall);
                edit.Flag(l, "dontpegtop", true);
            }
            else if (Doors.Parallel(l, wall))
            {
                // The back wall carries the picture, hung from the alcove's ceiling, so it sits just under the lintel.
                back = l;
                edit.Set(l.Front, x => x.Middle, (x, v) => x.Middle = v, texture);
                edit.Set(l.Front, x => x.OffsetX, (x, v) => x.OffsetX = v, 0);
                edit.Set(l.Front, x => x.OffsetY, (x, v) => x.OffsetY = v, 0);
            }
            else
            {
                // A side of the alcove: its texture hung from the floor, in line with the room's wall.
                edit.Flag(l, "dontpegbottom", true);
            }
        }

        return back ?? throw new InvalidOperationException("The switch's alcove has no back wall.");
    }

    /// <summary>
    /// Makes a switch on <paramref name="wall"/> at <paramref name="at"/> that works <paramref name="target"/> (a door
    /// or a lift) the way its own walls do, through <paramref name="edit"/>: the game's switch picture goes on the wall
    /// round the point (see <see cref="Place"/>), and the wall carrying it gets the action by the target's tag, used by
    /// the player again and again. Returns the switch, or null and why.
    /// </summary>
    public static (Line? Switch, string? Why) Make(StructureEdit edit, MapGeometry before, Line wall, MapPoint at, Sector target, Look look, Func<string, (int Width, int Height)?>? sizeOf = null, string family = "doom")
    {
        if (!wall.OnMap || !target.OnMap) return (null, null);
        if (wall.Back is not null) return (null, "A switch goes on a solid wall: click a wall with a room on one side only.");
        if (wall.Special != 0) return (null, "This wall already does something; click a plain wall for the switch.");
        if (ReferenceEquals(wall.Front.Sector, target)) return (null, "Put the switch on a wall outside it, where the player stands to use it.");

        // What the switch does: what the target's own walls do, by its tag.
        var tag = target.Tag != 0 ? target.Tag : before.HighestTag + 1;
        (int Special, int[] Args) action;
        PolyDoor? polyDoor = null;
        if (Doors.Recognise(before, target) is { } door) action = Doors.Encode(door.Settings, 0, door.Openers.FirstOrDefault()?.Args[1], tag, door.Kind);
        else if ((polyDoor = PolyDoors.Recognise(before, target, family)) is { } sliding && sliding.Openers.FirstOrDefault() is { } face) action = (face.Special, [.. face.Args]);
        else if (Lifts.Recognise(before, target) is { } lift) action = (Lifts.DownWaitUpStayLip, [tag, lift.Settings.Fast ? Lifts.FastSpeed : Lifts.NormalSpeed, lift.Settings.Delay, 0, 0]);
        else return (null, "A switch opens a door or lowers a lift: select a door or a lift first.");

        var piece = Place(edit, before, wall, at, look, look.Switch, sizeOf);

        // A sliding or swinging door is moved by its number, with its lock on the switch too; any other target by its tag.
        if (polyDoor is not null)
        {
            if (polyDoor.Settings.Lock != 0) edit.Field(piece, PolyDoors.LockField, Core.Maps.Udmf.UdmfValue.Int(polyDoor.Settings.Lock));
        }
        else if (target.Tag != tag)
        {
            edit.Set(target, s => s.Tag, (s, v) => s.Tag = v, tag);
        }
        edit.Set(piece, x => x.Special, (x, v) => x.Special = v, action.Special);
        for (var i = 0; i < 5; i++)
        {
            var k = i;
            edit.Set(piece, x => x.Args[k], (x, v) => x.Args[k] = v, i < action.Args.Length ? action.Args[k] : 0);
        }

        edit.Flag(piece, "playeruse", true);
        edit.Flag(piece, "repeatspecial", true);
        return (piece, null);
    }
}
