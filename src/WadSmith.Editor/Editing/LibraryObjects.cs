using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Audio;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Library;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>
/// An object from a free source (D61: a Realm667 monster, weapon, item or prop) brought whole into a project the first time
/// one is placed: its DECORATE or ZSCRIPT and its other definition lumps as root files of their own (<c>DECORATE.r667-748</c>:
/// GZDoom reads every lump of a name), its sprites, sounds and pictures, its credits; every class its page offers gets a
/// number from the project's DoomEdNums. Pictures drawn in another game's colours are turned into PNG so they keep them;
/// <c>replaces</c> is taken off; a sprite name the game or the project already uses is renamed. GZDoom only.
/// </summary>
public static class LibraryObjects
{
    /// <summary>What a placing gets back: the object to place, every object the entry brought, notes in words, and whether it was new.</summary>
    public sealed record Brought(CarriedActor Main, IReadOnlyList<CarriedActor> Actors, IReadOnlyList<string> Notes, bool New);

    /// <summary>The definition lumps copied in as root files of their own (GZDoom reads every one of each name).</summary>
    private static readonly HashSet<string> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DECORATE", "ZSCRIPT", "SNDINFO", "SNDSEQ", "GLDEFS", "DOOMDEFS", "HTICDEFS", "HEXNDEFS", "STRFDEFS", "ANIMDEFS", "TEXTURES",
        "KEYCONF", "LANGUAGE", "LOCKDEFS", "DECALDEF", "TERRAIN", "MODELDEF", "VOXELDEF", "LOADACS", "FONTDEFS", "TEXTCOLO", "REVERBS",
        "CVARINFO", "TRNSLATE", "SPLASHES", "BRIGHTMAPS",
    };

    /// <summary>Lumps never copied: they would change the whole game (its colours, its menus, its maps' rules) or are words for people.</summary>
    private static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "PLAYPAL", "COLORMAP", "TINTTAB", "XLATAB", "MAPINFO", "ZMAPINFO", "EMAPINFO", "UMAPINFO", "DMXGUS", "DMXGUSC", "DEHACKED", "SBARINFO",
        "MENUDEF", "GAMEINFO", "PNAMES", "TEXTURE1", "TEXTURE2", "ENDOOM", "CREDITS", "INFO", "README", "WADINFO", "ZSPROGS",
    };

    /// <summary>The kinds of lump the GLDEFS family holds, whose frame names follow a renamed sprite.</summary>
    private static readonly HashSet<string> LightDefinitions = new(StringComparer.OrdinalIgnoreCase) { "GLDEFS", "DOOMDEFS", "HTICDEFS", "HEXNDEFS", "STRFDEFS", "BRIGHTMAPS" };

    /// <summary>The project's record of an entry brought in before, or null.</summary>
    public static CopiedAsset? Of(Project project, LibraryItem item) => project.FindCopy(item.Source, item.Id, "library");

    /// <summary>
    /// Brings <paramref name="item"/> (its downloaded <paramref name="files"/>) into <paramref name="project"/>, once: a second
    /// placing finds the first. <paramref name="paletteOf"/> gives a family's colours for pictures drawn in another game's.
    /// Call on a worker.
    /// </summary>
    public static Brought Bring(Project project, GameResources into, LibraryItem item, IReadOnlyList<string> files, Func<string, Palette?> paletteOf)
    {
        var c = CultureInfo.InvariantCulture;
        if (Of(project, item) is not null)
        {
            var had = Recorded(project.CopiedNow(), item.Id).Select(t => new CarriedActor(t.Number, into.Things.Find(t.Number) ?? t, 0, 0, false)).ToList();
            if (had.Count > 0) return new Brought(Main(had, item), had, [], false);
        }

        var tag = item.Id;
        var notes = new List<string>();
        var containers = files.Select(ResourceContainer.Open).ToList();
        var entries = containers.SelectMany(k => k.Entries.Where(e => !e.IsMarker)).ToList();

        // The asset's own paths for files outside the namespaces (a PK3's actors/monster.txt), moved under its own folder.
        var others = entries.Where(e => e.Namespace == ResourceNamespace.Other).ToDictionary(e => e.FullPath, e => $"library/{tag}/{e.FullPath}", StringComparer.OrdinalIgnoreCase); // archive path
        string? Moved(string quoted)
        {
            var key = quoted.Replace('\\', '/').TrimStart('/'); // archive path
            if (others.TryGetValue(key, out var to)) return to;
            // A folder named in a MODELDEF Path: point it where its files went.
            var folder = key.TrimEnd('/') + "/"; // archive path
            return others.Keys.Any(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) ? $"library/{tag}/{key.TrimEnd('/')}" : null; // archive path
        }

        // The definition texts, and the scripts they include.
        var texts = entries.Where(e => e.Namespace == ResourceNamespace.Global && Definitions.Contains(e.Name)).ToList();
        var scripts = new List<(ResourceEntry Entry, bool ZScript)>();
        foreach (var e in texts.Where(e => e.Name.Equals("DECORATE", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("ZSCRIPT", StringComparison.OrdinalIgnoreCase))) scripts.Add((e, e.Name.Equals("ZSCRIPT", StringComparison.OrdinalIgnoreCase)));
        for (var i = 0; i < scripts.Count; i++)
        {
            foreach (var include in ActorScripts.Includes(Encoding.UTF8.GetString(scripts[i].Entry.Read())))
            {
                if (entries.FirstOrDefault(e => e.FullPath.Equals(include.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) is { } inc && scripts.All(s => s.Entry != inc)) scripts.Add((inc, scripts[i].ZScript));
            }
        }

        var declared = scripts.SelectMany(s => ActorScripts.Read(Encoding.UTF8.GetString(s.Entry.Read()), s.ZScript)).ToList();
        if (declared.Count == 0) throw new CorruptDataException($"{item.Title} declares no object WadSmith can place (no DECORATE or ZSCRIPT actor was found in it).");

        // A class the game or the project already has would stop GZDoom loading the map.
        var mine = project.CopiedNow().Where(r => r.Kind == "libraryactor").Select(r => r.Original).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in declared)
        {
            if (into.Things.FindClass(d.ClassName) is not null && !mine.Contains(d.ClassName) || mine.Contains(d.ClassName))
            {
                throw new CorruptDataException($"{item.Title} declares {d.ClassName}, a name this project already has{(mine.Contains(d.ClassName) ? " from another download" : " in the game")}; GZDoom would refuse both, so it was not brought in.");
            }
        }

        // Sprite names the game or the project already shows are renamed, so neither changes the other's pictures.
        var shipped = entries.Where(e => e.Namespace == ResourceNamespace.Sprites && e.Name.Length >= 6).Select(e => e.Name[..4].ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sprite in shipped.Order(StringComparer.Ordinal))
        {
            if (into.Sprites.Frames(sprite).Count == 0) continue;
            renames[sprite] = FreeSprite(into, shipped, renames.Values);
            notes.Add($"Its sprite {sprite} has the name of one this game already shows; it is {renames[sprite]} here.");
        }

        string Sprite(string name) => name.Length >= 4 && renames.TryGetValue(name[..4].ToUpperInvariant(), out var to) ? to + name[4..] : name;

        // Pictures drawn in another family's colours are turned into PNG in those colours.
        var palette = into.Textures.Palette;
        var theirs = item.Family == into.Game.Engine || item.Family == "doom" && into.Game.Engine == "chex" ? null : paletteOf(item.Family);
        // A PLAYPAL of its own counts only when it is not the game's (many downloads carry a copy of Doom's); it is never copied.
        var ownBytes = entries.FirstOrDefault(e => e.Name.Equals("PLAYPAL", StringComparison.OrdinalIgnoreCase)) is { Size: >= 768 } pal ? pal.Read() : null;
        var gameBytes = into.Find("PLAYPAL", ResourceNamespace.Global)?.Read();
        var own = ownBytes is not null && (gameBytes is null || !ownBytes.AsSpan(0, 768).SequenceEqual(gameBytes.AsSpan(0, Math.Min(768, gameBytes.Length)))) ? new Palette(ownBytes) : null;
        var from = own ?? theirs;
        if (from is null && item.Family != into.Game.Engine && item.Family != "doom") notes.Add($"It was drawn in {item.Style}'s colours and no game of that kind was found to read them, so its pictures may look off.");

        var written = new List<(string, byte[])>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, byte[] data)
        {
            if (paths.Add(path)) written.Add((path, data));
        }

        foreach (var e in entries)
        {
            var name = e.Name;
            if (Never.Contains(name) || MapLump(name)) continue;
            switch (e.Namespace)
            {
                case ResourceNamespace.Global when Definitions.Contains(name):
                {
                    var text = Encoding.UTF8.GetString(e.Read());
                    if (name.Equals("DECORATE", StringComparison.OrdinalIgnoreCase) || name.Equals("ZSCRIPT", StringComparison.OrdinalIgnoreCase))
                    {
                        var (rewritten, said) = ActorScripts.Rewrite(text, name.Equals("ZSCRIPT", StringComparison.OrdinalIgnoreCase), renames, Moved);
                        text = rewritten;
                        notes.AddRange(said);
                    }
                    else
                    {
                        if (LightDefinitions.Contains(name)) text = ActorScripts.RenameInDefinitions(text, renames);
                        text = ActorScripts.Rewrite(text, false, new Dictionary<string, string>(), Moved).Text;
                    }

                    var file = $"{name.ToUpperInvariant()}.{tag}";
                    for (var n = 2; paths.Contains(file); n++) file = string.Create(c, $"{name.ToUpperInvariant()}.{tag}-{n}");
                    Add(file, Encoding.UTF8.GetBytes(text));
                    break;
                }

                case ResourceNamespace.Other:
                {
                    var data = e.Read();
                    if (scripts.Any(s => s.Entry == e))
                    {
                        var (rewritten, said) = ActorScripts.Rewrite(Encoding.UTF8.GetString(data), scripts.First(s => s.Entry == e).ZScript, renames, Moved);
                        notes.AddRange(said);
                        data = Encoding.UTF8.GetBytes(rewritten);
                    }

                    Add(others[e.FullPath], data);
                    break;
                }

                case ResourceNamespace.Sprites or ResourceNamespace.Patches or ResourceNamespace.Graphics or ResourceNamespace.Flats or ResourceNamespace.Textures or ResourceNamespace.HiRes:
                {
                    var folder = e.Namespace switch
                    {
                        ResourceNamespace.Sprites => "sprites", ResourceNamespace.Patches => "patches", ResourceNamespace.Flats => "flats",
                        ResourceNamespace.Textures => "textures", ResourceNamespace.HiRes => "hires", _ => "graphics",
                    };
                    var lump = e.Namespace == ResourceNamespace.Sprites ? Sprite(name) : name;
                    var (path, data) = Picture(e, folder, lump, tag, from, palette);
                    Add(path, data);
                    break;
                }

                case ResourceNamespace.Sounds or ResourceNamespace.Music or ResourceNamespace.Acs or ResourceNamespace.Voxels or ResourceNamespace.Voices:
                {
                    var folder = e.Namespace switch { ResourceNamespace.Sounds => "sounds", ResourceNamespace.Music => "music", ResourceNamespace.Acs => "acs", ResourceNamespace.Voxels => "voxels", _ => "voices" };
                    Add(KeptPath(e, folder, tag), e.Read());
                    break;
                }

                case ResourceNamespace.Global:
                {
                    // A WAD keeps sounds, ACS and pictures as plain lumps: each goes where a PK3 keeps its kind, its name unchanged.
                    var data = e.Read();
                    var audio = AudioFormats.Detect(data);
                    if (audio is AudioKind.Mus or AudioKind.Midi or AudioKind.Module) Add($"music/{tag}/{name}.lmp", data);
                    else if (audio is not AudioKind.None) Add($"sounds/{tag}/{name}.lmp", data);
                    else if (data.Length >= 4 && data[0] == 'A' && data[1] == 'C' && data[2] == 'S') Add($"acs/{tag}/{name}.o", data);
                    else if (DoomPicture.IsValid(data) || ImageCodec.IsKnownImage(data)) { var (path, bytes) = Picture(e, "graphics", name, tag, from, palette); Add(path, bytes); }
                    break;
                }
            }
        }

        // The credits: what the entry's page says, the asset's own words, where it came from.
        var creditFile = CreditFile(item);
        var words = entries.Where(e => e.Name.Equals("CREDITS", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("INFO", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("README", StringComparison.OrdinalIgnoreCase));
        Add(creditFile, Encoding.UTF8.GetBytes(CreditText(item, words.Select(e => Encoding.UTF8.GetString(e.Read())))));
        project.WriteFiles(written);

        // Numbers for what the page offers to place, else every actor that shows a picture and is not a missile.
        var byName = declared.GroupBy(d => d.ClassName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var offered = item.Summons.Where(byName.ContainsKey).Select(s => byName[s]).ToList();
        if (offered.Count == 0) offered = [.. declared.Where(d => !d.Abstract && !d.Missile && Look(d, byName, into).Sprite is not null)];
        if (offered.Count == 0) offered = [declared[0]];

        var records = new List<CopiedAsset> { new() { Kind = "library", Game = item.Source, Original = item.Id, Name = item.Title, File = creditFile } };
        var things = new List<ThingType>();
        var taken = new HashSet<int>();
        foreach (var d in offered)
        {
            var number = FreeNumber(project, into, taken);
            taken.Add(number);
            var (sprite, frame, radius, height, scale) = Look(d, byName, into);
            var category = CategoryOf(d, byName, into, item);
            var title = offered.Count == 1 || d == offered[0] && item.Summons.Count > 0 ? item.Title : ModelProps.Title(d.ClassName);
            var spriteName = sprite is null ? "" : Sprite(sprite);
            records.Add(new CopiedAsset { Kind = "actor", Game = item.Source, Original = d.ClassName, Name = number.ToString(c), File = $"DECORATE.{tag}" });
            records.Add(new CopiedAsset { Kind = "libraryactor", Game = item.Source, Original = d.ClassName, File = tag, Name = string.Join('|', spriteName, frame.ToString(), radius.ToString(c), height.ToString(c), title.Replace('|', '/'), category.ToString(), scale.ToString(c)) });
            things.Add(new ThingType(number, d.ClassName, title, spriteName.Length > 0 ? spriteName : null, frame, radius, height, scale, scale, false, category));
        }

        project.Record(records);
        into.Carry(things, project.Folder);
        var actors = things.Select(t => new CarriedActor(t.Number, t, written.Count(w => w.Item1.StartsWith("sprites/", StringComparison.Ordinal)), written.Count(w => w.Item1.StartsWith("sounds/", StringComparison.Ordinal)), true)).ToList(); // archive path
        return new Brought(Main(actors, item), actors, notes, true);
    }

    /// <summary>The objects a project's record says it brought from free sources (all, or one entry's), as catalogue entries.</summary>
    public static IEnumerable<ThingType> Recorded(IReadOnlyList<CopiedAsset> copied, string? entry = null)
    {
        foreach (var r in copied.Where(r => r.Kind == "libraryactor" && (entry is null || r.File == entry)))
        {
            var p = r.Name.Split('|');
            var number = copied.FirstOrDefault(a => a.Kind == "actor" && a.Game == r.Game && a.Original == r.Original)?.Name;
            if (p.Length < 7 || number is null) continue;
            var c = CultureInfo.InvariantCulture;
            if (!int.TryParse(number, NumberStyles.Integer, c, out var n) || !double.TryParse(p[2], NumberStyles.Float, c, out var radius) || !double.TryParse(p[3], NumberStyles.Float, c, out var height) || !double.TryParse(p[6], NumberStyles.Float, c, out var scale)) continue;
            yield return new ThingType(n, r.Original, p[4], p[0].Length > 0 ? p[0] : null, p[1].Length > 0 ? p[1][0] : 'A', radius, height, scale, scale, false, Enum.TryParse<ThingCategory>(p[5], out var cat) ? cat : ThingCategory.Decoration);
        }
    }

    /// <summary>Where an entry's credits are kept in a project.</summary>
    public static string CreditFile(LibraryItem item) => $"credits/{item.Id}.txt";

    /// <summary>An entry's credits as a text: where it came from, what its author allows, who made it, and its own words.</summary>
    public static string CreditText(LibraryItem item, IEnumerable<string> own)
    {
        var credit = new StringBuilder();
        credit.Append(CultureInfo.InvariantCulture, $"{item.Title} — from {SourceName(item.Source)}, {item.Page}\n");
        credit.Append(CultureInfo.InvariantCulture, $"Brought into this project by WadSmith on {DateTime.UtcNow:yyyy-MM-dd}.\n");
        if (item.Licence is { Length: > 0 } licence) credit.Append("\nPermissions: ").Append(licence).Append('\n');
        if (item.Credits.Length > 0) credit.Append('\n').Append(item.Credits).Append('\n');
        foreach (var words in own) credit.Append('\n').Append(words.Replace("\r", "", StringComparison.Ordinal).Trim()).Append('\n');
        return credit.ToString();
    }

    /// <summary>
    /// Records a texture pack the first time one of its pictures is copied into <paramref name="project"/> (D61): its credits
    /// file and the record the Share check reads. Call on a worker.
    /// </summary>
    public static void CreditPack(Project project, LibraryItem item)
    {
        if (Of(project, item) is not null) return;
        var file = CreditFile(item);
        project.WriteFile(file, Encoding.UTF8.GetBytes(CreditText(item, [])));
        project.Record(new CopiedAsset { Kind = "library", Game = item.Source, Original = item.Id, Name = item.Title, File = file });
    }

    /// <summary>The words for a source in credits and notices.</summary>
    public static string SourceName(string source) => source switch { "realm667" => "Realm667", "idgames" => "the idgames archive", _ => source };

    private static CarriedActor Main(IReadOnlyList<CarriedActor> actors, LibraryItem item) =>
        item.Summons.Select(s => actors.FirstOrDefault(a => a.Thing.ClassName.Equals(s, StringComparison.OrdinalIgnoreCase))).OfType<CarriedActor>().FirstOrDefault() ?? actors[0];

    /// <summary>What an actor looks like and how big it is: its own values, else its parent's (in the download, then the game's).</summary>
    private static (string? Sprite, char Frame, double Radius, double Height, double Scale) Look(ActorScripts.Declared d, Dictionary<string, ActorScripts.Declared> byName, GameResources into)
    {
        string? sprite = d.Sprite;
        var frame = d.Frame;
        double? radius = d.Radius, height = d.Height, scale = d.Scale;
        var parent = d.Parent;
        for (var depth = 0; parent is not null && depth < 8 && (sprite is null || radius is null || height is null || scale is null); depth++)
        {
            if (byName.TryGetValue(parent, out var p))
            {
                if (sprite is null && p.Sprite is not null) (sprite, frame) = (p.Sprite, p.Frame);
                radius ??= p.Radius;
                height ??= p.Height;
                scale ??= p.Scale;
                parent = p.Parent;
                continue;
            }

            if (into.Things.FindClass(parent) is { } t)
            {
                if (sprite is null && t.Sprite is not null) (sprite, frame) = (t.Sprite, t.Frame);
                radius ??= t.Radius;
                height ??= t.Height;
                scale ??= t.ScaleX;
            }

            break;
        }

        return (sprite, frame, radius ?? 20, height ?? 16, scale is > 0 ? scale.Value : 1);
    }

    private static ThingCategory CategoryOf(ActorScripts.Declared d, Dictionary<string, ActorScripts.Declared> byName, GameResources into, LibraryItem item)
    {
        if (d.Monster) return ThingCategory.Monster;
        var parent = d.Parent;
        for (var depth = 0; parent is not null && depth < 8; depth++)
        {
            if (byName.TryGetValue(parent, out var p))
            {
                if (p.Monster) return ThingCategory.Monster;
                parent = p.Parent;
                continue;
            }

            if (into.Things.FindClass(parent) is { } t) return t.Category;
            break;
        }

        return item.Group switch
        {
            "Monsters" => ThingCategory.Monster, "Weapons" => ThingCategory.Weapon, "Ammunition" => ThingCategory.Ammo, "Health" => ThingCategory.Health,
            "Keys" => ThingCategory.Key, "Power-ups" => ThingCategory.Powerup, _ => ThingCategory.Decoration,
        };
    }

    /// <summary>A picture lump as it goes into the project: its own bytes when the game's colours read it, else PNG (offsets kept).</summary>
    private static (string Path, byte[] Data) Picture(ResourceEntry e, string folder, string lump, string tag, Palette? from, Palette palette)
    {
        var data = e.Read();
        var flat = e.Namespace == ResourceNamespace.Flats;
        var paletted = !ImageCodec.IsKnownImage(data) && (flat ? RawPicture.FlatSize(data.Length) > 0 : DoomPicture.IsValid(data));
        if (!paletted || from is null) return ($"{folder}/{tag}/{lump}.{(ImageCodec.IsPng(data) ? "png" : "lmp")}", data);
        var image = PictureLoader.Load(data, e.Namespace, from, lump).Image;
        var png = ImageCodec.EncodePng(image);
        if (!flat) png = ImageCodec.WithGrab(png, image.OffsetX, image.OffsetY);
        return ($"{folder}/{tag}/{lump}.png", png);
    }

    /// <summary>Where a file of a namespace folder goes: its own path in a PK3 (a SNDINFO may name it by path), its lump name under the entry's folder from a WAD.</summary>
    private static string KeptPath(ResourceEntry e, string folder, string tag) =>
        e.FullPath.Contains('/', StringComparison.Ordinal) ? e.FullPath : $"{folder}/{tag}/{e.Name}.lmp"; // archive path

    private static bool MapLump(string name) => name is "THINGS" or "LINEDEFS" or "SIDEDEFS" or "VERTEXES" or "SEGS" or "SSECTORS" or "NODES" or "SECTORS" or "REJECT" or "BLOCKMAP" or "BEHAVIOR" or "TEXTMAP" or "ENDMAP" or "ZNODES" or "SCRIPTS"
        || System.Text.RegularExpressions.Regex.IsMatch(name, "^(MAP[0-9][0-9]|E[1-9]M[1-9])$");

    /// <summary>A four-character sprite name neither the game, the download nor this bringing uses: the first two of "WL" and two more.</summary>
    private static string FreeSprite(GameResources into, HashSet<string> shipped, IEnumerable<string> chosen)
    {
        var used = chosen.ToHashSet(StringComparer.Ordinal);
        const string symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (var i = 0; i < symbols.Length * symbols.Length; i++)
        {
            var name = $"WL{symbols[i / 36]}{symbols[i % 36]}";
            if (!used.Contains(name) && !shipped.Contains(name) && into.Sprites.Frames(name).Count == 0) return name;
        }

        throw new CorruptDataException("Every sprite name WadSmith gives renamed downloads is taken in this project.");
    }

    private static int FreeNumber(Project project, GameResources into, HashSet<int> taken)
    {
        var used = new HashSet<int>(into.Things.All.Select(t => t.Number));
        used.UnionWith(taken);
        foreach (var r in project.CopiedNow().Where(r => r.Kind == "actor")) used.Add(int.Parse(r.Name, CultureInfo.InvariantCulture));
        var number = ActorBorrowing.FirstNumber;
        while (used.Contains(number)) number++;
        return number;
    }
}
