using System.Buffers.Binary;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Models;

/// <summary>
/// A voxel model in GZDoom's model axes: <see cref="Forward"/> × <see cref="Left"/> × <see cref="Up"/> cells, each 0 (empty) or
/// a colour number 1–255 into <see cref="Palette"/> (colour n is <c>Palette[n - 1]</c>). One cell is one map unit each way:
/// GZDoom draws voxels at the game's own pixel shape, as it draws sprites.
/// </summary>
public sealed class VoxelModel
{
    public VoxelModel(int forward, int left, int up, byte[] cells, IReadOnlyList<(byte R, byte G, byte B)> palette)
    {
        if (forward <= 0 || left <= 0 || up <= 0 || cells.Length != forward * left * up) throw new ArgumentException("The cells do not fill the size.", nameof(cells));
        (Forward, Left, Up, Cells, Palette) = (forward, left, up, cells, palette);
    }

    public int Forward { get; }

    public int Left { get; }

    public int Up { get; }

    public byte[] Cells { get; }

    public IReadOnlyList<(byte R, byte G, byte B)> Palette { get; }

    public byte this[int f, int l, int u] => f < 0 || l < 0 || u < 0 || f >= Forward || l >= Left || u >= Up ? (byte)0 : Cells[(f * Left + l) * Up + u];

    public int Count => Cells.Count(c => c != 0);

    /// <summary>
    /// The voxels' visible faces as a mesh standing on the middle of its base (origin at the bottom centre, as KVX's pivot),
    /// coloured from a 16 × 16 picture of the palette, for baking sprites and the 3D view.
    /// </summary>
    public Mesh ToMesh()
    {
        var picture = new RgbaImage(16, 16);
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = i < Palette.Count ? Palette[i] : ((byte)0, (byte)0, (byte)0);
            picture.Set(i % 16, i / 16, r, g, b);
        }

        var triangles = new List<MeshVertex>();
        float cx = Forward / 2f, cy = Left / 2f;
        for (var f = 0; f < Forward; f++)
        {
            for (var l = 0; l < Left; l++)
            {
                for (var u = 0; u < Up; u++)
                {
                    var c = this[f, l, u];
                    if (c == 0) continue;
                    var (tu, tv) = (((c - 1) % 16 + 0.5f) / 16, ((c - 1) / 16 + 0.5f) / 16);
                    float x0 = f - cx, x1 = f + 1 - cx, y0 = l - cy, y1 = l + 1 - cy, z0 = u, z1 = u + 1;
                    void Quad((float, float, float) a, (float, float, float) b, (float, float, float) d, (float, float, float) e)
                    {
                        foreach (var p in (ReadOnlySpan<(float X, float Y, float Z)>)[a, b, d, a, d, e]) triangles.Add(new MeshVertex(p.X, p.Y, p.Z, tu, tv));
                    }

                    // Faces seen from outside only, wound anticlockwise from outside.
                    if (this[f + 1, l, u] == 0) Quad((x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1));
                    if (this[f - 1, l, u] == 0) Quad((x0, y1, z0), (x0, y0, z0), (x0, y0, z1), (x0, y1, z1));
                    if (this[f, l + 1, u] == 0) Quad((x1, y1, z0), (x0, y1, z0), (x0, y1, z1), (x1, y1, z1));
                    if (this[f, l - 1, u] == 0) Quad((x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1));
                    if (this[f, l, u + 1] == 0) Quad((x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1));
                    if (this[f, l, u - 1] == 0) Quad((x0, y1, z0), (x1, y1, z0), (x1, y0, z0), (x0, y0, z0));
                }
            }
        }

        return new Mesh([new MeshSurface("voxels", triangles, null, picture)]);
    }
}

