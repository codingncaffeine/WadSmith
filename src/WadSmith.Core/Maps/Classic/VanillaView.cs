using System.Buffers.Binary;

namespace WadSmith.Core.Maps.Classic;

/// <summary>What one view of the 1993 renderer holds at once, each against its fixed table in <see cref="VanillaView"/>.</summary>
public readonly record struct ViewLoad(int Visplanes, int WallPieces, int Sprites, int Openings);

/// <summary>
/// The 1993 renderer's bookkeeping for one view, without the drawing (6b.4): the nodes walked front to back from the eye, walls
/// clipped against the solid ones nearer (r_bsp.c), each wall piece's columns marking the floor and ceiling planes beside it
/// (r_segs.c), planes found by height, picture and light and split when a piece's columns overlap ones a plane already has
/// (r_plane.c), and the objects of every room reached projected (r_things.c). Counted against vanilla's fixed tables: past
/// 128 visplanes or 20,480 openings the game stops; past 256 wall pieces walls go undrawn; past 128 sprites objects vanish.
/// It reads the nodes WadSmith built, as the port does, at the default 320×168 view. Doubles stand in for the game's fixed
/// point, so a count can differ from the game's by a plane or two; this is written from how the engine behaves, not its code.
/// </summary>
public sealed class VanillaView
{
    public const int MostVisplanes = 128;
    public const int MostWallPieces = 256;
    public const int MostSprites = 128;
    public const int MostOpenings = 320 * 64;

    private const int FineAngles = 8192;
    private const int AngleToFineShift = 19;
    private const uint Ang90 = 0x40000000;
    private const uint Ang180 = 0x80000000;
    private const double EyeHeight = 41;

    private readonly int _width;
    private readonly int _height;
    private readonly double _centerX;
    private readonly double _centerY;
    private readonly int[] _angleToX = new int[FineAngles / 2];
    private readonly uint[] _xToAngle;
    private readonly uint _clipAngle;

    private readonly (double X, double Y)[] _vertices;
    private readonly Seg[] _segs;
    private readonly (int Count, int First, int Sector)[] _subsectors;
    private readonly Node[] _nodes;
    private readonly Room[] _rooms;
    private readonly List<(double X, double Y)>[] _things;

    private sealed record Seg(int V1, int V2, uint Angle, int Front, int Back, bool Middle, bool Upper, bool Lower);

    private sealed record Node(double X, double Y, double Dx, double Dy, double[] Box0, double[] Box1, int Child0, int Child1);

    private sealed record Room(double Floor, double Ceiling, string FloorPicture, string CeilingPicture, int Light, bool Sky);

    private sealed class Plane(double height, string picture, int light, int width)
    {
        public readonly double Height = height;
        public readonly string Picture = picture;
        public readonly int Light = light;
        public int MinX = 320;
        public int MaxX = -1;
        public readonly byte[] Top = Filled(width);

        private static byte[] Filled(int width)
        {
            var top = new byte[width];
            Array.Fill(top, (byte)0xFF);
            return top;
        }
    }

