using WadSmith.Core.Graphics;

namespace WadSmith.Core.Models;

/// <summary>A corner of a triangle: where it is in the model's own units and where it reads its picture (0..1, top left first).</summary>
public readonly record struct MeshVertex(float X, float Y, float Z, float U, float V);

/// <summary>
/// The triangles of a model drawn with one picture. <see cref="Picture"/> is the file name the surface is skinned by in the
/// project; <see cref="Image"/> the decoded picture, null when the model named none or it could not be read.
/// </summary>
public sealed class MeshSurface
{
    public MeshSurface(string name, IReadOnlyList<MeshVertex> triangles, string? picture, RgbaImage? image)
    {
        Name = name;
        Triangles = triangles;
        Picture = picture;
        Image = image;
    }

    /// <summary>The surface's name in the source file (an OBJ material, an MD3 surface).</summary>
    public string Name { get; }

    /// <summary>Three corners per triangle, wound as the file had them.</summary>
    public IReadOnlyList<MeshVertex> Triangles { get; }

    public string? Picture { get; }

    public RgbaImage? Image { get; }
}

/// <summary>
/// A model's shape in GZDoom's model axes (D16, D42): X forward (an actor at angle 0 faces east), Y to its left (north), Z
/// up, in the model's own units. OBJ files are read the way GZDoom reads them (Y up, Z flipped), MD3 and IQM as they are,
/// so what WadSmith shows and bakes faces the way the engine draws it.
/// </summary>
public sealed class Mesh
{
    public Mesh(IReadOnlyList<MeshSurface> surfaces)
    {
        Surfaces = surfaces;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var s in surfaces)
        {
            foreach (var v in s.Triangles)
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }
        }

        if (minX > maxX) minX = maxX = minY = maxY = minZ = maxZ = 0;
        Min = (minX, minY, minZ);
        Max = (maxX, maxY, maxZ);
    }

    public IReadOnlyList<MeshSurface> Surfaces { get; }

    public (float X, float Y, float Z) Min { get; }

    public (float X, float Y, float Z) Max { get; }

    public int TriangleCount => Surfaces.Sum(s => s.Triangles.Count / 3);

    /// <summary>The widest distance from the upright axis through the model's middle, in model units.</summary>
    public float Reach
    {
        get
        {
            var cx = (Min.X + Max.X) / 2;
            var cy = (Min.Y + Max.Y) / 2;
            var r = 0f;
            foreach (var s in Surfaces)
            {
                foreach (var v in s.Triangles) r = MathF.Max(r, MathF.Sqrt((v.X - cx) * (v.X - cx) + (v.Y - cy) * (v.Y - cy)));
            }

            return r;
        }
    }
}

/// <summary>Turning a mesh about its upright axis.</summary>
public static class MeshTurning
{
    /// <summary>The mesh turned anticlockwise (seen from above) by <paramref name="degrees"/> about the model's own origin, as MODELDEF's AngleOffset turns it.</summary>
    public static Mesh Turned(this Mesh mesh, double degrees)
    {
        if (degrees % 360 == 0) return mesh;
        var a = degrees * Math.PI / 180;
        var (cos, sin) = ((float)Math.Round(Math.Cos(a), 12), (float)Math.Round(Math.Sin(a), 12));
        return new Mesh([.. mesh.Surfaces.Select(s => new MeshSurface(s.Name, [.. s.Triangles.Select(v => v with { X = v.X * cos - v.Y * sin, Y = v.X * sin + v.Y * cos })], s.Picture, s.Image))]);
    }
}
