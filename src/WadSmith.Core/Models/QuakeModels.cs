using System.Buffers.Binary;
using System.Text;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Models;

/// <summary>
/// A vertex-animated model of the Quake family: every frame's positions (in GZDoom's model axes, Z up), the triangles as
/// position and texture-corner indexes, the texture corners in pixels of the skin, the frames' names and the first skin.
/// </summary>
public sealed record AnimatedModel(IReadOnlyList<(string Name, (float X, float Y, float Z)[] Positions)> Frames, IReadOnlyList<(int A, int B, int C, int TA, int TB, int TC)> Triangles, IReadOnlyList<(float S, float T)> Corners, int SkinWidth, int SkinHeight, RgbaImage? Skin)
{
    /// <summary>Frame <paramref name="frame"/> as a mesh with the skin.</summary>
    public Mesh ToMesh(int frame = 0)
    {
        var p = Frames[Math.Clamp(frame, 0, Frames.Count - 1)].Positions;
        var list = new List<MeshVertex>(Triangles.Count * 3);
        foreach (var (a, b, c, ta, tb, tc) in Triangles)
        {
            foreach (var (v, t) in new[] { (a, ta), (b, tb), (c, tc) })
            {
                list.Add(new MeshVertex(p[v].X, p[v].Y, p[v].Z, Corners[t].S / SkinWidth, Corners[t].T / SkinHeight));
            }
        }

        return new Mesh([new MeshSurface("skin", list, null, Skin)]);
    }
}

/// <summary>
/// Quake's MDL models (version 6, "IDPO"): the first skin through Quake's palette, every frame (a frame group's frames
/// each), the triangles with the back side of a seam moved half the skin across, as Quake draws it. GZDoom reads no MDL;
/// the importer writes it as an MD2, which GZDoom reads with all its frames.
/// </summary>
public static class Mdl
{
    public static AnimatedModel Read(byte[] data, ReadOnlySpan<byte> palette, string name = "model")
    {
        if (data.Length < 84 || !data.AsSpan(0, 4).SequenceEqual("IDPO"u8)) throw new CorruptDataException($"{name} is not a Quake model.");
        if (I(4) != 6) throw new CorruptDataException($"{name} is model version {I(4)}; WadSmith reads Quake's version 6.");
        if (palette.Length < 768) throw new CorruptDataException($"{name}'s colours need Quake's palette (gfx/palette.lmp).");
        var scale = (F(8), F(12), F(16));
        var translate = (F(20), F(24), F(28));
        int skins = I(48), width = I(52), height = I(56), verts = I(60), tris = I(64), frames = I(68);
        if (width <= 0 || height <= 0 || width * height > 1 << 24 || verts <= 0 || tris <= 0 || frames <= 0 || verts > 65536 || tris > 65536) throw new CorruptDataException($"{name} has a header WadSmith cannot believe.");
        var at = 84;

        RgbaImage? skin = null;
        for (var s = 0; s < skins; s++)
        {
            var group = I(at);
            at += 4;
            var count = group == 0 ? 1 : I(at);
            if (group != 0) at += 4 + count * 4;
            if (s == 0)
            {
                Need(at, width * height);
                skin = new RgbaImage(width, height);
                for (var i = 0; i < width * height; i++)
                {
                    var c = data[at + i] * 3;
                    skin.Set(i % width, i / width, palette[c], palette[c + 1], palette[c + 2]);
                }
            }

            at += count * width * height;
        }

        // Texture corners; a corner on the seam gets a second one half the skin across, for the triangles facing back.
        Need(at, verts * 12);
        var corners = new List<(float S, float T)>();
        var seam = new int[verts];
        for (var v = 0; v < verts; v++) corners.Add((I(at + v * 12 + 4), I(at + v * 12 + 8)));
        for (var v = 0; v < verts; v++)
        {
            seam[v] = -1;
            if (I(at + v * 12) == 0) continue;
            seam[v] = corners.Count;
            corners.Add((corners[v].S + width / 2f, corners[v].T));
        }

        at += verts * 12;
        Need(at, tris * 16);
        var triangles = new List<(int, int, int, int, int, int)>(tris);
        for (var t = 0; t < tris; t++)
        {
            var front = I(at + t * 16) != 0;
            int a = I(at + t * 16 + 4), b = I(at + t * 16 + 8), c = I(at + t * 16 + 12);
            if ((uint)a >= verts || (uint)b >= verts || (uint)c >= verts) throw new CorruptDataException($"{name}: a triangle names vertex {Math.Max(a, Math.Max(b, c))} of {verts}.");
            int Corner(int v) => !front && seam[v] >= 0 ? seam[v] : v;
            // In the file's order: Quake and Quake II wind alike, and GZDoom reads MD2 as written.
            triangles.Add((a, b, c, Corner(a), Corner(b), Corner(c)));
        }

        at += tris * 16;
        var all = new List<(string, (float, float, float)[])>();
        for (var f = 0; f < frames; f++)
        {
            var type = I(at);
            at += 4;
            if (type == 0)
            {
                all.Add(Frame());
                continue;
            }

            var count = I(at);
            at += 4 + 8 + count * 4;
            for (var k = 0; k < count; k++) all.Add(Frame());
        }

        return new AnimatedModel(all, triangles, corners, width, height, skin);

        (string, (float, float, float)[]) Frame()
        {
            Need(at, 8 + 16 + verts * 4);
            var frameName = Text(at + 8, 16);
            var p = new (float, float, float)[verts];
            for (var v = 0; v < verts; v++)
            {
                var o = at + 24 + v * 4;
                p[v] = (data[o] * scale.Item1 + translate.Item1, data[o + 1] * scale.Item2 + translate.Item2, data[o + 2] * scale.Item3 + translate.Item3);
            }

            at += 24 + verts * 4;
            return (frameName, p);
        }

        int I(int offset) => offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) : throw new CorruptDataException($"{name} ends early.");
        float F(int offset) => offset + 4 <= data.Length ? BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset)) : throw new CorruptDataException($"{name} ends early.");
        void Need(int offset, long length)
        {
            if (offset < 0 || length < 0 || offset + length > data.Length) throw new CorruptDataException($"{name} ends early.");
        }

        string Text(int offset, int length)
        {
            var span = data.AsSpan(offset, length);
            var end = span.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
        }
    }
}

