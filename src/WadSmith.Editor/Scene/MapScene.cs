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

/// <summary>The part of a wall's face a texture sits on: the upper, where the ceiling beyond is lower; the middle; the lower, where the floor beyond is higher.</summary>
public enum WallPart
{
    Top,
    Middle,
    Bottom,
}

/// <summary>What a ray through the 3D view met: a wall's face and its part, a floor or a ceiling (sky or not), or an object.</summary>
public enum HitKind
{
    Wall,
    Floor,
    Ceiling,
    Thing,
}

/// <summary>
/// A surface met by a ray through the 3D view (<see cref="MapScene.Hit"/>): the element (a face, an area, an object), which
/// surface of it, whether it shows the sky, and where along the ray.
/// </summary>
public readonly record struct SceneHit(Element Element, HitKind Kind, WallPart Part, bool Sky, double Distance, double X, double Y, double Z)
{
    /// <summary>The surface a texture dropped here goes on.</summary>
    public SurfaceTarget Target => new(Element, Part, Kind == HitKind.Ceiling);

    /// <summary>The surface in words: "the upper part of a wall", "a floor", "an object".</summary>
    public string Words => Kind switch
    {
        HitKind.Wall => Part switch { WallPart.Top => "the upper part of this wall", WallPart.Bottom => "the lower part of this wall", _ => "this wall" },
        HitKind.Floor => Sky ? "this floor (open sky)" : "this floor",
        HitKind.Ceiling => Sky ? "this ceiling (open sky)" : "this ceiling",
        _ => "this object",
    };
}

/// <summary>A surface a texture goes on: a face's part, or an area's floor or ceiling.</summary>
public readonly record struct SurfaceTarget(Element Element, WallPart Part, bool Ceiling);

/// <summary>
/// Triangles drawn with one picture. Each corner is eight numbers: x, y, z in map units (z up), u, v in texture
/// pixels from the picture's top-left, the light level (0–255) of the surface, and the area's light colour and fog
/// colour, each a 24-bit RGB number (white light and no fog unless the area says otherwise).
/// </summary>
public sealed class SceneBatch(string texture, SurfaceKind kind)
{
    public const int Stride = 8;

    public string Texture { get; } = texture;

    public SurfaceKind Kind { get; } = kind;

    public List<float> Vertices { get; } = [];

    /// <summary>The element each triangle belongs to (a wall's face, a floor's or ceiling's area), in triangle order.</summary>
    public List<Element> Owners { get; } = [];

    /// <summary>For wall triangles, the wall the face belongs to, in triangle order; empty for floors and ceilings.</summary>
    public List<Line> Walls { get; } = [];

    /// <summary>Which surface each triangle is, in triangle order: a wall's part (<see cref="WallPart"/>), or 0 for a floor and 1 for a ceiling.</summary>
    public List<byte> Parts { get; } = [];

    public int VertexCount => Vertices.Count / Stride;

    internal void Add(double x, double y, double z, double u, double v, int light, Tint tint)
    {
        Vertices.Add((float)x);
        Vertices.Add((float)y);
        Vertices.Add((float)z);
        Vertices.Add((float)u);
        Vertices.Add((float)v);
        Vertices.Add(light);
        Vertices.Add(tint.LightColour);
        Vertices.Add(tint.FadeColour);
    }
}

