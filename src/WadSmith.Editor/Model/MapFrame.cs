using System.Collections;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Editor.Model;

/// <summary>
/// A read-only list stored in chunks, so a changed copy shares every chunk it did not change: after an
/// edit, publishing a new list copies one chunk per changed element instead of the whole list.
/// </summary>
public sealed class ChunkedList<T> : IReadOnlyList<T>
{
    internal const int Shift = 10;
    internal const int Size = 1 << Shift;
    internal const int Mask = Size - 1;

    private readonly T[][] _chunks;

    internal ChunkedList(T[][] chunks, int count)
    {
        _chunks = chunks;
        Count = count;
    }

    public static ChunkedList<T> Empty { get; } = new([], 0);

    public int Count { get; }

    public T this[int index] => (uint)index < (uint)Count ? _chunks[index >> Shift][index & Mask] : throw new ArgumentOutOfRangeException(nameof(index));

    public Builder ToBuilder() => new(_chunks, Count);

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return _chunks[i >> Shift][i & Mask];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Changes a list without touching the original: each chunk is copied the first time it is written.</summary>
    public sealed class Builder
    {
        private T[][] _chunks;
        private bool[] _owned;
        private int _count;

        internal Builder(T[][] chunks, int count)
        {
            _chunks = (T[][])chunks.Clone();
            _owned = new bool[chunks.Length];
            _count = count;
        }

        public int Count => _count;

        public T this[int index]
        {
            get => _chunks[index >> Shift][index & Mask];
            set
            {
                if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
                var c = index >> Shift;
                if (!_owned[c])
                {
                    _chunks[c] = (T[])_chunks[c].Clone();
                    _owned[c] = true;
                }

                _chunks[c][index & Mask] = value;
            }
        }

        public void Add(T item)
        {
            var c = _count >> Shift;
            if (c == _chunks.Length)
            {
                Array.Resize(ref _chunks, c + 1);
                Array.Resize(ref _owned, c + 1);
                _chunks[c] = new T[Size];
                _owned[c] = true;
            }

            _count++;
            this[_count - 1] = item;
        }

        /// <summary>The list as it is now; later writes to the builder copy their chunks again.</summary>
        public ChunkedList<T> ToList()
        {
            Array.Clear(_owned);
            return new ChunkedList<T>((T[][])_chunks.Clone(), _count);
        }
    }
}

/// <summary>A corner as it was when the frame was taken. <see cref="Element"/> is null once removed.</summary>
public readonly record struct VertexFrame(Vertex? Element, double X, double Y, KeyValuePair<string, UdmfValue>[] Extra);

public readonly record struct SectorFrame(Sector? Element, double FloorHeight, double CeilingHeight, string FloorTexture, string CeilingTexture, int Light, int Special, int Tag, KeyValuePair<string, UdmfValue>[] Extra);

/// <summary>A face; <see cref="Sector"/> is the area's slot, -1 when it faces nothing on the map.</summary>
public readonly record struct SideFrame(Side? Element, int Sector, double OffsetX, double OffsetY, string Top, string Middle, string Bottom, KeyValuePair<string, UdmfValue>[] Extra);

/// <summary>A wall; corners and faces are slots, <see cref="Back"/> -1 for none.</summary>
public readonly record struct LineFrame(Line? Element, int V1, int V2, int Front, int Back, int Special, int[] Args, int LineId, KeyValuePair<string, UdmfValue>[] Extra);

public readonly record struct ThingFrame(Thing? Element, double X, double Y, double Height, int Angle, int Type, int Special, int[] Args, int Tid, KeyValuePair<string, UdmfValue>[] Extra);

/// <summary>
/// The map at one <see cref="Version"/>, as plain read-only data that any thread may read while the UI
/// thread keeps editing. Saving, versions, outlines, hit testing and the canvas picture all work from a
/// frame on their own threads. Elements live at fixed slots (their <see cref="Element.Slot"/>); a
/// removed element leaves an empty slot, so the others never move.
/// </summary>
public sealed class MapFrame
{
    public required long Version { get; init; }

    public required string Name { get; init; }

    public required string Namespace { get; init; }

    public required KeyValuePair<string, UdmfValue>[] Globals { get; init; }

    public required UdmfBlock[] OtherBlocks { get; init; }

    public required (string Name, byte[] Data)[] OtherLumps { get; init; }

    public required ChunkedList<VertexFrame> Vertices { get; init; }

