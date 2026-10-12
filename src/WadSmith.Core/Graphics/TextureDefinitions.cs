using System.Buffers.Binary;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Graphics;

/// <summary>One patch placed in a composite texture.</summary>
public sealed record PatchPlacement(string Patch, int X, int Y)
{
    public bool FlipX { get; init; }

    public bool FlipY { get; init; }

    /// <summary>0, 90, 180 or 270 degrees.</summary>
    public int Rotate { get; init; }

    /// <summary>For the binary formats: the original fields, so the lump writes back byte-identical.</summary>
    internal short StepDir { get; init; }

    internal short Colormap { get; init; }

    internal short PatchIndex { get; init; } = -1;
}

/// <summary>A texture built from patches (TEXTURE1/TEXTURE2 or a TEXTURES definition).</summary>
public sealed class CompositeTexture
{
    public required string Name { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public List<PatchPlacement> Patches { get; } = [];

    public double ScaleX { get; init; } = 1;

    public double ScaleY { get; init; } = 1;

    /// <summary>Offsets scale with the texture (ZDoom's world panning).</summary>
    public bool WorldPanning { get; init; }

    /// <summary>A placeholder that draws nothing (the first texture of TEXTURE1, or TEXTURES' NullTexture).</summary>
    public bool IsNull { get; set; }

    /// <summary>Where the definition came from, for messages.</summary>
    public required string Origin { get; init; }

    internal NameField RawName { get; init; }

    internal int RawFlags { get; init; }

    internal int RawColumnDirectory { get; init; }

}

/// <summary>TEXTURE1/TEXTURE2 with PNAMES: the binary composite-texture lumps, in the Doom or the Strife layout.</summary>
public sealed class TextureLump
{
    public bool IsStrifeLayout { get; private init; }

    public List<CompositeTexture> Textures { get; } = [];

    /// <summary>The patch names (PNAMES) the textures index into.</summary>
    public static IReadOnlyList<NameField> ReadPatchNames(ReadOnlySpan<byte> pnames, string source = "PNAMES")
    {
        if (pnames.Length < 4) throw new CorruptDataException($"{source} is too short.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(pnames);
        if (count < 0 || 4L + 8L * count > pnames.Length) throw new CorruptDataException($"{source} says it holds {count} names but is only {pnames.Length} bytes.");
        var names = new NameField[count];
        for (var i = 0; i < count; i++) names[i] = NameField.Read(pnames.Slice(4 + 8 * i, 8));
        return names;
    }

    public static byte[] WritePatchNames(IReadOnlyList<NameField> names)
    {
        var data = new byte[4 + 8 * names.Count];
        BinaryPrimitives.WriteInt32LittleEndian(data, names.Count);
        for (var i = 0; i < names.Count; i++) names[i].Write(data.AsSpan(4 + 8 * i, 8));
        return data;
    }

    /// <summary>
    /// Reads a TEXTUREx lump. The Strife layout (no column directory, three-field patches) is
    /// detected as GZDoom detects it: a Doom-layout texture always has zero in the upper half of its
    /// unused column-directory field.
    /// </summary>
    public static TextureLump Read(ReadOnlySpan<byte> data, IReadOnlyList<NameField> patchNames, string source)
    {
        if (data.Length < 4) throw new CorruptDataException($"{source} is too short.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count < 0 || 4L + 4L * count > data.Length) throw new CorruptDataException($"{source} says it holds {count} textures but is only {data.Length} bytes.");

        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(4 + 4 * i)..]);
            if (offsets[i] < 0 || offsets[i] + 18 > data.Length) throw new CorruptDataException($"{source}: texture {i} starts outside the lump.");
        }

        var strife = false;
        foreach (var o in offsets)
        {
            if (o + 20 > data.Length) continue;
            var patchCount = BinaryPrimitives.ReadInt16LittleEndian(data[(o + 20)..]);
            if (patchCount < 0 || data[o + 18] != 0 || data[o + 19] != 0)
            {
                strife = true;
                break;
            }
        }