/// <summary>An area's light colour and fog colour (GZDoom's lightcolor and fadecolor), each a 24-bit RGB number.</summary>
public readonly record struct Tint(int LightColour, int FadeColour)
{
    public static readonly Tint Plain = new(0xFFFFFF, 0);

    /// <summary>The tint an area's fields give it.</summary>
    public static Tint Of(KeyValuePair<string, UdmfValue>[] extra) => new(Int(extra, "lightcolor", 0xFFFFFF), Int(extra, "fadecolor", 0));

    /// <summary>A surface's light: the area's, moved by its own offset (lightfloor, lightceiling, a side's light) or replaced by it when absolute.</summary>
    public static int Light(int areaLight, KeyValuePair<string, UdmfValue>[] extra, string offset, string absolute)
    {
        var value = Int(extra, offset, 0);
        return Math.Clamp(Bool(extra, absolute) ? value : areaLight + value, 0, 255);
    }

    internal static int Int(KeyValuePair<string, UdmfValue>[] extra, string name, int otherwise)
    {
        foreach (var (k, v) in extra)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) && v.Kind == UdmfValueKind.Integer) return (int)Math.Clamp(v.AsInt(), 0, 0xFFFFFF);
        }

        return otherwise;
    }

    private static bool Bool(KeyValuePair<string, UdmfValue>[] extra, string name)
    {
        foreach (var (k, v) in extra)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v.Kind == UdmfValueKind.Boolean && v.AsBool();
        }

        return false;
    }
}

/// <summary>An object standing in the 3D view: where its feet are, which way it faces, and its area's light.</summary>
public readonly record struct SceneThing(Thing Element, float X, float Y, float Z, int Type, int Angle, int Light);

/// <summary>
/// A light the 3D view shows: where it hangs, its colour (0–1 each; negative takes light away), how far it reaches as
/// GZDoom draws it (twice its size argument), and whether it is soft (lights each surface by the way it faces).
/// </summary>
public readonly record struct SceneLight(float X, float Y, float Z, float R, float G, float B, float Reach, bool Soft);

/// <summary>A sprite's picture as the 3D view needs it: size in pixels, the foot point (Doom's offsets) and its scale.</summary>
public readonly record struct SpriteShape(int Width, int Height, int OffsetX, int OffsetY, double ScaleX, double ScaleY);

/// <summary>
/// The objects of one type, drawn with that type's sprite: per corner ten numbers, the foot point
/// x, y, z, the corner's offset across and up in map units (turned to face the camera when drawn),
/// u, v in texture pixels, the light, and the area's light colour and fog colour.
/// </summary>
public sealed class SpriteBatch(int type)
{
    public const int Stride = 10;

    public int Type { get; } = type;

    public List<float> Vertices { get; } = [];

    /// <summary>The object each pair of triangles shows.</summary>
    public List<Thing> Owners { get; } = [];

    public int VertexCount => Vertices.Count / Stride;
}

/// <summary>
/// The map as triangles for the 3D view (D46), built on a worker from one <see cref="MapGeometry"/>:
/// every wall part with its texture anchored as Doom anchors it (pegging and offsets), floors and
/// ceilings cut into triangles, open sky marked, every corner lit by its area. Walls face only into the
/// area they belong to and ceilings face down, so from outside or above the rooms show like a dollhouse.
/// </summary>
public sealed class MapScene
{
    /// <summary>
    /// The two triangles of a sprite whose foot stands at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), in
    /// <see cref="SpriteBatch"/>'s layout. Doom's offsets: the picture's left edge sits OffsetX pixels left of the foot point,
    /// its top OffsetY above it.
    /// </summary>
    public static float[] SpriteQuad(double x, double y, double z, SpriteShape shape, float light) => SpriteQuad(x, y, z, shape, light, Tint.Plain);

    public static float[] SpriteQuad(double x, double y, double z, SpriteShape shape, float light, Tint tint)
    {
        var left = -shape.OffsetX * shape.ScaleX;
        var right = (shape.Width - shape.OffsetX) * shape.ScaleX;
        var top = shape.OffsetY * shape.ScaleY;
        var bottom = (shape.OffsetY - shape.Height) * shape.ScaleY;
        var quad = new List<float>(6 * SpriteBatch.Stride);
        void Corner(double across, double up, double u, double v) => quad.AddRange([(float)x, (float)y, (float)z, (float)across, (float)up, (float)u, (float)v, light, tint.LightColour, tint.FadeColour]);
        Corner(left, bottom, 0, shape.Height);
        Corner(right, bottom, shape.Width, shape.Height);
        Corner(right, top, shape.Width, 0);
        Corner(left, bottom, 0, shape.Height);
        Corner(right, top, shape.Width, 0);
        Corner(left, top, 0, 0);
        return [.. quad];
    }

