using System.Buffers.Binary;

namespace WadSmith.Core.Maps.Classic;

/// <summary>What a node build made, for the checks and the tests: the lumps' record counts and the vertices it added.</summary>
public sealed record NodeStats(int Segs, int Subsectors, int Nodes, int AddedVertices, int BlockmapBytes);

/// <summary>
/// WadSmith's own node builder (6b.2) for the classic ports, which cannot play a map without them: SEGS, SSECTORS and NODES
/// by binary space partition over the walls' sides, BLOCKMAP in 128-unit blocks, and an empty REJECT (no sight is skipped).
/// Vertices made by splitting a wall are rounded to whole units and added to the map. A partition is chosen among the
/// sides as the one that splits fewest and balances best; a set of sides that all face into one convex area of one
/// room is a subsector. Written from the format as the 1993 engine reads it (r_bsp.c, p_setup.c), not from another builder.
/// </summary>
public static class NodeBuilder
{
    /// <summary>A point within half a unit of a line counts as on it (split vertices are rounded to whole units).</summary>
    private const double OnLine = 0.5;

    /// <summary>How many sides are tried as the partition at each step, spread over the set.</summary>
    private const int Candidates = 64;

    private sealed class Seg
    {
        public int V1;
        public int V2;
        public int Line;
        public int Direction;
        public double Offset;
        public int Sector;
    }

