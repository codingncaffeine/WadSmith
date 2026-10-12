namespace WadSmith.Core.Containers;

/// <summary>A WAD, a PK3 or a folder: an ordered list of named resources.</summary>
public abstract class ResourceContainer
{
    private Dictionary<(ResourceNamespace, string), ResourceEntry>? _byName;

    /// <summary>The file or folder this came from, if any.</summary>
    public abstract string? Path { get; }

    /// <summary>A short label for messages: the file name.</summary>
    public string DisplayName => Path is null ? "(in memory)" : System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));

    public abstract IReadOnlyList<ResourceEntry> Entries { get; }

    /// <summary>Problems found while reading that did not stop the rest of the file from loading.</summary>
    public IReadOnlyList<string> Warnings => WarningList;

    protected List<string> WarningList { get; } = [];

    /// <summary>The last entry with this name in this namespace, as the engines resolve duplicates.</summary>
    public ResourceEntry? Find(string name, ResourceNamespace ns = ResourceNamespace.Global)
    {
        _byName ??= BuildIndex();
        return _byName.GetValueOrDefault((ns, LumpName.Normalize(name)));
    }

    /// <summary>The entry with this exact path (PK3, folder) or lump name (WAD), last one wins.</summary>
    public ResourceEntry? FindPath(string fullPath)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Entries[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return Entries[i];
        }

        return null;
    }

    private Dictionary<(ResourceNamespace, string), ResourceEntry> BuildIndex()
    {
        var index = new Dictionary<(ResourceNamespace, string), ResourceEntry>();
        foreach (var e in Entries)
        {
            if (e.IsMarker) continue;
            index[(e.Namespace, LumpName.Normalize(e.Name))] = e;
        }

        return index;
    }

    /// <summary>Opens a WAD, a PK3/ZIP or a folder, deciding by content rather than by extension.</summary>
    public static ResourceContainer Open(string path)
    {
        if (Directory.Exists(path)) return new FolderContainer(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no file at {path}.", path);

        Span<byte> magic = stackalloc byte[4];
        using (var f = File.OpenRead(path))
        {
            if (f.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4) throw new CorruptDataException($"{System.IO.Path.GetFileName(path)} is too short to be a WAD or a PK3.");
        }

        if (magic.SequenceEqual("IWAD"u8) || magic.SequenceEqual("PWAD"u8)) return WadContainer.Open(path);
        if (magic[0] == 'P' && magic[1] == 'K') return ZipContainer.Open(path);
        if (magic.SequenceEqual("ZWAD"u8)) throw new CorruptDataException($"{System.IO.Path.GetFileName(path)} is a compressed WAD, which no current engine reads.");
        if (magic[0] == '7' && magic[1] == 'z') throw new CorruptDataException($"{System.IO.Path.GetFileName(path)} is a 7-Zip archive (PK7). Re-pack it as a ZIP/PK3 to open it.");
        throw new CorruptDataException($"{System.IO.Path.GetFileName(path)} is not a WAD, PK3 or ZIP file.");
    }
}
