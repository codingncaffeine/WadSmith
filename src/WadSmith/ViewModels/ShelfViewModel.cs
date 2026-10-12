using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Editor;

namespace WadSmith.App.ViewModels;

/// <summary>What a shelf tile carries when dragged onto the map: a texture's name, or an object's number.</summary>
internal sealed record ShelfDrop(string? Texture, int Thing, string Label)
{
    /// <summary>The drag's format: an object handed over within WadSmith.</summary>
    public static readonly Avalonia.Input.DataFormat<ShelfDrop> Format = Avalonia.Input.DataFormat.CreateInProcessFormat<ShelfDrop>("wadsmith-shelf");
}

/// <summary>One picture on a shelf: what it is, in words, and what dragging it puts on the map.</summary>
internal sealed class ShelfTile(string label, string tooltip, string group, string search, Bitmap? picture, ShelfDrop drop)
{
    public string Label { get; } = label;

    public string Tooltip { get; } = tooltip;

    public string Group { get; } = group;

    /// <summary>Everything a search can match, in lower case.</summary>
    public string Search { get; } = search;

    public Bitmap? Picture { get; } = picture;

    public ShelfDrop Drop { get; } = drop;
}

/// <summary>
/// A shelf of the game's own textures or objects (D43), each a picture to drag onto the map. The
/// pictures are made on a worker when the shelf first opens; search and the group filter work on the
/// shelf's own list only.
/// </summary>
internal sealed partial class ShelfViewModel : ObservableObject
{
    private const int Thumb = 64;
    private readonly Func<IReadOnlyList<ShelfTile>> _build;
    private IReadOnlyList<ShelfTile> _all = [];
    private bool _started;

    private ShelfViewModel(string empty, IReadOnlyList<string> groups, Func<IReadOnlyList<ShelfTile>> build)
    {
        Groups = groups;
        _build = build;
        _note = empty;
    }

    public ObservableCollection<ShelfTile> Tiles { get; } = [];

    /// <summary>The groups to show: "All" first.</summary>
    public IReadOnlyList<string> Groups { get; }

    [ObservableProperty]
    private int _group;

    [ObservableProperty]
    private string _search = "";

    /// <summary>What the shelf says above its pictures: loading, how many, or why it is empty.</summary>
    [ObservableProperty]
    private string _note;

    /// <summary>Makes the pictures the first time the shelf is shown.</summary>
    public void Open()
    {
        if (_started) return;
        _started = true;
        Note = "Getting the pictures ready…";
        Task.Run(() =>
        {
            var tiles = _build();
            MainViewModel.PostToUi(() =>
            {
                _all = tiles;
                Filter();
            });
        });
    }

    partial void OnGroupChanged(int value) => Filter();

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        var group = Group > 0 && Group < Groups.Count ? Groups[Group] : null;
        var words = Search.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Tiles.Clear();
        foreach (var t in _all)
        {
            if ((group is null || t.Group == group) && words.All(t.Search.Contains)) Tiles.Add(t);
        }

        Note = _all.Count == 0 ? "Nothing here: the game's files were not found." : Tiles.Count == 0 ? "Nothing matches." : $"{Tiles.Count} of {_all.Count}. Drag one onto the map.";
    }

    /// <summary>The game's wall textures and floor and ceiling pictures, by name.</summary>
    public static ShelfViewModel Textures(GameResources? game) => new(
        game is null ? "Choose a base game to see its textures." : "",
        ["All", "Walls", "Floors and ceilings"],
        () =>
        {
            if (game is null) return [];
            var tiles = new List<ShelfTile>();
            foreach (var (infos, group, get) in new (IEnumerable<TextureInfo>, string, Func<string, RgbaImage?>)[] { (game.Textures.Walls, "Walls", game.Textures.GetWall), (game.Textures.Flats, "Floors and ceilings", game.Textures.GetFlat) })
            {
                foreach (var info in infos.OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    if (info.IsNull || info.Name is "-" or "") continue;
                    var picture = get(info.Name) is { } image ? Imaging.ToBitmap(Shrink(image)) : null;
                    var kind = group == "Walls" ? "Wall texture" : "Floor or ceiling";
                    tiles.Add(new ShelfTile(info.Name, $"{kind} {info.Name}, {info.Width} × {info.Height}. Drag it onto a wall, a floor or a ceiling.", group, info.Name.ToLowerInvariant(), picture, new ShelfDrop(info.Name, 0, info.Name)));
                }
            }

            return tiles;
        });

    /// <summary>The words each kind of object is shown under.</summary>
    private static readonly (ThingCategory Category, string Name)[] Kinds =
    [
        (ThingCategory.Monster, "Monsters"), (ThingCategory.Weapon, "Weapons"), (ThingCategory.Ammo, "Ammunition"), (ThingCategory.Health, "Health"),
        (ThingCategory.Armor, "Armor"), (ThingCategory.Key, "Keys"), (ThingCategory.Powerup, "Power-ups"), (ThingCategory.Decoration, "Decorations"),
        (ThingCategory.Start, "Player starts"), (ThingCategory.Marker, "Markers"),
    ];

    /// <summary>The game's objects with their sprites, by kind: monsters, weapons, ammunition and so on.</summary>
    public static ShelfViewModel Objects(GameResources? game) => new(
        game is null ? "Choose a base game to see its monsters and items." : "",
        ["All", .. Kinds.Select(k => k.Name)],
        () =>
        {
            if (game is null) return [];
            var tiles = new List<ShelfTile>();
            foreach (var (category, group) in Kinds)
            {
                foreach (var t in game.Things.All.Where(t => t.Category == category).OrderBy(t => t.Name, StringComparer.CurrentCulture))
                {
                    var picture = game.SpriteFor(t.Number) is { } image ? Imaging.ToBitmap(Shrink(image)) : null;
                    var search = $"{t.Name} {t.ClassName} {t.Number} {group}".ToLowerInvariant();
                    tiles.Add(new ShelfTile(t.Name, $"{t.Name} ({t.ClassName}, editor number {t.Number}). Drag it onto the map to place it.", group, search, picture, new ShelfDrop(null, t.Number, t.Name)));
                }
            }

            return tiles;
        });

    /// <summary>A picture shrunk to fit the tile, keeping its shape (nearest pixel, as the game's own pictures look).</summary>
    private static RgbaImage Shrink(RgbaImage image)
    {
        var scale = Math.Min(1.0, Math.Min((double)Thumb / image.Width, (double)Thumb / image.Height));
        if (scale >= 1) return image;
        var w = Math.Max(1, (int)(image.Width * scale));
        var h = Math.Max(1, (int)(image.Height * scale));
        var small = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Min(image.Height - 1, (int)(y / scale));
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Min(image.Width - 1, (int)(x / scale));
                Array.Copy(image.Pixels, (sy * image.Width + sx) * 4, small.Pixels, (y * w + x) * 4, 4);
            }
        }

        return small;
    }
}