/// <summary>
/// MagicaVoxel's <c>.vox</c> files (D16): the first model's size and voxels and the file's palette. MagicaVoxel stands a
/// model on Z with its front toward −Y; that front is turned to face Doom's forward. A file with several models or a scene
/// keeps its first model, said in a note.
/// </summary>
public static class MagicaVoxel
{
    public static VoxelModel Read(string path, ICollection<string>? notes = null)
    {
        var name = Path.GetFileName(path);
        var data = File.ReadAllBytes(path);
        if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual("VOX "u8)) throw new CorruptDataException($"{name} is not a MagicaVoxel model.");
        (int X, int Y, int Z)? size = null;
        byte[]? voxels = null;
        int models = 0, sizes = 0;
        (byte, byte, byte)[]? palette = null;
        var at = 8;
        if (Chunk(at).Id != "MAIN") throw new CorruptDataException($"{name} has no MAIN chunk.");
        at += 12;
        while (at + 12 <= data.Length)
        {
            var (id, content, children) = Chunk(at);
            var body = at + 12;
            if ((long)body + content > data.Length) throw new CorruptDataException($"{name} ends early.");
            switch (id)
            {
                case "SIZE" when content >= 12:
                    sizes++;
                    size ??= (Int(body), Int(body + 4), Int(body + 8));
                    break;
                case "XYZI" when content >= 4:
                    models++;
                    if (voxels is null)
                    {
                        var count = Int(body);
                        if (count < 0 || 4L + count * 4L > content) throw new CorruptDataException($"{name} ends early.");
                        voxels = data.AsSpan(body + 4, count * 4).ToArray();
                    }

                    break;
                case "RGBA" when content >= 1024:
                    palette = new (byte, byte, byte)[256];
                    for (var i = 0; i < 256; i++) palette[i] = (data[body + i * 4], data[body + i * 4 + 1], data[body + i * 4 + 2]);
                    break;
            }

            at = body + content + children;
        }

        if (size is not { } s || voxels is null) throw new CorruptDataException($"{name} has no model in it.");
        if (s.X <= 0 || s.Y <= 0 || s.Z <= 0 || s.X > 1024 || s.Y > 1024 || s.Z > 1024) throw new CorruptDataException($"{name} says its model is {s.X} × {s.Y} × {s.Z}.");
        if (models > 1) notes?.Add($"{name} holds {models} models; the first is brought in.");
        if (palette is null)
        {
            notes?.Add($"{name} carries no colours of its own; it is shown in greys.");
            palette = [.. Enumerable.Range(0, 256).Select(i => ((byte)(255 - i), (byte)(255 - i), (byte)(255 - i)))];
        }

        // MagicaVoxel (x, y, z), front toward -y → (forward = -y, left = x, up = z).
        var cells = new byte[s.X * s.Y * s.Z];
        for (var i = 0; i + 3 < voxels.Length; i += 4)
        {
            int x = voxels[i], y = voxels[i + 1], z = voxels[i + 2];
            if (x >= s.X || y >= s.Y || z >= s.Z || voxels[i + 3] == 0) continue;
            var forward = s.Y - 1 - y;
            cells[(forward * s.X + x) * s.Z + z] = voxels[i + 3];
        }

        return new VoxelModel(s.Y, s.X, s.Z, cells, palette);

        (string Id, int Content, int Children) Chunk(int offset) =>
            offset + 12 <= data.Length
                ? (System.Text.Encoding.ASCII.GetString(data, offset, 4), Int(offset + 4), Int(offset + 8))
                : throw new CorruptDataException($"{name} ends early.");

        int Int(int offset) => offset + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) : throw new CorruptDataException($"{name} ends early.");
    }
}

