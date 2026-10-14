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

    /// <summary>The pictures that are switches in this game, both states (GZDoom's list, then the files' ANIMDEFS and SWITCHES); read on first use.</summary>
    public IReadOnlySet<string> SwitchPictures => _switches?.Value ?? new HashSet<string>();

    private Lazy<IReadOnlySet<string>>? _switches;

    /// <summary>The music the game (and the project) has, by name, A to Z: every lump or file holding a MUS or MIDI score, or an Ogg, FLAC or MP3 stream.</summary>
    public IReadOnlyList<string> Music { get; private set; }

    /// <summary>Offers music the project brought in (a file of the user's own) among the game's own (any thread).</summary>
    public void AddMusic(string name)
    {
        lock (_carried)
        {
            if (!Music.Contains(name, StringComparer.OrdinalIgnoreCase)) Music = [.. Music.Append(name).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>The sky pictures the game has (every wall texture named SKY…), A to Z.</summary>
    public IReadOnlyList<string> Skies { get; private set; }

    /// <summary>Offers a sky the project made (a skybox from a 360° picture) among the game's own (any thread).</summary>
    public void AddSky(string name)
    {
        lock (_carried)
        {
            if (!Skies.Contains(name, StringComparer.OrdinalIgnoreCase)) Skies = [.. Skies.Append(name).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

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
            if (head.SequenceEqual("MUS\x1a"u8) || head.SequenceEqual("MThd"u8) || head.SequenceEqual("OggS"u8) || head.SequenceEqual("fLaC"u8) || head[..3].SequenceEqual("ID3"u8) || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && entry.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                || (entry.Namespace == ResourceNamespace.Music && Core.Audio.AudioFormats.Detect(data) == Core.Audio.AudioKind.Module))
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

    /// <summary>Duke Nukem 3D's art and enemies when this is a Build-engine game (D48), else null.</summary>
    public Core.Build.BuildArtContainer? Build { get; private init; }

    private readonly HashSet<ResourceContainer> _projectFiles = new(ReferenceEqualityComparer.Instance);

    /// <summary>The project's own folder among the files, when the game was opened for a project: what was copied in lives there.</summary>
    private ResourceContainer? ProjectFiles
    {
        init
        {
            if (value is not null) _projectFiles.Add(value);
        }
    }

    /// <summary>Whether an entry is the project's own (a picture or sound copied in), not the game's.</summary>
    public bool IsProjectFile(ResourceEntry entry)
    {
        lock (_carried) return _projectFiles.Contains(entry.Container);
    }

    /// <summary>A lump of the game's files by name and namespace, the last file that has it winning; null when none has.</summary>
    public ResourceEntry? Find(string name, ResourceNamespace ns) => _stack.Find(name, ns);

    private readonly object _carried = new();

    /// <summary>Counts changes to the project's own object pictures (a model turned and baked again); caches of sprite pictures compare it.</summary>
    public int PicturesVersion { get; private set; }

    /// <summary>The project's object pictures were written again under the same names: forget the ones read (any thread).</summary>
    public void PicturesChanged()
    {
        lock (_carried)
        {
            _sprites.Clear();
            PicturesVersion++;
        }
    }

    /// <summary>
    /// Takes in objects carried into the project from another game (D55): their sprites, now in the project's folder, join
    /// the sprite index, and the objects join the catalogue under their numbers here. Any thread.
    /// </summary>
    public void Carry(IEnumerable<ThingType> things, string projectFolder)
    {
        lock (_carried)
        {
            var files = ResourceContainer.Open(projectFolder);
            _projectFiles.Add(files);
            _stack.Add(files);
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

        // Quake (Phase 8b.6): its textures as pictures to borrow; GZDoom does not play it.
        if (found.Game.Engine == "quake")
        {
            var quake = new ResourceStack([Core.Quake.QuakeTextureContainer.Open(found.Path)]);
            return new GameResources(found.Game, [found.Path], quake, new TextureLibrary(quake), new SpriteIndex(quake), ThingCatalog.Empty, []);
        }

        // Duke Nukem 3D (D48): its art as pictures to borrow; GZDoom does not play it, so it has no objects of its own here yet.
        if (found.Game.Engine == "build")
        {
            var art = Core.Build.BuildArtContainer.Open(found.Path);
            var build = new ResourceStack([art]);
            return new GameResources(found.Game, [found.Path], build, new TextureLibrary(build), new SpriteIndex(build), ThingCatalog.Empty, []) { Build = art };
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
        ResourceContainer? projectFiles = null;
        if (projectFolder is not null && Directory.Exists(projectFolder)) stack.Add(projectFiles = ResourceContainer.Open(projectFolder));

        // Skies the project made from 360° pictures (Phase 11), offered with the game's own.
        var skyboxes = new List<string>();
        var things = engineData is null
            ? ThingCatalog.Empty
            : Catalogues.GetOrAdd((engineData, found.Game.Engine), key => new Lazy<ThingCatalog>(() => ThingCatalog.Load(ResourceContainer.Open(key.Engine), key.Family))).Value;
        // Objects carried in from the user's other games (D55), by the numbers the project gave them.
        if (projectFolder is not null && File.Exists(Path.Combine(projectFolder, Core.Projects.Project.SidecarName)))
        {
            try
            {
                var carried = new List<ThingType>();
                var info = Core.Projects.Project.Open(projectFolder).Info;
                skyboxes.AddRange(Editing.Skies.Recorded(info));

                // Duke Nukem 3D's enemies (D55): classes the project itself declares, each with a Doom monster's behaviour.
                foreach (var duke in info.Copied.Where(c => c.Kind == "dukeactor"))
                {
                    var parts = duke.Name.Split('|');
                    var number = info.Copied.FirstOrDefault(c => c.Kind == "actor" && c.Original == duke.Original)?.Name;
                    if (parts.Length < 4 || number is null || things.FindClass(parts[0]) is not { } parent) continue;
                    var scale = double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
                    carried.Add(parent with { Number = int.Parse(number, CultureInfo.InvariantCulture), ClassName = duke.Original, Name = parts[3], Sprite = parts[1], Frame = 'A', ScaleX = scale, ScaleY = scale });
                }

                // Props brought in as 3D models (Phase 11): classes the project declares, shown by their baked sprites.
                carried.AddRange(Editing.ModelProps.Recorded(info));

                // Objects downloaded from the free sources (D61): classes the project's own definition lumps declare.
                carried.AddRange(Editing.LibraryObjects.Recorded(info.Copied));

                // Sounds of the user's own placed in the map (Phase 7): one class plays them all.
                carried.AddRange(Editing.SoundFiles.Recorded(info));

                // Other games' objects take their definitions from the engine's data.
                foreach (var copy in info.Copied.Where(c => c.Kind == "actor" && c.Game != "duke3d" && c.Game != "model"))
                {
                    if (engineData is null || GameCatalog.Get(copy.Game) is not { } source ||!int.TryParse(copy.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
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

        var made = new GameResources(found.Game, files, stack, new TextureLibrary(stack), new SpriteIndex(stack), things, FindMusic(stack)) { ProjectFiles = projectFiles };
        foreach (var sky in skyboxes) made.AddSky(sky);
        made._switches = new(() => Core.Graphics.SwitchPictures.Read(engineData, found.Game.Engine, stack));
        return made;
    }

    /// <summary>
    /// A texture pack fetched from a free source (D61), opened over a game of the family it was drawn for (the project's own
    /// game when it is of that family, so the colours are the ones the map shows) for its pictures: its files are
    /// <see cref="Pack"/>, its id and title the pack's, and what is copied from it is recorded as the pack's. Null when no game
    /// of that family was found. Reads files: call it on a worker.
    /// </summary>
    public static GameResources? LoadPack(Core.Library.LibraryItem item, IReadOnlyList<string> packFiles, string? ownGameId, IReadOnlyList<FoundGame> games)
    {
        var found = games.FirstOrDefault(g => g.Game.Id == ownGameId && g.Game.Engine == item.Family)
            ?? games.FirstOrDefault(g => g.Game.Engine == item.Family && g.Game.Supported && g.Game.IsBaseGame);
        if (found is null) return null;
        var files = new List<string>();
        if (found.Game.AddOnFor is { } baseId && games.FirstOrDefault(g => g.Game.Id == baseId) is { } baseGame) files.Add(baseGame.Path);
        files.Add(found.Path);
        var under = files.Count;
        files.AddRange(packFiles.Where(File.Exists));
        var containers = files.Select(ResourceContainer.Open).ToList();
        var stack = new ResourceStack(containers);
        return new GameResources(found.Game with { Id = item.Id, Title = item.Title }, files, stack, new TextureLibrary(stack), new SpriteIndex(stack), ThingCatalog.Empty, [])
        {
            Pack = containers.Skip(under).ToHashSet(),
            Library = item,
        };
    }

    /// <summary>For a texture pack (<see cref="LoadPack"/>), the pack's own files: its pictures are the ones whose source is one of these.</summary>
    public IReadOnlySet<ResourceContainer>? Pack { get; private init; }

    /// <summary>The free source's entry these resources are (a texture pack), for its credits; null for a game.</summary>
    public Core.Library.LibraryItem? Library { get; private init; }

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
