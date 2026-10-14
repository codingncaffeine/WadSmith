using System.Globalization;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Classic;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Checks;

/// <summary>
/// The views a player has that overflow vanilla Doom's fixed tables (6b.4), as Problems for a project that plays in vanilla: the
/// map with whole-unit corners and its own nodes, as the classic save writes it, swept by <see cref="VanillaLimits"/> from the
/// starts and across every room, eight ways round. Each Problem shows the rooms the overflowing views are seen from.
/// </summary>
internal static class VanillaViews
{
    public static void Check(MapFrame f, List<Problem> found)
    {
        var (classic, rooms) = Classic(f);
        if (classic.Lines.Count == 0 || classic.Sectors.Count == 0) return;
        NodeBuilder.Build(classic);
        foreach (var table in VanillaLimits.Find(classic).GroupBy(o => o.Table))
        {
            var shown = table.Select(o => rooms[o.Room]).OfType<Element>().Distinct().ToList();
            var worst = table.Max(o => o.Count);
            var from = shown.Count == 1 ? "From one room" : string.Create(CultureInfo.InvariantCulture, $"From {shown.Count} rooms");
            var (rule, sentence) = table.Key switch
            {
                VanillaTable.Visplanes => ("visplanes", string.Create(CultureInfo.InvariantCulture, $"{from} a player can see more floors and ceilings at once than vanilla Doom can draw ({worst} of its {VanillaView.MostVisplanes}), and the game stops there with \"no more visplanes\". Fewer different heights, lights and floor pictures in sight there fixes it.")),
                VanillaTable.WallPieces => ("drawsegs", string.Create(CultureInfo.InvariantCulture, $"{from} a player can see more pieces of wall at once than vanilla Doom draws ({worst} of its {VanillaView.MostWallPieces}): the furthest walls go missing there.")),
                VanillaTable.Sprites => ("vissprites", string.Create(CultureInfo.InvariantCulture, $"{from} a player can see more objects at once than vanilla Doom draws ({worst} of its {VanillaView.MostSprites}): the furthest vanish.")),
                _ => ("openings", string.Create(CultureInfo.InvariantCulture, $"{from} a player can see more walls with objects before them than vanilla Doom keeps room for ({worst:N0} of its {VanillaView.MostOpenings:N0} openings), and the game can crash there.")),
            };
            found.Add(new Problem(rule, Severity.Warning, sentence, shown));
        }
    }

    /// <summary>
    /// The map in the classic records the renderer reads, with the room each classic room came from. A picture name the format
    /// cannot hold stands as a name of its own, so two such floors still differ; objects are those a single-player game on the
    /// hardest skill makes.
    /// </summary>
    internal static (ClassicMap Map, Sector?[] Rooms) Classic(MapFrame f)
    {
        var m = new ClassicMap { Name = "MAP01", Format = MapFormat.Doom };
        m.LumpOrder.AddRange(["THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SEGS", "SSECTORS", "NODES", "SECTORS", "REJECT", "BLOCKMAP"]);
        var standIns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NameField Name(string name)
        {
            if (name.Length == 0) return "-";
            if (name.Length <= 8 && name.All(c => c is > ' ' and <= '~')) return name.ToUpperInvariant();
            if (!standIns.TryGetValue(name, out var stand)) standIns[name] = stand = string.Create(CultureInfo.InvariantCulture, $"~{standIns.Count:X7}");
            return stand;
        }

        var vertices = new int[f.Vertices.Count];
        for (var i = 0; i < f.Vertices.Count; i++)
        {
            var v = f.Vertices[i];
            vertices[i] = v.Element is null ? -1 : m.Vertices.Count;
            if (v.Element is not null) m.Vertices.Add(new ClassicVertex(Whole(v.X), Whole(v.Y)));
        }

        var sectors = new int[f.Sectors.Count];
        var rooms = new List<Sector?>();
        for (var i = 0; i < f.Sectors.Count; i++)
        {
            var s = f.Sectors[i];
            sectors[i] = s.Element is null ? -1 : m.Sectors.Count;
            if (s.Element is null) continue;
            m.Sectors.Add(new ClassicSector(Whole(s.FloorHeight), Whole(s.CeilingHeight), Name(s.FloorTexture), Name(s.CeilingTexture), (short)Math.Clamp(s.Light, 0, 255), 0, 0));
            rooms.Add(s.Element);
        }

        var sides = new int[f.Sides.Count];
        for (var i = 0; i < f.Sides.Count; i++)
        {
            var s = f.Sides[i];
            sides[i] = s.Element is null || s.Sector < 0 || s.Sector >= sectors.Length || sectors[s.Sector] < 0 ? -1 : m.Sides.Count;
            if (sides[i] >= 0) m.Sides.Add(new ClassicSide(0, 0, Name(s.Top), Name(s.Bottom), Name(s.Middle), (ushort)sectors[s.Sector]));
        }

        foreach (var l in f.Lines)
        {
            if (l.Element is null || l.V1 < 0 || l.V2 < 0 || l.Front < 0 || vertices[l.V1] < 0 || vertices[l.V2] < 0 || sides[l.Front] < 0) continue;
            var back = l.Back >= 0 && sides[l.Back] >= 0 ? sides[l.Back] : -1;
            m.Lines.Add(new ClassicLine((ushort)vertices[l.V1], (ushort)vertices[l.V2], (ushort)(back >= 0 ? 4 : 1), 0, 0, (ushort)sides[l.Front], back >= 0 ? (ushort)back : ClassicLine.NoSide));
        }

        foreach (var t in f.Things)
        {
            if (t.Element is null) continue;
            var made = Flag(t.Extra, "skill4") && Flag(t.Extra, "single");
            m.Things.Add(new ClassicThing(Whole(t.X), Whole(t.Y), (short)t.Angle, (ushort)Math.Clamp(t.Type, 0, ushort.MaxValue), (ushort)(made ? 7 : 0)));
        }

        return (m, [.. rooms]);
    }

    private static short Whole(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);

    private static bool Flag(KeyValuePair<string, Core.Maps.Udmf.UdmfValue>[] extra, string key)
    {
        foreach (var (k, v) in extra)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v.AsBool();
        }

        return false;
    }
}
