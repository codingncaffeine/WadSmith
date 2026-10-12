using WadSmith.Core.Containers;

namespace WadSmith.Core.Maps;

/// <summary>How a map is stored.</summary>
public enum MapFormat
{
    /// <summary>The original binary format of Doom, Heretic and Strife.</summary>
    Doom,
    /// <summary>Hexen's binary format: things and lines carry an action and five arguments.</summary>
    Hexen,
    /// <summary>The Universal Doom Map Format: a TEXTMAP text lump.</summary>
    Udmf,
}

/// <summary>The lumps that make up one map, in file order, starting with the map's name marker.</summary>
public sealed class MapLumps
{
    public static readonly IReadOnlySet<string> ClassicNames = new HashSet<string>(LumpName.Comparer)
    {
        "THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SEGS", "SSECTORS", "NODES", "SECTORS", "REJECT", "BLOCKMAP", "BEHAVIOR", "SCRIPTS",
    };

    public MapLumps(string name, MapFormat format, IReadOnlyList<(string Name, byte[] Data)> lumps, ResourceContainer source)
    {
        Name = name;
        Format = format;
        Lumps = lumps;
        Source = source;
    }

    /// <summary>The map's slot name, such as MAP01 or E1M1.</summary>
    public string Name { get; }

    public MapFormat Format { get; }

    /// <summary>Everything after the name marker: THINGS, LINEDEFS … or TEXTMAP … ENDMAP.</summary>
    public IReadOnlyList<(string Name, byte[] Data)> Lumps { get; }

    public ResourceContainer Source { get; }

    public byte[]? Get(string lumpName)
    {
        foreach (var (n, d) in Lumps)
        {
            if (LumpName.Comparer.Equals(n, lumpName)) return d;
        }

        return null;
    }

    public override string ToString() => $"{Name} ({Format}, {Lumps.Count} lumps)";
}

/// <summary>Finds the maps in a container, the way the engines find them.</summary>
public static class MapLocator
{
    /// <summary>
    /// Every map in a WAD (a marker lump followed by THINGS or TEXTMAP), or every map WAD in the
    /// <c>maps/</c> directory of a PK3 or folder.
    /// </summary>
    public static IReadOnlyList<MapLumps> Find(ResourceContainer container)
    {
        if (container is WadContainer) return FindInWad(container);

        var maps = new List<MapLumps>();
        foreach (var e in container.Entries.Where(e => e.Namespace == ResourceNamespace.Maps && e.FullPath.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)))
        {
            WadContainer inner;
            try
            {
                inner = WadContainer.FromBytes(e.Read(), e.FullPath);
            }
            catch (CorruptDataException)
            {
                continue;
            }

            // A map WAD in a PK3 takes its name from the file, whatever its marker says.
            foreach (var m in FindInWad(inner).Take(1))
            {
                maps.Add(new MapLumps(e.Name.ToUpperInvariant(), m.Format, m.Lumps, container));
            }
        }

        return maps;
    }

    private static List<MapLumps> FindInWad(ResourceContainer wad)
    {
        var maps = new List<MapLumps>();
        var entries = wad.Entries;
        for (var i = 0; i + 1 < entries.Count; i++)
        {
            var next = entries[i + 1].Name;
            if (LumpName.Comparer.Equals(next, "TEXTMAP"))
            {
                var lumps = new List<(string, byte[])>();
                var j = i + 1;
                for (; j < entries.Count; j++)
                {
                    if (LumpName.Comparer.Equals(entries[j].Name, "ENDMAP")) break;
                    lumps.Add((entries[j].Name, entries[j].Read()));
                }

                if (j < entries.Count) lumps.Add((entries[j].Name, entries[j].Read()));
                maps.Add(new MapLumps(entries[i].Name, MapFormat.Udmf, lumps, wad));
                i = j;
            }
            else if (LumpName.Comparer.Equals(next, "THINGS"))
            {
                var lumps = new List<(string, byte[])>();
                var j = i + 1;
                for (; j < entries.Count && MapLumps.ClassicNames.Contains(entries[j].Name); j++)
                {
                    lumps.Add((entries[j].Name, entries[j].Read()));
                }

                var hexen = lumps.Any(l => LumpName.Comparer.Equals(l.Item1, "BEHAVIOR"));
                maps.Add(new MapLumps(entries[i].Name, hexen ? MapFormat.Hexen : MapFormat.Doom, lumps, wad));
                i = j - 1;
            }
        }

        return maps;
    }
}
