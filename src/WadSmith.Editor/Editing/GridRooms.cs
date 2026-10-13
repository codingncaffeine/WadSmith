using WadSmith.Core.Grids;
using WadSmith.Core.Maps;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.Editing;

/// <summary>What a tile of a grid map is: nothing (outside), a wall (with its own wall picture number), floor, or a door.</summary>
public enum GridKind
{
    Void,
    Wall,
    Floor,
    Door,
}

/// <summary>A tile of a grid map; a door says whether it is passed through east–west (its faces east and west).</summary>
public readonly record struct GridCell(GridKind Kind, int Wall = 0, bool PassedEastWest = false);

/// <summary>A grid map: tiles row by row from the north-west, and the player's starts (tile, angle in degrees, 0 east).</summary>
public sealed record GridMap(string Name, int Width, int Height, GridCell[] Cells, IReadOnlyList<(int X, int Y, int Angle)> Starts)
{
    public GridCell At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? new GridCell(GridKind.Void) : Cells[y * Width + x];
}

/// <summary>
/// Grid maps as rooms (D16, Phase 11): Wolfenstein 3D levels and Tiled maps. Each connected run of floor tiles is a room,
/// each door tile a door (Doom's DR door, made as the Door tool makes it: the rooms in front, the door behind, the door
/// picture above, the track beside); a wall tile's edge shows a wall picture chosen by its number. Edges in a line become
/// one wall where nothing else meets them. Tiles are <see cref="Cell"/> units square and as tall, as Wolfenstein's are cubes.
/// </summary>
public static class GridRooms
{
    public const double Cell = 128;

