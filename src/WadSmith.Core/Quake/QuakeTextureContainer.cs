using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Quake;

/// <summary>
/// Quake's PAK files seen as resources (Phase 8b.6): every wall texture as a texture by its short name, made into a PNG only
/// when it is read, and the palette as a PLAYPAL, so the shelves and copying into a project treat Quake's pictures as any
/// other game's. Opened from PAK0.PAK; the other PAK files beside it (PAK1.PAK of the full game) are read too.
/// </summary>
public sealed class QuakeTextureContainer : ResourceContainer
{
    private readonly List<ResourceEntry> _entries = [];

    private QuakeTextureContainer(string path, QuakeTextures textures)
    {
        Path = path;
        Textures = textures;
        var index = 0;
        _entries.Add(new ResourceEntry(this, index++, "PLAYPAL", "PLAYPAL", ResourceNamespace.Global, textures.PaletteRgb.Length, isMarker: false, () => textures.PaletteRgb));
        foreach (var texture in textures.Textures)
        {
            var t = texture;
            _entries.Add(new ResourceEntry(this, index++, t.Name, $"textures/{t.Name}.png", ResourceNamespace.Textures, t.Width * t.Height, isMarker: false, () => ImageCodec.EncodePng(textures.Picture(t))));
        }
    }

    public override string? Path { get; }

    public override IReadOnlyList<ResourceEntry> Entries => _entries;

    public QuakeTextures Textures { get; }

    /// <summary>Opens Quake from its first PAK file and every PAK file beside it, in order (PAK0, PAK1 …).</summary>
    public static new QuakeTextureContainer Open(string pak0)
    {
        var folder = System.IO.Path.GetDirectoryName(pak0) ?? ".";
        var paks = Directory.EnumerateFiles(folder).Where(f => System.IO.Path.GetExtension(f).Equals(".pak", StringComparison.OrdinalIgnoreCase) && PakArchive.IsPak(f))
            .Order(StringComparer.OrdinalIgnoreCase).Select(PakArchive.Open).ToList();
        return new QuakeTextureContainer(pak0, QuakeTextures.Read(paks));
    }
}
