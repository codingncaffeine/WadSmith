using System.Globalization;
using System.Collections.Concurrent;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Editor.App;

namespace WadSmith.Editor;

/// <summary>
/// The game a project is made for, opened for reading: its textures and floors, its sprites, its
/// palette and the objects the engine defines for it (names, sizes, first frames). The base game is
/// read as it is, never copied (D43); the project's own folder sits on top, so its pictures win.
/// Opening reads files, so call <see cref="Load"/> on a worker.
/// </summary>
public sealed class GameResources
{
    private static readonly ConcurrentDictionary<(string Engine, string Family), Lazy<ThingCatalog>> Catalogues = new();
    private readonly ConcurrentDictionary<int, RgbaImage?> _sprites = new();
    private readonly ConcurrentDictionary<int, byte[]?> _sounds = new();
    private readonly ResourceStack _stack;

    private GameResources(GameDefinition game, IReadOnlyList<string> files, ResourceStack stack, TextureLibrary textures, SpriteIndex sprites, ThingCatalog things, IReadOnlyList<string> music)
    {
        _stack = stack;
        Game = game;
        Files = files;
        Textures = textures;
        Sprites = sprites;
        Things = things;
        Music = music;
        Skies = [.. textures.Walls.Where(w => w.Name.StartsWith("SKY", StringComparison.OrdinalIgnoreCase)).Select(w => w.Name).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The music the game (and the project) has, by name, A to Z: every lump or file holding a MUS or MIDI score, or an Ogg, FLAC or MP3 stream.</summary>
    public IReadOnlyList<string> Music { get; }

    /// <summary>The sky pictures the game has (every wall texture named SKY…), A to Z.</summary>
    public IReadOnlyList<string> Skies { get; }

    /// <summary>The names of the music in <paramref name="stack"/>, judged by content, A to Z.</summary>
    public static IReadOnlyList<string> FindMusic(ResourceStack stack)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in stack.All(ResourceNamespace.Global).Concat(stack.All(ResourceNamespace.Music)))
        {
            if (entry.IsMarker || entry.Size < 4 || entry.Size > 64 * 1024 * 1024) continue;
            byte[] data;
            try
            {
                data = entry.Read();
            }
            catch (Exception ex) when (ex is IOException or Core.CorruptDataException)
            {
                continue;
            }

            var head = data.AsSpan(0, 4);
            if (head.SequenceEqual("MUS\x1a"u8) || head.SequenceEqual("MThd"u8) || head.SequenceEqual("OggS"u8) || head.SequenceEqual("fLaC"u8) || head[..3].SequenceEqual("ID3"u8) || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && entry.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
            {
                names.Add(entry.Name);
            }
        }

        return [.. names];
    }

    public GameDefinition Game { get; }

    /// <summary>The base game files read, in load order (a base game before its add-on).</summary>
    public IReadOnlyList<string> Files { get; }

    public TextureLibrary Textures { get; }

    public SpriteIndex Sprites { get; private set; }

    public ThingCatalog Things { get; private set; }

    /// <summary>A floor or ceiling picture by name; null for "-" or a name the game does not have.</summary>
    public RgbaImage? Flat(string name) => Textures.GetFlat(name);

    /// <summary>A lump of the game's files by name and namespace, the last file that has it winning; null when none has.</summary>
    public ResourceEntry? Find(string name, ResourceNamespace ns) => _stack.Find(name, ns);

    private readonly object _carried = new();

    /// <summary>
    /// Takes in objects carried into the project from another game (D55): their sprites, now in the project's folder, join
    /// the sprite index, and the objects join the catalogue under their numbers here. Any thread.
    /// </summary>
    public void Carry(IEnumerable<ThingType> things, string projectFolder)
    {
        lock (_carried)
        {
            _stack.Add(ResourceContainer.Open(projectFolder));
            Sprites = new SpriteIndex(_stack);
            Things = Things.With(things);
            _sprites.Clear();
        }
    }

    /// <summary>The front-facing first frame of an object's sprite (mirrored where the game stores it mirrored); null when it has none.</summary>
    /// <summary>
    /// Whether the game's own data can show a thing: it is in the catalogue and, when its class has a picture, the picture is
    /// here. Doom II's super shotgun is in the catalogue for every Doom game but not in Doom's own data: the game shows an
    /// error for it, so the shelf does not offer it and Problems says so.
    /// </summary>
    public bool CanShow(int type) => Things.Find(type) is { } t && (t.Sprite is null || Sprites.Front(t.Sprite, t.Frame) is not null);

    public RgbaImage? SpriteFor(int type) => _sprites.GetOrAdd(type, t =>
    {
        if (Things.Find(t) is not { Sprite: { } sprite } info || Sprites.Front(sprite, info.Frame) is not { } front) return null;
        try
        {
            var image = PictureLoader.Load(front.Entry.Read(), ResourceNamespace.Sprites, Textures.Palette, front.Entry.Name).Image;
            return front.Mirrored ? Mirror(image) : image;
        }
        catch (Core.CorruptDataException)
        {
            return null;
        }
    });

    /// <summary>
    /// The sound an object is known by (a monster's sight, a pickup's) as a WAV file, from the game's own Doom-format
    /// sound lump; null when it has none or holds another format. Reads a lump, so call it off the UI thread.
    /// </summary>
    public byte[]? SoundFor(int type) => _sounds.GetOrAdd(type, t =>
    {
        if (Things.Find(t)?.Sound is not { } lump || (_stack.Find(lump, ResourceNamespace.Sounds) ?? _stack.Find(lump)) is not { } entry) return null;
        var data = entry.Read();
        try
        {
            return Core.Audio.AudioFormats.Detect(data) switch
            {
                Core.Audio.AudioKind.DmxSound => Core.Audio.AudioFormats.DmxToWav(data),
                Core.Audio.AudioKind.Wav => data,
                _ => null,
            };
        }
        catch (Core.CorruptDataException)
        {
            return null;
        }
    });

    /// <summary>
    /// Opens the game <paramref name="gameId"/> from the games found on this computer, with the project's
    /// <paramref name="addOns"/> (a map pack it was started from; ones no longer there are skipped) and then the
    /// project folder on top. Null when the game is not among them.
    /// </summary>
    public static GameResources? Load(string gameId, string? projectFolder, IReadOnlyList<FoundGame> games, string? engineData, IReadOnlyList<string>? addOns = null)
    {
        var found = games.FirstOrDefault(g => g.Game.Id == gameId);
        if (found is null)
        {
            Log.Warn($"The game {gameId} was not found among the scanned folders; the map is shown without its textures.");
            return null;
        }

        var files = new List<string>();
        if (found.Game.AddOnFor is { } baseId && games.FirstOrDefault(g => g.Game.Id == baseId) is { } baseGame) files.Add(baseGame.Path);
        files.Add(found.Path);
        foreach (var addOn in addOns ?? [])
        {
            if (File.Exists(addOn)) files.Add(addOn);
            else Log.Warn($"{Path.GetFileName(addOn)} is no longer there; the map is shown without what it brought.");
        }

        var stack = new ResourceStack(files.Select(ResourceContainer.Open));
        if (projectFolder is not null && Directory.Exists(projectFolder)) stack.Add(ResourceContainer.Open(projectFolder));

        var things = engineData is null
            ? ThingCatalog.Empty
            : Catalogues.GetOrAdd((engineData, found.Game.Engine), key => new Lazy<ThingCatalog>(() => ThingCatalog.Load(ResourceContainer.Open(key.Engine), key.Family))).Value;
        // Objects carried in from the user's other games (D55), by the numbers the project gave them.
        if (projectFolder is not null && engineData is not null && File.Exists(Path.Combine(projectFolder, Core.Projects.Project.SidecarName)))
        {
            try
            {
                var carried = new List<ThingType>();
                foreach (var copy in Core.Projects.Project.Open(projectFolder).Info.Copied.Where(c => c.Kind == "actor"))
                {
                    if (GameCatalog.Get(copy.Game) is not { } source || !int.TryParse(copy.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
                    var family = Catalogues.GetOrAdd((engineData, source.Engine), key => new Lazy<ThingCatalog>(() => ThingCatalog.Load(ResourceContainer.Open(key.Engine), key.Family))).Value;
                    if (family.FindClass(copy.Original) is { } t) carried.Add(t with { Number = number });
                }

                if (carried.Count > 0) things = things.With(carried);
            }
            catch (Exception ex) when (ex is IOException or Core.CorruptDataException)
            {
                Log.Warn($"The objects carried into {projectFolder} could not be read: {ex.Message}");
            }
        }

        return new GameResources(found.Game, files, stack, new TextureLibrary(stack), new SpriteIndex(stack), things, FindMusic(stack));
    }

    private static RgbaImage Mirror(RgbaImage image)
    {
        var m = new RgbaImage(image.Width, image.Height) { OffsetX = image.Width - image.OffsetX, OffsetY = image.OffsetY };
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (r, g, b, a) = image.Get(x, y);
                m.Set(image.Width - 1 - x, y, r, g, b, a);
            }
        }

        return m;
    }
}
