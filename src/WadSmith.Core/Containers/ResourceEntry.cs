namespace WadSmith.Core.Containers;

/// <summary>One lump of a WAD, or one file of a PK3 or folder.</summary>
public sealed class ResourceEntry
{
    private readonly Func<byte[]> _read;

    internal ResourceEntry(ResourceContainer container, int index, string name, string fullPath, ResourceNamespace ns, long size, bool isMarker, Func<byte[]> read)
    {
        Container = container;
        Index = index;
        Name = name;
        FullPath = fullPath;
        Namespace = ns;
        Size = size;
        IsMarker = isMarker;
        _read = read;
    }

    public ResourceContainer Container { get; }

    /// <summary>Position in the container's own order.</summary>
    public int Index { get; }

    /// <summary>
    /// The short name used for lookups: the lump name in a WAD; in a PK3 the file name without its
    /// extension (it may be longer than eight characters there).
    /// </summary>
    public string Name { get; }

    /// <summary>The lump name in a WAD; the path with forward slashes in a PK3 or folder.</summary>
    public string FullPath { get; }

    public ResourceNamespace Namespace { get; }

    public long Size { get; }

    /// <summary>A zero-size namespace marker such as <c>F_START</c>.</summary>
    public bool IsMarker { get; }

    /// <summary>The content. Each call returns a fresh array the caller may keep or change.</summary>
    public byte[] Read() => _read();

    public override string ToString() => $"{FullPath} ({Namespace}, {Size} bytes)";
}
