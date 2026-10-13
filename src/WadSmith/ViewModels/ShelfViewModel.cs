using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Editor;

namespace WadSmith.App.ViewModels;

/// <summary>
/// What a shelf tile carries when dragged onto the map: a texture's name, an object's number, or a prefab; a picture from
/// another game says which (<paramref name="From"/>) and whether it is a floor; a 3D model file dragged in from the file manager is <paramref name="ModelFile"/>; one of Quake's own models is <paramref name="QuakeModel"/> (its path in the PAK).
/// </summary>
internal sealed record ShelfDrop(string? Texture, int Thing, string Label, WadSmith.Editor.Editing.Prefab? Prefab = null, GameResources? From = null, bool Flat = false, string? ModelFile = null, string? QuakeModel = null)
{
    /// <summary>The drag's format: an object handed over within WadSmith.</summary>
    public static readonly Avalonia.Input.DataFormat<ShelfDrop> Format = Avalonia.Input.DataFormat.CreateInProcessFormat<ShelfDrop>("wadsmith-shelf");
}

/// <summary>One picture on a shelf: what it is, in words, and what dragging it puts on the map.</summary>
internal sealed class ShelfTile(string label, string tooltip, string group, string search, Bitmap? picture, ShelfDrop drop, string? game = null)
{
    public string Label { get; } = label;

    /// <summary>The game it is from: null for the map's own game.</summary>
    public string? Game { get; } = game;

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
    private readonly Func<IEnumerable<IReadOnlyList<ShelfTile>>>[] _stages;
    private IReadOnlyList<ShelfTile> _all = [];
    private bool _started;
    private int _opened;

    /// <param name="empty">What the shelf says before its pictures are ready, or why it has none.</param>
    /// <param name="groups">The groups to choose from, "All" first.</param>
    /// <param name="stages">What the shelf holds, in parts made one after another on a worker; each part shows its batches as they are ready.</param>
    private ShelfViewModel(string empty, IReadOnlyList<string> groups, params Func<IEnumerable<IReadOnlyList<ShelfTile>>>[] stages)
    {
        Groups = groups;
        _stages = stages;
        _note = empty;
    }

    /// <summary>A stage made in one piece.</summary>
    private static Func<IEnumerable<IReadOnlyList<ShelfTile>>> Once(Func<IReadOnlyList<ShelfTile>> stage) => () => [stage()];

