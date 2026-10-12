using System.IO.Compression;

namespace WadSmith.Core.Containers;

/// <summary>
/// A PK3 (a ZIP archive laid out as the engine's resource tree). The archive is read into memory
/// once, so no file handle stays open and reads may come from any thread.
/// </summary>
public sealed class ZipContainer : ResourceContainer
{
    private readonly ZipArchive _archive;
    private readonly Lock _gate = new();
    private readonly List<ResourceEntry> _entries = [];

    private ZipContainer(byte[] data, string? path)
    {
        Path = path;
        try
        {
            _archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new CorruptDataException($"{DisplayName} is not a readable ZIP/PK3 archive: {ex.Message}", ex);
        }

        var index = 0;
        foreach (var z in _archive.Entries)
        {
            var full = z.FullName.Replace('\\', '/');
            if (full.EndsWith('/')) continue; // archive path
            var (ns, name) = PathNamespaces.Classify(full);
            var zipEntry = z;
            _entries.Add(new ResourceEntry(this, index++, name, full, ns, z.Length, isMarker: false, () => ReadEntry(zipEntry)));
        }
    }

    public override string? Path { get; }

    public override IReadOnlyList<ResourceEntry> Entries => _entries;

    public static new ZipContainer Open(string path) => new(File.ReadAllBytes(path), path);

    public static ZipContainer FromBytes(byte[] data, string? path = null) => new(data, path);

    private byte[] ReadEntry(ZipArchiveEntry entry)
    {
        lock (_gate)
        {
            try
            {
                using var s = entry.Open();
                var buffer = new byte[entry.Length];
                s.ReadExactly(buffer);
                return buffer;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                throw new CorruptDataException($"{entry.FullName} in {DisplayName} cannot be unpacked: {ex.Message}", ex);
            }
        }
    }
}

/// <summary>A folder on disk laid out like a PK3. Files are read when asked for, so edits on disk show up.</summary>
public sealed class FolderContainer : ResourceContainer
{
    private readonly List<ResourceEntry> _entries = [];

    public FolderContainer(string root)
    {
        Path = System.IO.Path.GetFullPath(root);
        var files = Directory.EnumerateFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden })
            .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(part => part.StartsWith('.'))) // archive path
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var index = 0;
        foreach (var rel in files)
        {
            var (ns, name) = PathNamespaces.Classify(rel);
            var absolute = System.IO.Path.Combine(Path, rel);
            _entries.Add(new ResourceEntry(this, index++, name, rel, ns, new FileInfo(absolute).Length, isMarker: false, () => File.ReadAllBytes(absolute)));
        }
    }

    public override string Path { get; }

    public override IReadOnlyList<ResourceEntry> Entries => _entries;
}
