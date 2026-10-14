using Avalonia.Media.Imaging;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Library;
using WadSmith.Editor;

namespace WadSmith.App.ViewModels;

/// <summary>
/// The free sources on the shelves' choosers (D61): Realm667's objects on the Objects shelf; the texture packs (the Doom
/// Wiki's, Realm667's texture stock, an idgames search) and each pack fetched on the Textures shelf; and on both, Freedoom
/// and Blasphemer while they are not found. Nothing is asked of the internet until a source is chosen.
/// </summary>
internal static class LibrarySources
{
    public const string Realm667Name = "Realm667 (free)";
    public const string PacksName = "Texture packs (free)";

    /// <summary>The Objects shelf's sources.</summary>
    public static IReadOnlyList<ShelfSource> ForObjects(AppServices services, GameResources? game, Func<Task> rescan) =>
        game is null ? [] : [Realm667Objects(services, game), .. FreeGameSources(services, rescan)];

    /// <summary>The Textures shelf's sources: the packs to fetch, the packs fetched, the free games.</summary>
    public static IReadOnlyList<ShelfSource> ForTextures(AppServices services, GameResources? game, Func<Task> rescan) =>
        game is null ? [] : [Packs(services, game), .. services.Library.InstalledPacks().Select(p => Pack(services, game, p.Item, p.Files)), .. FreeGameSources(services, rescan)];

    private static ShelfSource Realm667Objects(AppServices services, GameResources game) => new(Realm667Name, async (feed, cancel) =>
    {
        var store = services.Library;
        var style = StyleOf(game.Game.Engine);
        // The categories made in this game's style first.
        var categories = Realm667.Categories.Where(c => c.Kind == LibraryKind.Object).OrderBy(c => c.Style == style ? 0 : c.Style.Length == 0 ? 1 : 2).ToList();
        var count = 0;
        for (var i = 0; i < categories.Count; i++)
        {
            feed.Say($"Getting Realm667's list… ({i + 1} of {categories.Count})");
            var items = await store.Realm667Async(categories[i], cancel).ConfigureAwait(false);
            count += items.Count;
            feed.Add([.. items.Select(item => ItemTile(store, item, Realm667Name))]);
        }

        feed.Say(count == 0
            ? store.Online ? "Realm667 had nothing to list just now." : "WadSmith is set not to use the internet, and Realm667's list has not been fetched before."
            : $"Realm667: {count} monsters, weapons, items and props made by the community, free to use. Drag one onto the map: it is downloaded and brought into this project with its credits (GZDoom).");
    });

    /// <summary>A tile for an object of a free source: its preview fetched when seen, "Get" until it has been downloaded.</summary>
    private static ShelfTile ItemTile(LibraryStore store, LibraryItem item, string source)
    {
        var by = item.Field("Submitted") is { Length: > 0 } who ? $" by {who}" : "";
        var size = item.Size is null ? "" : $" ({item.SizeWords})";
        var about = item.Description.Length > 0 ? " " + Short(item.Description, 220) : "";
        var credits = item.Credits.Length > 0 ? "\nCredits: " + Short(item.Credits.Replace('\n', ';'), 300) : "";
        var tip = $"{item.Title}, {Lower(item.Group)} in {item.Style} style from Realm667{by}.{about}\nDrag it onto the map: it is downloaded{size} and brought into this project with its credits; the map then plays in GZDoom.{credits}";
        return new ShelfTile(item.Title, tip, item.Group, $"{item.Title} {item.Group} {item.Style} {item.Credits} realm667".ToLowerInvariant(), null, new ShelfDrop(null, 0, $"{item.Title} from Realm667", Library: item), source)
        {
            Badge = store.Fetched(item) is null ? "Get" : null,
            BadgeNow = () => store.Fetched(item) is null ? "Get" : null,
            LoadPicture = async cancel => Thumbnail(await store.PreviewAsync(item, cancel).ConfigureAwait(false)),
        };
    }