    /// <summary>
    /// The tiles of each of <paramref name="others"/>, made by <paramref name="make"/> on threads of their own (a few, never the
    /// shared pool), each game's batch handed on as soon as it is ready, so the first games show while the rest are made.
    /// </summary>
    private static IEnumerable<IReadOnlyList<ShelfTile>> PerGame(IReadOnlyList<Func<GameResources?>> others, Func<GameResources, IReadOnlyList<ShelfTile>> make)
    {
        if (others.Count == 0) yield break;
        using var done = new System.Collections.Concurrent.BlockingCollection<IReadOnlyList<ShelfTile>>();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Func<GameResources?>>(others);
        var workers = Math.Clamp(Environment.ProcessorCount / 2, 1, Math.Min(4, others.Count));
        var left = workers;
        for (var w = 0; w < workers; w++)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    while (queue.TryDequeue(out var open))
                    {
                        // Opening the game is most of the work: it happens here, on this thread, not before.
                        if (open() is not { } other) continue;
                        try
                        {
                            done.Add(make(other));
                        }
                        catch (Exception ex) when (ex is IOException or WadSmith.Core.CorruptDataException or InvalidOperationException)
                        {
                            WadSmith.Editor.App.Log.Warn($"The shelf could not show {other.Game.Title}'s pictures: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref left) == 0) done.CompleteAdding();
                }
            })
            { IsBackground = true, Name = "Shelf pictures", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        foreach (var batch in done.GetConsumingEnumerable()) yield return batch;
    }

    public ObservableCollection<ShelfTile> Tiles { get; } = [];

    /// <summary>The games the shelf can show (D55): the map's own first, then the user's other games as their pictures arrive.</summary>
    public ObservableCollection<string> Games { get; } = [];

    [ObservableProperty]
    private int _gameIndex;

    private string? _ownGame;

    partial void OnGameIndexChanged(int value) => Filter();

    /// <summary>The groups to show: "All" first.</summary>
    public IReadOnlyList<string> Groups { get; }

    [ObservableProperty]
    private int _group;

    [ObservableProperty]
    private string _search = "";

    /// <summary>What the shelf says above its pictures: loading, how many, or why it is empty.</summary>
    [ObservableProperty]
    private string _note;

    /// <summary>The words of the shelf's button for bringing in a file of the user's own (a 3D model on the Objects shelf); null for no button.</summary>
    public string? AddLabel { get; set; }

    public string AddTip { get; set; } = "";

    /// <summary>What the button does.</summary>
    public Action? Add { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AddFile() => Add?.Invoke();

    /// <summary>Makes the pictures again (a prefab was saved), at once if the shelf has been shown, else when it is.</summary>
    public void Refresh()
    {
        if (!_started) return;
        _started = false;
        Open();
    }

    /// <summary>Makes the pictures the first time the shelf is shown.</summary>
    public void Open()
    {
        if (_started) return;
        _started = true;
        var opening = ++_opened;
        Note = "Getting the pictures ready…";
        Task.Run(() =>
        {
            var first = true;
            foreach (var tiles in _stages.SelectMany(stage => stage()))
            {
                var replace = first;
                first = false;
                MainViewModel.PostToUi(() =>
                {
                    if (opening != _opened) return;
                    _all = replace ? tiles : [.. _all, .. tiles];
                    var chosen = GameIndex > 0 && GameIndex < Games.Count ? Games[GameIndex] : null;
                    var names = new List<string> { _ownGame ?? "This game" };
                    names.AddRange(_all.Select(t => t.Game).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture));
                    if (!names.SequenceEqual(Games))
                    {
                        Games.Clear();
                        foreach (var n in names) Games.Add(n);

                        // Clearing the list unselects it in the view, which takes the new items in after this turn: the choice is said
                        // again on the next one, even when its number is the same.
                        var index = chosen is null ? 0 : Math.Max(0, names.IndexOf(chosen));
                        GameIndex = index;
                        MainViewModel.PostToUi(() =>
                        {
                            if (opening != _opened) return;
                            GameIndex = -1;
                            GameIndex = index;
                        });
                    }

                    Filter();
                });
            }
        });
    }

    partial void OnGroupChanged(int value) => Filter();

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        var group = Group > 0 && Group < Groups.Count ? Groups[Group] : null;
        var game = GameIndex > 0 && GameIndex < Games.Count ? Games[GameIndex] : null;
        var words = Search.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Tiles.Clear();
        foreach (var t in _all)
        {
            if ((group is null || t.Group == group) && t.Game == game && words.All(t.Search.Contains)) Tiles.Add(t);
        }

        Note = _all.Count == 0 ? "Nothing here: the game's files were not found." : Tiles.Count == 0 ? "Nothing matches." : $"{Tiles.Count} of {_all.Count}. Drag one onto the map.";
    }

