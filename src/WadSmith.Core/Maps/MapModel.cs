using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Core.Maps;

/// <summary>
/// A map in UDMF terms, whatever format it came from. The fields every map has are typed; every
/// other field (flags, scales, colours, comments, mod-specific keys) is kept in <c>Extra</c> in its
/// original order, so nothing is lost on the way through. What a field means depends on
/// <see cref="Namespace"/> (zdoom, doom, heretic, hexen, strife).
/// </summary>
public sealed class MapModel
{
    public required string Name { get; set; }

    public required string Namespace { get; set; }

    public List<MapVertex> Vertices { get; } = [];

    public List<MapLine> Lines { get; } = [];

    public List<MapSide> Sides { get; } = [];

    public List<MapSector> Sectors { get; } = [];

    public List<MapThing> Things { get; } = [];

    /// <summary>Top-level assignments other than the namespace.</summary>
    public UdmfFields Globals { get; } = new();

    /// <summary>Blocks of types this model does not know, kept as they were.</summary>
    public List<UdmfBlock> OtherBlocks { get; } = [];

    /// <summary>Lumps stored with the map that are not its geometry: BEHAVIOR, SCRIPTS, DIALOGUE, ZNODES …</summary>
    public List<(string Name, byte[] Data)> OtherLumps { get; } = [];

    /// <summary>The lump order of the classic map this came from, so writing it back keeps the order.</summary>
    internal List<string> ClassicLumpOrder { get; } = [];
}

public sealed class MapVertex
{
    public double X { get; set; }

    public double Y { get; set; }

    public UdmfFields Extra { get; } = new();
}

/// <summary>A line (linedef). Flags are boolean fields in <see cref="Extra"/>; see <see cref="Flag"/>.</summary>
public sealed class MapLine
{
    public int V1 { get; set; }

    public int V2 { get; set; }

    /// <summary>Index of the front side; every line has one.</summary>
    public int Front { get; set; }

    /// <summary>Index of the back side, or -1 for a one-sided line.</summary>
    public int Back { get; set; } = -1;

    public int Special { get; set; }

    public int[] Args { get; } = new int[5];

    /// <summary>The line's id (its tag in the Doom namespaces), or -1 for none.</summary>
    public int Id { get; set; } = -1;

    public UdmfFields Extra { get; } = new();

    /// <summary>
    /// The flag word this line was read from in a classic map, so an unchanged line writes back the
    /// same bits even where the map model has no name for them. Never written to UDMF.
    /// </summary>
    public int? SourceFlags { get; set; }

    public bool Flag(string name) => Extra.TryGet(name, out var v) && v.AsBool();

    public void SetFlag(string name, bool on)
    {
        if (on) Extra[name] = true;
        else Extra.Remove(name);
    }
}

public sealed class MapSide
{
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    /// <summary>"-" means no texture.</summary>
    public string Top { get; set; } = "-";

    public string Bottom { get; set; } = "-";

    public string Middle { get; set; } = "-";

    public int Sector { get; set; }

    public UdmfFields Extra { get; } = new();

    /// <summary>The raw name fields of a classic map, kept so an unchanged side writes back byte-identical.</summary>
    internal (Containers.NameField Top, Containers.NameField Bottom, Containers.NameField Middle)? SourceNames { get; set; }
}

public sealed class MapSector
{
    public double FloorHeight { get; set; }

    public double CeilingHeight { get; set; }

    public string FloorTexture { get; set; } = "-";

    public string CeilingTexture { get; set; } = "-";

    public int Light { get; set; } = 160;

    public int Special { get; set; }

    /// <summary>The sector's tag, 0 for none.</summary>
    public int Id { get; set; }

    public UdmfFields Extra { get; } = new();

    internal (Containers.NameField Floor, Containers.NameField Ceiling)? SourceNames { get; set; }
}

/// <summary>A thing. Flags (skills, modes, ambush …) are boolean fields in <see cref="Extra"/>.</summary>
public sealed class MapThing
{
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Height above the floor (or below the ceiling for things that hang).</summary>
    public double Height { get; set; }

    public int Angle { get; set; }

    /// <summary>The editor number: which actor this is.</summary>
    public int Type { get; set; }

    public int Special { get; set; }

    public int[] Args { get; } = new int[5];

    /// <summary>The thing's id (tid), 0 for none.</summary>
    public int Id { get; set; }

    public UdmfFields Extra { get; } = new();

    /// <inheritdoc cref="MapLine.SourceFlags"/>
    public int? SourceFlags { get; set; }

    public bool Flag(string name) => Extra.TryGet(name, out var v) && v.AsBool();

    public void SetFlag(string name, bool on)
    {
        if (on) Extra[name] = true;
        else Extra.Remove(name);
    }
}
