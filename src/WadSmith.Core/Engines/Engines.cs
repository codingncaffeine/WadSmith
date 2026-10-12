namespace WadSmith.Core.Engines;

/// <summary>An installed engine WadSmith can play maps in.</summary>
/// <param name="Name">How it is shown: "GZDoom", "UZDoom", "GZDoom (Flatpak)", "Your engine".</param>
/// <param name="Program">What is started: the engine itself, or <c>flatpak</c>.</param>
/// <param name="Kind">"native", "flatpak" or "custom".</param>
public sealed record EngineInstall(string Name, string Program, string Kind);

/// <summary>
/// Finds the engines installed (§3.2) without running any of them (an engine started to ask its version starts the game):
/// a path the user chose, GZDoom and UZDoom on the PATH, and the Flatpak GZDoom by its installed folder.
/// </summary>
public static class EngineLocator
{
    public static IReadOnlyList<EngineInstall> Find(string? custom, Func<string, string?> env)
    {
        var found = new List<EngineInstall>();
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) found.Add(new EngineInstall("Your engine", custom, "custom"));
        var path = (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string? OnPath(string name) => path.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        foreach (var (file, name) in new[] { ("gzdoom", "GZDoom"), ("uzdoom", "UZDoom") })
        {
            if (OnPath(file) is { } program && found.All(f => f.Program != program)) found.Add(new EngineInstall(name, program, "native"));
        }

        var home = env("HOME") ?? "";
        if (OnPath("flatpak") is { } flatpak && new[] { Path.Combine(home, ".local/share/flatpak/app/org.zdoom.GZDoom"), "/var/lib/flatpak/app/org.zdoom.GZDoom" }.Any(Directory.Exists))
        {
            found.Add(new EngineInstall("GZDoom (Flatpak)", flatpak, "flatpak"));
        }

        return found;
    }
}

/// <summary>
/// What to play: the base game, the files on top (the project folder, add-ons), the map, the difficulty, where the engine
/// keeps its settings and log; and, for Play From Here, where the player starts instead (with WadSmith's small add-on that
/// puts them there, <see cref="PlayFrom"/>).
/// </summary>
public sealed record PlayRequest(string Iwad, IReadOnlyList<string> Files, string Map, int Skill, string Config, string Log, (double X, double Y, double Angle)? From = null, string? FromAddOn = null);

/// <summary>
/// Play From Here (re-check #2): an add-on of WadSmith's own, written into its data folder, whose event handler stands the
/// player at the spot in the <c>wadsmith_from</c> setting ("x y angle", on the floor there) on the map's first tic, then says
/// "WADSMITH FROM x y" in the log. The setting is never saved, so a plain Play starts at the map's own start. A ZScript
/// handler works the same in GZDoom and UZDoom; the console's warp would run before the map is loaded.
/// </summary>
public static class PlayFrom
{
    public const string Setting = "wadsmith_from";

    private const string Script = """
        version "4.6"

        // Written by WadSmith for Play From Here: stands the player at the spot in wadsmith_from on the first tic.
        class WadSmithPlayFrom : StaticEventHandler
        {
        	bool done;

        	override void WorldTick()
        	{
        		if (done || level.maptime < 1) return;
        		done = true;
        		let setting = CVar.FindCVar("wadsmith_from");
        		if (!setting) return;
        		Array<String> parts;
        		setting.GetString().Split(parts, " ", TOK_SKIPEMPTY);
        		let mo = players[consoleplayer].mo;
        		if (parts.Size() < 3 || !mo) return;
        		Vector2 at = (parts[0].ToDouble(), parts[1].ToDouble());
        		mo.SetOrigin((at, level.PointInSector(at).floorplane.ZatPoint(at)), false);
        		mo.angle = parts[2].ToDouble();
        		mo.vel = (0, 0, 0);
        		Console.Printf("WADSMITH FROM %.0f %.0f", mo.pos.x, mo.pos.y);
        	}
        }
        """;