    /// <summary>The first slot <paramref name="project"/> leaves free: E1M1 onward when its maps are named so, else MAP01 onward.</summary>
    public static string NextSlot(Core.Projects.Project project)
    {
        var used = project.Info.Maps.Select(m => m.Slot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var episodes = used.Any(s => s.Length == 4 && s[0] == 'E' && s[2] == 'M');
        var slots = episodes
            ? Enumerable.Range(1, 4).SelectMany(e => Enumerable.Range(1, 9).Select(m => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"E{e}M{m}")))
            : Enumerable.Range(1, 99).Select(m => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MAP{m:00}"));
        return slots.FirstOrDefault(s => !used.Contains(s)) ?? throw new Core.CorruptDataException("The project has no free map slot left.");
    }

    /// <summary>A Wolfenstein 3D level as a grid: walls 1–63, doors 90–101 (even passed east–west), floors from 106; player starts 19–22.</summary>
    public static GridMap FromWolf(WolfLevel level)
    {
        var cells = new GridCell[level.Width * level.Height];
        for (var i = 0; i < cells.Length; i++)
        {
            var code = level.Walls[i];
            cells[i] = code switch
            {
                >= 1 and <= 63 => new GridCell(GridKind.Wall, code),
                >= 90 and <= 101 => new GridCell(GridKind.Door, code, code % 2 == 0),
                >= 106 => new GridCell(GridKind.Floor),
                _ => new GridCell(GridKind.Void),
            };
        }

        var starts = new List<(int, int, int)>();
        for (var i = 0; i < level.Objects.Length; i++)
        {
            if (level.Objects[i] is >= 19 and <= 22) starts.Add((i % level.Width, i / level.Width, level.Objects[i] switch { 19 => 90, 20 => 0, 21 => 270, _ => 180 }));
        }

        return new GridMap(level.Name, level.Width, level.Height, cells, starts);
    }

    /// <summary>
    /// The rooms of <paramref name="grid"/> as a GZDoom map named <paramref name="slot"/>, in <paramref name="look"/>'s pictures;
    /// <paramref name="walls"/> are the pictures a wall's number picks from.
    /// </summary>
    public static MapModel Build(GridMap grid, string slot, Look look, IReadOnlyList<string> walls)
    {
        var map = new MapModel { Name = slot, Namespace = "zdoom" };
        var sectorOf = new int[grid.Width * grid.Height];
        Array.Fill(sectorOf, -1);

        // Rooms: four-connected floor tiles; each door tile alone.
        for (var start = 0; start < sectorOf.Length; start++)
        {
            var kind = grid.Cells[start].Kind;
            if (sectorOf[start] >= 0 || kind is GridKind.Void or GridKind.Wall) continue;
            var sector = map.Sectors.Count;
            map.Sectors.Add(new MapSector
            {
                FloorHeight = 0,
                CeilingHeight = kind == GridKind.Door ? 0 : Cell,
                FloorTexture = look.Floor,
                CeilingTexture = look.Ceiling,
                Light = look.Light,
            });
            var queue = new Queue<int>([start]);
            sectorOf[start] = sector;
            while (kind == GridKind.Floor && queue.TryDequeue(out var at))
            {
                int x = at % grid.Width, y = at / grid.Width;
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (grid.At(nx, ny).Kind != GridKind.Floor || sectorOf[ny * grid.Width + nx] >= 0) continue;
                    sectorOf[ny * grid.Width + nx] = sector;
                    queue.Enqueue(ny * grid.Width + nx);
                }
            }
        }

        // Edges between a tile of a room and what is beside it, each with the room on its right (Doom's front side).
        var edges = new List<Edge>();
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var here = sectorOf[y * grid.Width + x];
                if (here < 0) continue;
                // North, east, south and west edges: corners in grid points, the line running with the tile on its right.
                foreach (var (nx, ny, a, b) in new[] { (x, y - 1, (x, y), (x + 1, y)), (x + 1, y, (x + 1, y), (x + 1, y + 1)), (x, y + 1, (x + 1, y + 1), (x, y + 1)), (x - 1, y, (x, y + 1), (x, y)) })
                {
                    var beside = grid.At(nx, ny);
                    var there = beside.Kind is GridKind.Floor or GridKind.Door ? sectorOf[ny * grid.Width + nx] : -1;
                    if (there == here) continue;
                    if (there >= 0 && there < here) continue;
                    var cell = grid.At(x, y);
                    // A door's own edges: its faces toward the rooms it opens between, the track toward the walls.
                    string texture;
                    if (there >= 0) texture = "-";
                    else if (cell.Kind == GridKind.Door) texture = look.Track;
                    else texture = beside.Kind == GridKind.Wall && walls.Count > 0 ? walls[(beside.Wall - 1) % walls.Count] : look.Wall;
                    edges.Add(new Edge(a, b, here, there, texture));
                }
            }
        }

        // Points used by more than one straight run stay corners; edges in a line merge across the rest.
        var uses = new Dictionary<(int, int), int>();
        foreach (var e in edges)
        {
            uses[e.A] = uses.GetValueOrDefault(e.A) + 1;
            uses[e.B] = uses.GetValueOrDefault(e.B) + 1;
        }

        var byStart = edges.GroupBy(e => e.A).ToDictionary(g => g.Key, g => g.ToList());
        var done = new HashSet<Edge>();
        var vertices = new Dictionary<(int, int), int>();
        int Vertex((int X, int Y) p)
        {
            if (vertices.TryGetValue(p, out var v)) return v;
            map.Vertices.Add(new MapVertex { X = p.X * Cell, Y = -p.Y * Cell });
            return vertices[p] = map.Vertices.Count - 1;
        }

        foreach (var first in edges)
        {
            if (!done.Add(first)) continue;
            var end = first.B;
            // Walk on while the next edge goes the same way, between the same rooms, with the same picture.
            while (uses[end] == 2 && byStart.TryGetValue(end, out var nexts) && nexts.FirstOrDefault(n => !done.Contains(n) && n.Same(first)) is { } next)
            {
                done.Add(next);
                end = next.B;
            }

            var line = new MapLine { V1 = Vertex(first.A), V2 = Vertex(end) };
            var front = new MapSide { Sector = first.Here, Middle = first.There < 0 ? first.Texture : "-" };
            line.Front = map.Sides.Count;
            map.Sides.Add(front);
            if (first.There >= 0)
            {
                line.Back = map.Sides.Count;
                map.Sides.Add(new MapSide { Sector = first.There });
                line.Extra["twosided"] = Core.Maps.Udmf.UdmfValue.Bool(true);
                var (hereDoor, thereDoor) = (IsDoor(first.Here), IsDoor(first.There));
                if (hereDoor != thereDoor)
                {
                    // A door's face: the room in front, the door behind, its picture above, using it opens the door.
                    if (hereDoor) (line.V1, line.V2, line.Front, line.Back) = (line.V2, line.V1, line.Back, line.Front);
                    map.Sides[line.Front].Top = look.Door;
                    var (special, args) = Doors.Encode(new DoorSettings(false, false, 0), 0);
                    line.Special = special;
                    for (var i = 0; i < args.Length; i++) line.Args[i] = args[i];
                    foreach (var flag in new[] { "playeruse", "monsteruse", "repeatspecial" }) line.Extra[flag] = Core.Maps.Udmf.UdmfValue.Bool(true);
                }
            }
            else
            {
                line.Extra["blocking"] = Core.Maps.Udmf.UdmfValue.Bool(true);
                // A door's track stays put while the door rises.
                if (IsDoor(first.Here)) line.Extra["dontpegbottom"] = Core.Maps.Udmf.UdmfValue.Bool(true);
            }

            map.Lines.Add(line);
        }

        foreach (var (x, y, angle) in grid.Starts.Take(1))
        {
            map.Things.Add(new MapThing { X = (x + 0.5) * Cell, Y = -(y + 0.5) * Cell, Angle = angle, Type = 1 });
        }

        return map;

        bool IsDoor(int sector) => map.Sectors[sector].CeilingHeight == 0;
    }

    private sealed record Edge((int X, int Y) A, (int X, int Y) B, int Here, int There, string Texture)
    {
        public (int, int) Direction => (Math.Sign(B.X - A.X), Math.Sign(B.Y - A.Y));

        public bool Same(Edge other) => Direction == other.Direction && Here == other.Here && There == other.There && Texture == other.Texture;
    }
}
