using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Editor.Model;

/// <summary>
/// Anything on the map that can be selected. <see cref="Id"/> never changes while the map is open,
/// so selections, the undo history and smart objects can hold on to an element while others are
/// added or removed around it.
/// </summary>
public abstract class Element
{
    private static long _nextId;

    protected Element() => Id = Interlocked.Increment(ref _nextId);

    public long Id { get; }

    /// <summary>Where the element sits in the map's <see cref="MapFrame"/> lists; -1 when it is not on the map.</summary>
    public int Slot { get; internal set; } = -1;

    /// <summary>Whether the element is on the map (an undone addition is not).</summary>
    public bool OnMap => Slot >= 0;

    /// <summary>Every field the editor has no typed property for, kept in order.</summary>
    public UdmfFields Extra { get; } = new();

    public bool Flag(string name) => Extra.TryGet(name, out var v) && v.Kind == UdmfValueKind.Boolean && v.AsBool();

    public void SetFlag(string name, bool on)
    {
        if (on) Extra[name] = true;
        else Extra.Remove(name);
    }
}

/// <summary>A corner.</summary>
public sealed class Vertex : Element
{
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>A room's area (a sector): its floor, ceiling, light and type.</summary>
public sealed class Sector : Element
{
    public double FloorHeight { get; set; }

    public double CeilingHeight { get; set; }

    public string FloorTexture { get; set; } = "-";

    public string CeilingTexture { get; set; } = "-";

    public int Light { get; set; } = 160;

    public int Special { get; set; }

    /// <summary>The sector's tag, 0 for none.</summary>
    public int Tag { get; set; }
}

/// <summary>One face of a wall: its textures and their offsets, and the area it faces.</summary>
public sealed class Side : Element
{
    public Side(Sector sector) => Sector = sector;

    public Sector Sector { get; set; }

    /// <summary>The wall this face belongs to (each face belongs to one wall); null while no wall holds it.</summary>
    public Line? Line { get; internal set; }

    /// <summary>
    /// The face this one was copied from when the map was read: classic maps may share one face among
    /// several walls, and the editor gives each wall its own. While the copy still matches the
    /// original, the file keeps them as one (so an unchanged map writes back as it was read).
    /// </summary>
    public Side? PackedWith { get; internal set; }

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public string Top { get; set; } = "-";

    public string Middle { get; set; } = "-";

    public string Bottom { get; set; } = "-";
}

/// <summary>A wall (a line): from one corner to another, with a front face and maybe a back face.</summary>
public sealed class Line : Element
{
    private Side _front;
    private Side? _back;

    public Line(Vertex start, Vertex end, Side front)
    {
        Start = start;
        End = end;
        _front = front;
        front.Line = this;
    }

    public Vertex Start { get; set; }

    public Vertex End { get; set; }

    /// <summary>The face on the right going from <see cref="Start"/> to <see cref="End"/>.</summary>
    public Side Front
    {
        get => _front;
        set => _front = Adopt(_front, value, _back)!;
    }

    /// <summary>The face on the left, null for a wall with a room on one side only.</summary>
    public Side? Back
    {
        get => _back;
        set => _back = Adopt(_back, value, _front);
    }

    public int Special { get; set; }

    public int[] Args { get; } = new int[5];

    /// <summary>The line's id, -1 for none.</summary>
    public int LineId { get; set; } = -1;

    public double Length => Math.Sqrt((End.X - Start.X) * (End.X - Start.X) + (End.Y - Start.Y) * (End.Y - Start.Y));

    /// <summary>The other face of the wall from <paramref name="side"/>, null when there is none.</summary>
    public Side? Opposite(Side side) => ReferenceEquals(side, _front) ? _back : ReferenceEquals(side, _back) ? _front : null;

    /// <summary>Points a new face at this wall, and an old one it no longer holds at none.</summary>
    private Side? Adopt(Side? old, Side? value, Side? other)
    {
        if (old is not null && !ReferenceEquals(old, value) && !ReferenceEquals(old, other) && ReferenceEquals(old.Line, this)) old.Line = null;
        if (value is not null) value.Line = this;
        return value;
    }
}

/// <summary>A monster, item, decoration, light or start spot.</summary>
public sealed class Thing : Element
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Height { get; set; }

    public int Angle { get; set; }

    public int Type { get; set; }

    public int Special { get; set; }

    public int[] Args { get; } = new int[5];

    /// <summary>The thing's id (tid), 0 for none.</summary>
    public int Tid { get; set; }
}
