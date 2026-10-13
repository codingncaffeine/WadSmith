using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Where a turn, a resize or a mirror puts every point: x' = A·x + B·y + Tx, y' = C·x + D·y + Ty, in map
/// units (Y up, angles counterclockwise as the game's).
/// </summary>
public readonly record struct Placement(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Placement Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>A turn by <paramref name="degrees"/> counterclockwise round <paramref name="pivot"/>; quarter turns are exact.</summary>
    public static Placement Turn(double degrees, MapPoint pivot)
    {
        var quarter = degrees / 90;
        double cos, sin;
        if (Math.Abs(quarter - Math.Round(quarter)) < 1e-9)
        {
            (cos, sin) = (((int)Math.Round(quarter) % 4 + 4) % 4) switch { 0 => (1.0, 0.0), 1 => (0.0, 1.0), 2 => (-1.0, 0.0), _ => (0.0, -1.0) };
        }
        else
        {
            var r = degrees * Math.PI / 180;
            (cos, sin) = (Math.Cos(r), Math.Sin(r));
        }

        return Around(cos, -sin, sin, cos, pivot);
    }

    /// <summary>A resize by <paramref name="sx"/> across and <paramref name="sy"/> up and down, keeping <paramref name="pivot"/> still; a negative size mirrors.</summary>
    public static Placement Size(double sx, double sy, MapPoint pivot) => Around(sx, 0, 0, sy, pivot);

    private static Placement Around(double a, double b, double c, double d, MapPoint p) => new(a, b, c, d, p.X - a * p.X - b * p.Y, p.Y - c * p.X - d * p.Y);

    /// <summary>Whether the placement mirrors (every wall then has its rooms on the other hand).</summary>
    public bool Mirrors => A * D - B * C < 0;

    /// <summary>Whether it squashes everything onto a line or a point.</summary>
    public bool Flattens => Math.Abs(A * D - B * C) < 1e-9;

    /// <summary>
    /// Where a point goes, kept to the game's own precision (1/65536 of a unit), so turns that should land on
    /// whole units do.
    /// </summary>
    public MapPoint Apply(double x, double y) => new(Fixed(A * x + B * y + Tx), Fixed(C * x + D * y + Ty));

    /// <summary>The way an object facing <paramref name="angle"/> faces once placed, so it keeps facing what it faced.</summary>
    public int Facing(int angle)
    {
        var r = angle * Math.PI / 180;
        var (x, y) = (Math.Cos(r), Math.Sin(r));
        var turned = Math.Atan2(C * x + D * y, A * x + B * y) * 180 / Math.PI;
        return (((int)Math.Round(turned) % 360) + 360) % 360;
    }

    /// <summary>What the placement does, in the History panel's words.</summary>
    public string Describe()
    {
        if (Mirrors) return "Mirror";
        if (A == 1 && B == 0 && C == 0 && D == 1) return "Move";
        return Math.Abs(A * D - B * C - 1) < 1e-9 && Math.Abs(A * A + C * C - 1) < 1e-9 ? "Turn" : "Resize";
    }

    private static double Fixed(double v) => Math.Round(v * 65536) / 65536;
}

/// <summary>
/// Turns, resizes or mirrors a gathered selection from where it started: corners and objects are placed,
/// objects keep facing what they faced, and when the placement mirrors, the walls moving whole run the
/// other way so their rooms stay on their right. Every step of one drag merges into the first, so the drag
/// and the joins made when it is let go undo as one step.
/// </summary>
public sealed class TransformEdit : IEdit
{
    private readonly MoveSet _set;
    private readonly (double X, double Y)[] _vertexStart;
    private readonly (double X, double Y, int Angle)[] _thingStart;
    private readonly Line[] _whole;
    private readonly (Vertex Start, Vertex End)[] _ends;
    private readonly FlatCarry? _carry;
    private StructureEdit? _then;

    /// <summary>
    /// Reads where everything starts from the map: make it on the UI thread. Rooms placed whole carry their floor and ceiling
    /// textures, turned, sized and mirrored with them (<paramref name="flatSize"/>: a flat's size by name).
    /// </summary>
    public TransformEdit(MoveSet set, Placement placement, Func<string, (int Width, int Height)?>? flatSize = null)
    {
        _carry = FlatCarry.Of(set, flatSize);
        _set = set;
        _vertexStart = [.. set.Vertices.Select(v => (v.X, v.Y))];
        _thingStart = [.. set.Things.Select(t => (t.X, t.Y, t.Angle))];
        var moving = set.Vertices.ToHashSet(ReferenceEqualityComparer.Instance);
        _whole = [.. set.Lines.Where(l => l.OnMap && moving.Contains(l.Start) && moving.Contains(l.End))];
        _ends = [.. _whole.Select(l => (l.Start, l.End))];
        Placement = placement;
    }

    private TransformEdit(TransformEdit first, Placement placement)
    {
        _set = first._set;
        _vertexStart = first._vertexStart;
        _thingStart = first._thingStart;
        _whole = first._whole;
        _ends = first._ends;
        _carry = first._carry;
        Placement = placement;
    }

    public Placement Placement { get; private set; }

    public string Label => Placement.Describe();

    public IEnumerable<Element> Touched => _set.Vertices.Cast<Element>().Concat(_set.Things).Concat(_whole).Concat(_carry?.Rooms ?? []).Concat(_then?.Touched ?? []);

    /// <summary>The same edit placed another way from the same start (the next step of a drag).</summary>
    public TransformEdit To(Placement placement) => new(this, placement);

    public void Do(EditorMap map)
    {
        Place(Placement);
        _then?.Do(map);
    }

    public void Undo(EditorMap map)
    {
        _then?.Undo(map);
        Place(Placement.Identity);
    }

    public bool TryMerge(IEdit next)
    {
        if (next is StructureEdit join && _then is null)
        {
            _then = join;
            return true;
        }

        if (_then is not null || next is not TransformEdit t || !ReferenceEquals(t._set, _set)) return false;
        Placement = t.Placement;
        return true;
    }

    private void Place(Placement p)
    {
        for (var i = 0; i < _vertexStart.Length; i++)
        {
            var (x, y) = (_vertexStart[i].X, _vertexStart[i].Y);
            var to = p == Placement.Identity ? new MapPoint(x, y) : p.Apply(x, y);
            (_set.Vertices[i].X, _set.Vertices[i].Y) = (to.X, to.Y);
        }

        for (var i = 0; i < _thingStart.Length; i++)
        {
            var (x, y, angle) = _thingStart[i];
            var thing = _set.Things[i];
            if (p == Placement.Identity)
            {
                (thing.X, thing.Y, thing.Angle) = (x, y, angle);
                continue;
            }

            var to = p.Apply(x, y);
            (thing.X, thing.Y, thing.Angle) = (to.X, to.Y, p.Facing(angle));
        }

        // Every step of a drag places from the start, so each wall runs as it started or, mirrored, the other way.
        var mirrors = p.Mirrors;
        for (var i = 0; i < _whole.Length; i++) (_whole[i].Start, _whole[i].End) = mirrors ? (_ends[i].End, _ends[i].Start) : _ends[i];
        _carry?.Place(p);
    }
}
