using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Games;

/// <summary>What kind of object a type is, for the Objects shelf: from the engine's class tree.</summary>
public enum ThingCategory
{
    Start,
    Monster,
    Weapon,
    Ammo,
    Health,
    Armor,
    Key,

    /// <summary>Other things to pick up: power-ups, backpacks, maps.</summary>
    Powerup,

    /// <summary>Things that show but do nothing to the player: columns, lamps, bodies, barrels.</summary>
    Decoration,

    /// <summary>Invisible things that mark a spot: teleport destinations, sound and map spots.</summary>
    Marker,
}

/// <summary>
/// An object a map can place, as the engine defines it: its number in maps, its name, the sprite and
/// frame it shows when it appears, its size, its kind, the lump of the sound it is known by (a monster's sight, a pickup's), and for the Balance meter (D29) a
/// monster's health, how much a pickup gives, the ammunition it is (the class under Ammo: Clip for a box of bullets) and
/// what a weapon gives with it.
/// </summary>
public sealed record ThingType(int Number, string ClassName, string Name, string? Sprite, char Frame, double Radius, double Height, double ScaleX, double ScaleY, bool OnCeiling, ThingCategory Category = ThingCategory.Decoration, string? Sound = null, int? Health = null, int Amount = 0, string? AmmoType = null, int AmmoGive = 0);

/// <summary>What an object needs carried into another game (see <see cref="ThingCatalog.Needs"/>).</summary>
public sealed record ActorNeeds(string ClassName, IReadOnlyList<string> Classes, IReadOnlyList<string> OwnSprites, IReadOnlyList<string> Sprites, IReadOnlyList<string> Sounds);

/// <summary>
/// The objects of one game family, read from the engine's own data (gzdoom.pk3): the MAPINFO
/// <c>DoomEdNums</c> give each number its class, the ZScript actor classes give size, tag and the
/// first frame of the Spawn state (following parent classes), and the language tables turn a tag
/// such as <c>$FN_IMP</c> into "Imp". Read once per family on a worker.
/// </summary>
public sealed partial class ThingCatalog
{
    private readonly Dictionary<int, ThingType> _byNumber;
    private readonly Dictionary<string, ActorClass> _classes;
    private readonly IReadOnlyList<string> _soundTexts;

    /// <summary>The language tables' English strings by identifier, for tags such as <c>$FN_IMP</c>.</summary>
    private readonly Dictionary<string, string> _strings;