/// <summary>
/// Ken Silverman's KVX voxels, as Build games, Doom voxel packs and GZDoom use them: columns of slabs (top, length, which
/// faces show, colours) with z running downward, a pivot in 1/256 units, the palette (6-bit VGA levels) in the last 768
/// bytes. GZDoom turns a voxel a quarter left of its KVX axes (VOXELDEF's AngleOffset starts at 90): KVX x is the model's
/// left, y its forward. Only the first mip level is written; GZDoom makes do with one.
/// </summary>
public static class Kvx
{
    public static byte[] Write(VoxelModel model)
    {
        int xs = model.Left, ys = model.Forward, zs = model.Up;
        byte Cell(int x, int y, int z) => model[y, x, zs - 1 - z];
        var columns = new List<byte>[xs * ys];
        for (var x = 0; x < xs; x++)
        {
            for (var y = 0; y < ys; y++)
            {
                var column = columns[x * ys + y] = [];
                var z = 0;
                while (z < zs)
                {
                    if (Cell(x, y, z) == 0)
                    {
                        z++;
                        continue;
                    }

                    // A run of voxels whose sides show alike (GZDoom draws a slab's side faces for all its voxels).
                    var sides = Sides(x, y, z);
                    var start = z;
                    while (z < zs && Cell(x, y, z) != 0 && Sides(x, y, z) == sides) z++;
                    // A cell outside the grid reads as empty, so the very top and bottom show.
                    var top = Cell(x, y, start - 1) == 0;
                    var bottom = z >= zs || Cell(x, y, z) == 0;
                    if (sides == 0)
                    {
                        // No side shows: only an end that does, as a slab of one; the voxels between are buried.
                        if (top) Slab(start, 1, 16 | (bottom && z - start == 1 ? 32 : 0));
                        if (bottom && !(top && z - start == 1)) Slab(z - 1, 1, 32);
                        continue;
                    }

                    void Slab(int from, int length, int vis)
                    {
                        column.Add((byte)from);
                        column.Add((byte)length);
                        column.Add((byte)vis);
                        for (var k = from; k < from + length; k++) column.Add((byte)(Cell(x, y, k) - 1));
                    }

                    for (var from = start; from < z; from += 255)
                    {
                        var length = Math.Min(255, z - from);
                        Slab(from, length, sides | (from == start && top ? 16 : 0) | (from + length == z && bottom ? 32 : 0));
                    }
                }
            }
        }

        int Sides(int x, int y, int z) => (Cell(x - 1, y, z) == 0 ? 1 : 0) | (Cell(x + 1, y, z) == 0 ? 2 : 0) | (Cell(x, y - 1, z) == 0 ? 4 : 0) | (Cell(x, y + 1, z) == 0 ? 8 : 0);

        var offsetSize = (xs + 1) * 4 + xs * (ys + 1) * 2;
        var slabBytes = columns.Sum(c => c.Count);
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(24 + offsetSize + slabBytes);
        w.Write(xs);
        w.Write(ys);
        w.Write(zs);
        w.Write(xs * 256 / 2);
        w.Write(ys * 256 / 2);
        w.Write(zs * 256);
        var running = offsetSize;
        for (var x = 0; x <= xs; x++)
        {
            w.Write(running);
            if (x < xs) running += Enumerable.Range(0, ys).Sum(y => columns[x * ys + y].Count);
        }

        for (var x = 0; x < xs; x++)
        {
            short inColumn = 0;
            for (var y = 0; y <= ys; y++)
            {
                w.Write(inColumn);
                if (y < ys) inColumn = checked((short)(inColumn + columns[x * ys + y].Count));
            }
        }

        foreach (var column in columns) w.Write(column.ToArray());
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = i < model.Palette.Count ? model.Palette[i] : ((byte)0, (byte)0, (byte)0);
            w.Write((byte)(r >> 2));
            w.Write((byte)(g >> 2));
            w.Write((byte)(b >> 2));
        }

        return body.ToArray();
    }

    /// <summary>Reads the first mip level of a KVX back into model axes (for the 3D view, and KVX files brought in as they are).</summary>
    public static VoxelModel Read(byte[] data, string name = "voxel")
    {
        if (data.Length < 28 + 768) throw new CorruptDataException($"{name} is not a KVX voxel.");
        int I(int at) => at + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) : throw new CorruptDataException($"{name} ends early.");
        var numBytes = I(0);
        int xs = I(4), ys = I(8), zs = I(12);
        if (numBytes < 24 || numBytes + 4 > data.Length - 768 || xs <= 0 || ys <= 0 || zs <= 0 || xs > 1024 || ys > 1024 || zs > 255) throw new CorruptDataException($"{name} is not a KVX voxel.");
        var offsetSize = (xs + 1) * 4 + xs * (ys + 1) * 2;
        var slabs = 4 + 24 + offsetSize;
        var palette = new (byte, byte, byte)[256];
        var p = data.Length - 768;
        for (var i = 0; i < 256; i++) palette[i] = ((byte)(data[p + i * 3] * 255 / 63), (byte)(data[p + i * 3 + 1] * 255 / 63), (byte)(data[p + i * 3 + 2] * 255 / 63));
        var cells = new byte[ys * xs * zs];
        for (var x = 0; x < xs; x++)
        {
            var xoff = I(28 + x * 4) - offsetSize;
            for (var y = 0; y < ys; y++)
            {
                var o = 28 + (xs + 1) * 4 + (x * (ys + 1) + y) * 2;
                int from = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(o)), to = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(o + 2));
                for (var at = slabs + xoff + from; at < slabs + xoff + to;)
                {
                    if (at + 3 > data.Length) throw new CorruptDataException($"{name} ends early.");
                    int top = data[at], length = data[at + 1];
                    for (var k = 0; k < length && top + k < zs; k++)
                    {
                        if (at + 3 + k >= data.Length) throw new CorruptDataException($"{name} ends early.");
                        cells[(y * xs + x) * zs + (zs - 1 - (top + k))] = (byte)(data[at + 3 + k] + 1);
                    }

                    at += 3 + length;
                }
            }
        }

        return new VoxelModel(ys, xs, zs, cells, palette);
    }
}
