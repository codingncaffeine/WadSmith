using System.Buffers.Binary;

namespace WadSmith.Core.Containers;

public enum WadKind
{
    /// <summary>A base game's own data file (DOOM2.WAD, HERETIC.WAD …).</summary>
    Iwad,
    /// <summary>A patch WAD: maps and resources loaded on top of a base game.</summary>
    Pwad,
}

/// <summary>
/// A WAD file held in memory: a 12-byte header, lump data, and a directory of 16-byte entries
/// (offset, size, eight-byte name).
/// </summary>
public sealed class WadContainer : ResourceContainer
{
    public const int HeaderSize = 12;
    public const int DirectoryEntrySize = 16;

    private readonly byte[] _data;
    private readonly List<ResourceEntry> _entries = [];

    private WadContainer(byte[] data, string? path)
    {
        _data = data;
        Path = path;
        Parse();
    }

    public override string? Path { get; }

    public WadKind Kind { get; private set; }

    public override IReadOnlyList<ResourceEntry> Entries => _entries;

    /// <summary>The raw bytes of the whole file.</summary>
    public ReadOnlyMemory<byte> Bytes => _data;

    public static new WadContainer Open(string path) => new(File.ReadAllBytes(path), path);

    public static WadContainer FromBytes(byte[] data, string? path = null) => new(data, path);

    /// <summary>A lump's bytes without copying them.</summary>
    public ReadOnlyMemory<byte> LumpData(ResourceEntry entry)
    {
        var (offset, size) = _spans[entry.Index];
        return _data.AsMemory(offset, size);
    }

    private readonly List<(int Offset, int Size)> _spans = [];