    private MapScene(long version, IReadOnlyList<SceneBatch> batches, IReadOnlyList<SpriteBatch> sprites, IReadOnlyList<SceneThing> things, IReadOnlyList<SceneLight> lights, (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) bounds)
    {
        Version = version;
        Batches = batches;
        Sprites = sprites;
        Things = things;
        Lights = lights;
        Bounds = bounds;
    }

    /// <summary>The map's point lights (GZDoom's dynamic lights), steady at their first reach.</summary>
    public IReadOnlyList<SceneLight> Lights { get; }

    public long Version { get; }

    public IReadOnlyList<SceneBatch> Batches { get; }

    /// <summary>The objects that show a sprite, one batch per object type.</summary>
    public IReadOnlyList<SpriteBatch> Sprites { get; }

    public IReadOnlyList<SceneThing> Things { get; }

    public (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds { get; }

    /// <summary>
    /// What a ray from <paramref name="origin"/> along <paramref name="direction"/> meets first: a wall's
    /// face (the side seen), a floor's or ceiling's area, or an object, counting only surfaces that face
    /// the ray (the ones the view draws). Sprites turn to face a camera looking along <paramref name="cameraYaw"/>.
    /// Tests every triangle: call it on a worker.
    /// </summary>
    public Element? Pick((double X, double Y, double Z) origin, (double X, double Y, double Z) direction, double cameraYaw) => Hit(origin, direction, cameraYaw)?.Element;

    /// <summary>
    /// What a ray meets first (as <see cref="Pick"/>), with which surface of it and where: a wall's face and its part, a
    /// floor or ceiling (sky or not), or an object. Tests every triangle: call it on a worker.
    /// </summary>
    public SceneHit? Hit((double X, double Y, double Z) origin, (double X, double Y, double Z) direction, double cameraYaw)
    {
        SceneHit? best = null;
        var bestT = double.MaxValue;
        foreach (var b in Batches)
        {
            var v = b.Vertices;
            for (var i = 0; i < v.Count; i += SceneBatch.Stride * 3)
            {
                const int s = SceneBatch.Stride;
                var t = Distance(origin, direction, (v[i], v[i + 1], v[i + 2]), (v[i + s], v[i + s + 1], v[i + s + 2]), (v[i + 2 * s], v[i + 2 * s + 1], v[i + 2 * s + 2]), facing: true);
                if (t <= 0 || t >= bestT) continue;
                bestT = t;
                var tri = i / (s * 3);
                var part = b.Parts[tri];
                var kind = b.Kind == SurfaceKind.Wall ? HitKind.Wall : part == 1 ? HitKind.Ceiling : HitKind.Floor;
                best = new SceneHit(b.Owners[tri], kind, b.Kind == SurfaceKind.Wall ? (WallPart)part : WallPart.Middle, b.Kind == SurfaceKind.Sky, t, origin.X + direction.X * t, origin.Y + direction.Y * t, origin.Z + direction.Z * t);
            }
        }

        var yaw = cameraYaw * Math.PI / 180;
        var (ax, ay) = (Math.Sin(yaw), -Math.Cos(yaw));
        foreach (var b in Sprites)
        {
            var v = b.Vertices;
            for (var i = 0; i < v.Count; i += SpriteBatch.Stride * 3)
            {
                (double, double, double) Corner(int k) => (v[k] + ax * v[k + 3], v[k + 1] + ay * v[k + 3], v[k + 2] + v[k + 4]);
                var t = Distance(origin, direction, Corner(i), Corner(i + SpriteBatch.Stride), Corner(i + 2 * SpriteBatch.Stride), facing: false);
                if (t <= 0 || t >= bestT) continue;
                bestT = t;
                best = new SceneHit(b.Owners[i / (SpriteBatch.Stride * 6)], HitKind.Thing, WallPart.Middle, false, t, origin.X + direction.X * t, origin.Y + direction.Y * t, origin.Z + direction.Z * t);
            }
        }

        return best;
    }

    /// <summary>The triangles of one surface (the hovered one), in <see cref="Highlight"/>'s layout: a face's part alone, a floor or ceiling alone, an object's sprite.</summary>
    public (float[] Surfaces, Dictionary<int, float[]> Sprites) Surface(SceneHit hit)
    {
        var surfaces = new List<float>();
        var sprites = new Dictionary<int, float[]>();
        if (hit.Kind == HitKind.Thing)
        {
            foreach (var b in Sprites)
            {
                for (var k = 0; k < b.Owners.Count; k++)
                {
                    if (ReferenceEquals(b.Owners[k], hit.Element)) sprites[b.Type] = [.. b.Vertices.GetRange(k * SpriteBatch.Stride * 6, SpriteBatch.Stride * 6)];
                }
            }

            return ([], sprites);
        }

        var part = hit.Kind == HitKind.Wall ? (byte)hit.Part : hit.Kind == HitKind.Ceiling ? (byte)1 : (byte)0;
        foreach (var b in Batches)
        {
            if ((b.Kind == SurfaceKind.Wall) != (hit.Kind == HitKind.Wall)) continue;
            for (var tri = 0; tri < b.Owners.Count; tri++)
            {
                if (ReferenceEquals(b.Owners[tri], hit.Element) && b.Parts[tri] == part) surfaces.AddRange(b.Vertices.GetRange(tri * SceneBatch.Stride * 3, SceneBatch.Stride * 3));
            }
        }

        return ([.. surfaces], sprites);
    }

    /// <summary>
    /// The triangles of the given elements, for tinting the selection in the view: wall and floor
    /// corners in the batches' layout, and per object type the sprite corners. A face shows alone; a
    /// wall shows both its faces.
    /// </summary>
    public (float[] Surfaces, Dictionary<int, float[]> Sprites) Highlight(IReadOnlySet<Element> elements)
    {
        var surfaces = new List<float>();
        foreach (var b in Batches)
        {
            var walls = b.Walls.Count == b.Owners.Count;
            for (var tri = 0; tri < b.Owners.Count; tri++)
            {
                if (!elements.Contains(b.Owners[tri]) && !(walls && elements.Contains(b.Walls[tri]))) continue;
                surfaces.AddRange(b.Vertices.GetRange(tri * SceneBatch.Stride * 3, SceneBatch.Stride * 3));
            }
        }

        var sprites = new Dictionary<int, float[]>();
        foreach (var b in Sprites)
        {
            var list = new List<float>();
            for (var k = 0; k < b.Owners.Count; k++)
            {
                if (elements.Contains(b.Owners[k])) list.AddRange(b.Vertices.GetRange(k * SpriteBatch.Stride * 6, SpriteBatch.Stride * 6));
            }

            if (list.Count > 0) sprites[b.Type] = [.. list];
        }

        return ([.. surfaces], sprites);
    }

    /// <summary>Möller–Trumbore: distance along the ray to the triangle, or 0 when missed (or, if <paramref name="facing"/>, seen from behind).</summary>
    private static double Distance((double X, double Y, double Z) o, (double X, double Y, double Z) d, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c, bool facing)
    {
        var e1 = (X: b.X - a.X, Y: b.Y - a.Y, Z: b.Z - a.Z);
        var e2 = (X: c.X - a.X, Y: c.Y - a.Y, Z: c.Z - a.Z);
        var p = (X: d.Y * e2.Z - d.Z * e2.Y, Y: d.Z * e2.X - d.X * e2.Z, Z: d.X * e2.Y - d.Y * e2.X);
        var det = e1.X * p.X + e1.Y * p.Y + e1.Z * p.Z;

        // Counter-clockwise triangles face the viewer: det > 0 when the ray meets the front.
        if (facing ? det <= 1e-9 : Math.Abs(det) <= 1e-9) return 0;
        var inv = 1 / det;
        var s = (X: o.X - a.X, Y: o.Y - a.Y, Z: o.Z - a.Z);
        var u = (s.X * p.X + s.Y * p.Y + s.Z * p.Z) * inv;
        if (u < 0 || u > 1) return 0;
        var q = (X: s.Y * e1.Z - s.Z * e1.Y, Y: s.Z * e1.X - s.X * e1.Z, Z: s.X * e1.Y - s.Y * e1.X);
        var v = (d.X * q.X + d.Y * q.Y + d.Z * q.Z) * inv;
        if (v < 0 || u + v > 1) return 0;
        var t = (e2.X * q.X + e2.Y * q.Y + e2.Z * q.Z) * inv;
        return t > 1e-6 ? t : 0;
    }

    /// <summary>The flat that means open sky in every Doom-engine game.</summary>
    public static bool IsSky(string flat) => flat.Equals("F_SKY1", StringComparison.OrdinalIgnoreCase) || flat.Equals("F_SKY", StringComparison.OrdinalIgnoreCase);

    /// <param name="geometry">The map's shapes.</param>
    /// <param name="wallSize">A wall texture's size in pixels, or null when the game has no such texture.</param>
    /// <param name="sprite">An object type's sprite shape, or null when it shows none.</param>
    /// <param name="hidden">Groups hidden for clarity: their rooms, the faces seen from them and their objects are left out.</param>
    public static MapScene Build(MapGeometry geometry, Func<string, (int Width, int Height)?> wallSize, Func<int, SpriteShape?>? sprite = null, IReadOnlySet<string>? hidden = null)
    {
        var f = geometry.Frame;

        // Groups hidden for clarity: their rooms, the faces seen from them, and their objects are left out.
        bool Hidden(KeyValuePair<string, WadSmith.Core.Maps.Udmf.UdmfValue>[] extra) => hidden is { Count: > 0 } && Editing.Grouping.GroupIn(extra) is { } name && hidden.Contains(name);
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
            if (sector.Element is null || Hidden(sector.Extra)) continue;
            var loops = geometry.LoopsAt(s);
            if (loops.Count == 0) continue;
            minZ = Math.Min(minZ, sector.FloorHeight);
            maxZ = Math.Max(maxZ, sector.CeilingHeight);
            var triangles = Triangulator.Triangulate(loops);
            var light = Math.Clamp(sector.Light, 0, 255);
            var tint = Tint.Of(sector.Extra);
            var (floorLight, ceilingLight) = (Tint.Light(light, sector.Extra, "lightfloor", "lightfloorabsolute"), Tint.Light(light, sector.Extra, "lightceiling", "lightceilingabsolute"));
            var floor = Batch(sector.FloorTexture, IsSky(sector.FloorTexture) ? SurfaceKind.Sky : SurfaceKind.Floor);
            var ceiling = Batch(sector.CeilingTexture, IsSky(sector.CeilingTexture) ? SurfaceKind.Sky : SurfaceKind.Ceiling);
            var owner = sector.Element;

            // Flats line up with the map's grid (texture x is map x, texture y minus map y), turned, moved and scaled by the
            // area's own fields as GZDoom does.
            var (onFloor, onCeiling) = (FlatTransform.Of(sector.Extra, false), FlatTransform.Of(sector.Extra, true));
            var (floorPlane, ceilingPlane) = (SlopePlane.Floor(sector.Extra), SlopePlane.Ceiling(sector.Extra));
            for (var i = 0; i < triangles.Count; i += 3)
            {
                floor.Owners.Add(owner);
                ceiling.Owners.Add(owner);
                floor.Parts.Add(0);
                ceiling.Parts.Add(1);
                foreach (var p in (MapPoint[])[triangles[i], triangles[i + 1], triangles[i + 2]])
                {
                    var (u, v) = onFloor.Texel(p.X, p.Y);
                    floor.Add(p.X, p.Y, floorPlane?.Z(p.X, p.Y) ?? sector.FloorHeight, u, v, floorLight, tint);
                }

                // Ceilings face down: the same triangles the other way round.
                foreach (var p in (MapPoint[])[triangles[i + 2], triangles[i + 1], triangles[i]])
                {
                    var (u, v) = onCeiling.Texel(p.X, p.Y);
                    ceiling.Add(p.X, p.Y, ceilingPlane?.Z(p.X, p.Y) ?? sector.CeilingHeight, u, v, ceilingLight, tint);
                }
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
            var line = l.Element;
            var upperUnpegged = Flag(l.Extra, "dontpegtop");
            var lowerUnpegged = Flag(l.Extra, "dontpegbottom");
            AddFace(new MapPoint(a.X, a.Y), new MapPoint(b.X, b.Y), front, back);
            if (back is { } bs) AddFace(new MapPoint(b.X, b.Y), new MapPoint(a.X, a.Y), bs, front);

            void AddFace(MapPoint from, MapPoint to, SideFrame side, SideFrame? other)
            {
                if (side.Sector >= 0 && Hidden(f.Sectors[side.Sector].Extra)) return;
                if (side.Sector < 0) return;
                var here = f.Sectors[side.Sector];
                var light = Tint.Light(Math.Clamp(here.Light, 0, 255), side.Extra, "light", "lightabsolute");
                var tint = Tint.Of(here.Extra);
                var length = from.DistanceTo(to);

                // Heights at each end of the wall: a sloped floor or ceiling gives each end its own.
                var (hereFloor, hereCeiling) = (Ends(here, from, to, false), Ends(here, from, to, true));
                if (other is not { Sector: >= 0 } o)
                {
                    // One-sided: the middle texture from floor to ceiling, hung from the ceiling unless
                    // anchored at the bottom.
                    var h = Height(side.Middle);
                    var top = lowerUnpegged ? here.FloorHeight + h : here.CeilingHeight;
                    Quad(side.Middle, from, to, hereFloor, hereCeiling, top, side, length, light, tint, line, WallPart.Middle);
                    return;
                }

                var there = f.Sectors[o.Sector];
                var (thereFloor, thereCeiling) = (Ends(there, from, to, false), Ends(there, from, to, true));

                // Upper part, where this side's ceiling is higher; none between two skies (Doom's sky trick).
                if ((hereCeiling.From > thereCeiling.From || hereCeiling.To > thereCeiling.To) && !(IsSky(here.CeilingTexture) && IsSky(there.CeilingTexture)))
                {
                    var top = upperUnpegged ? here.CeilingHeight : there.CeilingHeight + Height(side.Top);
                    Quad(side.Top, from, to, thereCeiling, hereCeiling, top, side, length, light, tint, line, WallPart.Top);
                }

                // Lower part, where the floor beyond is higher.
                if (thereFloor.From > hereFloor.From || thereFloor.To > hereFloor.To)
                {
                    var top = lowerUnpegged ? here.CeilingHeight : there.FloorHeight;
                    Quad(side.Bottom, from, to, hereFloor, thereFloor, top, side, length, light, tint, line, WallPart.Bottom);
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
                    if (z1 > z0) Quad(side.Middle, from, to, (z0, z0), (z1, z1), top - side.OffsetY, side, length, light, tint, line, WallPart.Middle);
                }
            }
        }

        var things = new List<SceneThing>();
        var lights = new List<SceneLight>();
        var sprites = new Dictionary<int, SpriteBatch>();
        foreach (var t in f.Things)
        {
            if (t.Element is null || Hidden(t.Extra)) continue;
            var area = geometry.SectorAt(new MapPoint(t.X, t.Y)) is { } sector && sector.Slot < f.Sectors.Count ? f.Sectors[sector.Slot] : (SectorFrame?)null;
            var z = (area is { } standing ? SlopePlane.FloorAt(standing, new MapPoint(t.X, t.Y)) : 0) + t.Height;
            var light = Math.Clamp(area?.Light ?? 160, 0, 255);
            var tint = area is { } inArea ? Tint.Of(inArea.Extra) : Tint.Plain;
            things.Add(new SceneThing(t.Element, (float)t.X, (float)t.Y, (float)z, t.Type, t.Angle, light));
            if (Smart.Lights.Kind(t.Type) is not null && t.Args[3] > 0)
            {
                var sign = Smart.Lights.Subtracts(t.Type) ? -1f : 1f;
                // GZDoom draws a light out to twice its size argument (measured: a reach-256 light fades to nothing 513 units out).
                lights.Add(new SceneLight((float)t.X, (float)t.Y, (float)z, sign * t.Args[0] / 255f, sign * t.Args[1] / 255f, sign * t.Args[2] / 255f, 2f * t.Args[3], t.Type is >= 9830 and <= 9834));
            }

            if (sprite?.Invoke(t.Type) is not { } shape) continue;
            if (!sprites.TryGetValue(t.Type, out var batch)) sprites[t.Type] = batch = new SpriteBatch(t.Type);
            batch.Owners.Add(t.Element);

            batch.Vertices.AddRange(SpriteQuad(t.X, t.Y, z, shape, light, tint));
        }

        var (x0, y0, x1, y1) = geometry.Extent();
        if (minZ > maxZ) (minZ, maxZ) = (0, 128);
        return new MapScene(f.Version, [.. batches.Values.Where(b => b.VertexCount > 0)], [.. sprites.Values], things, lights, (x0, y0, minZ, x1, y1, maxZ));

        double Height(string texture) => texture is "-" or "" ? 128 : wallSize(texture)?.Height ?? 128;

        // An area's floor or ceiling height at a wall's two ends (its plane's when sloped).
        static (double From, double To) Ends(SectorFrame area, MapPoint from, MapPoint to, bool ceiling) => ceiling
            ? (SlopePlane.CeilingAt(area, from), SlopePlane.CeilingAt(area, to))
            : (SlopePlane.FloorAt(area, from), SlopePlane.FloorAt(area, to));

        // A wall part from z0 to z1 (each at the wall's two ends) whose texture's top row sits at height top (before the side's offsets).
        void Quad(string texture, MapPoint from, MapPoint to, (double From, double To) z0, (double From, double To) z1, double top, SideFrame side, double length, int light, Tint tint, Line wall, WallPart part)
        {
            if ((z1.From <= z0.From && z1.To <= z0.To) || side.Element is not { } face) return;
            z1 = (Math.Max(z1.From, z0.From), Math.Max(z1.To, z0.To));
            var batch = Batch(texture is "" ? "-" : texture, SurfaceKind.Wall);
            batch.Owners.Add(face);
            batch.Owners.Add(face);
            batch.Walls.Add(wall);
            batch.Walls.Add(wall);
            batch.Parts.Add((byte)part);
            batch.Parts.Add((byte)part);
            var u0 = side.OffsetX;
            var u1 = side.OffsetX + length;
            double V(double z) => top - z + side.OffsetY;

            // Counter-clockwise as seen from the area the face belongs to.
            batch.Add(from.X, from.Y, z0.From, u0, V(z0.From), light, tint);
            batch.Add(to.X, to.Y, z0.To, u1, V(z0.To), light, tint);
            batch.Add(to.X, to.Y, z1.To, u1, V(z1.To), light, tint);
            batch.Add(from.X, from.Y, z0.From, u0, V(z0.From), light, tint);
            batch.Add(to.X, to.Y, z1.To, u1, V(z1.To), light, tint);
            batch.Add(from.X, from.Y, z1.From, u0, V(z1.From), light, tint);
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
