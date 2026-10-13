using System.Text;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Graphics;

/// <summary>
/// The pictures that are switches in a game, in both their states (Eureka's "non-animating switch textures" check needs
/// them): GZDoom's own list for the game (its <c>animdefs.txt</c>, <c>switch doom 1 SW1BRCOM on pic SW2BRCOM</c>), then the
/// ANIMDEFS and Boom SWITCHES lumps of the loaded files, as GZDoom reads them.
/// </summary>
public static class SwitchPictures
{
    /// <summary>Every switch picture for <paramref name="family"/> ("doom", "heretic", "hexen", "strife"; Chex counts as Doom), by name, any case.</summary>
    public static IReadOnlySet<string> Read(string? engineDataPath, string family, ResourceStack stack)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var game = family == "chex" ? "doom" : family;
        if (engineDataPath is not null && File.Exists(engineDataPath) && ResourceContainer.Open(engineDataPath).FindPath("animdefs.txt") is { } own)
        {
            Animdefs(Encoding.Latin1.GetString(own.Read()), game, names);
        }

        foreach (var lump in stack.FindAll("ANIMDEFS")) Animdefs(Encoding.Latin1.GetString(lump.Read()), game, names);
        foreach (var lump in stack.FindAll("SWITCHES")) Switches(lump.Read(), names);
        return names;
    }

    private static readonly HashSet<string> Games = new(StringComparer.OrdinalIgnoreCase) { "doom", "heretic", "hexen", "strife", "any" };

    /// <summary>
    /// The switches of an ANIMDEFS text for a game: <c>switch [game [n]] NAME on|off [sound S] pic NAME tics N [rand A B] …</c>,
    /// the switch's own picture and each <c>pic</c> it turns into. A switch for another game is left out.
    /// </summary>
    public static void Animdefs(string text, string game, ISet<string> into)
    {
        var tokens = Tokens(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Equals("switch", StringComparison.OrdinalIgnoreCase)) continue;
            var at = i + 1;
            var forGame = "any";
            if (at < tokens.Count && Games.Contains(tokens[at]))
            {
                forGame = tokens[at++];
                if (at < tokens.Count && int.TryParse(tokens[at], out _)) at++;
            }

            if (at >= tokens.Count) break;
            var mine = forGame.Equals("any", StringComparison.OrdinalIgnoreCase) || forGame.Equals(game, StringComparison.OrdinalIgnoreCase);
            var pictures = new List<string> { tokens[at++] };
            while (at < tokens.Count)
            {
                var word = tokens[at].ToLowerInvariant();
                if (word is "on" or "off")
                {
                    at++;
                }
                else if (word is "pic" && at + 1 < tokens.Count)
                {
                    pictures.Add(tokens[at + 1]);
                    at += 2;
                }
                else if (word is "sound" or "tics")
                {
                    at += 2;
                }
                else if (word is "rand")
                {
                    at += 3;
                }
                else
                {
                    break;
                }
            }

            if (mine) into.UnionWith(pictures);
            i = at - 1;
        }
    }

    /// <summary>Boom's SWITCHES lump: 20-byte records, an off picture and an on picture of 9 bytes each and a game (0 ends the list).</summary>
    public static void Switches(byte[] data, ISet<string> into)
    {
        for (var at = 0; at + 20 <= data.Length; at += 20)
        {
            if (BitConverter.ToInt16(data, at + 18) == 0) break;
            into.Add(Name(data, at));
            into.Add(Name(data, at + 9));
        }

        static string Name(byte[] data, int at)
        {
            var end = Array.IndexOf(data, (byte)0, at, 9);
            return Encoding.Latin1.GetString(data, at, (end < 0 ? at + 9 : end) - at);
        }
    }

    // Words of the text with // and /* */ comments left out and quotes taken off.
    private static List<string> Tokens(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                sb.Append(' ');
            }
            else
            {
                sb.Append(text[i] == '"' ? ' ' : text[i]);
            }
        }

        return [.. sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
    }
}