    private ThingCatalog(Dictionary<int, ThingType> byNumber, Dictionary<string, ActorClass>? classes = null, WadSmith.Core.Audio.SoundInfo? sounds = null, IReadOnlyList<string>? soundTexts = null, Dictionary<string, string>? strings = null)
    {
        _byNumber = byNumber;
        _classes = classes ?? new(StringComparer.OrdinalIgnoreCase);
        Sounds = sounds ?? WadSmith.Core.Audio.SoundInfo.Empty;
        _soundTexts = soundTexts ?? [];
        _strings = strings ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public static ThingCatalog Empty { get; } = new([]);

    /// <summary>The family's sound table, as the engine reads it for these games.</summary>
    public WadSmith.Core.Audio.SoundInfo Sounds { get; }

    /// <summary>The object of a class, by its class name; null when the catalogue has no number for it.</summary>
    public ThingType? FindClass(string className) => _byNumber.Values.FirstOrDefault(t => string.Equals(t.ClassName, className, StringComparison.OrdinalIgnoreCase));

    /// <summary>This catalogue with <paramref name="extra"/> objects added (objects carried in from another game); the classes stay shared.</summary>
    public ThingCatalog With(IEnumerable<ThingType> extra)
    {
        var numbers = new Dictionary<int, ThingType>(_byNumber);
        foreach (var t in extra) numbers[t.Number] = t;
        return new ThingCatalog(numbers, _classes, Sounds, _soundTexts, _strings);
    }

    /// <summary>
    /// This catalogue with the game's own sound tables read after the engine's, as the engine reads them: the SNDINFO lumps of
    /// the game's files, in load order. Hexen's file names most of its monsters' sounds itself (BishopSight is SYAB2D there,
    /// in no table of the engine's). The objects' sounds are looked up again in the whole table. This catalogue when there are none.
    /// </summary>
    public ThingCatalog WithGameSounds(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return this;
        var soundTexts = _soundTexts.Concat(texts).ToList();
        var sounds = WadSmith.Core.Audio.SoundInfo.Parse(soundTexts);
        var numbers = new Dictionary<int, ThingType>(_byNumber.Count);
        foreach (var (number, t) in _byNumber)
        {
            numbers[number] = _classes.ContainsKey(t.ClassName) && SoundOf(_classes, t.ClassName, t.Category) is { } name ? t with { Sound = sounds.LumpFor(name) ?? t.Sound } : t;
        }

        return new ThingCatalog(numbers, _classes, sounds, soundTexts, _strings);
    }

    /// <summary>
    /// This catalogue with the objects <paramref name="files"/> define, read in load order (D65 B2: a friend's PK3 of monsters, a
    /// game's own actors): the actor classes of their ZScript and DECORATE (the ZSCRIPT and DECORATE lumps, following #include),
    /// the numbers their MAPINFO's DoomEdNums and DECORATE's actor lines give, and the names their LANGUAGE tables give. A class
    /// that replaces another takes that one's numbers, as the engine spawns it there. The Doom Builder keys in a class's comments
    /// name it (//$Title), file it on the shelf (//$Category) and pick its picture (//$Sprite). Their sound tables are read by
    /// <see cref="WithGameSounds"/>, after this. This catalogue when they define nothing.
    /// </summary>
    public ThingCatalog WithResources(IEnumerable<ResourceContainer> files)
    {
        var classes = new Dictionary<string, ActorClass>(_classes, StringComparer.OrdinalIgnoreCase);
        var strings = new Dictionary<string, string>(_strings, StringComparer.OrdinalIgnoreCase);
        var numbered = new Dictionary<int, string>();
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(ActorClass c)
        {
            // The engine's own classes stay its own; a later file's class of the same name takes an earlier one's place.
            if (_classes.ContainsKey(c.Name)) return;
            classes[c.Name] = c;
            own.Add(c.Name);
        }

        foreach (var file in files)
        {
            var seen = new HashSet<ResourceEntry>(ReferenceEqualityComparer.Instance);
            List<ResourceEntry> Lumps(string name) => [.. file.Entries.Where(e => !e.IsMarker && e.Namespace == ResourceNamespace.Global && LumpName.Comparer.Equals(e.Name, name))];

            // A lump and the ones it includes, each once (an include names a path in the file, a lump's name in a WAD).
            IEnumerable<string> Texts(IEnumerable<ResourceEntry> roots, Regex include)
            {
                var queue = new Queue<ResourceEntry>(roots);
                while (queue.Count > 0)
                {
                    var entry = queue.Dequeue();
                    if (!seen.Add(entry)) continue;
                    var text = Encoding.UTF8.GetString(entry.Read());
                    yield return text;
                    foreach (Match m in include.Matches(StripComments(text)))
                    {
                        var path = m.Groups[1].Value.Replace('\\', '/');
                        if ((file.FindPath(path) ?? file.Find(System.IO.Path.GetFileNameWithoutExtension(path))) is { } next) queue.Enqueue(next);
                    }
                }
            }

            foreach (var text in Texts(Lumps("ZSCRIPT"), HashInclude()))
            {
                foreach (var c in ParseClasses(BlankComments(text), text)) Add(c);
            }

            foreach (var text in Texts(Lumps("DECORATE"), HashInclude()))
            {
                foreach (var (c, number) in ParseDecorate(BlankComments(text), text))
                {
                    Add(c);
                    if (number is { } n && classes.ContainsKey(c.Name)) numbered[n] = c.Name;
                }
            }

            // GZDoom reads a file's ZMAPINFO in place of its MAPINFO.
            var mapInfo = Lumps("ZMAPINFO") is { Count: > 0 } z ? z : Lumps("MAPINFO");
            foreach (var text in Texts(mapInfo, IncludeLine()))
            {
                foreach (Match block in DoomEdNumsBlock().Matches(StripComments(text)))
                {
                    foreach (Match entry in EdNumEntry().Matches(block.Groups[1].Value)) numbered[int.Parse(entry.Groups[1].Value, CultureInfo.InvariantCulture)] = entry.Groups[2].Value;
                }
            }

            foreach (var text in Texts(Lumps("LANGUAGE"), HashInclude()))
            {
                foreach (Match m in DefEntry().Matches(StripComments(text))) strings[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        if (own.Count == 0 && numbered.Count == 0) return this;

        // Each number to the class the engine spawns there: the files' numbers over the engine's, then what replaces that class.
        var replacement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in own)
        {
            if (classes[name].Replaces is { } replaced) replacement[replaced] = name;
        }

        string Spawned(string name)
        {
            for (var i = 0; i < 8 && replacement.TryGetValue(name, out var next) && !next.Equals(name, StringComparison.OrdinalIgnoreCase); i++) name = next;
            return name;
        }

        var numbers = new Dictionary<int, ThingType>(_byNumber);
        foreach (var (number, name) in _byNumber.Where(kv => kv.Value.Category != ThingCategory.Start).Select(kv => (kv.Key, kv.Value.ClassName)).Concat(numbered.Select(kv => (kv.Key, kv.Value))))
        {
            var spawned = Spawned(name);
            if ((!numbered.ContainsKey(number) && !own.Contains(spawned)) || !classes.ContainsKey(spawned)) continue;
            numbers[number] = Keyed(MakeType(number, spawned, classes, strings, Sounds), classes[spawned].Keys);
        }

        return new ThingCatalog(numbers, classes, Sounds, _soundTexts, strings);
    }

    /// <summary>An object as the Doom Builder keys in its class's comments say: its title, its shelf (a kind the words name), its picture.</summary>
    private static ThingType Keyed(ThingType t, Dictionary<string, string> keys)
    {
        if (keys.TryGetValue("Title", out var title) && title.Length > 0) t = t with { Name = title };
        if (keys.TryGetValue("Category", out var category) && CategoryNamed(category) is { } kind) t = t with { Category = kind };
        if (keys.TryGetValue("Sprite", out var sprite) && sprite.Length >= 5 && !sprite.Contains(':', StringComparison.Ordinal)) t = t with { Sprite = sprite[..4].ToUpperInvariant(), Frame = char.ToUpperInvariant(sprite[4]) };
        return t;
    }

    /// <summary>The kind of object a Doom Builder category names ("Monsters", "Items/Keys", "Light sources"); null for one it does not.</summary>
    private static ThingCategory? CategoryNamed(string category)
    {
        var words = category.ToLowerInvariant();
        if (words.Contains("monster", StringComparison.Ordinal) || words.Contains("enem", StringComparison.Ordinal)) return ThingCategory.Monster;
        if (words.Contains("weapon", StringComparison.Ordinal)) return ThingCategory.Weapon;
        if (words.Contains("ammo", StringComparison.Ordinal)) return ThingCategory.Ammo;
        if (words.Contains("health", StringComparison.Ordinal)) return ThingCategory.Health;
        if (words.Contains("armor", StringComparison.Ordinal) || words.Contains("armour", StringComparison.Ordinal)) return ThingCategory.Armor;
        if (words.Contains("key", StringComparison.Ordinal)) return ThingCategory.Key;
        if (words.Contains("power", StringComparison.Ordinal) || words.Contains("artifact", StringComparison.Ordinal) || words.Contains("item", StringComparison.Ordinal) || words.Contains("pickup", StringComparison.Ordinal)) return ThingCategory.Powerup;
        if (words.Contains("decoration", StringComparison.Ordinal) || words.Contains("light", StringComparison.Ordinal) || words.Contains("obstacle", StringComparison.Ordinal) || words.Contains("prop", StringComparison.Ordinal)) return ThingCategory.Decoration;
        if (words.Contains("marker", StringComparison.Ordinal) || words.Contains("spot", StringComparison.Ordinal)) return ThingCategory.Marker;
        return null;
    }

    /// <summary>
    /// What an object of <paramref name="className"/> needs carried into another game (D55): every class it is made of or
    /// names (what it fires, drops or spawns, written in quotes in its definition), with their ancestors; the sprite names
    /// their states show; and the sound names their definitions use that this family's table knows. <c>Own</c> are the
    /// sprites the class itself and its ancestors show (the ones whose names must not clash).
    /// </summary>
    public ActorNeeds Needs(string className)
    {
        var classes = new List<string>();
        var queue = new Queue<string>([className]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            for (var c = _classes.GetValueOrDefault(name); c is not null; c = c.Parent is null ? null : _classes.GetValueOrDefault(c.Parent))
            {
                // The engine's own base classes are in every game, and name the engine's whole common stock: the walk stops there.
                if (EngineBases.Contains(c.Name) || !seen.Add(c.Name)) break;
                classes.Add(c.Name);
                foreach (Match m in QuotedWord().Matches(c.Body))
                {
                    var word = m.Groups[1].Value;
                    if (_classes.ContainsKey(word) && !seen.Contains(word)) queue.Enqueue(word);
                }
            }
        }

        IEnumerable<string> SpritesOf(IEnumerable<string> names) => names.Select(n => _classes[n]).SelectMany(c => c.States).Select(StateSprite).OfType<string>();
        var own = new List<string>();
        for (var c = _classes.GetValueOrDefault(className); c is not null && !EngineBases.Contains(c.Name); c = c.Parent is null ? null : _classes.GetValueOrDefault(c.Parent)) own.Add(c.Name);
        var sounds = classes.Select(n => _classes[n].Body).SelectMany(b => QuotedWord().Matches(b).Select(m => m.Groups[1].Value)).Where(w => Sounds.LumpFor(w) is not null);
        return new ActorNeeds(className, classes, [.. SpritesOf(own).Distinct(StringComparer.Ordinal)], [.. SpritesOf(classes).Distinct(StringComparer.Ordinal)], [.. sounds.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// A class's states as the engine runs them (D55): its own state blocks, then each block of its ancestors under a label it
    /// does not define itself. A block is its labels ("Death:", "XDeath:") and the lines that follow them, without the ';'.
    /// </summary>
    public IReadOnlyList<(IReadOnlyList<string> Labels, IReadOnlyList<string> Lines)> StateBlocks(string className)
    {
        var blocks = new List<(IReadOnlyList<string> Labels, IReadOnlyList<string> Lines)>();
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The engine's base classes' states (Null, the freeze and crush deaths) stay theirs: the walk stops there.
        for (var c = _classes.GetValueOrDefault(className); c is not null && !EngineBases.Contains(c.Name); c = c.Parent is null ? null : _classes.GetValueOrDefault(c.Parent))
        {
            var own = new List<(List<string> Labels, List<string> Lines)>();
            foreach (var entry in c.States)
            {
                if (entry.EndsWith(':'))
                {
                    if (own.Count == 0 || own[^1].Lines.Count > 0) own.Add(([], []));
                    own[^1].Labels.Add(entry.TrimEnd(':'));
                }
                else if (own.Count > 0)
                {
                    own[^1].Lines.Add(entry);
                }
            }

            foreach (var (labels, lines) in own)
            {
                var fresh = labels.Where(l => !defined.Contains(l)).ToList();
                if (fresh.Count == 0) continue;
                blocks.Add((fresh, lines));
            }

            foreach (var (labels, _) in own) defined.UnionWith(labels);
        }

        return blocks;
    }

    /// <summary>
    /// The weapon slot <paramref name="weaponClass"/> has in its own game: the number of the first player class's
    /// <c>Player.WeaponSlot</c> that names it (Heretic's Blaster is 4, Hexen's Fighter's Axe 2); null when none does.
    /// </summary>
    public int? SlotOf(string weaponClass)
    {
        foreach (var c in _classes.Values)
        {
            foreach (Match m in WeaponSlot().Matches(c.Body))
            {
                if (QuotedWord().Matches(m.Groups[2].Value).Any(q => string.Equals(q.Groups[1].Value, weaponClass, StringComparison.OrdinalIgnoreCase)))
                {
                    return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
        }

        return null;
    }

    /// <summary>The engine's base classes, the same in every game: an object's needs stop at them.</summary>
    private static readonly HashSet<string> EngineBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "Object", "Thinker", "Actor", "StateProvider", "Inventory", "CustomInventory", "FakeInventory", "Weapon", "WeaponGiver", "Ammo", "BackpackItem",
        "Health", "HealthPickup", "Armor", "BasicArmor", "BasicArmorPickup", "BasicArmorBonus", "HexenArmor", "Powerup", "PowerupGiver", "Key",
        "PuzzleItem", "MapRevealer", "ScoreItem", "SwitchableDecoration", "SwitchingDecoration", "RandomSpawner", "SpecialSpot", "BossSpot",
    };

    /// <summary>The sprite a state line shows ("SRTC ABC 4 Bright" → SRTC); null for a jump, a stop or a placeholder.</summary>
    private static string? StateSprite(string line)
    {
        if (StateLine().Match(line) is not { Success: true } m) return null;
        var sprite = m.Groups[1].Value;
        return sprite is "TNT1" or "####" or "----" or "GOTO" or "STOP" or "LOOP" or "WAIT" or "FAIL" ? null : sprite;
    }

    public IReadOnlyCollection<ThingType> All => _byNumber.Values;

    public ThingType? Find(int number) => _byNumber.GetValueOrDefault(number);

    /// <summary>The MAPINFO file each engine family starts from (GZDoom's iwadinfo.txt picks the same ones).</summary>
    public static string MapInfoFor(string family) => family switch
    {
        "heretic" => "mapinfo/heretic.txt",
        "hexen" => "mapinfo/hexen.txt",
        "strife" => "mapinfo/strife.txt",
        "chex" => "mapinfo/chex.txt",
        _ => "mapinfo/doomcommon.txt",
    };

    /// <summary>The engine's sound tables for each family, in load order: the common one, then the game's own.</summary>
    public static IReadOnlyList<string> SoundInfoFor(string family) =>
        ["sndinfo.txt", $"filter/game-{family switch { "heretic" => "heretic", "hexen" => "hexen", "strife" => "strife", _ => "doomchex" }}/sndinfo.txt"];

    /// <summary>The class the engine uses for the player in each family.</summary>
    public static string PlayerClassFor(string family) => family switch
    {
        "heretic" => "HereticPlayer",
        "hexen" => "FighterPlayer",
        "strife" => "StrifePlayer",
        "chex" => "ChexPlayer",
        _ => "DoomPlayer",
    };

    public static ThingCatalog Load(ResourceContainer engine, string family)
    {
        string? Text(string path) => engine.FindPath(path) is { } e ? Encoding.UTF8.GetString(e.Read()) : null;

        // Numbers to class names, following includes; later entries replace earlier ones.
        var numbers = new Dictionary<int, string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ReadMapInfo(string path)
        {
            if (!seen.Add(path) || Text(path) is not { } text) return;
            text = StripComments(text);
            foreach (Match m in IncludeLine().Matches(text)) ReadMapInfo(m.Groups[1].Value);
            foreach (Match block in DoomEdNumsBlock().Matches(text))
            {
                foreach (Match entry in EdNumEntry().Matches(block.Groups[1].Value))
                {
                    numbers[int.Parse(entry.Groups[1].Value, CultureInfo.InvariantCulture)] = entry.Groups[2].Value;
                }
            }
        }

        ReadMapInfo(MapInfoFor(family));

        // Player and deathmatch starts are built into the engine rather than listed in MAPINFO.
        var player = PlayerClassFor(family);
        var starts = new Dictionary<int, string>
        {
            [1] = "Player 1 start",
            [2] = "Player 2 start",
            [3] = "Player 3 start",
            [4] = "Player 4 start",
            [11] = "Deathmatch start",
        };
        if (family == "hexen")
        {
            for (var i = 0; i < 4; i++) starts[9100 + i] = $"Player {i + 5} start";
        }

        foreach (var number in starts.Keys) numbers[number] = player;

        var classes = new Dictionary<string, ActorClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in engine.Entries)
        {
            if (!e.FullPath.StartsWith("zscript/", StringComparison.OrdinalIgnoreCase) || !e.FullPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var c in ParseClasses(StripComments(Encoding.UTF8.GetString(e.Read())))) classes.TryAdd(c.Name, c);
        }

        var strings = LanguageStrings(engine);
        var soundTexts = SoundInfoFor(family).Select(Text).OfType<string>().ToList();
        var sounds = WadSmith.Core.Audio.SoundInfo.Parse(soundTexts);
        var types = new Dictionary<int, ThingType>();
        foreach (var (number, className) in numbers)
        {
            if (classes.ContainsKey(className)) types[number] = MakeType(number, className, classes, strings, sounds, starts.GetValueOrDefault(number));
        }

        return new ThingCatalog(types, classes, sounds, soundTexts, strings);
    }

    /// <summary>
    /// The object a number places when it names <paramref name="className"/>: size, picture, kind, sound and Balance figures
    /// read from the class and its parents, its name from its tag through the language tables (<paramref name="start"/>, for a
    /// player start, names it instead).
    /// </summary>
    private static ThingType MakeType(int number, string className, Dictionary<string, ActorClass> classes, Dictionary<string, string> strings, WadSmith.Core.Audio.SoundInfo sounds, string? start = null)
    {
        var radius = Inherited(classes, className, c => c.Radius) ?? 20;
        var height = Inherited(classes, className, c => c.Height) ?? 16;
        var scale = Inherited(classes, className, c => c.Scale) ?? 1;
        var scaleX = Inherited(classes, className, c => c.ScaleX) ?? scale;
        var scaleY = Inherited(classes, className, c => c.ScaleY) ?? scale;
        var ceiling = Inherited(classes, className, c => c.SpawnCeiling) ?? false;
        var tag = Inherited(classes, className, c => c.Tag);
        var (sprite, frame) = SpawnFrame(classes, className);
        var declared = classes[className].Name;
        var name = start ?? NameOf(tag, declared, NamedAncestor(classes, className), strings);
        var category = start is not null ? ThingCategory.Start : CategoryOf(classes, className, sprite);
        var sound = SoundOf(classes, className, category);
        var ammoType = category == ThingCategory.Ammo ? AmmoClass(classes, className) : category == ThingCategory.Weapon ? Inherited(classes, className, c => c.AmmoTypeName) : null;
        return new ThingType(number, declared, name, sprite, frame, radius, height, scaleX, scaleY, ceiling, category, sound is null ? null : sounds.LumpFor(sound), Inherited(classes, className, c => c.Health), Inherited(classes, className, c => c.Amount) ?? 0, ammoType, Inherited(classes, className, c => c.AmmoGive) ?? 0);
    }

    /// <summary>The sound an object of a class stands for: a monster's sight (else active) sound, a pickup's, a decoration's active one.</summary>
    private static string? SoundOf(Dictionary<string, ActorClass> classes, string className, ThingCategory category) => category switch
    {
        ThingCategory.Monster => Inherited(classes, className, c => c.SeeSound) ?? Inherited(classes, className, c => c.ActiveSound),
        ThingCategory.Weapon or ThingCategory.Ammo or ThingCategory.Health or ThingCategory.Armor or ThingCategory.Key or ThingCategory.Powerup => Inherited(classes, className, c => c.PickupSound),
        ThingCategory.Decoration => Inherited(classes, className, c => c.ActiveSound),
        _ => null,
    };

    /// <summary>The ammunition a pickup is: the class in its chain whose parent is Ammo (a box of bullets is Clip).</summary>
    private static string? AmmoClass(Dictionary<string, ActorClass> classes, string name)
    {
        for (var c = classes.GetValueOrDefault(name); c is not null; c = c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
        {
            if (string.Equals(c.Parent, "Ammo", StringComparison.OrdinalIgnoreCase)) return c.Name;
        }

        return null;
    }

    /// <summary>The kind of object a class is: the nearest of its ancestors that says, else whether it shows a sprite.</summary>
    private static ThingCategory CategoryOf(Dictionary<string, ActorClass> classes, string name, string? sprite)
    {
        if (Inherited(classes, name, c => c.Monster) == true) return ThingCategory.Monster;
        for (var c = classes.GetValueOrDefault(name); c is not null; c = c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
        {
            switch (c.Name.ToUpperInvariant())
            {
                case "KEY": return ThingCategory.Key;
                case "WEAPON": return ThingCategory.Weapon;
                case "AMMO": return ThingCategory.Ammo;
                case "HEALTH" or "HEALTHPICKUP": return ThingCategory.Health;
                case "ARMOR" or "BASICARMORPICKUP" or "BASICARMORBONUS" or "HEXENARMOR": return ThingCategory.Armor;
                case "INVENTORY": return ThingCategory.Powerup;
            }
        }

        return sprite is null ? ThingCategory.Marker : ThingCategory.Decoration;
    }

    private sealed class ActorClass
    {
        /// <summary>Set by the Monster combo flag or +ISMONSTER.</summary>
        public bool? Monster { get; set; }

        /// <summary>Skip_Super: the class's defaults start from scratch, not from its parent's (its states still come from the parent).</summary>
        public bool SkipSuper { get; set; }

        public required string Name { get; init; }

        public string? Parent { get; init; }

        /// <summary>The class it replaces (ZScript's and DECORATE's <c>replaces</c>): the engine spawns it wherever that one would be.</summary>
        public string? Replaces { get; init; }

        /// <summary>The Doom Builder keys in its definition's comments (//$Title, //$Category, //$Sprite), by key.</summary>
        public Dictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The class's whole definition, for what it names (D55).</summary>
        public string Body { get; init; } = "";

        public double? Radius { get; set; }

        public double? Height { get; set; }

        public double? Scale { get; set; }

        public double? ScaleX { get; set; }

        public double? ScaleY { get; set; }

        public bool? SpawnCeiling { get; set; }

        public string? Tag { get; set; }

        public string? SeeSound { get; set; }

        public string? ActiveSound { get; set; }

        public string? PickupSound { get; set; }

        public int? Health { get; set; }

        public int? Amount { get; set; }

        public int? AmmoGive { get; set; }

        public string? AmmoTypeName { get; set; }

        /// <summary>State lines in order, with labels as "Label:" entries.</summary>
        public List<string> States { get; } = [];
    }

    /// <summary>A default property from the class or the nearest parent that sets it; a class with Skip_Super takes nothing from its parents.</summary>
    private static T? Inherited<T>(Dictionary<string, ActorClass> classes, string name, Func<ActorClass, T?> get)
        where T : struct
    {
        for (var c = classes.GetValueOrDefault(name); c is not null; c = c.SkipSuper || c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
        {
            if (get(c) is { } v) return v;
        }

        return null;
    }

    private static string? Inherited(Dictionary<string, ActorClass> classes, string name, Func<ActorClass, string?> get)
    {
        for (var c = classes.GetValueOrDefault(name); c is not null; c = c.SkipSuper || c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
        {
            if (get(c) is { } v) return v;
        }

        return null;
    }

    /// <summary>The sprite and frame of the first state under Spawn, following Goto and parent classes; null for an invisible object.</summary>
    private static (string? Sprite, char Frame) SpawnFrame(Dictionary<string, ActorClass> classes, string name)
    {
        var label = "Spawn";
        var skip = 0;
        for (var hops = 0; hops < 16; hops++)
        {
            // The nearest class (itself or a parent) that has the label.
            ActorClass? owner = null;
            int at = -1;
            for (var c = classes.GetValueOrDefault(name); c is not null && owner is null; c = c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
            {
                at = c.States.FindIndex(s => string.Equals(s, label + ":", StringComparison.OrdinalIgnoreCase));
                if (at >= 0) owner = c;
            }

            if (owner is null) return (null, 'A');
            for (var i = at + 1; i < owner.States.Count; i++)
            {
                var line = owner.States[i];
                if (line.EndsWith(':')) continue;
                var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

                // "Goto Death+4": the fifth state after the label. A state line with several frames ("TROO IJKLM") is that many states.
                if (skip > 0 && !words[0].Equals("Goto", StringComparison.OrdinalIgnoreCase) && words.Length >= 2)
                {
                    var count = words[1].Trim('"').Length;
                    if (skip >= count)
                    {
                        skip -= count;
                        continue;
                    }

                    var frames0 = words[1].Trim('"').ToUpperInvariant();
                    var sprite0 = words[0].Trim('"').ToUpperInvariant();
                    var picked = frames0[skip];
                    skip = 0;
                    if (sprite0.Length != 4 || sprite0 is "TNT1" or "####" or "----" || picked is < 'A' or > ']') return (null, 'A');
                    return (sprite0, picked);
                }

                if (words[0].Equals("Goto", StringComparison.OrdinalIgnoreCase) && words.Length > 1)
                {
                    var target = words[1];
                    if (target.StartsWith("Super::", StringComparison.OrdinalIgnoreCase))
                    {
                        name = owner.Parent ?? name;
                        target = target[7..];
                    }
                    else
                    {
                        name = owner.Name;
                    }

                    var parts = target.Split('+');
                    label = parts[0];
                    skip = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
                    break;
                }

                if (words[0] is "Stop" or "stop" or "Loop" or "loop" or "Wait" or "wait" or "Fail" or "fail") return (null, 'A');
                if (words.Length < 2) return (null, 'A');
                var sprite = words[0].Trim('"').ToUpperInvariant();
                var frames = words[1].Trim('"').ToUpperInvariant();
                if (sprite.Length != 4 || sprite is "TNT1" or "####" or "----" || frames.Length == 0 || frames[0] is < 'A' or > ']') return (null, 'A');
                return (sprite, frames[0]);
            }
        }

        return (null, 'A');
    }

    /// <summary>The nearest class in the chain (itself first) that declares a tag, whatever Skip_Super says: the name it is a variant of.</summary>
    private static (string Name, string Tag)? NamedAncestor(Dictionary<string, ActorClass> classes, string name)
    {
        for (var c = classes.GetValueOrDefault(name); c is not null; c = c.Parent is null ? null : classes.GetValueOrDefault(c.Parent))
        {
            if (c.Tag is not null) return (c.Name, c.Tag);
        }

        return null;
    }

    private static string? Resolve(string tag, Dictionary<string, string> strings)
    {
        var text = tag;
        for (var i = 0; i < 4 && text.StartsWith('$'); i++)
        {
            if (!strings.TryGetValue(text.TrimStart('$'), out var next)) break;
            text = next;
        }

        return !text.StartsWith('$') && text.Length > 0 ? text : null;
    }

    /// <summary>ZDoom's scripted marines by what they carry: their class names ("MarineRocket") read like weapons on a shelf.</summary>
    private static readonly Dictionary<string, string> MarineNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ScriptedMarine"] = "Marine (scripted)",
        ["MarineFist"] = "Marine with fists",
        ["MarineBerserk"] = "Marine, berserk",
        ["MarineChainsaw"] = "Marine with a chainsaw",
        ["MarinePistol"] = "Marine with a pistol",
        ["MarineShotgun"] = "Marine with a shotgun",
        ["MarineSSG"] = "Marine with a super shotgun",
        ["MarineChaingun"] = "Marine with a chaingun",
        ["MarineRocket"] = "Marine with a rocket launcher",
        ["MarinePlasma"] = "Marine with a plasma rifle",
        ["MarineRailgun"] = "Marine with a railgun",
        ["MarineBFG"] = "Marine with a BFG",
    };

    private static string NameOf(string? tag, string className, (string Name, string Tag)? named, Dictionary<string, string> strings)
    {
        if (MarineNames.TryGetValue(className, out var marine)) return marine;

        // A variant of a named class (StealthDoomImp of DoomImp's "Imp", DeadZombieMan of "Zombieman") says what it
        // adds: "Stealth imp", "Dead zombieman".
        if (named is { } n && !n.Name.Equals(className, StringComparison.OrdinalIgnoreCase) && className.Contains(n.Name, StringComparison.OrdinalIgnoreCase) && Resolve(n.Tag, strings) is { } parentName)
        {
            var extra = Words(className.Replace(n.Name, " ", StringComparison.OrdinalIgnoreCase)).Trim();
            if (extra.Length > 0) return Sentence($"{extra} {parentName.ToLowerInvariant()}");
        }

        // A name taken over from a differently named parent (StealthBaron from BaronOfHell) would hide which one it
        // is: its own class name says it ("Stealth baron").
        var own = named is { } m && m.Name.Equals(className, StringComparison.OrdinalIgnoreCase);
        if (tag is not null && own && Resolve(tag, strings) is { } text) return text;

        // No tag: the class name in words ("TallGreenColumn" becomes "Tall green column"; "DeadDoomImp" "Dead imp").
        var words = Words(className);
        return words.Length == 0 ? className : Sentence(words);
    }

    /// <summary>A class name in lower-case words, without the "Doom" that only tells same-named classes of other games apart.</summary>
    private static string Words(string className)
    {
        var words = CamelWords().Replace(className, " $1").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Count > 1) words.Remove("doom");
        return string.Join(' ', words);
    }

    private static string Sentence(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>English strings by identifier, from language.def (aliases such as "$$CC_IMP") and the language CSV tables.</summary>
    private static Dictionary<string, string> LanguageStrings(ResourceContainer engine)
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in engine.Entries)
        {
            var path = e.FullPath;
            if (!path.StartsWith("language.", StringComparison.OrdinalIgnoreCase)) continue;
            var text = Encoding.UTF8.GetString(e.Read());
            if (path.EndsWith(".def", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".enu", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in DefEntry().Matches(StripComments(text))) strings.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            }
            else if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".0", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in Csv(text))
                {
                    if (row.Count >= 2 && row[1].Length > 0 && row[0].Length > 0) strings.TryAdd(row[1], row[0]);
                }
            }
        }

        return strings;
    }

    /// <summary>Rows of a CSV table (quoted fields may hold commas, quotes and line breaks).</summary>
    private static IEnumerable<List<string>> Csv(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\n':
                    row.Add(field.ToString().TrimEnd('\r'));
                    field.Clear();
                    yield return row;
                    row = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>
    /// The actor classes declared in one ZScript file (<paramref name="text"/>, without its comments): their parent, what they
    /// replace, the properties WadSmith uses and their state lines. With <paramref name="raw"/>, the same file with its comments
    /// (blanked to spaces in <paramref name="text"/>, so the two line up), each class's Doom Builder keys are read from them.
    /// </summary>
    private static List<ActorClass> ParseClasses(string text, string? raw = null)
    {
        var found = new List<ActorClass>();
        foreach (Match m in ClassHeader().Matches(text))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchingBrace(text, open);
            if (close < 0) continue;
            var body = text[(open + 1)..close];
            var replaces = ReplacesWord().Match(m.Value);
            var c = new ActorClass { Name = m.Groups[1].Value, Parent = m.Groups[2].Success ? m.Groups[2].Value : null, Body = body, Replaces = replaces.Success ? replaces.Groups[1].Value : null };
            if (raw is not null) ReadKeys(c, raw[open..close]);
            if (BlockAfter(body, DefaultHeader()) is { } defaults) ReadDefaults(c, defaults);
            if (BlockAfter(body, StatesHeader()) is { } states) ReadStates(c, states);
            found.Add(c);
        }

        return found;
    }

    /// <summary>
    /// The actors declared in one DECORATE file (<paramref name="text"/> with its comments blanked, <paramref name="raw"/> with
    /// them, for the Doom Builder keys): <c>actor Name : Parent replaces Other 1234 { … }</c>, each with the number its line gives.
    /// </summary>
    private static List<(ActorClass Class, int? Number)> ParseDecorate(string text, string raw)
    {
        var found = new List<(ActorClass, int?)>();
        foreach (Match m in DecorateHeader().Matches(text))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchingBrace(text, open);
            if (close < 0) continue;
            var body = text[(open + 1)..close];
            var c = new ActorClass { Name = m.Groups[1].Value, Parent = m.Groups[2].Success ? m.Groups[2].Value : null, Body = body, Replaces = m.Groups[3].Success ? m.Groups[3].Value : null };
            ReadKeys(c, raw[open..close]);

            // Its properties are the lines outside its States block.
            var states = StatesHeader().Match(body);
            var statesClose = states.Success ? MatchingBrace(body, states.Index + states.Length - 1) : -1;
            ReadDefaults(c, statesClose < 0 ? body : body[..states.Index] + body[(statesClose + 1)..]);
            if (statesClose >= 0) ReadStates(c, body[(states.Index + states.Length)..statesClose]);
            found.Add((c, m.Groups[4].Success && int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null));
        }

        return found;
    }

    /// <summary>The Doom Builder keys among a definition's comments: <c>//$Title "Fire imp"</c>, <c>//$Category Monsters</c>, <c>//$Sprite TROOA1</c>.</summary>
    private static void ReadKeys(ActorClass c, string raw)
    {
        foreach (Match k in EditorKey().Matches(raw)) c.Keys.TryAdd(k.Groups[1].Value, k.Groups[2].Value.Trim().Trim('"').Trim());
    }

    /// <summary>A class's default properties, one per line or between semicolons (a ZScript Default block, a DECORATE actor's lines).</summary>
    private static void ReadDefaults(ActorClass c, string defaults)
    {
        foreach (var raw in defaults.Split((char[])[';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;
            if (item[0] is '+' or '-')
            {
                foreach (var flag in item.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var on = flag[0] == '+';
                    var name = flag.TrimStart('+', '-');
                    if (name.Equals("SPAWNCEILING", StringComparison.OrdinalIgnoreCase)) c.SpawnCeiling = on;
                    if (name.Equals("ISMONSTER", StringComparison.OrdinalIgnoreCase)) c.Monster = on;
                }

                continue;
            }

            if (item.Equals("Monster", StringComparison.OrdinalIgnoreCase)) c.Monster = true;
            if (item.Equals("Skip_Super", StringComparison.OrdinalIgnoreCase)) c.SkipSuper = true;
            var space = item.IndexOfAny([' ', '\t']);
            if (space < 0) continue;
            var key = item[..space];
            var value = item[(space + 1)..].Trim();
            switch (key.ToUpperInvariant())
            {
                case "RADIUS": c.Radius = Number(value); break;
                case "HEIGHT": c.Height = Number(value); break;
                case "SCALE": c.Scale = Number(value); break;
                case "XSCALE": c.ScaleX = Number(value); break;
                case "YSCALE": c.ScaleY = Number(value); break;
                case "TAG": c.Tag = value.Trim('"'); break;
                case "SEESOUND": c.SeeSound = value.Trim('"'); break;
                case "ACTIVESOUND": c.ActiveSound = value.Trim('"'); break;
                case "INVENTORY.PICKUPSOUND": c.PickupSound = value.Trim('"'); break;
                case "HEALTH": c.Health = (int?)Number(value); break;
                case "INVENTORY.AMOUNT": c.Amount = (int?)Number(value); break;
                case "WEAPON.AMMOGIVE" or "WEAPON.AMMOGIVE1": c.AmmoGive = (int?)Number(value); break;
                case "WEAPON.AMMOTYPE" or "WEAPON.AMMOTYPE1": c.AmmoTypeName = value.Trim('"'); break;
            }
        }
    }

    /// <summary>A class's state lines in order, with labels as "Label:" entries.</summary>
    private static void ReadStates(ActorClass c, string states)
    {
        foreach (var raw in states.Split((char[])[';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            // Leading labels ("Melee:", "Spawn:"), possibly several, possibly followed by a state.
            while (LeadingLabel().Match(item) is { Success: true } label)
            {
                c.States.Add(label.Groups[1].Value + ":");
                item = item[label.Length..].Trim();
            }

            if (item.Length > 0) c.States.Add(item);
        }
    }

    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? BlockAfter(string body, Regex header)
    {
        var m = header.Match(body);
        if (!m.Success) return null;
        var open = m.Index + m.Length - 1;
        var close = MatchingBrace(body, open);
        return close < 0 ? null : body[(open + 1)..close];
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>The text with its comments turned to spaces (line breaks kept), so it lines up with the text as written.</summary>
    private static string BlankComments(string text)
    {
        var chars = text.ToCharArray();
        var inString = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (inString)
            {
                if (chars[i] == '\\') i++;
                else if (chars[i] == '"') inString = false;
                continue;
            }

            if (chars[i] == '"')
            {
                inString = true;
            }
            else if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                for (; i < chars.Length && chars[i] != '\n'; i++) chars[i] = ' ';
            }
            else if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? chars.Length : end + 2;
                for (; i < end; i++)
                {
                    if (chars[i] != '\n') chars[i] = ' ';
                }

                i--;
            }
        }

        return new string(chars);
    }

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                sb.Append(ch);
                if (ch == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                sb.Append(ch);
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') sb.Append('\n');
                    i++;
                }

                i++;
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"(?im)^\s*include\s+""([^""]+)""")]
    private static partial Regex IncludeLine();

    [GeneratedRegex(@"(?im)^\s*#include\s+""([^""]+)""")]
    private static partial Regex HashInclude();

    [GeneratedRegex(@"(?i)\breplaces\s+([A-Za-z_]\w*)")]
    private static partial Regex ReplacesWord();

    // DECORATE: "actor Name : Parent replaces Other 1234 {", every part after the name optional.
    [GeneratedRegex(@"(?i)(?<![A-Za-z_.])actor\s+([A-Za-z_][\w.]*)\s*(?::\s*([A-Za-z_][\w.]*))?\s*(?:replaces\s+([A-Za-z_][\w.]*))?\s*(-?\d+)?\s*(?:native\s*)?\{")]
    private static partial Regex DecorateHeader();

    [GeneratedRegex(@"(?m)//\$(Title|Category|Sprite)[ \t]+([^\r\n]+)")]
    private static partial Regex EditorKey();

    [GeneratedRegex(@"(?is)\bDoomEdNums\s*\{(.*?)\}")]
    private static partial Regex DoomEdNumsBlock();

    [GeneratedRegex("""(?m)^\s*(\d+)\s*=\s*"?([A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex EdNumEntry();

    // ZScript keywords ignore case: Heretic's keys, ammunition and artifacts are declared "Class".
    [GeneratedRegex(@"(?i)(?<![A-Za-z_])(?<!extend\s+)(?<!mixin\s+)class\s+([A-Za-z_]\w*)\s*(?::\s*([A-Za-z_]\w*))?[^{;]*\{")]
    private static partial Regex ClassHeader();

    [GeneratedRegex(@"(?i)\bDefault\s*\{")]
    private static partial Regex DefaultHeader();

    [GeneratedRegex(@"(?i)\bStates\s*(\([^)]*\))?\s*\{")]
    private static partial Regex StatesHeader();

    [GeneratedRegex(@"""([A-Za-z0-9_/.\-]{2,64})""")]
    private static partial Regex QuotedWord();

    [GeneratedRegex(@"(?i)Player\.WeaponSlot\s+(\d)\s*,([^;\n]*)")]
    private static partial Regex WeaponSlot();

    [GeneratedRegex(@"^""?([A-Z0-9_#-]{4})""?\s+""?[A-Z\[\]\\^#""]+")]
    private static partial Regex StateLine();

    [GeneratedRegex(@"^([A-Za-z_][\w.]*)\s*:(?!:)")]
    private static partial Regex LeadingLabel();

    [GeneratedRegex("""(?m)^\s*([A-Za-z_]\w*)\s*=\s*"((?:[^"\\]|\\.)*)"\s*;""")]
    private static partial Regex DefEntry();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex CamelWords();
}