    /// <summary>Builds the nodes, the blockmap and the reject of <paramref name="map"/> and puts them in its lumps; split vertices are added to its vertices.</summary>
    public static NodeStats Build(ClassicMap map)
    {
        var vertices = map.Vertices.Select(v => (X: (int)v.X, Y: (int)v.Y)).ToList();
        var lookup = new Dictionary<(int, int), int>();
        for (var i = 0; i < vertices.Count; i++) lookup.TryAdd(vertices[i], i);
        var original = vertices.Count;

        int VertexAt(int x, int y)
        {
            if (lookup.TryGetValue((x, y), out var i)) return i;
            vertices.Add((x, y));
            lookup[(x, y)] = vertices.Count - 1;
            return vertices.Count - 1;
        }

        // A seg for each side of each wall: the front from start to end, the back from end to start.
        var segs = new List<Seg>();
        for (var i = 0; i < map.Lines.Count; i++)
        {
            var l = map.Lines[i];
            if (l.V1 >= vertices.Count || l.V2 >= vertices.Count || vertices[l.V1] == vertices[l.V2]) continue;
            if (l.Front != ClassicLine.NoSide && l.Front < map.Sides.Count) segs.Add(new Seg { V1 = l.V1, V2 = l.V2, Line = i, Direction = 0, Sector = map.Sides[l.Front].Sector });
            if (l.Back != ClassicLine.NoSide && l.Back < map.Sides.Count) segs.Add(new Seg { V1 = l.V2, V2 = l.V1, Line = i, Direction = 1, Sector = map.Sides[l.Back].Sector });
        }

        var outSegs = new List<Seg>();
        var subsectors = new List<(int Count, int First)>();
        var nodes = new List<byte[]>();

        double Cross((int X, int Y) a, (int X, int Y) b, (int X, int Y) p) => ((double)b.X - a.X) * ((double)p.Y - a.Y) - ((double)b.Y - a.Y) * ((double)p.X - a.X);
        double Length((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(((double)b.X - a.X) * (b.X - a.X) + ((double)b.Y - a.Y) * (b.Y - a.Y));

        // Which side of the line a → b a point is on: -1 in front (right), +1 behind (left), 0 within half a unit.
        int Side((int X, int Y) a, (int X, int Y) b, (int X, int Y) p)
        {
            var d = Cross(a, b, p) / Length(a, b);
            return d < -OnLine ? -1 : d > OnLine ? 1 : 0;
        }

        bool Convex(List<Seg> set)
        {
            var sector = set[0].Sector;
            foreach (var s in set)
            {
                if (s.Sector != sector) return false;
                var (a, b) = (vertices[s.V1], vertices[s.V2]);
                foreach (var o in set)
                {
                    if (ReferenceEquals(o, s)) continue;
                    if (Side(a, b, vertices[o.V1]) > 0 || Side(a, b, vertices[o.V2]) > 0) return false;
                }
            }

            return true;
        }

        (int Front, int Back, int Splits) Score(Seg p, List<Seg> set)
        {
            var (a, b) = (vertices[p.V1], vertices[p.V2]);
            int front = 0, back = 0, splits = 0;
            foreach (var s in set)
            {
                var (s1, s2) = (Side(a, b, vertices[s.V1]), Side(a, b, vertices[s.V2]));
                if (s1 == 0 && s2 == 0)
                {
                    var same = ((double)b.X - a.X) * (vertices[s.V2].X - vertices[s.V1].X) + ((double)b.Y - a.Y) * (vertices[s.V2].Y - vertices[s.V1].Y) > 0;
                    if (same) front++;
                    else back++;
                }
                else if (s1 <= 0 && s2 <= 0) front++;
                else if (s1 >= 0 && s2 >= 0) back++;
                else splits++;
            }

            return (front, back, splits);
        }

        (int MinX, int MinY, int MaxX, int MaxY) Box(List<Seg> set)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (var s in set)
            {
                foreach (var v in new[] { vertices[s.V1], vertices[s.V2] })
                {
                    x0 = Math.Min(x0, v.X);
                    y0 = Math.Min(y0, v.Y);
                    x1 = Math.Max(x1, v.X);
                    y1 = Math.Max(y1, v.Y);
                }
            }

            return (x0, y0, x1, y1);
        }

        int Subsector(List<Seg> set)
        {
            subsectors.Add((set.Count, outSegs.Count));
            outSegs.AddRange(set);
            return (subsectors.Count - 1) | 0x8000;
        }

        int Divide(List<Seg> set, int depth)
        {
            if (set.Count == 0) throw new InvalidOperationException("The node builder reached an empty set of walls.");
            if (depth > 512 || Convex(set)) return Subsector(set);

            // The partition: the side that splits fewest and balances best, among those with something behind it.
            Seg? best = null;
            var bestCost = double.MaxValue;
            var step = Math.Max(1, set.Count / Candidates);
            for (var i = 0; i < set.Count; i += step)
            {
                var (front, back, splits) = Score(set[i], set);
                if (back == 0 && splits == 0) continue;
                if (front == 0 && splits == 0) continue;
                var cost = splits * 8.0 + Math.Abs(front - back);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = set[i];
                }
            }

            // Every sampled side had the whole set on one side: try them all.
            if (best is null && step > 1)
            {
                foreach (var s in set)
                {
                    var (front, back, splits) = Score(s, set);
                    if ((back > 0 || splits > 0) && (front > 0 || splits > 0))
                    {
                        best = s;
                        break;
                    }
                }
            }

            if (best is null) return Subsector(set);

            var (a, b) = (vertices[best.V1], vertices[best.V2]);
            var fronts = new List<Seg>();
            var backs = new List<Seg>();
            foreach (var s in set)
            {
                var (p1, p2) = (vertices[s.V1], vertices[s.V2]);
                var (s1, s2) = (Side(a, b, p1), Side(a, b, p2));
                if (s1 == 0 && s2 == 0)
                {
                    var same = ((double)b.X - a.X) * (p2.X - p1.X) + ((double)b.Y - a.Y) * (p2.Y - p1.Y) > 0;
                    (same ? fronts : backs).Add(s);
                }
                else if (s1 <= 0 && s2 <= 0)
                {
                    fronts.Add(s);
                }
                else if (s1 >= 0 && s2 >= 0)
                {
                    backs.Add(s);
                }
                else
                {
                    // Split where the side crosses the partition, at a whole-unit point.
                    var (c1, c2) = (Cross(a, b, p1), Cross(a, b, p2));
                    var t = c1 / (c1 - c2);
                    var m = VertexAt((int)Math.Round(p1.X + (p2.X - p1.X) * t), (int)Math.Round(p1.Y + (p2.Y - p1.Y) * t));
                    if (m == s.V1 || m == s.V2)
                    {
                        // Rounding landed on an end: the side goes whole to the side of its other end.
                        (((m == s.V1) ? s2 : s1) < 0 ? fronts : backs).Add(s);
                        continue;
                    }

                    var first = new Seg { V1 = s.V1, V2 = m, Line = s.Line, Direction = s.Direction, Offset = s.Offset, Sector = s.Sector };
                    var second = new Seg { V1 = m, V2 = s.V2, Line = s.Line, Direction = s.Direction, Offset = s.Offset + Length(p1, vertices[m]), Sector = s.Sector };
                    (s1 < 0 ? fronts : backs).Add(first);
                    (s2 < 0 ? fronts : backs).Add(second);
                }
            }

            if (fronts.Count == 0 || backs.Count == 0) return Subsector(set);
            var frontBox = Box(fronts);
            var backBox = Box(backs);
            var right = Divide(fronts, depth + 1);
            var left = Divide(backs, depth + 1);

            var node = new byte[28];
            BinaryPrimitives.WriteInt16LittleEndian(node, (short)a.X);
            BinaryPrimitives.WriteInt16LittleEndian(node.AsSpan(2), (short)a.Y);
            BinaryPrimitives.WriteInt16LittleEndian(node.AsSpan(4), (short)(b.X - a.X));
            BinaryPrimitives.WriteInt16LittleEndian(node.AsSpan(6), (short)(b.Y - a.Y));
            WriteBox(node.AsSpan(8), frontBox);
            WriteBox(node.AsSpan(16), backBox);
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(24), (ushort)right);
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(26), (ushort)left);
            nodes.Add(node);
            return nodes.Count - 1;
        }

        if (segs.Count > 0) Divide(segs, 0);

        // The lumps, in the format the 1993 engine reads.
        var segBytes = new byte[outSegs.Count * 12];
        for (var i = 0; i < outSegs.Count; i++)
        {
            var s = outSegs[i];
            var r = segBytes.AsSpan(i * 12, 12);
            var (p1, p2) = (vertices[s.V1], vertices[s.V2]);
            var angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            if (angle < 0) angle += 2 * Math.PI;
            BinaryPrimitives.WriteUInt16LittleEndian(r, (ushort)s.V1);
            BinaryPrimitives.WriteUInt16LittleEndian(r[2..], (ushort)s.V2);
            BinaryPrimitives.WriteUInt16LittleEndian(r[4..], (ushort)((int)(angle / (2 * Math.PI) * 65536) & 0xFFFF));
            BinaryPrimitives.WriteUInt16LittleEndian(r[6..], (ushort)s.Line);
            BinaryPrimitives.WriteInt16LittleEndian(r[8..], (short)s.Direction);
            BinaryPrimitives.WriteInt16LittleEndian(r[10..], (short)Math.Round(s.Offset));
        }

        var ssBytes = new byte[subsectors.Count * 4];
        for (var i = 0; i < subsectors.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(ssBytes.AsSpan(i * 4), (ushort)subsectors[i].Count);
            BinaryPrimitives.WriteUInt16LittleEndian(ssBytes.AsSpan(i * 4 + 2), (ushort)subsectors[i].First);
        }

        var nodeBytes = nodes.SelectMany(n => n).ToArray();
        for (var i = original; i < vertices.Count; i++) map.Vertices.Add(new ClassicVertex((short)vertices[i].X, (short)vertices[i].Y));
        var blockmap = Blockmap(map);
        var reject = new byte[(map.Sectors.Count * map.Sectors.Count + 7) / 8];
        map.SetLumps(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SEGS"] = segBytes,
            ["SSECTORS"] = ssBytes,
            ["NODES"] = nodeBytes,
            ["REJECT"] = reject,
            ["BLOCKMAP"] = blockmap,
        });
        return new NodeStats(outSegs.Count, subsectors.Count, nodes.Count, vertices.Count - original, blockmap.Length);
    }

    /// <summary>A node's bounding box, as the engine reads it: top, bottom, left, right.</summary>
    private static void WriteBox(Span<byte> r, (int MinX, int MinY, int MaxX, int MaxY) box)
    {
        BinaryPrimitives.WriteInt16LittleEndian(r, (short)box.MaxY);
        BinaryPrimitives.WriteInt16LittleEndian(r[2..], (short)box.MinY);
        BinaryPrimitives.WriteInt16LittleEndian(r[4..], (short)box.MinX);
        BinaryPrimitives.WriteInt16LittleEndian(r[6..], (short)box.MaxX);
    }

    /// <summary>The blockmap: the map in 128-unit blocks from just below and left of its lowest corner, each block listing the walls that pass through it.</summary>
    public static byte[] Blockmap(ClassicMap map)
    {
        var used = map.Lines.SelectMany(l => new[] { l.V1, l.V2 }).Where(v => v < map.Vertices.Count).Select(v => map.Vertices[v]).ToList();
        if (used.Count == 0) used.Add(new ClassicVertex(0, 0));
        int ox = used.Min(v => v.X) - 8, oy = used.Min(v => v.Y) - 8;
        int columns = (used.Max(v => v.X) - ox) / 128 + 1, rows = (used.Max(v => v.Y) - oy) / 128 + 1;
        var blocks = new List<int>[columns * rows];
        for (var i = 0; i < blocks.Length; i++) blocks[i] = [];

        for (var i = 0; i < map.Lines.Count; i++)
        {
            var l = map.Lines[i];
            if (l.V1 >= map.Vertices.Count || l.V2 >= map.Vertices.Count) continue;
            var (a, b) = (map.Vertices[l.V1], map.Vertices[l.V2]);
            int c0 = (Math.Min(a.X, b.X) - ox) / 128, c1 = (Math.Max(a.X, b.X) - ox) / 128;
            int r0 = (Math.Min(a.Y, b.Y) - oy) / 128, r1 = (Math.Max(a.Y, b.Y) - oy) / 128;
            for (var row = r0; row <= r1; row++)
            {
                for (var col = c0; col <= c1; col++)
                {
                    double x0 = ox + col * 128, y0 = oy + row * 128;
                    if (Crosses(a.X, a.Y, b.X, b.Y, x0, y0, x0 + 128, y0 + 128)) blocks[row * columns + col].Add(i);
                }
            }
        }

        var words = new List<ushort> { (ushort)(short)ox, (ushort)(short)oy, (ushort)columns, (ushort)rows };
        var offsets = words.Count;
        words.AddRange(new ushort[blocks.Length]);
        for (var i = 0; i < blocks.Length; i++)
        {
            words[offsets + i] = (ushort)Math.Min(words.Count, ushort.MaxValue);
            words.Add(0);
            words.AddRange(blocks[i].Select(n => (ushort)n));
            words.Add(0xFFFF);
        }

        var bytes = new byte[words.Count * 2];
        for (var i = 0; i < words.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), words[i]);
        return bytes;
    }

    /// <summary>Whether the segment from (x1, y1) to (x2, y2) passes through the box, its edges included (Liang–Barsky).</summary>
    private static bool Crosses(double x1, double y1, double x2, double y2, double bx0, double by0, double bx1, double by1)
    {
        double t0 = 0, t1 = 1, dx = x2 - x1, dy = y2 - y1;
        foreach (var (p, q) in new[] { (-dx, x1 - bx0), (dx, bx1 - x1), (-dy, y1 - by0), (dy, by1 - y1) })
        {
            if (p == 0)
            {
                if (q < 0) return false;
                continue;
            }

            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0) return false;
                t1 = Math.Min(t1, r);
            }
        }

        return true;
    }
}
