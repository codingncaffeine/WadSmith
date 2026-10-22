using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WadSmith.Core.Games;

namespace WadSmith.App.ViewModels;

/// <summary>A map in a set on a game's page: one of the game's own (remixed), or one in a pack or an add-on (edited with it loaded).</summary>
internal sealed record SetMap(string Label, MapChoice? Own, PackMapChoice? Pack);

/// <summary>
/// A set of maps on a game's page, shown as a card: the game's own maps, an official add-on's, or a collection found near the
/// games (a loose WAD, or an archive with every WAD in it). Picking its card shows its maps.
/// </summary>
internal sealed partial class MapSetViewModel(GameTileViewModel tile, string key, string title, string where, IReadOnlyList<SetMap> maps, IReadOnlyList<MapFile> packs) : ObservableObject
{
    /// <summary>The game whose page it is on.</summary>
    public GameTileViewModel Tile { get; } = tile;

    /// <summary>Whether it can be played as it is: the game itself, an add-on, or a collection of one WAD (not a heap of them).</summary>
    public bool CanPlay => Key == "own" || Packs.Count == 1;

    /// <summary>What makes it the same set in a later look: "own", or the archive's or the WAD's path.</summary>
    public string Key { get; } = key;

    public string Title { get; } = title;

    /// <summary>What it is and where it was found: "The game's own maps", "In DOOM WADs".</summary>
    public string Where { get; } = where;

    public IReadOnlyList<SetMap> Maps { get; } = maps;

    /// <summary>The packs its maps are in (none for the game's own): the card's picture is made from them.</summary>
    public IReadOnlyList<MapFile> Packs { get; } = packs;

    /// <summary>"32 maps", or "1,502 WADs · 1,611 maps" for a collection.</summary>
    public string Count
    {
        get
        {
            var maps = Maps.Count == 1 ? "1 map" : $"{Maps.Count:N0} maps";
            return Packs.Count > 1 ? $"{Packs.Count:N0} WADs · {maps}" : maps;
        }
    }

    /// <summary>The card's picture: the game's cover, a pack's title screen, or its first map drawn; null until made.</summary>
    [ObservableProperty]
    private Bitmap? _picture;

    /// <summary>A title screen fills the card; a drawn map sits whole on black.</summary>
    [ObservableProperty]
    private Avalonia.Media.Stretch _pictureStretch = Avalonia.Media.Stretch.UniformToFill;

    /// <summary>What its picture is made from: its packs (a pack added or gone makes another).</summary>
    public string PictureKey => string.Join("|", Packs.Take(SetPictures.Looked).Select(p => p.Name).Prepend(Key));

    /// <summary>The set's maps for one game page: its own maps, its add-ons' maps, then each collection near the games.</summary>
    public static IReadOnlyList<MapSetViewModel> For(GameTileViewModel tile)
    {
        var sets = new List<MapSetViewModel>();
        if (tile.Maps.Count > 0)
        {
            sets.Add(new MapSetViewModel(tile, "own", tile.Title, "The game's own maps", [.. tile.Maps.Select(m => new SetMap(m.Label, m, null))], []));
        }

        foreach (var (title, maps) in tile.AddOns)
        {
            if (maps.Count == 0) continue;
            sets.Add(new MapSetViewModel(tile, maps[0].Pack.Path, title, "An official add-on", [.. maps.Select(m => new SetMap(m.Slot, null, m))], [maps[0].Pack]));
        }

        // A collection is the archive the WADs came in, or the loose WAD itself.
        foreach (var g in tile.PackMaps.GroupBy(m => m.Pack.Path, StringComparer.Ordinal).OrderBy(g => Path.GetFileNameWithoutExtension(g.Key), StringComparer.OrdinalIgnoreCase))
        {
            var packs = g.Select(m => m.Pack).Distinct().ToList();
            var several = packs.Count > 1;
            var folder = Path.GetFileName(Path.GetDirectoryName(g.Key)) ?? "";
            var maps = g.Select(m => new SetMap(several ? $"{Path.GetFileNameWithoutExtension(m.Pack.Inside ?? m.Pack.Path)} › {m.Slot}" : m.Slot, null, m)).ToList();
            sets.Add(new MapSetViewModel(tile, g.Key, Path.GetFileNameWithoutExtension(g.Key), folder.Length > 0 ? $"In {folder}" : "", maps, packs));
        }

        return sets;
    }

    /// <summary>Whether <paramref name="text"/> is in its title, or in one of its maps' names.</summary>
    public bool Matches(string text) => Title.Contains(text, StringComparison.OrdinalIgnoreCase) || Maps.Any(m => m.Label.Contains(text, StringComparison.OrdinalIgnoreCase));
}