    private void Parse()
    {
        var name = Path is null ? "This WAD" : System.IO.Path.GetFileName(Path);
        if (_data.Length < HeaderSize) throw new CorruptDataException($"{name} is too short to be a WAD.");

        var ident = _data.AsSpan(0, 4);
        if (ident.SequenceEqual("IWAD"u8)) Kind = WadKind.Iwad;
        else if (ident.SequenceEqual("PWAD"u8)) Kind = WadKind.Pwad;
        else throw new CorruptDataException($"{name} does not start with IWAD or PWAD, so it is not a WAD.");

        var count = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(4));
        var dirOffset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(8));
        if (count < 0) throw new CorruptDataException($"{name} says it holds a negative number of lumps.");
        if (dirOffset < 0 || (long)dirOffset + (long)count * DirectoryEntrySize > _data.Length)
            throw new CorruptDataException($"{name}'s lump directory runs past the end of the file; the file is probably cut short.");

        var markers = new MarkerTracker();
        for (var i = 0; i < count; i++)
        {
            var e = _data.AsSpan(dirOffset + i * DirectoryEntrySize, DirectoryEntrySize);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(e);
            var size = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
            var lumpName = LumpName.Read(e[8..]);

            if (size < 0)
            {
                WarningList.Add($"Lump {i} ({lumpName}) has a negative size; it was read as empty.");
                size = 0;
            }

            if (size > 0 && (offset < 0 || (long)offset + size > _data.Length))
            {
                WarningList.Add($"Lump {i} ({lumpName}) runs past the end of the file; it was read as empty.");
                size = 0;
            }

            if (size == 0) offset = 0;

            var (ns, isMarker) = markers.Classify(lumpName, size);
            var index = i;
            _spans.Add((offset, size));
            _entries.Add(new ResourceEntry(this, index, lumpName, lumpName, ns, size, isMarker, () => _data.AsSpan(_spans[index].Offset, _spans[index].Size).ToArray()));
        }

        foreach (var w in markers.Unclosed()) WarningList.Add(w);
    }

    /// <summary>
    /// Follows the marker pairs that put lumps in namespaces. Both the one- and two-letter forms are
    /// accepted (<c>F_START</c>/<c>FF_START</c>, either end closes either start), the numbered
    /// sub-markers inside (<c>F1_START</c>) are ignored, and a namespace left open runs to the end of
    /// the file, as the engines read it.
    /// </summary>
    private sealed class MarkerTracker
    {
        private static readonly (string Letters, ResourceNamespace Ns)[] Kinds =
        [
            ("S", ResourceNamespace.Sprites),
            ("F", ResourceNamespace.Flats),
            ("P", ResourceNamespace.Patches),
            ("TX", ResourceNamespace.Textures),
            ("HI", ResourceNamespace.HiRes),
            ("C", ResourceNamespace.Colormaps),
            ("A", ResourceNamespace.Acs),
            ("V", ResourceNamespace.Voices),
            ("VX", ResourceNamespace.Voxels),
        ];

        private ResourceNamespace _current = ResourceNamespace.Global;
        private string? _openedBy;

        public (ResourceNamespace Ns, bool IsMarker) Classify(string name, int size)
        {
            if (size == 0 && TryMarker(name, out var ns, out var isStart, out var isSub))
            {
                if (isSub) return (_current, true);
                if (isStart)
                {
                    _current = ns;
                    _openedBy = name;
                }
                else if (_current == ns)
                {
                    _current = ResourceNamespace.Global;
                    _openedBy = null;
                }

                return (ResourceNamespace.Global, true);
            }

            return (_current, false);
        }

        public IEnumerable<string> Unclosed()
        {
            if (_openedBy is not null) yield return $"{_openedBy} is never closed; everything after it was read as part of that group.";
        }

        private static bool TryMarker(string name, out ResourceNamespace ns, out bool isStart, out bool isSub)
        {
            ns = ResourceNamespace.Global;
            isStart = false;
            isSub = false;
            var upper = LumpName.Normalize(name);
            string stem;
            if (upper.EndsWith("_START", StringComparison.Ordinal))
            {
                isStart = true;
                stem = upper[..^6];
            }
            else if (upper.EndsWith("_END", StringComparison.Ordinal))
            {
                stem = upper[..^4];
            }
            else
            {
                return false;
            }

            foreach (var (letters, kind) in Kinds)
            {
                // S_START and SS_START; F_START, FF_START; and the numbered F1_START, F2_START, F3_START.
                if (stem == letters || (letters.Length == 1 && stem == letters + letters))
                {
                    ns = kind;
                    return true;
                }

                if (letters.Length == 1 && stem.Length == 2 && stem[0] == letters[0] && char.IsAsciiDigit(stem[1]))
                {
                    ns = kind;
                    isSub = true;
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>Writes WAD files: header, lump data in order, then the directory.</summary>
public static class WadWriter
{
    public static byte[] Write(WadKind kind, IEnumerable<(string Name, ReadOnlyMemory<byte> Data)> lumps)
    {
        var list = lumps.ToList();
        foreach (var (name, _) in list) LumpName.Validate(name);

        long total = WadContainer.HeaderSize;
        foreach (var (_, data) in list) total += data.Length;
        var dirOffset = total;
        total += (long)list.Count * WadContainer.DirectoryEntrySize;
        if (total > int.MaxValue) throw new InvalidOperationException("A WAD cannot be larger than 2 GB.");

        var output = new byte[total];
        (kind == WadKind.Iwad ? "IWAD"u8 : "PWAD"u8).CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), list.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), (int)dirOffset);

        var pos = WadContainer.HeaderSize;
        var dir = (int)dirOffset;
        foreach (var (name, data) in list)
        {
            data.Span.CopyTo(output.AsSpan(pos));
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(dir), data.Length == 0 ? 0 : pos);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(dir + 4), data.Length);
            LumpName.Write(name, output.AsSpan(dir + 8, LumpName.Length));
            pos += data.Length;
            dir += WadContainer.DirectoryEntrySize;
        }

        return output;
    }
}

/// <summary>Reads only a WAD's lump names (header and directory), without loading its data.</summary>
public static class WadDirectory
{
    /// <summary>The lump names of the WAD at <paramref name="path"/>, or null when it is not a readable WAD.</summary>
    public static IReadOnlyList<string>? ReadNames(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> header = stackalloc byte[WadContainer.HeaderSize];
            if (f.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return null;
            if (!header[..4].SequenceEqual("IWAD"u8) && !header[..4].SequenceEqual("PWAD"u8)) return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var dir = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            if (count < 0 || count > 1_000_000 || dir < 0 || (long)dir + (long)count * WadContainer.DirectoryEntrySize > f.Length) return null;
            var buffer = new byte[count * WadContainer.DirectoryEntrySize];
            f.Seek(dir, SeekOrigin.Begin);
            f.ReadExactly(buffer);
            var names = new string[count];
            for (var i = 0; i < count; i++) names[i] = LumpName.Read(buffer.AsSpan(i * WadContainer.DirectoryEntrySize + 8, 8));
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }
}
