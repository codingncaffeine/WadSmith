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

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public string Top { get; set; } = "-";

    public string Middle { get; set; } = "-";

    public string Bottom { get; set; } = "-";
}

/// <summary>A wall (a line): from one corner to another, with a front face and maybe a back face.</summary>
public sealed class Line : Element
{
    public Line(Vertex start, Vertex end, Side front)
    {
        Start = start;
        End = end;
        Front = front;
    }

    public Vertex Start { get; set; }

    public Vertex End { get; set; }

    public Side Front { get; set; }

    public Side? Back { get; set; }

    public int Special { get; set; }

    public int[] Args { get; } = new int[5];

    /// <summary>The line's id, -1 for none.</summary>
    public int LineId { get; set; } = -1;

    public double Length => Math.Sqrt((End.X - Start.X) * (End.X - Start.X) + (End.Y - Start.Y) * (End.Y - Start.Y));
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