        var lump = new TextureLump { IsStrifeLayout = strife };
        for (var i = 0; i < count; i++)
        {
            var r = data[offsets[i]..];
            var name = NameField.Read(r[..8]);
            var flags = BinaryPrimitives.ReadInt32LittleEndian(r[8..]);
            int width = BinaryPrimitives.ReadInt16LittleEndian(r[12..]);
            int height = BinaryPrimitives.ReadInt16LittleEndian(r[14..]);
            var columnDir = strife ? 0 : BinaryPrimitives.ReadInt32LittleEndian(r[16..]);
            int patchCount = BinaryPrimitives.ReadInt16LittleEndian(r[(strife ? 16 : 20)..]);
            var patchSize = strife ? 6 : 10;
            var first = strife ? 18 : 22;
            if (patchCount < 0 || first + (long)patchCount * patchSize > r.Length) throw new CorruptDataException($"{source}: texture {name.Text} runs past the end of the lump.");

            // ZDoom stores a scale in the otherwise unused upper bytes of the flags field.
            var scaleX = (flags >> 16) & 0xFF;
            var scaleY = (flags >> 24) & 0xFF;
            var tex = new CompositeTexture
            {
                Name = name.Text,
                Width = width,
                Height = height,
                Origin = source,
                RawName = name,
                RawFlags = flags,
                RawColumnDirectory = columnDir,
                ScaleX = scaleX == 0 ? 1 : 8.0 / scaleX,
                ScaleY = scaleY == 0 ? 1 : 8.0 / scaleY,
                WorldPanning = (flags & 0x8000) != 0,
            };

            for (var p = 0; p < patchCount; p++)
            {
                var pr = r[(first + p * patchSize)..];
                var index = BinaryPrimitives.ReadInt16LittleEndian(pr[4..]);
                var patchName = index >= 0 && index < patchNames.Count ? patchNames[index].Text : "";
                tex.Patches.Add(new PatchPlacement(patchName, BinaryPrimitives.ReadInt16LittleEndian(pr), BinaryPrimitives.ReadInt16LittleEndian(pr[2..]))
                {
                    PatchIndex = index,
                    StepDir = strife ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(pr[6..]),
                    Colormap = strife ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(pr[8..]),
                });
            }

            lump.Textures.Add(tex);
        }

        return lump;
    }

    /// <summary>Writes the lump in its own layout; an unchanged lump writes back byte-identical.</summary>
    public byte[] Write()
    {
        var patchSize = IsStrifeLayout ? 6 : 10;
        var header = IsStrifeLayout ? 18 : 22;
        using var body = new MemoryStream();
        var offsets = new int[Textures.Count];
        var start = 4 + 4 * Textures.Count;
        for (var i = 0; i < Textures.Count; i++)
        {
            var t = Textures[i];
            offsets[i] = start + (int)body.Length;
            var rec = new byte[header + patchSize * t.Patches.Count];
            (t.RawName.Text == t.Name ? t.RawName : NameField.FromText(t.Name)).Write(rec.AsSpan(0, 8));
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(8), t.RawFlags);
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(12), (short)t.Width);
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(14), (short)t.Height);
            if (!IsStrifeLayout) BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(16), t.RawColumnDirectory);
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(IsStrifeLayout ? 16 : 20), (short)t.Patches.Count);
            for (var p = 0; p < t.Patches.Count; p++)
            {
                var pp = t.Patches[p];
                var o = header + p * patchSize;
                BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(o), (short)pp.X);
                BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(o + 2), (short)pp.Y);
                BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(o + 4), pp.PatchIndex);
                if (!IsStrifeLayout)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(o + 6), pp.StepDir);
                    BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(o + 8), pp.Colormap);
                }
            }

            body.Write(rec);
        }

        var output = new byte[start + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(output, Textures.Count);
        for (var i = 0; i < offsets.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4 + 4 * i), offsets[i]);
        body.ToArray().CopyTo(output, start);
        return output;
    }
}