    /// <summary>The texture packs to fetch: the Doom Wiki's (through idgames), then Realm667's texture stock; and a search of idgames.</summary>
    private static ShelfSource Packs(AppServices services, GameResources game)
    {
        ShelfSource.Feed? fed = null;
        var source = new ShelfSource(PacksName, async (feed, cancel) =>
        {
            fed = feed;
            var store = services.Library;
            feed.Say("Getting the Doom Wiki's texture packs…");
            var packs = await store.TexturePacksAsync(cancel).ConfigureAwait(false);
            feed.Add([.. packs.Select(p => PackTile(services, game, feed, p))]);
            var count = packs.Count;
            foreach (var category in Realm667.Categories.Where(c => c.Kind == LibraryKind.TexturePack))
            {
                feed.Say($"Getting Realm667's texture stock ({category.Style})…");
                var items = await store.Realm667Async(category, cancel).ConfigureAwait(false);
                count += items.Count;
                feed.Add([.. items.Select(p => PackTile(services, game, feed, p))]);
            }

            feed.Say(count == 0
                ? store.Online ? "No texture packs could be listed just now." : "WadSmith is set not to use the internet, and the texture packs have not been listed before."
                : "Texture packs: the Doom Wiki's picks first, then Realm667's. Click one to download it; its textures then have a place of their own on this list. Type a name to look for more on idgames.");
        })
        {
            Searcher = words => words.Length < 3 || fed is not { } feed ? null : new ShelfTile($"Find “{words}”", $"Looks for texture packs and other resources named “{words}” in the idgames archive and adds them here.", "Texture packs", "", null, new ShelfDrop(null, 0, words), PacksName)
            {
                Badge = "Find",
                Activate = () => _ = Task.Run(async () =>
                {
                    feed.Say($"Looking for “{words}” on idgames…");
                    try
                    {
                        var found = await services.Library.SearchIdgamesAsync(words, CancellationToken.None).ConfigureAwait(false);
                        feed.Add([.. found.Select(p => PackTile(services, game, feed, p, words))]);
                        feed.Say(found.Count == 0 ? $"idgames has nothing named “{words}” among its graphics." : $"idgames: {found.Count} found for “{words}”. Click one to download it.");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or WadSmith.Core.CorruptDataException or TaskCanceledException)
                    {
                        feed.Say($"idgames could not be reached: {ex.Message}");
                    }
                }),
            },
        };
        return source;
    }