    /// <param name="built">A map whose nodes are built (SEGS, SSECTORS and NODES among its lumps).</param>
    /// <param name="width">The view's width in columns (320 at full size).</param>
    /// <param name="height">The view's height in rows (168 with the status bar).</param>
    public VanillaView(ClassicMap built, int width = 320, int height = 168)
    {
        (_width, _height) = (width, height);
        (_centerX, _centerY) = (width / 2, height / 2);
        _xToAngle = new uint[width + 1];

        // The angle tables (r_main.c): a 90° field of view over the width, each fine angle to the column it lands in.
        var focal = _centerX / Math.Tan(Math.PI / 4);
        for (var i = 0; i < FineAngles / 2; i++)
        {
            var tangent = Math.Tan((i - FineAngles / 4 + 0.5) * 2 * Math.PI / FineAngles);
            int x;
            if (tangent > 2) x = -1;
            else if (tangent < -2) x = width + 1;
            else x = Math.Clamp((int)Math.Ceiling(_centerX - tangent * focal), -1, width + 1);
            _angleToX[i] = x;
        }

        for (var x = 0; x <= width; x++)
        {
            var i = 0;
            while (_angleToX[i] > x) i++;
            _xToAngle[x] = (uint)(i << AngleToFineShift) - Ang90;
        }

        for (var i = 0; i < FineAngles / 2; i++)
        {
            if (_angleToX[i] == -1) _angleToX[i] = 0;
            else if (_angleToX[i] == width + 1) _angleToX[i] = width;
        }

        _clipAngle = _xToAngle[0];

        // The map as the game loads it (p_setup.c).
        _vertices = [.. built.Vertices.Select(v => ((double)v.X, (double)v.Y))];
        _rooms = [.. built.Sectors.Select(s => new Room(s.FloorHeight, s.CeilingHeight, s.FloorTexture.Text, s.CeilingTexture.Text, s.Light, IsSky(s.CeilingTexture.Text)))];
        var segs = Lump(built, "SEGS");
        _segs = new Seg[segs.Length / 12];
        for (var i = 0; i < _segs.Length; i++)
        {
            var r = segs.AsSpan(i * 12, 12);
            var line = built.Lines[BinaryPrimitives.ReadUInt16LittleEndian(r[6..])];
            var direction = BinaryPrimitives.ReadInt16LittleEndian(r[8..]);
            var (own, other) = direction == 0 ? (line.Front, line.Back) : (line.Back, line.Front);
            var side = built.Sides[own];
            var twoSided = (line.Flags & 4) != 0 && other != ClassicLine.NoSide && other < built.Sides.Count;
            _segs[i] = new Seg(BinaryPrimitives.ReadUInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]), (uint)BinaryPrimitives.ReadUInt16LittleEndian(r[4..]) << 16,
                side.Sector, twoSided ? built.Sides[other].Sector : -1, Textured(side.Middle.Text), Textured(side.Upper.Text), Textured(side.Lower.Text));
        }

        var subsectors = Lump(built, "SSECTORS");
        _subsectors = new (int, int, int)[subsectors.Length / 4];
        for (var i = 0; i < _subsectors.Length; i++)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(subsectors.AsSpan(i * 4));
            var first = BinaryPrimitives.ReadUInt16LittleEndian(subsectors.AsSpan(i * 4 + 2));
            _subsectors[i] = (count, first, first < _segs.Length ? _segs[first].Front : 0);
        }

        var nodes = Lump(built, "NODES");
        _nodes = new Node[nodes.Length / 28];
        for (var i = 0; i < _nodes.Length; i++)
        {
            var at0 = i * 28;
            double Short(int at) => BinaryPrimitives.ReadInt16LittleEndian(nodes.AsSpan(at0 + at));
            _nodes[i] = new Node(Short(0), Short(2), Short(4), Short(6), [Short(8), Short(10), Short(12), Short(14)], [Short(16), Short(18), Short(20), Short(22)],
                BinaryPrimitives.ReadUInt16LittleEndian(nodes.AsSpan(at0 + 24)), BinaryPrimitives.ReadUInt16LittleEndian(nodes.AsSpan(at0 + 26)));
        }

        // The objects a single-player game on the hardest skill spawns into a room's list (not the starts, which make no
        // object, and not teleport destinations, which stay out of rooms' lists).
        _things = [.. _rooms.Select(_ => new List<(double, double)>())];
        var hexen = built.Format == MapFormat.Hexen;
        foreach (var t in built.Things)
        {
            if ((t.Flags & 4) == 0 || (hexen ? (t.Flags & 256) == 0 : (t.Flags & 16) != 0)) continue;
            if (t.Type is >= 1 and <= 4 or 11 or 14 || (hexen && t.Type is >= 3000 and <= 3002)) continue;
            var room = RoomAt(t.X, t.Y);
            if (room >= 0) _things[room].Add((t.X, t.Y));
        }
    }

    /// <summary>The room a point is in, by the nodes as the game finds it (R_PointInSubsector), or -1 when the map has none.</summary>
    public int RoomAt(double x, double y)
    {
        if (_subsectors.Length == 0) return -1;
        if (_nodes.Length == 0) return _subsectors[0].Sector;
        var n = _nodes.Length - 1;
        while ((n & 0x8000) == 0)
        {
            var node = _nodes[n];
            n = OnSide(x, y, node) == 0 ? node.Child0 : node.Child1;
        }

        return _subsectors[n & 0x7FFF].Sector;
    }

    /// <summary>The floor of a room, for an eye height over it.</summary>
    public double FloorOf(int room) => _rooms[room].Floor;

    /// <summary>Whether a player fits standing in a room (56 units between floor and ceiling).</summary>
    public bool Standable(int room) => _rooms[room].Ceiling - _rooms[room].Floor >= 56;

    // The view being counted.
    private double _viewX;
    private double _viewY;
    private double _viewZ;
    private uint _viewAngle;
    private readonly List<(int First, int Last)> _solid = [];
    private readonly List<Plane> _planes = [];
    private short[] _floorClip = [];
    private short[] _ceilingClip = [];
    private bool[] _roomsAdded = [];
    private int _wallPieces;
    private int _sprites;
    private int _openings;
    private Plane? _floorPlane;
    private Plane? _ceilingPlane;
    private int _room;

    /// <summary>The load of the view from (<paramref name="x"/>, <paramref name="y"/>) at a player's eye height over the floor there, facing <paramref name="degrees"/> (0 east, anticlockwise).</summary>
    public ViewLoad Count(double x, double y, double degrees)
    {
        var room = RoomAt(x, y);
        return room < 0 ? default : Count(x, y, _rooms[room].Floor + EyeHeight, degrees);
    }

    /// <summary>The load of the view from a given eye height.</summary>
    public ViewLoad Count(double x, double y, double z, double degrees)
    {
        (_viewX, _viewY, _viewZ) = (x, y, z);
        _viewAngle = (uint)(long)Math.Round((degrees % 360 + 360) % 360 / 360 * 4294967296.0);
        _solid.Clear();
        _solid.Add((-0x7FFFFFFF, -1));
        _solid.Add((_width, 0x7FFFFFFF));
        _planes.Clear();
        _floorClip = new short[_width];
        _ceilingClip = new short[_width];
        Array.Fill(_floorClip, (short)_height);
        Array.Fill(_ceilingClip, (short)-1);
        _roomsAdded = new bool[_rooms.Length];
        (_wallPieces, _sprites, _openings) = (0, 0, 0);
        if (_subsectors.Length > 0) RenderNode(_nodes.Length == 0 ? -1 : _nodes.Length - 1);
        return new ViewLoad(_planes.Count, _wallPieces, _sprites, _openings);
    }

    private void RenderNode(int n)
    {
        if (n == -1)
        {
            Subsector(0);
            return;
        }

        if ((n & 0x8000) != 0)
        {
            Subsector(n & 0x7FFF);
            return;
        }

        var node = _nodes[n];
        var side = OnSide(_viewX, _viewY, node);
        RenderNode(side == 0 ? node.Child0 : node.Child1);
        if (BoxVisible(side == 0 ? node.Box1 : node.Box0)) RenderNode(side == 0 ? node.Child1 : node.Child0);
    }

    /// <summary>0 when the point is on the front (right) of the node's line, 1 on its back (R_PointOnSide).</summary>
    private static int OnSide(double x, double y, Node node)
    {
        if (node.Dx == 0) return x <= node.X ? (node.Dy > 0 ? 1 : 0) : (node.Dy < 0 ? 1 : 0);
        if (node.Dy == 0) return y <= node.Y ? (node.Dx < 0 ? 1 : 0) : (node.Dx > 0 ? 1 : 0);
        var (dx, dy) = (x - node.X, y - node.Y);
        return dy * node.Dx < node.Dy * dx ? 0 : 1;
    }

    // For each place of the eye round a box (left/inside/right × top/inside/bottom), the two corners that bound what it sees.
    private static readonly int[][] Corners = [[3, 0, 2, 1], [3, 0, 2, 0], [3, 1, 2, 0], [0, 0, 0, 0], [2, 0, 2, 1], [0, 0, 0, 0], [3, 1, 3, 0], [0, 0, 0, 0], [2, 0, 3, 1], [2, 1, 3, 1], [2, 1, 3, 0]];

    /// <summary>Whether any of a node box could be seen past the solid walls already drawn (R_CheckBBox); box = top, bottom, left, right.</summary>
    private bool BoxVisible(double[] box)
    {
        var boxX = _viewX <= box[2] ? 0 : _viewX < box[3] ? 1 : 2;
        var boxY = _viewY >= box[0] ? 0 : _viewY > box[1] ? 1 : 2;
        var position = (boxY << 2) + boxX;
        if (position == 5) return true;
        var c = Corners[position];
        var angle1 = PointToAngle(box[c[0]], box[c[1]]) - _viewAngle;
        var angle2 = PointToAngle(box[c[2]], box[c[3]]) - _viewAngle;
        var span = angle1 - angle2;
        if (span >= Ang180) return true;
        var tspan = angle1 + _clipAngle;
        if (tspan > 2 * _clipAngle)
        {
            tspan -= 2 * _clipAngle;
            if (tspan >= span) return false;
            angle1 = _clipAngle;
        }

        tspan = _clipAngle - angle2;
        if (tspan > 2 * _clipAngle)
        {
            tspan -= 2 * _clipAngle;
            if (tspan >= span) return false;
            angle2 = (uint)-(int)_clipAngle;
        }

        var sx1 = _angleToX[(angle1 + Ang90) >> AngleToFineShift];
        var sx2 = _angleToX[(angle2 + Ang90) >> AngleToFineShift];
        if (sx1 == sx2) return false;
        sx2--;
        var start = 0;
        while (_solid[start].Last < sx2) start++;
        return !(sx1 >= _solid[start].First && sx2 <= _solid[start].Last);
    }

    private uint PointToAngle(double x, double y)
    {
        var (dx, dy) = (x - _viewX, y - _viewY);
        if (dx == 0 && dy == 0) return 0;
        var a = Math.Atan2(dy, dx);
        if (a < 0) a += 2 * Math.PI;
        return (uint)(long)(a / (2 * Math.PI) * 4294967296.0);
    }

    private static double Sine(uint angle) => Math.Sin(angle / 4294967296.0 * 2 * Math.PI);

    private void Subsector(int index)
    {
        var (count, first, room) = _subsectors[index];
        _room = room;
        var r = _rooms[room];
        _floorPlane = r.Floor < _viewZ ? FindPlane(r.Floor, r.FloorPicture, r.Light) : null;
        _ceilingPlane = r.Ceiling > _viewZ || r.Sky ? FindPlane(r.Ceiling, r.CeilingPicture, r.Light) : null;
        AddSprites(room);
        for (var i = first; i < first + count && i < _segs.Length; i++) AddLine(_segs[i]);
    }

    /// <summary>The first plane of this height, picture and light, or a new one (R_FindPlane); every sky is one plane.</summary>
    private Plane FindPlane(double height, string picture, int light)
    {
        if (IsSky(picture)) (height, light) = (0, 0);
        foreach (var p in _planes)
        {
            if (p.Height == height && p.Light == light && string.Equals(p.Picture, picture, StringComparison.OrdinalIgnoreCase)) return p;
        }

        var made = new Plane(height, picture, light, _width) { MinX = _width };
        _planes.Add(made);
        return made;
    }

    /// <summary>The plane, or a new one of the same kind when columns of start–stop are already marked in it (R_CheckPlane).</summary>
    private Plane CheckPlane(Plane plane, int start, int stop)
    {
        int unionLow, intersectLow, unionHigh, intersectHigh;
        if (start < plane.MinX) (intersectLow, unionLow) = (plane.MinX, start);
        else (unionLow, intersectLow) = (plane.MinX, start);
        if (stop > plane.MaxX) (intersectHigh, unionHigh) = (plane.MaxX, stop);
        else (unionHigh, intersectHigh) = (plane.MaxX, stop);

        var x = intersectLow;
        while (x <= intersectHigh && plane.Top[x] == 0xFF) x++;
        if (x > intersectHigh)
        {
            (plane.MinX, plane.MaxX) = (unionLow, unionHigh);
            return plane;
        }

        var made = new Plane(plane.Height, plane.Picture, plane.Light, _width) { MinX = start, MaxX = stop };
        _planes.Add(made);
        return made;
    }

    private void AddLine(Seg seg)
    {
        var (a, b) = (_vertices[seg.V1], _vertices[seg.V2]);
        var angle1 = PointToAngle(a.X, a.Y);
        var angle2 = PointToAngle(b.X, b.Y);
        var span = angle1 - angle2;
        if (span >= Ang180) return;
        var wallAngle1 = angle1;
        angle1 -= _viewAngle;
        angle2 -= _viewAngle;
        var tspan = angle1 + _clipAngle;
        if (tspan > 2 * _clipAngle)
        {
            tspan -= 2 * _clipAngle;
            if (tspan >= span) return;
            angle1 = _clipAngle;
        }

        tspan = _clipAngle - angle2;
        if (tspan > 2 * _clipAngle)
        {
            tspan -= 2 * _clipAngle;
            if (tspan >= span) return;
            angle2 = (uint)-(int)_clipAngle;
        }

        var x1 = _angleToX[(angle1 + Ang90) >> AngleToFineShift];
        var x2 = _angleToX[(angle2 + Ang90) >> AngleToFineShift];
        if (x1 == x2) return;

        var front = _rooms[_room];
        if (seg.Back < 0)
        {
            ClipSolid(seg, wallAngle1, x1, x2 - 1);
            return;
        }

        var back = _rooms[seg.Back];
        if (back.Ceiling <= front.Floor || back.Floor >= front.Ceiling)
        {
            ClipSolid(seg, wallAngle1, x1, x2 - 1);
            return;
        }

        if (back.Ceiling == front.Ceiling && back.Floor == front.Floor && back.Light == front.Light && !seg.Middle
            && string.Equals(back.CeilingPicture, front.CeilingPicture, StringComparison.OrdinalIgnoreCase) && string.Equals(back.FloorPicture, front.FloorPicture, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClipPass(seg, wallAngle1, x1, x2 - 1);
    }

    /// <summary>A solid wall: its uncovered parts are stored and the solid list grows over them (R_ClipSolidWallSegment).</summary>
    private void ClipSolid(Seg seg, uint wallAngle1, int first, int last)
    {
        var start = 0;
        while (_solid[start].Last < first - 1) start++;
        if (first < _solid[start].First)
        {
            if (last < _solid[start].First - 1)
            {
                StoreWallRange(seg, wallAngle1, first, last);
                _solid.Insert(start, (first, last));
                return;
            }

            StoreWallRange(seg, wallAngle1, first, _solid[start].First - 1);
            _solid[start] = (first, _solid[start].Last);
        }

        if (last <= _solid[start].Last) return;
        var next = start;
        while (last >= _solid[next + 1].First - 1)
        {
            StoreWallRange(seg, wallAngle1, _solid[next].Last + 1, _solid[next + 1].First - 1);
            next++;
            if (last <= _solid[next].Last)
            {
                _solid[start] = (_solid[start].First, _solid[next].Last);
                _solid.RemoveRange(start + 1, next - start);
                return;
            }
        }

        StoreWallRange(seg, wallAngle1, _solid[next].Last + 1, last);
        _solid[start] = (_solid[start].First, last);
        if (next > start) _solid.RemoveRange(start + 1, next - start);
    }

    /// <summary>A wall that can be seen past: its uncovered parts are stored, the solid list stays (R_ClipPassWallSegment).</summary>
    private void ClipPass(Seg seg, uint wallAngle1, int first, int last)
    {
        var start = 0;
        while (_solid[start].Last < first - 1) start++;
        if (first < _solid[start].First)
        {
            if (last < _solid[start].First - 1)
            {
                StoreWallRange(seg, wallAngle1, first, last);
                return;
            }

            StoreWallRange(seg, wallAngle1, first, _solid[start].First - 1);
        }

        if (last <= _solid[start].Last) return;
        while (last >= _solid[start + 1].First - 1)
        {
            StoreWallRange(seg, wallAngle1, _solid[start].Last + 1, _solid[start + 1].First - 1);
            start++;
            if (last <= _solid[start].Last) return;
        }

        StoreWallRange(seg, wallAngle1, _solid[start].Last + 1, last);
    }

    /// <summary>One wall piece over columns start–stop (R_StoreWallRange): marks the planes beside it and clips what is behind.</summary>
    private void StoreWallRange(Seg seg, uint wallAngle1, int start, int stop)
    {
        // Past the table the game drops the piece, drawn and clipped by nothing (walls go missing); the count goes on.
        if (++_wallPieces > MostWallPieces) return;

        var normal = seg.Angle + Ang90;
        var offset = (uint)Math.Abs((long)(int)(normal - wallAngle1));
        if (offset > Ang90) offset = Ang90;
        var a = _vertices[seg.V1];
        var distance = Math.Sqrt((a.X - _viewX) * (a.X - _viewX) + (a.Y - _viewY) * (a.Y - _viewY)) * Sine(Ang90 - offset);
        double ScaleAt(int x)
        {
            var visual = _viewAngle + _xToAngle[x];
            var sinA = Sine(Ang90 + (visual - _viewAngle));
            var sinB = Sine(Ang90 + (visual - normal));
            var num = _centerX * sinB;
            var den = distance * sinA;
            return den > num / 65536 ? Math.Clamp(num / den, 256 / 65536.0, 64) : 64;
        }

        var scale = ScaleAt(start);
        var step = stop > start ? (ScaleAt(stop) - scale) / (stop - start) : 0;

        var front = _rooms[_room];
        var top = front.Ceiling - _viewZ;
        var bottom = front.Floor - _viewZ;
        bool markFloor, markCeiling, upper = false, lower = false, masked = false;
        double high = 0, low = 0;
        int silhouette = 0;
        bool topClipSet, bottomClipSet;
        if (seg.Back < 0)
        {
            markFloor = markCeiling = true;
            silhouette = 3;
            topClipSet = bottomClipSet = true;
        }
        else
        {
            var back = _rooms[seg.Back];
            topClipSet = bottomClipSet = false;
            if (front.Floor > back.Floor || back.Floor > _viewZ) silhouette |= 1;
            if (front.Ceiling < back.Ceiling || back.Ceiling < _viewZ) silhouette |= 2;
            if (back.Ceiling <= front.Floor)
            {
                bottomClipSet = true;
                silhouette |= 1;
            }

            if (back.Floor >= front.Ceiling)
            {
                topClipSet = true;
                silhouette |= 2;
            }

            high = back.Ceiling - _viewZ;
            low = back.Floor - _viewZ;
            if (front.Sky && back.Sky) top = high;
            markFloor = low != bottom || back.Light != front.Light || !string.Equals(back.FloorPicture, front.FloorPicture, StringComparison.OrdinalIgnoreCase);
            markCeiling = high != top || back.Light != front.Light || !string.Equals(back.CeilingPicture, front.CeilingPicture, StringComparison.OrdinalIgnoreCase);
            if (back.Ceiling <= front.Floor || back.Floor >= front.Ceiling) markFloor = markCeiling = true;
            upper = high < top && seg.Upper;
            lower = low > bottom && seg.Lower;
            if (seg.Middle)
            {
                masked = true;
                _openings += stop + 1 - start;
            }
        }

        if (front.Floor >= _viewZ) markFloor = false;
        if (front.Ceiling <= _viewZ && !front.Sky) markCeiling = false;

        var topFrac = _centerY - top * scale;
        var topStep = -step * top;
        var bottomFrac = _centerY - bottom * scale;
        var bottomStep = -step * bottom;
        var highFrac = _centerY - high * scale;
        var highStep = -step * high;
        var lowFrac = _centerY - low * scale;
        var lowStep = -step * low;

        if (markCeiling && _ceilingPlane is { } ceiling) _ceilingPlane = CheckPlane(ceiling, start, stop);
        if (markFloor && _floorPlane is { } floor) _floorPlane = CheckPlane(floor, start, stop);

        for (var x = start; x <= stop; x++)
        {
            var yl = (int)Math.Ceiling(topFrac);
            if (yl < _ceilingClip[x] + 1) yl = _ceilingClip[x] + 1;
            if (markCeiling && _ceilingPlane is { } c)
            {
                var (t, b) = (_ceilingClip[x] + 1, yl - 1);
                if (b >= _floorClip[x]) b = _floorClip[x] - 1;
                if (t <= b) c.Top[x] = (byte)t;
            }

            var yh = (int)Math.Floor(bottomFrac);
            if (yh >= _floorClip[x]) yh = _floorClip[x] - 1;
            if (markFloor && _floorPlane is { } f)
            {
                var (t, b) = (yh + 1, _floorClip[x] - 1);
                if (t <= _ceilingClip[x]) t = _ceilingClip[x] + 1;
                if (t <= b) f.Top[x] = (byte)t;
            }

            if (seg.Back < 0)
            {
                // A one-sided wall closes the column, drawn or not: no middle picture leaves only the planes' marks.
                if (seg.Middle)
                {
                    _ceilingClip[x] = (short)_height;
                    _floorClip[x] = -1;
                }
                else
                {
                    if (markCeiling) _ceilingClip[x] = (short)(yl - 1);
                    if (markFloor) _floorClip[x] = (short)(yh + 1);
                }
            }
            else
            {
                if (upper)
                {
                    var mid = (int)Math.Floor(highFrac);
                    if (mid >= _floorClip[x]) mid = _floorClip[x] - 1;
                    _ceilingClip[x] = (short)(mid >= yl ? mid : yl - 1);
                }
                else if (markCeiling)
                {
                    _ceilingClip[x] = (short)(yl - 1);
                }

                if (lower)
                {
                    var mid = (int)Math.Ceiling(lowFrac);
                    if (mid <= _ceilingClip[x]) mid = _ceilingClip[x] + 1;
                    _floorClip[x] = (short)(mid <= yh ? mid : yh + 1);
                }
                else if (markFloor)
                {
                    _floorClip[x] = (short)(yh + 1);
                }
            }

            topFrac += topStep;
            bottomFrac += bottomStep;
            highFrac += highStep;
            lowFrac += lowStep;
        }

        // The sprites' clip copies (openings) the piece keeps for what is drawn over it later.
        if (((silhouette & 2) != 0 || masked) && !topClipSet) _openings += stop + 1 - start;
        if (((silhouette & 1) != 0 || masked) && !bottomClipSet) _openings += stop + 1 - start;
    }

    /// <summary>Every object of a room, the first time the room is reached in this view (R_AddSprites, R_ProjectSprite).</summary>
    private void AddSprites(int room)
    {
        if (_roomsAdded[room]) return;
        _roomsAdded[room] = true;
        var (cos, sin) = (Math.Cos(_viewAngle / 4294967296.0 * 2 * Math.PI), Math.Sin(_viewAngle / 4294967296.0 * 2 * Math.PI));
        foreach (var (x, y) in _things[room])
        {
            var (tx, ty) = (x - _viewX, y - _viewY);
            var tz = tx * cos + ty * sin;
            if (tz < 4) continue;
            var side = -(ty * cos - tx * sin);
            if (Math.Abs(side) > tz * 4) continue;
            var xscale = _centerX / tz;

            // A sprite of the usual width (32 units, centred) decides whether it reaches the view's edges.
            var left = (int)Math.Floor(_centerX + (side - 16) * xscale);
            if (left > _width) continue;
            var right = (int)Math.Floor(_centerX + (side + 16) * xscale) - 1;
            if (right < 0) continue;
            _sprites++;
        }
    }

    private static bool IsSky(string picture) => picture.Equals("F_SKY1", StringComparison.OrdinalIgnoreCase) || picture.Equals("F_SKY", StringComparison.OrdinalIgnoreCase) || picture.Equals("F_SKY001", StringComparison.OrdinalIgnoreCase);

    private static bool Textured(string name) => name.Length > 0 && name != "-";

    private static byte[] Lump(ClassicMap map, string name) =>
        map.OtherLumps.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).Data ?? throw new InvalidOperationException($"The map has no {name}: build its nodes first.");
}
