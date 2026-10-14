using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>
/// Pictures from another game the user owns, copied into the project the first time one is used (D43): a wall texture to
/// <c>textures/NAME.png</c>, a floor to <c>flats/NAME.png</c>, in its own colours (PNG keeps the other game's palette), named
/// as in its game unless this game already has that name. The project's record notes every copy for the Share check.
/// </summary>
public static class Borrowing
{
    /// <summary>
    /// Copies the picture <paramref name="name"/> of <paramref name="from"/> into <paramref name="project"/> (once: a second use
    /// finds the first copy) and adds it to <paramref name="into"/>'s textures. Returns its name in the project. Call on a worker.
    /// </summary>
    public static string CopyPicture(Project project, GameResources into, GameResources from, string name, bool flat)
    {
        var kind = flat ? "flat" : "wall";
        if (project.FindCopy(from.Game.Id, name, kind) is { } done && into.Textures.Has(done.Name)) return done.Name;
        // A game whose pictures are all one kind (Duke Nukem 3D's tiles) lends a wall picture for a floor too.
        var image = (flat ? from.Textures.GetFlat(name) ?? from.Textures.GetWall(name) : from.Textures.GetWall(name)) ?? throw new CorruptDataException($"{from.Game.Title} has no {(flat ? "floor" : "wall texture")} called {name}.");
        var chosen = FreeName(into.Textures, name, from.Game);
        var file = $"{(flat ? "flats" : "textures")}/{chosen}.png";
        project.WriteFile(file, ImageCodec.EncodePng(image));
        var entry = ResourceContainer.Open(project.Folder).FindPath(file) ?? throw new CorruptDataException($"{file} was written but could not be read back.");
        into.Textures.Add(entry);
        project.Record(new CopiedAsset { Kind = kind, Game = from.Game.Id, Original = name, Name = chosen, File = file });
        if (from.Library is { } pack) LibraryObjects.CreditPack(project, pack);
        return chosen;
    }

    /// <summary>
    /// Copies into <paramref name="project"/> the pictures a paste from <paramref name="from"/> uses that this game shows
    /// otherwise or not at all: from the same game, those it lacks (copied in from another game, or the other project's own);
    /// from another game, every one whose picture here differs. Skies stay the engine's. Returns the pictures that came in
    /// under another name and how many were copied. Call on a worker.
    /// </summary>
    public static (Dictionary<string, string> Renamed, int Copied) Carry(Project project, GameResources into, GameResources from, IEnumerable<(string Name, bool Flat)> pictures)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        var same = from.Game.Id == into.Game.Id;
        foreach (var (name, flat) in pictures.Distinct())
        {
            if (name is "" or "-" || name.StartsWith("F_SKY", StringComparison.OrdinalIgnoreCase)) continue;
            var theirs = flat ? from.Textures.GetFlat(name) : from.Textures.GetWall(name);
            if (theirs is null) continue;
            if (into.Textures.Has(name) && (same || ((flat ? into.Textures.GetFlat(name) : into.Textures.GetWall(name)) is { } ours && ours.Width == theirs.Width && ours.Height == theirs.Height && ours.Pixels.AsSpan().SequenceEqual(theirs.Pixels)))) continue;
            var chosen = CopyPicture(project, into, from, name, flat);
            copied++;
            if (!string.Equals(chosen, name, StringComparison.OrdinalIgnoreCase)) renamed[name] = chosen;
        }

        return (renamed, copied);
    }

    /// <summary>
    /// The name a copy goes by: its own when free, else with its game's letter in front (H for Heretic…), else with a number
    /// at the end; never more than 8 characters, as the games' names are.
    /// </summary>
    public static string FreeName(TextureLibrary library, string name, GameDefinition game)
    {
        var upper = name.ToUpperInvariant();
        if (!library.Has(upper)) return upper;
        var letter = game.Engine switch { "heretic" => 'H', "hexen" => 'X', "strife" => 'S', "chex" => 'C', _ => 'D' };
        var lettered = (letter + upper)[..Math.Min(8, upper.Length + 1)];
        if (!library.Has(lettered)) return lettered;
        for (var n = 1; n < 1000; n++)
        {
            var suffix = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var numbered = upper[..Math.Min(upper.Length, 8 - suffix.Length)] + suffix;
            if (!library.Has(numbered)) return numbered;
        }

        throw new CorruptDataException($"No free name was found for {name}.");
    }
}
