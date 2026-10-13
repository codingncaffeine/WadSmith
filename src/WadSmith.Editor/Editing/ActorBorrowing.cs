using System.Globalization;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>An object carried in: the number the map places it by, and what came with it.</summary>
public sealed record CarriedActor(int Number, ThingType Thing, int Sprites, int Sounds, bool New);

/// <summary>
/// Objects from another game the user owns, carried into a project the first time one is placed (D55): every sprite the
/// object and what it fires, drops or spawns show, as PNG in their own colours with their offsets; the sounds they make,
/// with the project's own SNDINFO naming them; and a number in the project's MAPINFO the map places it by. GZDoom already
/// knows how every Heretic, Hexen and Strife object behaves; only its pictures and sounds live in the other game's file.
/// </summary>
public static class ActorBorrowing
{
    /// <summary>The first number objects carried in are given, above the games' own.</summary>
    public const int FirstNumber = 20000;

    /// <summary>The project's sound table WadSmith writes, marked so a file of the user's own is left alone.</summary>
    public const string SoundFile = "sndinfo.txt";

    private const string SoundMark = "// Written by WadSmith: the sounds of objects carried in from other games. Edits here are replaced.";

    /// <summary>Why <paramref name="thing"/> of <paramref name="from"/> cannot be carried into <paramref name="into"/> yet; null when it can.</summary>
    public static string? WhyNot(GameResources into, GameResources from, ThingType thing)
    {
        if (thing.Category is ThingCategory.Start or ThingCategory.Marker) return "Player starts and markers belong to each game.";
        var needs = from.Things.Needs(thing.ClassName);
        var clash = needs.OwnSprites.FirstOrDefault(s => into.Sprites.Frames(s).Count > 0 && from.Sprites.Frames(s).Count > 0);
        return clash is null ? null : $"{thing.Name} draws the sprite {clash}, which {into.Game.Title} has too; objects whose pictures share a name with this game's come in a later update.";
    }

    /// <summary>
    /// Carries <paramref name="thing"/> of <paramref name="from"/> into <paramref name="project"/> (once: a second use finds the
    /// first) and into <paramref name="into"/>'s catalogue. Writes files: call it on a worker.
    /// </summary>
    public static CarriedActor Carry(Project project, GameResources into, GameResources from, ThingType thing)
    {
        if (project.FindCopy(from.Game.Id, thing.ClassName, "actor") is { } done && int.TryParse(done.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var had) && into.Things.Find(had) is not null)
        {
            return new CarriedActor(had, into.Things.Find(had)!, 0, 0, false);
        }

        if (WhyNot(into, from, thing) is { } why) throw new CorruptDataException(why);
        var needs = from.Things.Needs(thing.ClassName);

        // The sprites this game lacks, in the other game's colours, offsets kept.
        var sprites = 0;
        foreach (var sprite in needs.Sprites.Where(s => into.Sprites.Frames(s).Count == 0))
        {
            foreach (var (_, entry) in from.Sprites.Frames(sprite))
            {
                var file = $"sprites/{entry.Name}.png";
                if (project.FindCopy(from.Game.Id, entry.Name, "sprite") is null)
                {
                    var image = PictureLoader.Load(entry.Read(), ResourceNamespace.Sprites, from.Textures.Palette, entry.Name).Image;
                    project.WriteFile(file, ImageCodec.EncodePng(image));
                    project.Record(new CopiedAsset { Kind = "sprite", Game = from.Game.Id, Original = entry.Name, Name = entry.Name, File = file });
                }

                sprites++;
            }
        }

        // The sounds this game's table lacks, as their lumps, named in the project's own table.
        var sounds = 0;
        foreach (var name in needs.Sounds.Where(n => into.Things.Sounds.LumpFor(n) is null))
        {
            var (lines, lumps) = from.Things.Sounds.Needs(name);
            foreach (var lump in lumps)
            {
                if (project.FindCopy(from.Game.Id, lump, "sound") is not null || from.Find(lump, ResourceNamespace.Sounds) is not { } entry && from.Find(lump, ResourceNamespace.Global) is not { } _) continue;
                var source = from.Find(lump, ResourceNamespace.Sounds) ?? from.Find(lump, ResourceNamespace.Global)!;
                var file = $"sounds/{lump.ToUpperInvariant()}.lmp";
                project.WriteFile(file, source.Read());
                project.Record(new CopiedAsset { Kind = "sound", Game = from.Game.Id, Original = lump, Name = lump.ToUpperInvariant(), File = file });
                sounds++;
            }

            foreach (var line in lines)
            {
                if (project.FindCopy(from.Game.Id, line, "sndinfo") is null) project.Record(new CopiedAsset { Kind = "sndinfo", Game = from.Game.Id, Original = line, Name = line, File = SoundFile });
            }
        }

        WriteSoundTable(project);

        // A number this game and the project leave free.
        var taken = new HashSet<int>(into.Things.All.Select(t => t.Number));
        foreach (var c in project.Info.Copied.Where(c => c.Kind == "actor")) taken.Add(int.Parse(c.Name, CultureInfo.InvariantCulture));
        var number = FirstNumber;
        while (taken.Contains(number)) number++;
        project.Record(new CopiedAsset { Kind = "actor", Game = from.Game.Id, Original = thing.ClassName, Name = number.ToString(CultureInfo.InvariantCulture), File = "" });

        var carried = thing with { Number = number };
        into.Carry([carried], project.Folder);
        return new CarriedActor(number, carried, sprites, sounds, true);
    }

    /// <summary>Writes the project's sound table from the lines carried in; a table of the user's own is left alone.</summary>
    public static void WriteSoundTable(Project project)
    {
        var lines = project.Info.Copied.Where(c => c.Kind == "sndinfo").Select(c => c.Original).Distinct(StringComparer.Ordinal).ToList();
        var path = Path.Combine(project.Folder, SoundFile);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != SoundMark) return;
        }

        if (lines.Count == 0) return;
        project.WriteFile(SoundFile, Encoding.UTF8.GetBytes(SoundMark + "\n\n" + string.Join("\n", lines) + "\n"));
    }
}