    /// <summary>A pack to fetch: clicking downloads it (the note says how far) and adds it to the chooser, chosen.</summary>
    private static ShelfTile PackTile(AppServices services, GameResources game, ShelfSource.Feed feed, LibraryItem item, string? found = null)
    {
        var store = services.Library;
        var size = item.Size is null ? "" : $" ({item.SizeWords})";
        var licence = item.Licence is { Length: > 0 } l ? "\nPermissions: " + Short(l, 300) : "";
        var where = item.Source == "realm667" ? "Realm667" : "the idgames archive";
        var about = item.Description.Length > 0 ? " " + Short(item.Description, 260) : "";
        var tip = $"{item.Title}, a texture pack from {where}.{about}\nClick it to download it{size}; its textures then have a place of their own on this list, and each one you use is copied into this project with the pack's credits.{licence}";
        ShelfTile? tile = null;
        tile = new ShelfTile(item.Title, tip, "Texture packs", $"{item.Title} {item.Description} {item.Credits} {found} texture pack".ToLowerInvariant(), null, new ShelfDrop(null, 0, item.Title), PacksName)
        {
            Badge = store.Fetched(item) is null ? "Get" : "Open",
            BadgeNow = () => store.Fetched(item) is null ? "Get" : "Open",
            LoadPicture = async cancel => Thumbnail(await store.PreviewAsync(item, cancel).ConfigureAwait(false)) ?? Swatch(item.Title),
            Activate = () => _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<(long Done, long? Total)>(p => feed.Say(p.Total is { } t ? $"Getting {item.Title}… {p.Done / 1048576.0:0.0} of {t / 1048576.0:0.0} MB" : $"Getting {item.Title}… {p.Done / 1048576.0:0.0} MB"));
                    if (store.Fetched(item) is null) feed.Say($"Getting {item.Title}…");
                    var files = await store.FetchAsync(item, progress, CancellationToken.None).ConfigureAwait(false);
                    MainViewModel.PostToUi(() => tile!.Badge = "Open");
                    feed.AddSource(Pack(services, game, item, files));
                    feed.Say($"{item.Title} is downloaded: its textures are under “{item.Title}” on this list.");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or WadSmith.Core.CorruptDataException or TaskCanceledException or InvalidDataException)
                {
                    WadSmith.Editor.App.Log.Warn($"Fetching the pack {item.Id} failed: {ex.Message}");
                    feed.Say($"{item.Title} could not be downloaded: {ex.Message}");
                }
            }),
        };
        return tile;
    }

    /// <summary>A fetched texture pack as a source of its own: its textures, each copied into the project when used.</summary>
    private static ShelfSource Pack(AppServices services, GameResources game, LibraryItem item, IReadOnlyList<string> files) => new(item.Title, (feed, cancel) =>
    {
        feed.Say($"Reading {item.Title}…");
        if (services.OpenPack(item, files, game.Game.Id) is not { Pack: { } own } pack)
        {
            feed.Say($"{item.Title} was drawn for {StyleWords(item.Family)}, and no game of that kind was found to read its colours.");
            return Task.CompletedTask;
        }

        // A pack that carries the game's own textures too (a megawad's TEXTURE1 repeats them) shows only what differs.
        var mine = game.Game.Engine == item.Family ? game : null;
        var batch = new List<ShelfTile>();
        var count = 0;
        foreach (var (infos, flat) in new (IEnumerable<TextureInfo>, bool)[] { (pack.Textures.Walls, false), (pack.Textures.Flats, true) })
        {
            foreach (var info in infos.Where(i => own.Contains(i.Source) && !i.IsNull && i.Name is not ("-" or "")).OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                var image = flat ? pack.Textures.GetFlat(info.Name) : pack.Textures.GetWall(info.Name);
                if (image is null) continue;
                if (mine is not null && (flat ? mine.Textures.GetFlat(info.Name) : mine.Textures.GetWall(info.Name)) is { } ours && ours.Width == image.Width && ours.Height == image.Height && ours.Pixels.AsSpan().SequenceEqual(image.Pixels)) continue;
                var kind = flat ? "floor or ceiling" : "wall texture";
                batch.Add(new ShelfTile(info.Name, $"{item.Title}'s {kind} {info.Name}, {image.Width} × {image.Height}. Drag it onto a wall, a floor or a ceiling: it is copied into this project, with the pack's credits.", flat ? "Floors and ceilings" : "Walls", $"{info.Name} {item.Title}".ToLowerInvariant(), Imaging.ToBitmap(ShelfViewModel.Shrink(image)), new ShelfDrop(info.Name, 0, $"{info.Name} from {item.Title}", From: pack, Flat: flat), item.Title));
                count++;
                if (batch.Count >= 48)
                {
                    feed.Add(batch);
                    batch = [];
                }
            }
        }

        feed.Add(batch);
        var licence = item.Licence is { Length: > 0 } l ? " Permissions: " + Short(l, 160) : "";
        feed.Say($"{item.Title}: {count} textures. Drag one onto a wall or a floor.{licence}");
        return Task.CompletedTask;
    });

    /// <summary>Freedoom and Blasphemer while they are not found: a tile that fetches the game, after which it is a game like the others.</summary>
    private static IEnumerable<ShelfSource> FreeGameSources(AppServices services, Func<Task> rescan)
    {
        foreach (var release in FreeGames.All.Where(r => !services.HasFreeGame(r)))
        {
            var name = $"{release.Title} (free)";
            yield return new ShelfSource(name, (feed, _) =>
            {
                ShelfTile? tile = null;
                tile = new ShelfTile($"Get {release.Title}", $"Downloads {release.Title} {release.Version} ({release.Size / 1048576.0:0} MB): {release.What}. {release.Licence}. It is checked against its published checksum, and its textures and objects then show on the shelves for any map.", "Texture packs", release.Title.ToLowerInvariant(), null, new ShelfDrop(null, 0, release.Title), name)
                {
                    Badge = "Get",
                    Activate = () => MainViewModel.PostToUi(async () =>
                    {
                        try
                        {
                            var progress = new Progress<(long Done, long? Total)>(p => feed.Say(p.Total is { } t ? $"Getting {release.Title}… {p.Done / 1048576.0:0} of {t / 1048576.0:0} MB" : $"Getting {release.Title}… {p.Done / 1048576.0:0} MB"));
                            feed.Say($"Getting {release.Title}…");
                            await services.InstallFreeGameAsync(release, progress, services.ShuttingDown).ConfigureAwait(true);
                            feed.Say($"{release.Title} is in; looking for it among your games…");
                            await rescan().ConfigureAwait(true);
                            var title = (services.Games ?? []).FirstOrDefault(g => release.Files.Any(f => string.Equals(Path.GetFileName(g.Path), f, StringComparison.OrdinalIgnoreCase)))?.Game.Title;
                            feed.Reload(title);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                        {
                            WadSmith.Editor.App.Log.Warn($"Fetching {release.Title} failed: {ex.Message}");
                            feed.Say($"{release.Title} could not be fetched: {ex.Message} Try again later.");
                        }
                    }),
                };
                feed.Add([tile]);
                feed.Say($"{release.Title}: {release.What}. {release.Licence}. Click Get to download it ({release.Size / 1048576.0:0} MB).");
                return Task.CompletedTask;
            })
            {
                Gone = () => services.HasFreeGame(release),
            };
        }
    }

    /// <summary>A preview picture shrunk to a tile, or null when it could not be read.</summary>
    private static Bitmap? Thumbnail(byte[]? data)
    {
        if (data is null) return null;
        try
        {
            return Imaging.ToBitmap(ShelfViewModel.Shrink(ImageCodec.Decode(data, "preview")));
        }
        catch (WadSmith.Core.CorruptDataException)
        {
            return null;
        }
    }

    /// <summary>A pack without a picture of its own: a small wall of bricks in a colour of its name (drawn here, D61), so the tiles do not stand empty.</summary>
    private static Bitmap Swatch(string name)
    {
        var hue = (uint)name.Aggregate(17, (h, c) => (h * 31) + c) % 360;
        var image = new RgbaImage(64, 48);
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var row = y / 8;
                var mortar = y % 8 == 7 || (x + (row % 2 == 0 ? 0 : 8)) % 16 == 15;
                var shade = mortar ? 0.25 : 0.55 + (0.12 * (((x / 16) + row) % 3) / 2.0);
                var (r, g, b) = Hsv(hue, 0.45, shade);
                image.Set(x, y, r, g, b, 255);
            }
        }

        return Imaging.ToBitmap(image);
    }

    private static (byte R, byte G, byte B) Hsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        var (r, g, b) = (int)(hue / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        var m = value - c;
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static string StyleOf(string engine) => engine switch { "heretic" or "hexen" => "Heretic/Hexen", "strife" => "Strife", _ => "Doom" };

    private static string StyleWords(string family) => family switch { "heretic" => "Heretic", "hexen" => "Hexen", "strife" => "Strife", _ => "Doom" };

    private static string Lower(string group) => group switch { "Monsters" => "a monster", "Weapons" => "a weapon", "Ammunition" => "ammunition", "Health" => "health or armour", "Keys" => "a key", "Power-ups" => "a power-up", _ => "a decoration" };

    private static string Short(string text, int most)
    {
        var t = text.Replace('\n', ' ').Trim();
        return t.Length <= most ? t : t[..most].TrimEnd() + "…";
    }
}