    public required ChunkedList<LineFrame> Lines { get; init; }

    public required ChunkedList<SideFrame> Sides { get; init; }

    public required ChunkedList<SectorFrame> Sectors { get; init; }

    public required ChunkedList<ThingFrame> Things { get; init; }

    internal static KeyValuePair<string, UdmfValue>[] Copy(UdmfFields fields) => fields.Count == 0 ? [] : [.. fields];

    internal static VertexFrame Of(Vertex v) => new(v, v.X, v.Y, Copy(v.Extra));

    internal static SectorFrame Of(Sector s) => new(s, s.FloorHeight, s.CeilingHeight, s.FloorTexture, s.CeilingTexture, s.Light, s.Special, s.Tag, Copy(s.Extra));

    internal static SideFrame Of(Side s) => new(s, s.Sector.Slot, s.OffsetX, s.OffsetY, s.Top, s.Middle, s.Bottom, Copy(s.Extra));

    internal static LineFrame Of(Line l) => new(l, l.Start.Slot, l.End.Slot, l.Front.Slot, l.Back?.Slot ?? -1, l.Special, [.. l.Args], l.LineId, Copy(l.Extra));

    internal static ThingFrame Of(Thing t) => new(t, t.X, t.Y, t.Height, t.Angle, t.Type, t.Special, [.. t.Args], t.Tid, Copy(t.Extra));

    /// <summary>The map as the index-based model the file formats use; empty slots are left out.</summary>
    public MapModel ToModel()
    {
        var m = new MapModel { Name = Name, Namespace = Namespace };
        foreach (var (k, v) in Globals) m.Globals[k] = v;
        m.OtherBlocks.AddRange(OtherBlocks);
        m.OtherLumps.AddRange(OtherLumps);

        var vIndex = Compact(Vertices, v => v.Element is not null);
        var sectorIndex = Compact(Sectors, s => s.Element is not null);
        var sideIndex = Compact(Sides, s => s.Element is not null);

        foreach (var v in Vertices)
        {
            if (v.Element is null) continue;
            var x = new MapVertex { X = v.X, Y = v.Y };
            Fill(v.Extra, x.Extra);
            m.Vertices.Add(x);
        }

        foreach (var s in Sectors)
        {
            if (s.Element is null) continue;
            var x = new MapSector { FloorHeight = s.FloorHeight, CeilingHeight = s.CeilingHeight, FloorTexture = s.FloorTexture, CeilingTexture = s.CeilingTexture, Light = s.Light, Special = s.Special, Id = s.Tag };
            Fill(s.Extra, x.Extra);
            m.Sectors.Add(x);
        }

        foreach (var s in Sides)
        {
            if (s.Element is null) continue;
            var x = new MapSide { OffsetX = s.OffsetX, OffsetY = s.OffsetY, Top = s.Top, Middle = s.Middle, Bottom = s.Bottom, Sector = s.Sector >= 0 ? sectorIndex[s.Sector] : -1 };
            Fill(s.Extra, x.Extra);
            m.Sides.Add(x);
        }

        foreach (var l in Lines)
        {
            if (l.Element is null) continue;
            var x = new MapLine { V1 = vIndex[l.V1], V2 = vIndex[l.V2], Front = sideIndex[l.Front], Back = l.Back >= 0 ? sideIndex[l.Back] : -1, Special = l.Special, Id = l.LineId };
            l.Args.CopyTo(x.Args, 0);
            Fill(l.Extra, x.Extra);
            m.Lines.Add(x);
        }

        foreach (var t in Things)
        {
            if (t.Element is null) continue;
            var x = new MapThing { X = t.X, Y = t.Y, Height = t.Height, Angle = t.Angle, Type = t.Type, Special = t.Special, Id = t.Tid };
            t.Args.CopyTo(x.Args, 0);
            Fill(t.Extra, x.Extra);
            m.Things.Add(x);
        }

        return m;
    }

    /// <summary>Slot to file index, skipping empty slots (-1 for those).</summary>
    private static int[] Compact<T>(ChunkedList<T> list, Func<T, bool> present)
    {
        var map = new int[list.Count];
        var next = 0;
        for (var i = 0; i < list.Count; i++) map[i] = present(list[i]) ? next++ : -1;
        return map;
    }

    private static void Fill(KeyValuePair<string, UdmfValue>[] from, UdmfFields to)
    {
        foreach (var (k, v) in from) to[k] = v;
    }
}
