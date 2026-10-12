using System.Text;

namespace WadSmith.Core.Containers;

/// <summary>The eight-byte names WAD files use for lumps, textures, flats and sprites.</summary>
public static class LumpName
{
    public const int Length = 8;

    /// <summary>Names compare without regard to case, as the engines compare them.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Reads a name field: up to eight bytes, ending at the first zero byte. Bytes after the zero are
    /// ignored (some tools leave garbage there). Bytes above 127 are kept as Latin-1 characters so a
    /// name always survives a round trip.
    /// </summary>
    public static string Read(ReadOnlySpan<byte> field)
    {
        if (field.Length > Length) field = field[..Length];
        var end = field.IndexOf((byte)0);
        if (end >= 0) field = field[..end];
        return Encoding.Latin1.GetString(field);
    }

    /// <summary>Writes a name into an eight-byte field, padding with zero bytes.</summary>
    public static void Write(string name, Span<byte> field)
    {
        if (field.Length < Length) throw new ArgumentException("A name field is eight bytes.", nameof(field));
        Validate(name);
        field[..Length].Clear();
        Encoding.Latin1.GetBytes(name, field);
    }

    /// <summary>Throws when a name cannot be stored in a WAD: longer than eight characters or not Latin-1.</summary>
    public static void Validate(string name)
    {
        if (name.Length > Length) throw new ArgumentException($"\"{name}\" is longer than eight characters, which a WAD cannot store.", nameof(name));
        foreach (var c in name)
        {
            if (c > 0xFF || c == '\0') throw new ArgumentException($"\"{name}\" holds a character a WAD cannot store.", nameof(name));
        }
    }

    /// <summary>The uppercase form engines use when they look a name up.</summary>
    public static string Normalize(string name) => name.ToUpperInvariant();
}

/// <summary>
/// An eight-byte name field from a binary record. It reads as its text, but keeps the raw bytes it
/// was read from: old map editors left memory leftovers after the terminating zero, and writing the
/// raw bytes back keeps an unchanged map byte-identical. A name built from text writes clean.
/// </summary>
public readonly struct NameField : IEquatable<NameField>
{
    private readonly ulong _raw;
    private readonly bool _hasRaw;

    private NameField(string text, ulong raw, bool hasRaw)
    {
        Text = text;
        _raw = raw;
        _hasRaw = hasRaw;
    }

    public string Text => field ?? "";

    public static NameField Read(ReadOnlySpan<byte> field8) =>
        new(LumpName.Read(field8), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(field8), true);

    public static NameField FromText(string text)
    {
        LumpName.Validate(text);
        return new NameField(text, 0, false);
    }

    public static implicit operator NameField(string text) => FromText(text);

    public void Write(Span<byte> field8)
    {
        if (_hasRaw)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(field8, _raw);
            return;
        }

        LumpName.Write(Text, field8);
    }

    /// <summary>Two fields are equal when their text is; leftover bytes do not count.</summary>
    public bool Equals(NameField other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is NameField other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public static bool operator ==(NameField a, NameField b) => a.Equals(b);

    public static bool operator !=(NameField a, NameField b) => !a.Equals(b);

    public override string ToString() => Text;
}
