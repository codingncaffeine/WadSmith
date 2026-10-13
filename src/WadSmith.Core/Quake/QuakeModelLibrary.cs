namespace WadSmith.Core.Quake;

/// <summary>
/// Quake's own models (D43): every <c>progs/*.mdl</c> in the PAK files beside PAK0 (a later PAK's copy winning), with the
/// palette they are drawn through. The weapon models seen in the player's hand (<c>v_*.mdl</c>) are left out.
/// </summary>
public sealed class QuakeModelLibrary
{
    private readonly IReadOnlyList<PakArchive> _paks;

    private QuakeModelLibrary(IReadOnlyList<PakArchive> paks, byte[] palette, IReadOnlyList<string> models) => (_paks, Palette, Models) = (paks, palette, models);

    /// <summary>Quake's palette (768 bytes).</summary>
    public byte[] Palette { get; }

    /// <summary>The models' paths inside the PAK files ("progs/soldier.mdl"), in name order.</summary>
    public IReadOnlyList<string> Models { get; }

    /// <summary>Opens the library from Quake's PAK0 and every PAK file beside it. Reads the PAKs' tables: call it on a worker.</summary>
    public static QuakeModelLibrary Open(string pak0)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(pak0)) ?? ".";
        var paks = Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).Equals(".pak", StringComparison.OrdinalIgnoreCase) && PakArchive.IsPak(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(PakArchive.Open)
            .ToList();
        var palette = paks.Select(p => p.Read("gfx/palette.lmp")).FirstOrDefault(p => p is { Length: >= 768 }) ?? throw new CorruptDataException("Quake's palette (gfx/palette.lmp) was not found in its PAK files.");
        var models = paks.SelectMany(p => p.Names)
            .Where(n => n.StartsWith("progs/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(n).StartsWith("v_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new QuakeModelLibrary(paks, palette[..768], models);
    }

    /// <summary>A model's bytes, from the last PAK that has it.</summary>
    public byte[] Read(string model) => _paks.Select(p => p.Read(model)).LastOrDefault(b => b is not null) ?? throw new CorruptDataException($"Quake has no {model}.");
}
