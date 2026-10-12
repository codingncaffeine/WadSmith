using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Editor.Model;

/// <summary>
/// The map being edited, as an object graph: lines point at their corners and faces, faces at their
/// areas. Built from a <see cref="MapModel"/> and written back to one. Changed only on the UI thread,
/// through the history; <see cref="Version"/> counts every change so readers know when to refresh, and
/// after each change <see cref="Frame"/> holds a read-only copy for other threads, updated only where
/// the change touched it. The element lists change only through the map itself.
/// </summary>
public sealed class EditorMap
{
    private MapFrame? _frame;

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

    /// <summary>The map as of <see cref="Version"/>, for any thread to read.</summary>
    public MapFrame Frame => Volatile.Read(ref _frame) ?? throw new InvalidOperationException("The map has no frame yet.");

    /// <summary>Records a change to <paramref name="touched"/>: a new version and a frame with their new values.</summary>
    internal void Touch(IEnumerable<Element> touched)
    {
        Version++;
        var f = Frame;
        ChunkedList<VertexFrame>.Builder? vertices = null;
        ChunkedList<LineFrame>.Builder? lines = null;
        ChunkedList<SideFrame>.Builder? sides = null;
        ChunkedList<SectorFrame>.Builder? sectors = null;
        ChunkedList<ThingFrame>.Builder? things = null;
        foreach (var e in touched)
        {
            if (!e.OnMap) continue;
            switch (e)
            {
                case Vertex v:
                    (vertices ??= f.Vertices.ToBuilder())[v.Slot] = MapFrame.Of(v);
                    break;
                case Line l:
                    (lines ??= f.Lines.ToBuilder())[l.Slot] = MapFrame.Of(l);
                    break;
                case Side sd:
                    (sides ??= f.Sides.ToBuilder())[sd.Slot] = MapFrame.Of(sd);
                    break;
                case Sector sc:
                    (sectors ??= f.Sectors.ToBuilder())[sc.Slot] = MapFrame.Of(sc);
                    break;
                case Thing t:
                    (things ??= f.Things.ToBuilder())[t.Slot] = MapFrame.Of(t);
                    break;
            }
        }

        Volatile.Write(ref _frame, new MapFrame
        {
            Version = Version,
            Name = Name,
            Namespace = Namespace,
            Globals = MapFrame.Copy(Globals),
            OtherBlocks = f.OtherBlocks,
            OtherLumps = f.OtherLumps,
            Vertices = vertices?.ToList() ?? f.Vertices,
            Lines = lines?.ToList() ?? f.Lines,
            Sides = sides?.ToList() ?? f.Sides,
            Sectors = sectors?.ToList() ?? f.Sectors,
            Things = things?.ToList() ?? f.Things,
        });
        Changed?.Invoke(Version);
    }

    /// <summary>Numbers every element by its place and takes the first frame.</summary>
    private void TakeFirstFrame()
    {
        static void Number<TElement>(List<TElement> list)
            where TElement : Element
        {
            for (var i = 0; i < list.Count; i++) list[i].Slot = i;
        }

        Number(Vertices);
        Number(Sectors);
        Number(Sides);
        Number(Lines);
        Number(Things);
        Volatile.Write(ref _frame, FrameFromScratch());
    }

    /// <summary>A frame built from every element (what <see cref="Touch"/> keeps up to date piece by piece).</summary>
    internal MapFrame FrameFromScratch()
    {
        static ChunkedList<TFrame> Build<TElement, TFrame>(List<TElement> list, Func<TElement, TFrame> of)
        {
            var b = ChunkedList<TFrame>.Empty.ToBuilder();
            foreach (var e in list) b.Add(of(e));
            return b.ToList();
        }

        return new MapFrame
        {
            Version = Version,
            Name = Name,
            Namespace = Namespace,
            Globals = MapFrame.Copy(Globals),
            OtherBlocks = [.. OtherBlocks],
            OtherLumps = [.. OtherLumps],
            Vertices = Build(Vertices, MapFrame.Of),
            Lines = Build(Lines, MapFrame.Of),
            Sides = Build(Sides, MapFrame.Of),
            Sectors = Build(Sectors, MapFrame.Of),
            Things = Build(Things, MapFrame.Of),
        };
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

        e.TakeFirstFrame();
        return e;
    }

    /// <summary>The map as the index-based model the file formats use, in the editor's current order.</summary>
    public MapModel ToModel() => Frame.ToModel();

    private static void Copy(UdmfFields from, UdmfFields to)
    {
        foreach (var (k, v) in from) to[k] = v;
    }
}