    /// <summary>Writes the add-on (again: it is small) and returns its path.</summary>
    public static string Write(string dataFolder)
    {
        var folder = Path.Combine(dataFolder, "engine");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "wadsmith-play.pk3");
        var temp = path + ".tmp";
        using (var zip = System.IO.Compression.ZipFile.Open(temp, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text.Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            Add("zscript.txt", Script);
            Add("cvarinfo.txt", "server noarchive string wadsmith_from = \"\";\n");
            Add("mapinfo.txt", "GameInfo\n{\n\tAddEventHandlers = \"WadSmithPlayFrom\"\n}\n");
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }
}

/// <summary>The engine's command line for a Play (§4.5).</summary>
public static class PlayCommand
{
    public const string FlatpakApp = "org.zdoom.GZDoom";

    public static (string Program, IReadOnlyList<string> Arguments) For(EngineInstall engine, PlayRequest r)
    {
        var args = new List<string>();
        if (engine.Kind == "flatpak")
        {
            // The sandbox sees only the folders it is given: the game's, the project's, and WadSmith's settings and log.
            args.Add("run");
            foreach (var dir in new[] { r.Iwad, r.Config, r.Log, r.FromAddOn }.OfType<string>().Select(Path.GetDirectoryName).Concat(r.Files).OfType<string>().Distinct(StringComparer.Ordinal)) args.Add($"--filesystem={dir}");
            args.Add(FlatpakApp);
        }

        args.AddRange(["-iwad", r.Iwad]);
        foreach (var f in r.Files) args.AddRange(["-file", f]);
        if (r.From is { } from && r.FromAddOn is { } addOn)
        {
            args.AddRange(["-file", addOn, "+set", PlayFrom.Setting, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{from.X:0.##} {from.Y:0.##} {from.Angle:0.##}")]);
        }

        args.AddRange(["+map", r.Map, "-skill", r.Skill.ToString(System.Globalization.CultureInfo.InvariantCulture), "-config", r.Config, "+logfile", r.Log]);
        return (engine.Program, args);
    }
}

/// <summary>
/// The engine settings WadSmith plays with: its own file, made once as a copy of the user's GZDoom settings so their
/// controls and screen carry over; the user's own file is only ever read (D19).
/// </summary>
public static class EngineConfig
{
    /// <summary>The settings file to pass with <c>-config</c>, copying the user's the first time.</summary>
    public static string Prepare(string dataFolder, Func<string, string?> env)
    {
        var folder = Path.Combine(dataFolder, "engine");
        Directory.CreateDirectory(folder);
        var mine = Path.Combine(folder, "gzdoom.ini");
        if (File.Exists(mine)) return mine;
        var home = env("HOME") ?? "";
        var theirs = new[] { Path.Combine(home, ".config/gzdoom/gzdoom.ini"), Path.Combine(home, ".var/app", PlayCommand.FlatpakApp, ".config/gzdoom/gzdoom.ini") }.FirstOrDefault(File.Exists);
        if (theirs is not null) File.Copy(theirs, mine);
        return mine;
    }
}

/// <summary>A Play's request from a project: its base game (an add-on loads over it), its folder on top, its map.</summary>
public static class PlayRequests
{
    /// <summary>The request, or null and why when the project's game is not among <paramref name="games"/>.</summary>
    public static (PlayRequest? Request, string? Why) For(Projects.Project project, string slot, IReadOnlyList<Games.FoundGame> games, string config, string log, int skill)
    {
        var game = games.FirstOrDefault(g => g.Game.Id == project.Info.Game);
        if (game is null) return (null, $"The project's game ({project.Info.Game}) was not found on this computer; add its folder in Preferences.");
        var files = new List<string>();
        var iwad = game.Path;
        if (game.Game.AddOnFor is { } baseId)
        {
            if (games.FirstOrDefault(g => g.Game.Id == baseId) is not { } baseGame) return (null, $"{game.Game.Title} plays over another game that was not found.");
            iwad = baseGame.Path;
            files.Add(game.Path);
        }

        files.Add(Path.GetFullPath(project.Folder));
        return (new PlayRequest(iwad, files, slot, Math.Clamp(skill, 1, 5), config, log), null);
    }
}