/// <summary>
/// Quake II's MD2 models ("IDP2", version 8), which GZDoom reads: read for the first frame and its skin (a PCX or any
/// picture the skin's name finds beside the model), and written from any <see cref="AnimatedModel"/> with all its frames.
/// </summary>
public static class Md2
{
    public static AnimatedModel Read(string path, ICollection<string>? notes = null)
    {
        var name = Path.GetFileName(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var data = File.ReadAllBytes(path);
        if (data.Length < 68 || !data.AsSpan(0, 4).SequenceEqual("IDP2"u8)) throw new CorruptDataException($"{name} is not an MD2 model.");
        int I(int at) => at + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) : throw new CorruptDataException($"{name} ends early.");
        if (I(4) != 8) throw new CorruptDataException($"{name} is MD2 version {I(4)}; GZDoom reads version 8.");
        int width = I(8), height = I(12), frameSize = I(16), skins = I(20), verts = I(24), sts = I(28), tris = I(32), frames = I(40);
        int ofsSkins = I(44), ofsSt = I(48), ofsTris = I(52), ofsFrames = I(56);
        if (width <= 0 || height <= 0 || verts <= 0 || tris <= 0 || frames <= 0 || (long)ofsFrames + (long)frameSize * frames > data.Length || (long)ofsTris + tris * 12L > data.Length || (long)ofsSt + sts * 4L > data.Length)
        {
            throw new CorruptDataException($"{name} ends early.");
        }

        var corners = new List<(float, float)>(sts);
        for (var s = 0; s < sts; s++) corners.Add((BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(ofsSt + s * 4)), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(ofsSt + s * 4 + 2))));
        var triangles = new List<(int, int, int, int, int, int)>(tris);
        for (var t = 0; t < tris; t++)
        {
            int S(int k) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ofsTris + t * 12 + k * 2));
            if (S(0) >= verts || S(1) >= verts || S(2) >= verts || S(3) >= sts || S(4) >= sts || S(5) >= sts) throw new CorruptDataException($"{name}: a triangle names a vertex it does not have.");
            triangles.Add((S(0), S(1), S(2), S(3), S(4), S(5)));
        }

        var all = new List<(string, (float, float, float)[])>(frames);
        for (var f = 0; f < frames; f++)
        {
            var at = ofsFrames + f * frameSize;
            var scale = (Fl(at), Fl(at + 4), Fl(at + 8));
            var move = (Fl(at + 12), Fl(at + 16), Fl(at + 20));
            var span = data.AsSpan(at + 24, 16);
            var end = span.IndexOf((byte)0);
            var p = new (float, float, float)[verts];
            for (var v = 0; v < verts; v++)
            {
                var o = at + 40 + v * 4;
                if (o + 3 > data.Length) throw new CorruptDataException($"{name} ends early.");
                p[v] = (data[o] * scale.Item1 + move.Item1, data[o + 1] * scale.Item2 + move.Item2, data[o + 2] * scale.Item3 + move.Item3);
            }

            all.Add((Encoding.ASCII.GetString(end < 0 ? span : span[..end]), p));
        }

        RgbaImage? skin = null;
        for (var s = 0; s < skins && skin is null; s++)
        {
            var skinName = Encoding.ASCII.GetString(data.AsSpan(ofsSkins + s * 64, 64)).TrimEnd('\0').Replace('\\', '/');
            var file = ObjModel.Find(folder, Path.GetFileName(skinName)) ?? ObjModel.Guess(folder, Path.GetFileNameWithoutExtension(skinName));
            if (file is not null) skin = Picture(file, notes);
        }

        if (skin is null && ObjModel.Beside(folder, Path.GetFileNameWithoutExtension(path)) is { } beside)
        {
            skin = Picture(beside, notes);
            notes?.Add($"The model names no skin found beside it, so {Path.GetFileName(beside)} was used.");
        }

        return new AnimatedModel(all, triangles, corners, width, height, skin);

        float Fl(int at) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at));
    }

    private static RgbaImage? Picture(string file, ICollection<string>? notes)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            return Pcx.Looks(bytes) ? Pcx.Decode(bytes, Path.GetFileName(file)) : Tga.DecodeAny(bytes, Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException)
        {
            notes?.Add($"The picture {Path.GetFileName(file)} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The model as an MD2 with every frame, its skin named <paramref name="skin"/> (a picture beside it); positions packed to
    /// bytes per frame, as MD2 keeps them. Its GL commands draw each triangle as a strip of its three corners: GZDoom refuses an
    /// MD2 without them ("invalid NumGLCommands") and shows no model.
    /// </summary>
    public static byte[] Write(AnimatedModel model, string skin)
    {
        var verts = model.Frames[0].Positions.Length;
        var frameSize = 40 + verts * 4;
        const int header = 68;
        var glCommands = model.Triangles.Count * 10 + 1;
        int ofsSkins = header, ofsSt = ofsSkins + 64, ofsTris = ofsSt + model.Corners.Count * 4, ofsFrames = ofsTris + model.Triangles.Count * 12, ofsGl = ofsFrames + frameSize * model.Frames.Count;
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write("IDP2"u8);
        foreach (var v in new[] { 8, model.SkinWidth, model.SkinHeight, frameSize, 1, verts, model.Corners.Count, model.Triangles.Count, glCommands, model.Frames.Count, ofsSkins, ofsSt, ofsTris, ofsFrames, ofsGl, ofsGl + glCommands * 4 }) w.Write(v);
        var skinName = new byte[64];
        Encoding.ASCII.GetBytes(skin, 0, Math.Min(63, skin.Length), skinName, 0);
        w.Write(skinName);
        foreach (var (s, t) in model.Corners)
        {
            w.Write((short)Math.Round(s));
            w.Write((short)Math.Round(t));
        }

        foreach (var (a, b, c, ta, tb, tc) in model.Triangles)
        {
            foreach (var i in new[] { a, b, c, ta, tb, tc }) w.Write(checked((ushort)i));
        }

        foreach (var (frameName, p) in model.Frames)
        {
            float minX = p.Min(v => v.X), minY = p.Min(v => v.Y), minZ = p.Min(v => v.Z);
            float sx = Math.Max((p.Max(v => v.X) - minX) / 255f, 1e-6f), sy = Math.Max((p.Max(v => v.Y) - minY) / 255f, 1e-6f), sz = Math.Max((p.Max(v => v.Z) - minZ) / 255f, 1e-6f);
            w.Write(sx); w.Write(sy); w.Write(sz);
            w.Write(minX); w.Write(minY); w.Write(minZ);
            var nameBytes = new byte[16];
            Encoding.ASCII.GetBytes(frameName, 0, Math.Min(15, frameName.Length), nameBytes, 0);
            w.Write(nameBytes);
            foreach (var (x, y, z) in p)
            {
                w.Write((byte)Math.Clamp(Math.Round((x - minX) / sx), 0, 255));
                w.Write((byte)Math.Clamp(Math.Round((y - minY) / sy), 0, 255));
                w.Write((byte)Math.Clamp(Math.Round((z - minZ) / sz), 0, 255));
                w.Write((byte)0);
            }
        }

        // The GL commands: a strip of three per triangle (its count, then each corner's skin place in 0–1 and its vertex), then 0.
        foreach (var (a, b, c, ta, tb, tc) in model.Triangles)
        {
            w.Write(3);
            foreach (var (vertex, corner) in new[] { (a, ta), (b, tb), (c, tc) })
            {
                w.Write(model.Corners[corner].S / Math.Max(1, model.SkinWidth));
                w.Write(model.Corners[corner].T / Math.Max(1, model.SkinHeight));
                w.Write(vertex);
            }
        }

        w.Write(0);
        return body.ToArray();
    }
}
