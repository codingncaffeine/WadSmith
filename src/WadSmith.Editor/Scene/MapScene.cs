using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Scene;

/// <summary>What a batch of triangles is, which decides where its picture comes from and how it is drawn.</summary>
public enum SurfaceKind
{
    /// <summary>A wall part, drawn with a wall texture.</summary>
    Wall,

    /// <summary>A floor, drawn with a flat.</summary>
    Floor,

    /// <summary>A ceiling, drawn with a flat.</summary>
    Ceiling,

    /// <summary>Open sky: drawn with the sky, whatever its flat says.</summary>
    Sky,
}

/// <summary>
/// Triangles drawn with one picture. Each corner is six numbers: x, y, z in map units (z up), u, v in
/// texture pixels from the picture's top-left, and the light level (0–255) of the area it belongs to.
/// </summary>
public sealed class SceneBatch(string texture, SurfaceKind kind)
{
    public const int Stride = 6;

    public string Texture { get; } = texture;

    public SurfaceKind Kind { get; } = kind;

    public List<float> Vertices { get; } = [];

    public int VertexCount => Vertices.Count / Stride;

    internal void Add(double x, double y, double z, double u, double v, int light)
    {
        Vertices.Add((float)x);
        Vertices.Add((float)y);
        Vertices.Add((float)z);
        Vertices.Add((float)u);
        Vertices.Add((float)v);
        Vertices.Add(light);
    }
}

/// <summary>An object standing in the 3D view: where its feet are and which way it faces.</summary>
public readonly record struct SceneThing(Thing Element, float X, float Y, float Z, int Type, int Angle);

/// <summary>
/// The map as triangles for the 3D view (D46), built on a worker from one <see cref="MapGeometry"/>:
/// every wall part with its texture anchored as Doom anchors it (pegging and offsets), floors and
/// ceilings cut into triangles, open sky marked, every corner lit by its area. Walls face only into the
/// area they belong to and ceilings face down, so from outside or above the rooms show like a dollhouse.
/// </summary>
public sealed class MapScene
{
    private MapScene(long version, IReadOnlyList<SceneBatch> batches, IReadOnlyList<SceneThing> things, (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) bounds)
    {
        Version = version;
        Batches = batches;
        Things = things;
        Bounds = bounds;
    }

    public long Version { get; }

    public IReadOnlyList<SceneBatch> Batches { get; }

    public IReadOnlyList<SceneThing> Things { get; }

