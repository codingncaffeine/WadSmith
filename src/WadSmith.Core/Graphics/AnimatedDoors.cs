using System.Text;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Graphics;

/// <summary>
/// A door that slides open by playing pictures on its faces, as Strife's do (ANIMDEFS <c>animateddoor</c>): the picture it
/// shows closed, the frames it plays in turn (the first is the closed picture), and the sounds of its opening and closing.
/// </summary>
public sealed record AnimatedDoor(string Name, IReadOnlyList<string> Frames, string? OpenSound, string? CloseSound);

/// <summary>
/// The pictures that are sliding doors in a game, as GZDoom reads them: its own <c>animdefs.txt</c> (Strife's seven sliding
/// doors), then the ANIMDEFS lumps of the loaded files, a later definition of a picture replacing an earlier one.
/// </summary>
public static class AnimatedDoors
{
    /// <summary>Every sliding door picture of the engine's data and the loaded files, by name (any case).</summary>
    public static IReadOnlyDictionary<string, AnimatedDoor> Read(string? engineDataPath, ResourceStack stack)
    {
        var doors = new Dictionary<string, AnimatedDoor>(StringComparer.OrdinalIgnoreCase);
        if (engineDataPath is not null && File.Exists(engineDataPath) && ResourceContainer.Open(engineDataPath).FindPath("animdefs.txt") is { } own)
        {
            Animdefs(Encoding.Latin1.GetString(own.Read()), doors);
        }

        foreach (var lump in stack.FindAll("ANIMDEFS")) Animdefs(Encoding.Latin1.GetString(lump.Read()), doors);
        return doors;
    }

    /// <summary>
    /// The sliding doors of an ANIMDEFS text: <c>animateddoor NAME [opensound S] [closesound S] pic A pic B …</c>, a <c>pic</c>
    /// given by number counting on from the door's own picture (1 is itself) as GZDoom counts it, in the picture's name order.
    /// </summary>
    public static void Animdefs(string text, IDictionary<string, AnimatedDoor> into)
    {
        var tokens = SwitchPictures.Tokens(text);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (!tokens[i].Equals("animateddoor", StringComparison.OrdinalIgnoreCase)) continue;
            var name = tokens[i + 1];
            var at = i + 2;
            string? open = null, close = null;
            var frames = new List<string>();
            while (at + 1 < tokens.Count)
            {
                var word = tokens[at].ToLowerInvariant();
                if (word == "opensound") open = tokens[at + 1];
                else if (word == "closesound") close = tokens[at + 1];
                else if (word == "pic") frames.Add(int.TryParse(tokens[at + 1], out var n) ? Numbered(name, n) : tokens[at + 1]);
                else if (word is "allowdecals") { at++; continue; }
                else break;
                at += 2;
            }

            if (frames.Count == 0) frames.Add(name);
            into[name] = new AnimatedDoor(name, frames, open, close);
            i = at - 1;
        }
    }

    /// <summary>The picture <paramref name="n"/> - 1 on from a door's own, by the number its name ends in (DORQTR01 and 3: DORQTR03).</summary>
    private static string Numbered(string name, int n)
    {
        var digits = name.Length - name.TrimEnd("0123456789".ToCharArray()).Length;
        if (digits == 0 || !int.TryParse(name[^digits..], out var first)) return name;
        return name[..^digits] + (first + n - 1).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }
}
