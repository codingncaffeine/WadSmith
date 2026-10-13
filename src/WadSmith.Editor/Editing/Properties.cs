using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Copy and paste properties (Doom Builder's Ctrl+Shift+C / Ctrl+Shift+V, Eureka's "copy properties"): what one room,
/// wall, face or object looks like and does, laid onto others of its kind. Never where things are, which way an object
/// faces, or the numbers that link machinery (tags, ids): a pasted room keeps its tag, so its switch still works.
/// </summary>
public static class Properties
{
    /// <summary>What was copied last, in this run of the app (any map); null before the first copy.</summary>
    public static Copied? Held { get; private set; }

    /// <summary>The fields that name or link an element, or WadSmith's own grouping, and never travel with its looks.</summary>
    private static bool Kept(string field) =>
        field is "id" or "moreids" or "comment" || field.StartsWith("user_wadsmith_", StringComparison.Ordinal);

    /// <summary>Copies the properties of <paramref name="from"/>; false (and nothing held) for a corner, which has none worth copying.</summary>
    public static bool Copy(Element from)
    {
        Copied? held = from switch
        {
            Sector s => new RoomCopy(s.FloorHeight, s.CeilingHeight, s.FloorTexture, s.CeilingTexture, s.Light, s.Special, Fields(s)),
            Side side => new FaceCopy(Face(side), side.OffsetX, side.OffsetY, Fields(side)),
            Line l => new WallCopy(Face(l.Front), l.Back is { } b ? Face(b) : null, l.Flag("dontpegtop"), l.Flag("dontpegbottom")),
            Thing t => new ObjectCopy(t.Type, t.Height, t.Special, [.. t.Args], Fields(t)),
            _ => null,
        };
        if (held is null) return false;
        Held = held;
        return true;
    }

    /// <summary>Whether what is held can go onto <paramref name="to"/>: a room onto rooms, an object onto objects, a wall or a face onto walls and faces.</summary>
    public static bool Fits(Copied held, Element to) => (held, to) switch
    {
        (RoomCopy, Sector) => true,
        (ObjectCopy, Thing) => true,
        (WallCopy or FaceCopy, Line or Side) => true,
        _ => false,
    };

    /// <summary>Lays what is held onto each of <paramref name="targets"/> it fits, through <paramref name="e"/>; returns how many took it.</summary>
    public static int Paste(StructureEdit e, Copied held, IEnumerable<Element> targets)
    {
        var count = 0;
        foreach (var target in targets)
        {
            if (!target.OnMap || !Fits(held, target)) continue;
            count++;
            switch (held, target)
            {
                case (RoomCopy r, Sector s):
                    e.Set(s, x => x.FloorHeight, (x, v) => x.FloorHeight = v, r.Floor);
                    e.Set(s, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, r.Ceiling);
                    e.Set(s, x => x.FloorTexture, (x, v) => x.FloorTexture = v, r.FloorTexture);
                    e.Set(s, x => x.CeilingTexture, (x, v) => x.CeilingTexture = v, r.CeilingTexture);
                    e.Set(s, x => x.Light, (x, v) => x.Light = v, r.Light);
                    e.Set(s, x => x.Special, (x, v) => x.Special = v, r.Special);
                    SetFields(e, s, r.Fields);
                    break;
                case (ObjectCopy o, Thing t):
                    e.Set(t, x => x.Type, (x, v) => x.Type = v, o.Type);
                    e.Set(t, x => x.Height, (x, v) => x.Height = v, o.Height);
                    e.Set(t, x => x.Special, (x, v) => x.Special = v, o.Special);
                    for (var i = 0; i < o.Args.Length && i < t.Args.Length; i++)
                    {
                        var arg = i;
                        e.Set(t, x => x.Args[arg], (x, v) => x.Args[arg] = v, o.Args[arg]);
                    }

                    SetFields(e, t, o.Fields);
                    break;
                case (FaceCopy f, Side side):
                    Dress(e, side, f.Face);
                    e.Set(side, x => x.OffsetX, (x, v) => x.OffsetX = v, f.OffsetX);
                    e.Set(side, x => x.OffsetY, (x, v) => x.OffsetY = v, f.OffsetY);
                    SetFields(e, side, f.Fields);
                    break;
                case (FaceCopy f, Line l):
                    Dress(e, l.Front, f.Face);
                    if (l.Back is { } back) Dress(e, back, f.Face);
                    break;
                case (WallCopy w, Line l):
                    Dress(e, l.Front, w.Front);
                    if (l.Back is { } behind) Dress(e, behind, w.Back ?? w.Front);
                    e.Flag(l, "dontpegtop", w.UpperHangs);
                    e.Flag(l, "dontpegbottom", w.LowerContinues);
                    break;
                case (WallCopy w, Side side):
                    Dress(e, side, side.Line is { } owner && ReferenceEquals(owner.Back, side) && w.Back is { } b ? b : w.Front);
                    break;
            }
        }

        return count;
    }

