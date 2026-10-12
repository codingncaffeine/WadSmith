using System.Globalization;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;

namespace WadSmith.Cli.Commands;

/// <summary>Builds a large map for performance work: a grid of square rooms with varied heights.</summary>
internal sealed class StressGenCommand : ICommand
{
    public string Name => "stress-gen";

    public string Summary => "Write a large test map (a grid of rooms) for measuring speed.";

    public string Usage => """
        Usage: wadsmith-cli stress-gen --out=FILE.wad [--lines=N]

        Writes MAP01 as a grid of square rooms, 128 units each, with floors stepping up and down,
        in UDMF. N is roughly the number of walls (default 100000).
        """;

    public int Run(CommandArgs args, TextWriter output, TextWriter error)
    {
        var outPath = args.Option("out") ?? throw new UsageException("stress-gen needs --out=FILE.wad.");
        var linesText = args.Option("lines") ?? "100000";
        args.RejectUnread();
        if (!int.TryParse(linesText, NumberStyles.None, CultureInfo.InvariantCulture, out var target) || target < 16) throw new UsageException("--lines must be a whole number of at least 16.");

        // An n-by-n grid has 2n(n+1) walls.
        var n = (int)Math.Ceiling((-2 + Math.Sqrt(4 + 8.0 * target)) / 4);
        var m = Build(n);
        var lumps = new List<(string, ReadOnlyMemory<byte>)> { ("MAP01", Array.Empty<byte>()) };
        lumps.AddRange(MapConversion.ToUdmfLumps(m).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data)));
        File.WriteAllBytes(outPath, WadWriter.Write(WadKind.Pwad, lumps));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n}x{n} rooms: {m.Lines.Count} walls, {m.Sectors.Count} areas, {m.Vertices.Count} corners written to {outPath}."));
        return ExitCode.Ok;
    }

    internal static MapModel Build(int n)
    {
        const int size = 128;
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        int V(int x, int y) => y * (n + 1) + x;
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++) m.Vertices.Add(new MapVertex { X = x * size, Y = y * size });
        }

        int S(int x, int y) => y * n + x;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                m.Sectors.Add(new MapSector { FloorHeight = ((x * 7 + y * 13) % 5) * 8, CeilingHeight = 128 + ((x + y) % 3) * 16, FloorTexture = "FLOOR4_8", CeilingTexture = "CEIL3_5", Light = 160 });
            }
        }

        void Wall(int v1, int v2, int front, int back)
        {
            m.Sides.Add(new MapSide { Sector = front, Middle = back < 0 ? "STARTAN3" : "-" });
            var line = new MapLine { V1 = v1, V2 = v2, Front = m.Sides.Count - 1 };
            if (back >= 0)
            {
                m.Sides.Add(new MapSide { Sector = back });
                line.Back = m.Sides.Count - 1;
                line.SetFlag("twosided", true);
            }

            m.Lines.Add(line);
        }

        // Horizontal walls: the area above is in front when the wall runs west (right-hand side is north).
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var above = y < n ? S(x, y) : -1;
                var below = y > 0 ? S(x, y - 1) : -1;
                if (above >= 0) Wall(V(x + 1, y), V(x, y), above, below);
                else Wall(V(x, y), V(x + 1, y), below, -1);
            }
        }

        for (var x = 0; x <= n; x++)
        {
            for (var y = 0; y < n; y++)
            {
                var right = x < n ? S(x, y) : -1;
                var left = x > 0 ? S(x - 1, y) : -1;
                if (right >= 0) Wall(V(x, y), V(x, y + 1), right, left);
                else Wall(V(x, y + 1), V(x, y), left, -1);
            }
        }

        var start = new MapThing { X = size / 2, Y = size / 2, Type = 1, Angle = 90 };
        foreach (var f in new[] { "skill1", "skill2", "skill3", "skill4", "skill5", "single", "coop", "dm" }) start.SetFlag(f, true);
        m.Things.Add(start);
        return m;
    }
}