    public (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds { get; }

    /// <summary>The flat that means open sky in every Doom-engine game.</summary>
    public static bool IsSky(string flat) => flat.Equals("F_SKY1", StringComparison.OrdinalIgnoreCase) || flat.Equals("F_SKY", StringComparison.OrdinalIgnoreCase);

    /// <param name="geometry">The map's shapes.</param>
    /// <param name="wallSize">A wall texture's size in pixels, or null when the game has no such texture.</param>
    public static MapScene Build(MapGeometry geometry, Func<string, (int Width, int Height)?> wallSize)
    {
        var f = geometry.Frame;
        var batches = new Dictionary<(string, SurfaceKind), SceneBatch>();
        SceneBatch Batch(string texture, SurfaceKind kind)
        {
            var key = (texture.ToUpperInvariant(), kind);
            if (!batches.TryGetValue(key, out var b)) batches[key] = b = new SceneBatch(key.Item1, kind);
            return b;
        }

        double minZ = double.MaxValue, maxZ = double.MinValue;

        // Floors and ceilings.
        for (var s = 0; s < f.Sectors.Count; s++)
        {
            var sector = f.Sectors[s];
            if (sector.Element is null) continue;
            var loops = geometry.LoopsAt(s);
            if (loops.Count == 0) continue;
            minZ = Math.Min(minZ, sector.FloorHeight);
            maxZ = Math.Max(maxZ, sector.CeilingHeight);
            var triangles = Triangulator.Triangulate(loops);
            var light = Math.Clamp(sector.Light, 0, 255);
            var floor = Batch(sector.FloorTexture, IsSky(sector.FloorTexture) ? SurfaceKind.Sky : SurfaceKind.Floor);
            var ceiling = Batch(sector.CeilingTexture, IsSky(sector.CeilingTexture) ? SurfaceKind.Sky : SurfaceKind.Ceiling);
            for (var i = 0; i < triangles.Count; i += 3)
            {
                // Flats line up with the map's grid: texture x is map x, texture y is minus map y.
                foreach (var p in (MapPoint[])[triangles[i], triangles[i + 1], triangles[i + 2]]) floor.Add(p.X, p.Y, sector.FloorHeight, p.X, -p.Y, light);

                // Ceilings face down: the same triangles the other way round.
                foreach (var p in (MapPoint[])[triangles[i + 2], triangles[i + 1], triangles[i]]) ceiling.Add(p.X, p.Y, sector.CeilingHeight, p.X, -p.Y, light);
            }
        }

        // Walls: each face of each line, as seen from the area it faces.
        foreach (var l in f.Lines)
        {
            if (l.Element is null) continue;
            var a = f.Vertices[l.V1];
            var b = f.Vertices[l.V2];
            var front = f.Sides[l.Front];
            var back = l.Back >= 0 ? f.Sides[l.Back] : (SideFrame?)null;
            var upperUnpegged = Flag(l.Extra, "dontpegtop");
            var lowerUnpegged = Flag(l.Extra, "dontpegbottom");
            AddFace(new MapPoint(a.X, a.Y), new MapPoint(b.X, b.Y), front, back);
            if (back is { } bs) AddFace(new MapPoint(b.X, b.Y), new MapPoint(a.X, a.Y), bs, front);

            void AddFace(MapPoint from, MapPoint to, SideFrame side, SideFrame? other)
            {
                if (side.Sector < 0) return;
                var here = f.Sectors[side.Sector];
                var light = Math.Clamp(here.Light, 0, 255);
                var length = from.DistanceTo(to);
                if (other is not { Sector: >= 0 } o)
                {
                    // One-sided: the middle texture from floor to ceiling, hung from the ceiling unless
                    // anchored at the bottom.
                    var h = Height(side.Middle);
                    var top = lowerUnpegged ? here.FloorHeight + h : here.CeilingHeight;
                    Quad(side.Middle, from, to, here.FloorHeight, here.CeilingHeight, top, side, length, light);
                    return;
                }

                var there = f.Sectors[o.Sector];

                // Upper part, where this side's ceiling is higher; none between two skies (Doom's sky trick).
                if (here.CeilingHeight > there.CeilingHeight && !(IsSky(here.CeilingTexture) && IsSky(there.CeilingTexture)))
                {
                    var top = upperUnpegged ? here.CeilingHeight : there.CeilingHeight + Height(side.Top);
                    Quad(side.Top, from, to, there.CeilingHeight, here.CeilingHeight, top, side, length, light);
                }

                // Lower part, where the floor beyond is higher.
                if (there.FloorHeight > here.FloorHeight)
                {
                    var top = lowerUnpegged ? here.CeilingHeight : there.FloorHeight;
                    Quad(side.Bottom, from, to, here.FloorHeight, there.FloorHeight, top, side, length, light);
                }

                // A middle texture on a two-sided wall (a grate, a fence) is drawn once, not repeated up the wall.
                if (side.Middle is not ("-" or ""))
                {
                    var bottom = Math.Max(here.FloorHeight, there.FloorHeight);
                    var ceiling = Math.Min(here.CeilingHeight, there.CeilingHeight);
                    var h = Height(side.Middle);
                    var top = (lowerUnpegged ? bottom + h : ceiling) + side.OffsetY;
                    var z0 = Math.Max(bottom, top - h);
                    var z1 = Math.Min(ceiling, top);
                    if (z1 > z0) Quad(side.Middle, from, to, z0, z1, top - side.OffsetY, side, length, light);
                }
            }
        }

        var things = new List<SceneThing>();
        foreach (var t in f.Things)
        {
            if (t.Element is null) continue;
            var floor = geometry.SectorAt(new MapPoint(t.X, t.Y)) is { } sector && sector.Slot < f.Sectors.Count ? f.Sectors[sector.Slot].FloorHeight : 0;
            things.Add(new SceneThing(t.Element, (float)t.X, (float)t.Y, (float)(floor + t.Height), t.Type, t.Angle));
        }

        var (x0, y0, x1, y1) = geometry.Extent();
        if (minZ > maxZ) (minZ, maxZ) = (0, 128);
        return new MapScene(f.Version, [.. batches.Values.Where(b => b.VertexCount > 0)], things, (x0, y0, minZ, x1, y1, maxZ));

        double Height(string texture) => texture is "-" or "" ? 128 : wallSize(texture)?.Height ?? 128;

        // A wall part from z0 to z1 whose texture's top row sits at height top (before the side's offsets).
        void Quad(string texture, MapPoint from, MapPoint to, double z0, double z1, double top, SideFrame side, double length, int light)
        {
            if (z1 <= z0) return;
            var batch = Batch(texture is "" ? "-" : texture, SurfaceKind.Wall);
            var u0 = side.OffsetX;
            var u1 = side.OffsetX + length;
            double V(double z) => top - z + side.OffsetY;

            // Counter-clockwise as seen from the area the face belongs to.
            batch.Add(from.X, from.Y, z0, u0, V(z0), light);
            batch.Add(to.X, to.Y, z0, u1, V(z0), light);
            batch.Add(to.X, to.Y, z1, u1, V(z1), light);
            batch.Add(from.X, from.Y, z0, u0, V(z0), light);
            batch.Add(to.X, to.Y, z1, u1, V(z1), light);
            batch.Add(from.X, from.Y, z1, u0, V(z1), light);
        }
    }

    private static bool Flag(KeyValuePair<string, UdmfValue>[] extra, string name)
    {
        foreach (var (k, v) in extra)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v.Kind == UdmfValueKind.Boolean && v.AsBool();
        }

        return false;
    }
}
