using System.Globalization;
using WadSmith.Core.Exporting;

namespace WadSmith.Core.Engines;

/// <summary>
/// An installed classic port WadSmith can play a map in (Phase 12): its name, the program, its kind (chocolate, crispy,
/// dsda, woof, nugget) and the exports it plays, best first.
/// </summary>
public sealed record PortInstall(string Name, string Program, string Kind, IReadOnlyList<string> Exports)
{
    /// <summary>The export to play in it: the project's own classic target when the port plays that, its best otherwise.</summary>
    public string ExportFor(string target) => Exports.Contains(target) ? target : Exports[0];
}

/// <summary>
/// Finds the classic ports on the PATH for a game family without running any of them: Chocolate and Crispy (one program
/// per game), dsda-doom (Doom, Heretic, Hexen), Woof and Nugget Doom (Doom). None is in Arch's official repositories
/// (the AUR has them); Debian and Ubuntu package Chocolate and Crispy.
/// </summary>
public static class PortLocator
{
    private static readonly string[] Classic = [ClassicPorts.LimitRemoving, ClassicPorts.Vanilla];
    private static readonly string[] Boom = [ClassicPorts.Mbf21, ClassicPorts.Boom, ClassicPorts.LimitRemoving, ClassicPorts.Vanilla];

    /// <summary>The game names Chocolate and Crispy put after their own (chocolate-doom, crispy-heretic …).</summary>
    private static string GameWord(string family) => family switch { "heretic" => "heretic", "hexen" => "hexen", "strife" => "strife", _ => "doom" };

    private static string Title(string family) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(GameWord(family));

    public static IReadOnlyList<PortInstall> Find(string family, Func<string, string?> env)
    {
        var path = (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        // Windows names its programs .exe; everywhere else the name is the program.
        string? OnPath(string name) => path.SelectMany(d => new[] { Path.Combine(d, name), Path.Combine(d, name + ".exe") }).FirstOrDefault(File.Exists);
        var game = GameWord(family);
        var candidates = new List<(string Program, string Name, string Kind, IReadOnlyList<string> Exports)>
        {
            ($"chocolate-{game}", $"Chocolate {Title(family)}", "chocolate", [ClassicPorts.Vanilla]),
            ($"crispy-{game}", $"Crispy {Title(family)}", "crispy", Classic),
        };
        if (family is "doom" or "heretic" or "hexen") candidates.Add(("dsda-doom", "dsda-doom", "dsda", family == "doom" ? Boom : Classic));
        if (family == "doom")
        {
            candidates.Add(("woof", "Woof!", "woof", Boom));
            candidates.Add(("nugget-doom", "Nugget Doom", "nugget", Boom));
        }

        var found = new List<PortInstall>();
        foreach (var (program, name, kind, exports) in candidates)
        {
            if (OnPath(program) is { } at) found.Add(new PortInstall(name, at, kind, exports));
        }

        return found;
    }
}

/// <summary>The command that plays an exported map in a classic port, with WadSmith's own copy of the port's settings.</summary>
public static class PortPlay
{
    /// <summary>
    /// The program and arguments for a map of game family <paramref name="family"/>: the base game, the game's add-on and other WADs, the exported file, the map by its
    /// slot (-warp E M or -warp N), the difficulty (1–5), the compatibility a Boom port is told, and the settings files.
    /// </summary>
    public static (string Program, IReadOnlyList<string> Arguments) For(PortInstall port, string exportId, string family, string iwad, IReadOnlyList<string> files, string wad, string slot, int skill, IReadOnlyList<string> settings)
    {
        var args = new List<string> { "-iwad", iwad };
        foreach (var f in files.Append(wad)) args.AddRange(["-file", f]);
        args.Add("-warp");
        args.AddRange(Warp(slot));
        args.AddRange(["-skill", Math.Clamp(skill, 1, 5).ToString(CultureInfo.InvariantCulture)]);
        var level = ClassicPorts.Get(exportId)?.Complevel;
        switch (port.Kind)
        {
            case "dsda" when family == "doom":
                // Boom and MBF21 by their own levels; vanilla as Doom II (2), or as The Ultimate Doom (3) for ExMy maps.
                args.AddRange(["-complevel", (level ?? (Warp(slot).Count == 2 ? 3 : 2)).ToString(CultureInfo.InvariantCulture)]);
                break;
            case "woof" or "nugget":
                args.AddRange(["-complevel", exportId switch { ClassicPorts.Mbf21 => "mbf21", ClassicPorts.Boom => "boom", _ => "vanilla" }]);
                break;
        }

        args.AddRange(settings);
        return (port.Program, args);
    }

    /// <summary>-warp's arguments for a slot: episode and map for ExMy, the number for MAPxx.</summary>
    public static IReadOnlyList<string> Warp(string slot)
    {
        var s = slot.ToUpperInvariant();
        if (s.Length == 4 && s[0] == 'E' && s[2] == 'M' && char.IsDigit(s[1]) && char.IsDigit(s[3])) return [s[1].ToString(), s[3].ToString()];
        if (s.StartsWith("MAP", StringComparison.Ordinal) && int.TryParse(s[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return [n.ToString(CultureInfo.InvariantCulture)];
        throw new ArgumentException($"{slot} is not a map slot a classic port can warp to.", nameof(slot));
    }

    /// <summary>
    /// The settings arguments for a port, with WadSmith's own copy of its files in <paramref name="dataFolder"/>/engine,
    /// made once from the user's (which are only read, as with GZDoom, D19): Chocolate and Crispy keep two files
    /// (-config and -extraconfig), dsda-doom, Woof and Nugget one.
    /// </summary>
    public static IReadOnlyList<string> Settings(PortInstall port, string dataFolder, Func<string, string?> env)
    {
        var program = Path.GetFileName(port.Program);
        var folder = Path.Combine(dataFolder, "engine", program);
        Directory.CreateDirectory(folder);
        var home = env("HOME") ?? "";
        var data = env("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");
        string Mine(string name, params string[] theirs)
        {
            var mine = Path.Combine(folder, name);
            if (!File.Exists(mine) && theirs.FirstOrDefault(File.Exists) is { } from) File.Copy(from, mine);
            return mine;
        }

        return port.Kind switch
        {
            "chocolate" or "crispy" => ["-config", Mine("default.cfg", Path.Combine(data, program, "default.cfg")), "-extraconfig", Mine(program + ".cfg", Path.Combine(data, program, program + ".cfg"))],
            "dsda" => ["-config", Mine("dsda-doom.cfg", Path.Combine(data, "dsda-doom", "dsda-doom.cfg"), Path.Combine(home, ".dsda-doom", "dsda-doom.cfg"))],
            _ => ["-config", Mine(program + ".cfg", Path.Combine(data, program, program + ".cfg"))],
        };
    }
}
