using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;

namespace WadSmith.Core.Build;

/// <summary>
/// A Build group file seen as resources (D48): every art tile as a texture named <see cref="BuildArt.NameOf"/>, made into a PNG
/// only when it is read, and the game palette as a PLAYPAL, so the shelves, the 3D view and copying into a project treat
/// Duke Nukem 3D's art as any other game's pictures.
/// </summary>
public sealed class BuildArtContainer : ResourceContainer
{
    private readonly List<ResourceEntry> _entries = [];

    private BuildArtContainer(string path, BuildArt art)
    {
        Path = path;
        Art = art;
        var index = 0;
        _entries.Add(new ResourceEntry(this, index++, "PLAYPAL", "PLAYPAL", ResourceNamespace.Global, art.PaletteRgb.Length, isMarker: false, () => art.PaletteRgb));
        foreach (var tile in art.Tiles.OrderBy(t => t.Number))
        {
            var name = BuildArt.NameOf(tile.Number);
            var t = tile;
            _entries.Add(new ResourceEntry(this, index++, name, $"textures/{name}.png", ResourceNamespace.Textures, tile.Width * tile.Height, isMarker: false, () => ImageCodec.EncodePng(art.Picture(t))));
        }
    }

    public override string? Path { get; }

    public override IReadOnlyList<ResourceEntry> Entries => _entries;

    public BuildArt Art { get; }

    public static new BuildArtContainer Open(string grpPath) => new(grpPath, BuildArt.Read(GrpArchive.Open(grpPath)));
}