    /// <summary>What a held copy is of, in a few words ("a room's", "an object's").</summary>
    public static string Of(Copied held) => held switch
    {
        RoomCopy => "the room's",
        ObjectCopy => "the object's",
        FaceCopy => "the face's",
        _ => "the wall's",
    };

    private static FacePictures Face(Side side) => new(side.Top, side.Middle, side.Bottom, side.Line?.Back is null);

    /// <summary>
    /// Gives a face the pictures of another: as they are between faces of the same sort; a solid face takes the other's
    /// main picture as its middle; an open face takes a solid one's middle as its upper and lower and keeps its own middle
    /// (a picture across an opening would block the view).
    /// </summary>
    private static void Dress(StructureEdit e, Side side, FacePictures from)
    {
        var solid = side.Line?.Back is null;
        var (top, middle, bottom) = from.Solid == solid ? (from.Top, from.Middle, from.Bottom)
            : solid ? (side.Top, from.Main, side.Bottom)
            : (from.Main, side.Middle, from.Main);
        e.Set(side, x => x.Top, (x, v) => x.Top = v, top);
        e.Set(side, x => x.Middle, (x, v) => x.Middle = v, middle);
        e.Set(side, x => x.Bottom, (x, v) => x.Bottom = v, bottom);
    }

    private static KeyValuePair<string, UdmfValue>[] Fields(Element e) => [.. e.Extra.Where(f => !Kept(f.Key))];

    // The target's own fields become the copy's, leaving the ones that name or link it.
    private static void SetFields(StructureEdit e, Element target, KeyValuePair<string, UdmfValue>[] fields)
    {
        foreach (var name in target.Extra.Select(f => f.Key).Where(k => !Kept(k) && !fields.Any(f => f.Key == k)).ToList()) e.Field(target, name, null);
        foreach (var (name, value) in fields) e.Field(target, name, value);
    }
}

/// <summary>Properties copied from one element, to paste onto others of its kind.</summary>
public abstract record Copied;

/// <summary>A face's three pictures, and whether its wall is solid (one-sided).</summary>
public sealed record FacePictures(string Top, string Middle, string Bottom, bool Solid)
{
    /// <summary>The picture the face is seen by: the middle of a solid face, else its lower, upper or middle, the first it has.</summary>
    public string Main => Solid ? Middle : new[] { Bottom, Top, Middle }.FirstOrDefault(p => p != "-" && p.Length > 0) ?? "-";
}

/// <summary>A room's heights, pictures, brightness, effect and other fields.</summary>
public sealed record RoomCopy(double Floor, double Ceiling, string FloorTexture, string CeilingTexture, int Light, int Special, KeyValuePair<string, UdmfValue>[] Fields) : Copied;

/// <summary>One face's pictures, how they are shifted, and its other fields (scale, lighting).</summary>
public sealed record FaceCopy(FacePictures Face, double OffsetX, double OffsetY, KeyValuePair<string, UdmfValue>[] Fields) : Copied;

/// <summary>A wall's pictures on each face and how its upper and lower pictures are anchored (not offsets, flags that block, or its action: Eureka's choice).</summary>
public sealed record WallCopy(FacePictures Front, FacePictures? Back, bool UpperHangs, bool LowerContinues) : Copied;

/// <summary>An object's kind, height, action and flags (not where it stands, which way it faces, or its id).</summary>
public sealed record ObjectCopy(int Type, double Height, int Special, int[] Args, KeyValuePair<string, UdmfValue>[] Fields) : Copied;
