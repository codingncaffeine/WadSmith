using WadSmith.Core.Graphics;

namespace WadSmith.Core.Models;

/// <summary>
/// Draws a model into Doom-style sprite pictures (D42's sprite baking): seen level from each of the eight sprite rotations,
/// one pixel per map unit, picture colours read nearest (the hard-edged look of the games' own sprites), lit from above
/// the viewer's shoulder. The pictures stand in for the model where it is not drawn: the plan, the shelves, renderers
/// without models, and classic ports.
/// </summary>
public static class MeshBaker
{
    /// <summary>
    /// Doom's sprite rotation <paramref name="rotation"/> (1 = seen from the front, 3 = from its left, 5 = from behind) of
    /// <paramref name="mesh"/> at <paramref name="scale"/> map units per model unit, the origin at <paramref name="origin"/> in
    /// model units (the middle of its base). Heights are divided by <paramref name="stretch"/>, as GZDoom draws a model
    /// (1.2 for the Doom games' tall pixels). The picture's offsets put the origin at its foot.
    /// </summary>
    public static RgbaImage Bake(Mesh mesh, double scale, (float X, float Y, float Z) origin, int rotation, double stretch = 1.2)
    {
        // The viewer stands (rotation - 1) × 45° anticlockwise from where the model faces (+X) and looks at it; its right hand
        // points along (-sin, cos).
        var angle = (rotation - 1) * Math.PI / 4;
        float dx = (float)Math.Cos(angle), dy = (float)Math.Sin(angle), rx = -dy, ry = dx;
        float sx = (float)scale, sz = (float)(scale / stretch);

        (float X, float Y, float Depth) Project(MeshVertex v)
        {
            float px = v.X - origin.X, py = v.Y - origin.Y;
            return ((px * rx + py * ry) * sx, -(v.Z - origin.Z) * sz, (px * dx + py * dy) * sx);
        }

        float left = float.MaxValue, right = float.MinValue, top = float.MaxValue, bottom = float.MinValue;
        foreach (var surface in mesh.Surfaces)
        {
            foreach (var v in surface.Triangles)
            {
                var p = Project(v);
                left = MathF.Min(left, p.X); right = MathF.Max(right, p.X);
                top = MathF.Min(top, p.Y); bottom = MathF.Max(bottom, p.Y);
            }
        }

        if (left > right) return new RgbaImage(1, 1);
        var x0 = (int)MathF.Floor(left);
        var y0 = (int)MathF.Floor(top);
        var width = Math.Clamp((int)MathF.Ceiling(right) - x0 + 1, 1, 4096);
        var height = Math.Clamp((int)MathF.Ceiling(bottom) - y0 + 1, 1, 4096);
        var image = new RgbaImage(width, height) { OffsetX = -x0, OffsetY = -y0 };
        var depth = new float[width * height];
        Array.Fill(depth, float.MinValue);

        // The light comes from above and behind the viewer's right shoulder.
        var (lx, ly, lz) = Normalise(dx * 0.6f + rx * 0.35f, dy * 0.6f + ry * 0.35f, 0.75f);
        foreach (var surface in mesh.Surfaces)
        {
            var t = surface.Triangles;
            for (var i = 0; i + 2 < t.Count; i += 3)
            {
                var (a, b, c) = (t[i], t[i + 1], t[i + 2]);
                // The face's light, both sides lit alike (models are often single-sided where they meet the eye).
                var (nx, ny, nz) = Normalise(
                    (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y),
                    (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z),
                    (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                var shade = 0.45f + 0.55f * MathF.Abs(nx * lx + ny * ly + nz * lz);
                Fill(image, depth, Project(a), Project(b), Project(c), a, b, c, x0, y0, surface.Image, shade);
            }
        }

        return image;
    }

    private static void Fill(RgbaImage image, float[] depth, (float X, float Y, float Depth) pa, (float X, float Y, float Depth) pb, (float X, float Y, float Depth) pc, MeshVertex a, MeshVertex b, MeshVertex c, int x0, int y0, RgbaImage? picture, float shade)
    {
        var area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
        if (MathF.Abs(area) < 1e-9f) return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))) - x0);
        var maxX = Math.Min(image.Width - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))) - x0);
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))) - y0);
        var maxY = Math.Min(image.Height - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))) - y0);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                // Pixel centres, so a picture's row of pixels samples where it sits.
                float px = x + x0 + 0.5f, py = y + y0 + 0.5f;
                var wa = ((pb.X - px) * (pc.Y - py) - (pb.Y - py) * (pc.X - px)) / area;
                var wb = ((pc.X - px) * (pa.Y - py) - (pc.Y - py) * (pa.X - px)) / area;
                var wc = 1 - wa - wb;
                if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f) continue;
                var z = wa * pa.Depth + wb * pb.Depth + wc * pc.Depth;
                var at = y * image.Width + x;
                if (z <= depth[at]) continue;
                byte r = 170, g = 170, bl = 170;
                if (picture is not null)
                {
                    var u = wa * a.U + wb * b.U + wc * c.U;
                    var v = wa * a.V + wb * b.V + wc * c.V;
                    var tx = (int)MathF.Floor((u - MathF.Floor(u)) * picture.Width) % picture.Width;
                    var ty = (int)MathF.Floor((v - MathF.Floor(v)) * picture.Height) % picture.Height;
                    var (cr, cg, cb, ca) = picture.Get(tx, ty);
                    if (ca < 128) continue;
                    (r, g, bl) = (cr, cg, cb);
                }

                depth[at] = z;
                image.Set(x, y, Light(r, shade), Light(g, shade), Light(bl, shade));
            }
        }
    }

    private static byte Light(byte value, float shade) => (byte)Math.Clamp((int)MathF.Round(value * shade), 0, 255);

    private static (float, float, float) Normalise(float x, float y, float z)
    {
        var length = MathF.Sqrt(x * x + y * y + z * z);
        return length < 1e-12f ? (0, 0, 1) : (x / length, y / length, z / length);
    }
}
