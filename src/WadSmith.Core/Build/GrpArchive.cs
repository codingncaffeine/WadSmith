using System.Buffers.Binary;
using System.Text;

namespace WadSmith.Core.Build;

/// <summary>
/// A Build-engine group file (Duke Nukem 3D's DUKE3D.GRP, D48): the 12-byte "KenSilverman" signature and a count, a table of
/// 12-byte names with their sizes, then the files one after another. Read in place, never changed.
/// </summary>
public sealed class GrpArchive
{
    public const string Signature = "KenSilverman";

    private readonly string _path;
    private readonly Dictionary<string, (long Offset, int Size)> _files = new(StringComparer.OrdinalIgnoreCase);

    private GrpArchive(string path) => _path = path;

    /// <summary>The names of the files inside, in their order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Whether the file at <paramref name="path"/> starts with the group file's signature.</summary>
    public static bool IsGrp(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            return f.ReadAtLeast(head, 12, throwOnEndOfStream: false) == 12 && head.SequenceEqual(Encoding.ASCII.GetBytes(Signature));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static GrpArchive Open(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[16];
        if (f.ReadAtLeast(head, 16, throwOnEndOfStream: false) < 16 || Encoding.ASCII.GetString(head, 0, 12) != Signature)
        {
            throw new CorruptDataException($"{Path.GetFileName(path)} is not a Build group file.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12));
        if (count is < 0 or > 100_000) throw new CorruptDataException($"{Path.GetFileName(path)} says it holds {count} files.");
        var table = new byte[count * 16];
        if (f.ReadAtLeast(table, table.Length, throwOnEndOfStream: false) < table.Length) throw new CorruptDataException($"{Path.GetFileName(path)}'s table of files is cut short.");

        var grp = new GrpArchive(path);
        var names = new List<string>(count);
        long offset = 16 + table.Length;
        for (var i = 0; i < count; i++)
        {
            var name = Encoding.ASCII.GetString(table, i * 16, 12).TrimEnd('\0', ' ');
            var size = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 16 + 12));
            if (size < 0 || offset + size > f.Length) throw new CorruptDataException($"{Path.GetFileName(path)}: {name} runs past the end of the file.");
            grp._files[name] = (offset, size);
            names.Add(name);
            offset += size;
        }

        grp.Names = names;
        return grp;
    }

    /// <summary>The bytes of the file <paramref name="name"/> inside, or null when there is none.</summary>
    public byte[]? Read(string name)
    {
        if (!_files.TryGetValue(name, out var at)) return null;
        using var f = File.OpenRead(_path);
        f.Position = at.Offset;
        var data = new byte[at.Size];
        f.ReadExactly(data);
        return data;
    }

    /// <summary>The size of the file <paramref name="name"/> inside, or -1.</summary>
    public long SizeOf(string name) => _files.TryGetValue(name, out var at) ? at.Size : -1;
}