    /// <summary>
    /// The game's wall textures and floor and ceiling pictures, by name; then those of the user's other games
    /// (<paramref name="others"/> lists them, opened on the shelf's own threads), copied into the project when used. From a game of the same
    /// family (Doom for a Doom II map) only the pictures this game lacks.
    /// </summary>
    public static ShelfViewModel Textures(GameResources? game, Func<IReadOnlyList<Func<GameResources?>>>? others = null) => new ShelfViewModel(
        game is null ? "Choose a base game to see its textures." : "",
        ["All", "Walls", "Floors and ceilings"],
        Once(() =>
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
        }),
        () => game is null ? [] : PerGame(others?.Invoke() ?? [], other =>
        {
            var tiles = new List<ShelfTile>();
            {
                var family = other.Game.Engine == game.Game.Engine;
                foreach (var (infos, flat) in new (IEnumerable<TextureInfo>, bool)[] { (other.Textures.Walls, false), (other.Textures.Flats, true) })
                {
                    foreach (var info in infos.OrderBy(i => i.Name, StringComparer.Ordinal))
                    {
                        if (info.IsNull || info.Name is "-" or "" || info.Name.StartsWith("F_SKY", StringComparison.OrdinalIgnoreCase) || (family && game.Textures.Has(info.Name))) continue;
                        var image = flat ? other.Textures.GetFlat(info.Name) : other.Textures.GetWall(info.Name);
                        if (image is null) continue;
                        var kind = flat ? "floor or ceiling" : "wall texture";
                        tiles.Add(new ShelfTile(info.Name, $"{other.Game.Title}'s {kind} {info.Name}, {image.Width} × {image.Height}. Drag it onto a wall, a floor or a ceiling: it is copied into this project, in its own colours.", flat ? "Floors and ceilings" : "Walls", $"{info.Name} {other.Game.Title}".ToLowerInvariant(), Imaging.ToBitmap(Shrink(image)), new ShelfDrop(info.Name, 0, $"{info.Name} from {other.Game.Title}", From: other, Flat: flat), other.Game.Title));
                    }
                }
            }

            return tiles;
        })) { _ownGame = game?.Game.Title };

    /// <summary>The words each kind of object is shown under.</summary>
    private static readonly (ThingCategory Category, string Name)[] Kinds =
    [
        (ThingCategory.Monster, "Monsters"), (ThingCategory.Weapon, "Weapons"), (ThingCategory.Ammo, "Ammunition"), (ThingCategory.Health, "Health"),
        (ThingCategory.Armor, "Armor"), (ThingCategory.Key, "Keys"), (ThingCategory.Powerup, "Power-ups"), (ThingCategory.Decoration, "Decorations"),
        (ThingCategory.Start, "Player starts"), (ThingCategory.Marker, "Markers"),
    ];

    private const string PrefabGroup = "Prefabs";

    /// <summary>The group of the props brought in from 3D model files (Phase 11).</summary>
    public const string ModelGroup = "Your models";

    /// <summary>
    /// The game's objects with their sprites, by kind: monsters, weapons, ammunition and so on; and the project's prefabs
    /// (<paramref name="prefabs"/> lists them on the worker), each pictured from above.
    /// </summary>
    public static ShelfViewModel Objects(GameResources? game, Func<IReadOnlyList<WadSmith.Editor.Editing.Prefab>>? prefabs = null, Func<IReadOnlyList<Func<GameResources?>>>? others = null, Func<IReadOnlyCollection<int>>? models = null) => new ShelfViewModel(
        game is null ? "Choose a base game to see its monsters and items." : "",
        ["All", .. Kinds.Select(k => k.Name), PrefabGroup, ModelGroup],
        Once(() =>
        {
            var tiles = new List<ShelfTile>();
            foreach (var prefab in prefabs?.Invoke() ?? [])
            {
                Bitmap? picture = null;
                try
                {
                    var model = WadSmith.Core.Maps.MapConversion.FromUdmf("PREFAB", WadSmith.Core.Maps.Udmf.UdmfText.Parse(File.ReadAllText(prefab.Path)));
                    var frame = WadSmith.Editor.Model.EditorMap.FromModel(model).Frame;
                    using var png = new MemoryStream(Canvas.MapThumbnail.Render(frame, new Canvas.MapLook(game), Thumb, Thumb));
                    picture = new Bitmap(png);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WadSmith.Core.CorruptDataException or FormatException)
                {
                    WadSmith.Editor.App.Log.Warn($"The prefab {prefab.Path} could not be pictured: {ex.Message}");
                }

                tiles.Add(new ShelfTile(prefab.Name, $"The prefab {prefab.Name}, saved from this project. Drag it onto the map to put a copy there.", PrefabGroup, $"{prefab.Name} prefab".ToLowerInvariant(), picture, new ShelfDrop(null, 0, prefab.Name, prefab)));
            }

            if (game is null) return tiles;
            var props = models?.Invoke() ?? [];
            foreach (var (category, group) in Kinds)
            {
                // Only what this game's own data can show: another game's objects (Doom II's super shotgun in Doom) would show an error in the game.
                foreach (var t in game.Things.All.Where(t => t.Category == category && game.CanShow(t.Number)).OrderBy(t => t.Name, StringComparer.CurrentCulture))
                {
                    var picture = game.SpriteFor(t.Number) is { } image ? Imaging.ToBitmap(Shrink(image)) : null;
                    var prop = props.Contains(t.Number);
                    var search = $"{t.Name} {t.ClassName} {t.Number} {group}{(prop ? " model prop 3d" : "")}".ToLowerInvariant();
                    tiles.Add(new ShelfTile(t.Name, prop ? $"{t.Name}, a 3D model brought into this project (editor number {t.Number}). Drag it onto the map to place it; GZDoom draws the model." : $"{t.Name} ({t.ClassName}, editor number {t.Number}). Drag it onto the map to place it.", prop ? ModelGroup : group, search, picture, new ShelfDrop(null, t.Number, t.Name)));
                }
            }

            return tiles;
        }),
        // The user's other games (D55): their monsters, weapons, decorations and pickups, brought into the project when placed
        // with everything they fire, drop or spawn; those whose pictures share a name with this game's come in a later update.
        () => game is null ? [] : PerGame(others?.Invoke() ?? [], other =>
        {
            var tiles = new List<ShelfTile>();

            // Quake's own models (D43, Phase 11): props pictured from the front, brought in as models when placed.
            if (other.Game.Engine == "quake")
            {
                WadSmith.Core.Quake.QuakeModelLibrary library;
                try
                {
                    library = WadSmith.Core.Quake.QuakeModelLibrary.Open(other.Files[0]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WadSmith.Core.CorruptDataException)
                {
                    WadSmith.Editor.App.Log.Warn($"Quake's models could not be listed: {ex.Message}");
                    return tiles;
                }

                foreach (var model in library.Models)
                {
                    Bitmap? picture = null;
                    try
                    {
                        var mesh = WadSmith.Core.Models.Mdl.Read(library.Read(model), library.Palette, model).ToMesh();
                        var origin = ((mesh.Min.X + mesh.Max.X) / 2, (mesh.Min.Y + mesh.Max.Y) / 2, mesh.Min.Z);
                        picture = Imaging.ToBitmap(Shrink(WadSmith.Core.Models.MeshBaker.Bake(mesh, WadSmith.Editor.Editing.ModelProps.Stretch, origin, 1)));
                    }
                    catch (WadSmith.Core.CorruptDataException ex)
                    {
                        WadSmith.Editor.App.Log.Warn($"Quake's {model} could not be pictured: {ex.Message}");
                        continue;
                    }

                    var title = WadSmith.Editor.Editing.ModelProps.Title(Path.GetFileNameWithoutExtension(model));
                    tiles.Add(new ShelfTile(title, $"Quake's {title} ({model}). Drag it onto the map: its model and skin are copied into this project as a prop that stands still, and GZDoom draws it.", "Decorations", $"{title} {model} quake model decorations".ToLowerInvariant(), picture, new ShelfDrop(null, 0, $"{title} from Quake", From: other, QuakeModel: model), other.Game.Title));
                }

                return tiles;
            }

            // Duke Nukem 3D's enemies (D55): Duke's look, a Doom monster's behaviour.
            if (other.Build is { } build)
            {
                foreach (var enemy in build.Enemies)
                {
                    var picture = build.Art.Tile(enemy.PictureTile) is { } tile ? Imaging.ToBitmap(Shrink(build.Art.Picture(tile))) : null;
                    var behaviour = WadSmith.Core.Build.DukeEnemies.Behaviours.FirstOrDefault(b => b.Class == enemy.Behaviour).Name ?? enemy.Behaviour;
                    tiles.Add(new ShelfTile(enemy.Name, $"Duke Nukem 3D's {enemy.Name}. Drag it onto the map: it looks like Duke's and behaves like a {behaviour.ToLowerInvariant()} (Duke's own behaviour lives in scripts GZDoom cannot run); its frames are copied into this project, and the map plays in GZDoom.", "Monsters", $"{enemy.Name} {enemy.Key} monsters duke".ToLowerInvariant(), picture, new ShelfDrop(null, enemy.BaseTile, $"{enemy.Name} from Duke Nukem 3D", From: other), other.Game.Title));
                }

                return tiles;
            }

            {
                foreach (var (category, group) in Kinds.Where(k => k.Category is ThingCategory.Monster or ThingCategory.Weapon or ThingCategory.Decoration or ThingCategory.Health or ThingCategory.Armor or ThingCategory.Ammo or ThingCategory.Powerup))
                {
                    foreach (var t in other.Things.All.Where(t => t.Category == category && other.CanShow(t.Number)).OrderBy(t => t.Name, StringComparer.CurrentCulture))
                    {
                        if (WadSmith.Editor.Editing.ActorBorrowing.WhyNot(game, other, t) is not null) continue;
                        var picture = other.SpriteFor(t.Number) is { } image ? Imaging.ToBitmap(Shrink(image)) : null;
                        var weapon = t.Category == ThingCategory.Weapon ? $" It goes in weapon slot {other.Things.SlotOf(t.ClassName)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, as in {other.Game.Title}; its ammunition shows on GZDoom's other HUDs, not on this game's status bar." : "";
                        tiles.Add(new ShelfTile(t.Name, $"{other.Game.Title}'s {t.Name} ({t.ClassName}). Drag it onto the map: its pictures and sounds are copied into this project, and the map plays in GZDoom.{weapon}", group, $"{t.Name} {t.ClassName} {group} {other.Game.Title}".ToLowerInvariant(), picture, new ShelfDrop(null, t.Number, $"{t.Name} from {other.Game.Title}", From: other), other.Game.Title));
                    }
                }
            }

            return tiles;
        })) { _ownGame = game?.Game.Title };

    /// <summary>A picture shrunk to fit the tile, keeping its shape (nearest pixel, as the game's own pictures look).</summary>
    internal static RgbaImage Shrink(RgbaImage image)
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
