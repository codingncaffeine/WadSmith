using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Editor.Model;

/// <summary>
/// The map being edited, as an object graph: lines point at their corners and faces, faces at their
/// areas. Built from a <see cref="MapModel"/> and written back to one. Changed only on the UI thread,
/// through the history; <see cref="Version"/> counts every change so readers know when to refresh.
/// </summary>
public sealed class EditorMap
{
    public required string Name { get; set; }

    public required string Namespace { get; set; }

    public List<Vertex> Vertices { get; } = [];

    public List<Line> Lines { get; } = [];

    public List<Side> Sides { get; } = [];

    public List<Sector> Sectors { get; } = [];

    public List<Thing> Things { get; } = [];

    public UdmfFields Globals { get; } = new();

    public List<UdmfBlock> OtherBlocks { get; } = [];

    public List<(string Name, byte[] Data)> OtherLumps { get; } = [];

    /// <summary>Goes up by one with every change.</summary>
    public long Version { get; private set; }

    /// <summary>Raised after every change, with the new version.</summary>
    public event Action<long>? Changed;

    internal void Touch()
    {
        Version++;
        Changed?.Invoke(Version);
    }

    public static EditorMap FromModel(MapModel m)
    {
        var e = new EditorMap { Name = m.Name, Namespace = m.Namespace };
        foreach (var (k, v) in m.Globals) e.Globals[k] = v;
        e.OtherBlocks.AddRange(m.OtherBlocks);
        e.OtherLumps.AddRange(m.OtherLumps);

        foreach (var v in m.Vertices)
        {
            var x = new Vertex { X = v.X, Y = v.Y };
            Copy(v.Extra, x.Extra);
            e.Vertices.Add(x);
        }

        foreach (var s in m.Sectors)
        {
            var x = new Sector { FloorHeight = s.FloorHeight, CeilingHeight = s.CeilingHeight, FloorTexture = s.FloorTexture, CeilingTexture = s.CeilingTexture, Light = s.Light, Special = s.Special, Tag = s.Id };
            Copy(s.Extra, x.Extra);
            e.Sectors.Add(x);
        }

        // A face pointing at a missing area is kept, attached to a placeholder, so nothing is lost;
        // the checks report it.
        Sector? orphan = null;
        foreach (var s in m.Sides)
        {
            var sector = s.Sector >= 0 && s.Sector < e.Sectors.Count ? e.Sectors[s.Sector] : orphan ??= new Sector();
            var x = new Side(sector) { OffsetX = s.OffsetX, OffsetY = s.OffsetY, Top = s.Top, Middle = s.Middle, Bottom = s.Bottom };
            Copy(s.Extra, x.Extra);
            e.Sides.Add(x);
        }

        foreach (var l in m.Lines)
        {
            if (l.V1 < 0 || l.V1 >= e.Vertices.Count || l.V2 < 0 || l.V2 >= e.Vertices.Count || l.Front < 0 || l.Front >= e.Sides.Count)
                throw new Core.CorruptDataException($"{m.Name} has a line that points at a missing corner or face; run validate on it.");
            var x = new Line(e.Vertices[l.V1], e.Vertices[l.V2], e.Sides[l.Front])
            {
                Back = l.Back >= 0 && l.Back < e.Sides.Count ? e.Sides[l.Back] : null,
                Special = l.Special,
                LineId = l.Id,
            };
            l.Args.CopyTo(x.Args, 0);
            Copy(l.Extra, x.Extra);
            e.Lines.Add(x);
        }

        foreach (var t in m.Things)
        {
            var x = new Thing { X = t.X, Y = t.Y, Height = t.Height, Angle = t.Angle, Type = t.Type, Special = t.Special, Tid = t.Id };
            t.Args.CopyTo(x.Args, 0);
            Copy(t.Extra, x.Extra);
            e.Things.Add(x);
        }

        return e;
    }

    /// <summary>The map as the index-based model the file formats use, in the editor's current order.</summary>
    public MapModel ToModel()
    {
        var m = new MapModel { Name = Name, Namespace = Namespace };
        foreach (var (k, v) in Globals) m.Globals[k] = v;
        m.OtherBlocks.AddRange(OtherBlocks);
        m.OtherLumps.AddRange(OtherLumps);

        var vIndex = Index(Vertices);
        var sIndex = Index(Sectors);
        var sideIndex = Index(Sides);

        foreach (var v in Vertices)
        {
            var x = new MapVertex { X = v.X, Y = v.Y };
            Copy(v.Extra, x.Extra);
            m.Vertices.Add(x);
        }

        foreach (var s in Sectors)
        {
            var x = new MapSector { FloorHeight = s.FloorHeight, CeilingHeight = s.CeilingHeight, FloorTexture = s.FloorTexture, CeilingTexture = s.CeilingTexture, Light = s.Light, Special = s.Special, Id = s.Tag };
            Copy(s.Extra, x.Extra);
            m.Sectors.Add(x);
        }

        foreach (var s in Sides)
        {
            var x = new MapSide { OffsetX = s.OffsetX, OffsetY = s.OffsetY, Top = s.Top, Middle = s.Middle, Bottom = s.Bottom, Sector = sIndex.GetValueOrDefault(s.Sector, -1) };
            Copy(s.Extra, x.Extra);
            m.Sides.Add(x);
        }

        foreach (var l in Lines)
        {
            var x = new MapLine
            {
                V1 = vIndex[l.Start],
                V2 = vIndex[l.End],
                Front = sideIndex[l.Front],
                Back = l.Back is null ? -1 : sideIndex[l.Back],
                Special = l.Special,
                Id = l.LineId,
            };
            l.Args.CopyTo(x.Args, 0);
            Copy(l.Extra, x.Extra);
            m.Lines.Add(x);
        }

        foreach (var t in Things)
        {
            var x = new MapThing { X = t.X, Y = t.Y, Height = t.Height, Angle = t.Angle, Type = t.Type, Special = t.Special, Id = t.Tid };
            t.Args.CopyTo(x.Args, 0);
            Copy(t.Extra, x.Extra);
            m.Things.Add(x);
        }

        return m;
    }

    private static Dictionary<T, int> Index<T>(List<T> list)
        where T : notnull
    {
        var d = new Dictionary<T, int>(list.Count, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        for (var i = 0; i < list.Count; i++) d[list[i]] = i;
        return d;
    }

    private static void Copy(UdmfFields from, UdmfFields to)
    {
        foreach (var (k, v) in from) to[k] = v;
    }
}
