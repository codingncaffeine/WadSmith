namespace WadSmith.Core.Engines;

/// <summary>An installed engine WadSmith can play maps in.</summary>
/// <param name="Name">How it is shown: "UZDoom", "GZDoom", "UZDoom (Flatpak)", "UZDoom (AppImage)", "Your engine".</param>
/// <param name="Program">What is started: the engine itself (an AppImage is one file), or <c>flatpak</c>.</param>
/// <param name="Kind">"native", "flatpak", "appimage" or "custom" (a file the user chose).</param>
/// <param name="App">A Flatpak's application ID: org.zdoom.UZDoom.</param>
/// <param name="Location">What stands for a Flatpak when it is chosen: its exported command.</param>
public sealed record EngineInstall(string Name, string Program, string Kind, string? App = null, string? Location = null)
{
    /// <summary>The path Preferences keeps when this engine is chosen: the engine itself, or a Flatpak's exported command.</summary>
    public string Choice => Location ?? Program;

    /// <summary>Whose data file and settings the engine uses, "uzdoom" or "gzdoom"; an engine of any other name counts as GZDoom.</summary>
    public string Family => App == EngineLocator.UZDoomApp || Name.StartsWith("UZDoom", StringComparison.Ordinal)
        || Path.GetFileName(Choice).Contains("uzdoom", StringComparison.OrdinalIgnoreCase) ? "uzdoom" : "gzdoom";
}

/// <summary>
/// Finds the engines installed (§3.2) without running any of them (an engine started to ask its version starts the game).
/// UZDoom comes before GZDoom, which is no longer developed (D64): each where its packages put it — on the PATH, or in the
/// folders packages use when the PATH leaves them out (Debian's /usr/games); as a Flatpak, a user install before the
/// system's; as an AppImage in the folders people keep them in; and on Windows, where it is a folder unpacked anywhere,
/// in Program Files, the user's Programs, winget's, Scoop's and Chocolatey's folders, and Downloads, the desktop or a
/// Games folder (or a folder there named after it). A path the user chose comes first.
/// </summary>
public static class EngineLocator
{
    public const string UZDoomApp = "org.zdoom.UZDoom";

    public const string GZDoomApp = "org.zdoom.GZDoom";

    /// <summary>What to do when no engine is found, in words for the user: where UZDoom is to be had on this system, and Preferences for one installed elsewhere.</summary>
    public static string NoEngine => OperatingSystem.IsWindows()
        ? "No game engine was found to play in. Download UZDoom from its releases on GitHub (or: winget install UZDoom.UZDoom) and unzip it; if WadSmith does not find it, choose its uzdoom.exe in Preferences, under Engine. GZDoom works too."
        : "No game engine was found to play in. Install UZDoom: from Flathub (org.zdoom.UZDoom), as the AppImage from its releases on GitHub, or on Arch as uzdoom-bin from the AUR. GZDoom works too, and one installed where WadSmith does not look can be chosen in Preferences, under Engine.";

    /// <summary>The engines WadSmith knows, the one it prefers first: the program's name, the name shown, the Flatpak's ID.</summary>
    private static readonly (string File, string Name, string App)[] Known = [("uzdoom", "UZDoom", UZDoomApp), ("gzdoom", "GZDoom", GZDoomApp)];

    /// <summary>
    /// The engines installed, the one Play uses first: <paramref name="custom"/> when the user chose one that is still there
    /// (named as found, or after its file), then UZDoom's installs, then GZDoom's.
    /// </summary>
    public static IReadOnlyList<EngineInstall> Find(string? custom, Func<string, string?> env)
    {
        var found = Installed(env);
        if (string.IsNullOrWhiteSpace(custom)) return found;
        var chosen = Chosen(custom, found, env);
        return chosen is null ? found : [chosen, .. found.Where(f => f != chosen)];
    }

    /// <summary>The engine at <paramref name="custom"/>: one of <paramref name="found"/> when it is that file (keeping its name), else as <see cref="Describe"/> makes it; null when nothing is there.</summary>
    public static EngineInstall? Chosen(string custom, IReadOnlyList<EngineInstall> found, Func<string, string?> env) =>
        found.FirstOrDefault(f => SameFile(f.Choice, custom)) ?? Describe(custom, env);

    /// <summary>
    /// The engine at a path the user chose, or null when nothing is there: a Flatpak's exported command (or its folder) is
    /// that Flatpak, an AppImage is one, and anything else is started as it is, named after its file when that names an engine.
    /// </summary>
    public static EngineInstall? Describe(string path, Func<string, string?> env)
    {
        if (!OperatingSystem.IsWindows() && FlatpakApp(path) is { } app && (File.Exists(path) || Directory.Exists(path)) && OnPath("flatpak", env) is { } flatpak)
        {
            return new EngineInstall($"{Known.FirstOrDefault(k => k.App == app).Name ?? app} (Flatpak)", flatpak, "flatpak", app, path);
        }

        if (!File.Exists(path)) return null;
        var known = Known.FirstOrDefault(k => Path.GetFileName(path).Contains(k.File, StringComparison.OrdinalIgnoreCase)).Name ?? "Your engine";
        return AppImage.IsAppImage(path) ? new EngineInstall($"{known} (AppImage)", path, "appimage") : new EngineInstall(known, path, "custom");
    }

    /// <summary>
    /// The folders the engines found (and the one the user chose) are in, a link followed to the engine itself: on Windows
    /// GZDoom keeps its data file there, and players put their games beside it.
    /// </summary>
    public static IReadOnlyList<string> Folders(string? custom, Func<string, string?> env) =>
        [.. Find(custom, env).Where(e => e.Kind != "flatpak").Select(e => Path.GetDirectoryName(RealPath(e.Program))).OfType<string>().Distinct(WindowsFolders.PathComparer)];

    /// <summary>Where Flatpaks are installed: the user's (under XDG_DATA_HOME) and the system's, or where flatpak's own variables move them.</summary>
    public static IEnumerable<string> FlatpakRoots(Func<string, string?> env)
    {
        var home = env("HOME") ?? "";
        yield return env("FLATPAK_USER_DIR") is { Length: > 0 } user && Path.IsPathRooted(user) ? user
            : Path.Combine(env("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathRooted(d) ? d : Path.Combine(home, ".local", "share"), "flatpak");
        yield return env("FLATPAK_SYSTEM_DIR") is { Length: > 0 } system && Path.IsPathRooted(system) ? system : "/var/lib/flatpak";
    }

    /// <summary>The engines installed, UZDoom's before GZDoom's, each found once however many ways lead to it.</summary>
    private static List<EngineInstall> Installed(Func<string, string?> env)
    {
        var windows = OperatingSystem.IsWindows();
        var found = new List<EngineInstall>();
        void Add(EngineInstall engine)
        {
            if (found.All(f => !SameFile(f.Choice, engine.Choice))) found.Add(engine);
        }

        // Where packages put engines when the PATH leaves those folders out: Debian's games folder, the official .deb's.
        string[] alsoOnLinux = ["/usr/games", "/usr/local/games", "/usr/local/bin", "/usr/bin"];
        foreach (var (file, name, app) in Known)
        {
            // Installers' and package managers' folders before the PATH: what Scoop and Chocolatey put on the PATH is a
            // stand-in program, away from the engine's files.
            if (windows) foreach (var program in WindowsPlaces(file, name, env).Where(File.Exists)) Add(new EngineInstall(name, program, "native"));
            var folders = PathFolders(env).Concat(windows ? [] : [.. alsoOnLinux, Path.Combine("/opt", file)]);
            if (folders.Select(f => Path.Combine(f, windows ? file + ".exe" : file)).FirstOrDefault(File.Exists) is { } onPath && !(windows && found.Any(f => f.Name == name) && StandIn(onPath)))
            {
                Add(new EngineInstall(name, onPath, "native"));
            }

            if (windows) continue;
            if (Flatpak(app, env) is { } exported && OnPath("flatpak", env) is { } flatpak) Add(new EngineInstall($"{name} (Flatpak)", flatpak, "flatpak", app, exported));
            foreach (var image in AppImages(file, env)) Add(new EngineInstall($"{name} (AppImage)", image, "appimage"));
        }

        return found;
    }

    /// <summary>The installed Flatpak's exported command (or its folder when it exports none), a user install first; null when it is not installed.</summary>
    private static string? Flatpak(string app, Func<string, string?> env)
    {
        foreach (var root in FlatpakRoots(env))
        {
            if (!Directory.Exists(Path.Combine(root, "app", app, "current", "active", "files"))) continue;
            var exported = Path.Combine(root, "exports", "bin", app);
            return File.Exists(exported) ? exported : Path.Combine(root, "app", app);
        }

        return null;
    }

    /// <summary>The Flatpak a path stands for: an exported command (…/exports/bin/ID) or an installed app's folder (…/flatpak/app/ID).</summary>
    private static string? FlatpakApp(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var id = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        if (!id.Contains('.') || parent is null) return null;
        var above = Path.GetFileName(Path.GetDirectoryName(parent));
        return (Path.GetFileName(parent), above) is ("bin", "exports") or ("app", "flatpak") ? id : null;
    }

    /// <summary>
    /// The engine's AppImages that can be started (one never started is not yet a program: choosing it in Preferences
    /// makes it one), newest first: in the home folder's Applications and AppImages (where AppImageLauncher and Gear Lever
    /// keep them), ~/.local/bin, ~/bin and Games, and the downloads folder and the desktop the user's folder settings name.
    /// </summary>
    private static IEnumerable<string> AppImages(string file, Func<string, string?> env)
    {
        if (env("HOME") is not { Length: > 0 } home) return [];
        var folders = new[] { "Applications", "AppImages", ".local/bin", "bin", "Games", "Downloads", "Desktop" }.Select(f => Path.Combine(home, f))
            .Concat(UserFolders(home, env, "XDG_DOWNLOAD_DIR", "XDG_DESKTOP_DIR")).Distinct(StringComparer.Ordinal);
        var images = new List<FileInfo>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                images.AddRange(new DirectoryInfo(folder).EnumerateFiles("*").Where(f => f.Name.Contains(file, StringComparison.OrdinalIgnoreCase)
                    && f.Name.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase) && (f.UnixFileMode & UnixFileMode.UserExecute) != 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return images.OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName);
    }

    /// <summary>The folders the user's folder settings (user-dirs.dirs) give <paramref name="keys"/>: a Downloads folder in their language.</summary>
    private static IEnumerable<string> UserFolders(string home, Func<string, string?> env, params string[] keys)
    {
        var config = env("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathRooted(c) ? c : Path.Combine(home, ".config");
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(config, "user-dirs.dirs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var at = line.IndexOf('=', StringComparison.Ordinal);
            if (at < 0 || !keys.Contains(line[..at].Trim(), StringComparer.Ordinal)) continue;
            var value = line[(at + 1)..].Trim().Trim('"').Replace("$HOME", home, StringComparison.Ordinal);
            if (Path.IsPathRooted(value)) yield return value;
        }
    }

    /// <summary>
    /// Where a Windows engine is put: by its installer (Program Files, the user's Programs, where Chocolatey's GZDoom goes), by
    /// winget (its package folder and the link it puts on the PATH), by Scoop (the user's and the global one), and by hand
    /// into Downloads, the desktop or a Games folder, or into a folder there named after it (Explorer's "Extract all" names
    /// the folder after the zip: Windows-UZDoom-Release-x86_64).
    /// </summary>
    private static IEnumerable<string> WindowsPlaces(string file, string name, Func<string, string?> env)
    {
        var exe = file + ".exe";
        foreach (var programs in WindowsFolders.ProgramFiles(env)) yield return Path.Combine(programs, name, exe);
        var profile = WindowsFolders.UserProfile(env);
        if (WindowsFolders.LocalAppData(env) is { } local)
        {
            yield return Path.Combine(local, "Programs", name, exe);
            var winget = Path.Combine(local, "Microsoft", "WinGet");
            foreach (var package in Subfolders(Path.Combine(winget, "Packages"), $"{name}.{name}_*")) yield return Path.Combine(package, exe);
            yield return Path.Combine(winget, "Links", exe);
        }

        var scoops = new[] { env("SCOOP") ?? (profile is null ? null : Path.Combine(profile, "scoop")), env("SCOOP_GLOBAL") ?? (env("ProgramData") is { Length: > 0 } data ? Path.Combine(data, "scoop") : null) };
        foreach (var scoop in scoops.OfType<string>()) yield return Path.Combine(scoop, "apps", file, "current", exe);

        var byHand = new[] { profile is null ? null : Path.Combine(profile, "Downloads"), profile is null ? null : Path.Combine(profile, "Desktop"), profile is null ? null : Path.Combine(profile, "Games"), env("SystemDrive") is { Length: > 0 } drive ? Path.Combine(drive + Path.DirectorySeparatorChar, "Games") : null };
        foreach (var folder in byHand.OfType<string>())
        {
            yield return Path.Combine(folder, exe);
            foreach (var unpacked in Subfolders(folder, $"*{name}*")) yield return Path.Combine(unpacked, exe);
        }
    }

    /// <summary>The folders in <paramref name="folder"/> whose names match <paramref name="pattern"/>; none when it cannot be read.</summary>
    private static IEnumerable<string> Subfolders(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A program on the PATH that only starts the engine from elsewhere: Scoop's shims, Chocolatey's.</summary>
    private static bool StandIn(string program)
    {
        var folder = Path.GetDirectoryName(program);
        return Path.GetFileName(folder) is { } name && (name.Equals("shims", StringComparison.OrdinalIgnoreCase)
            || (name.Equals("bin", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(folder)) is { } above && above.Equals("chocolatey", StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> PathFolders(Func<string, string?> env) => (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    private static string? OnPath(string name, Func<string, string?> env) =>
        PathFolders(env).Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? name + ".exe" : name)).FirstOrDefault(File.Exists);

    /// <summary>Whether two paths are the same file: written alike, or one a link to the other.</summary>
    private static bool SameFile(string a, string b) =>
        WindowsFolders.PathComparer.Equals(a, b) || WindowsFolders.PathComparer.Equals(RealPath(a), RealPath(b));

    private static string RealPath(string file)
    {
        try
        {
            return new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return file;
        }
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

    /// <summary>
    /// Writes the add-on and returns its path. The same add-on is the same bytes, and a file that already holds them is
    /// left alone: a game still running from the last Play has it open, and Windows will not replace an open file.
    /// </summary>
    public static string Write(string dataFolder)
    {
        var folder = Path.Combine(dataFolder, "engine");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "wadsmith-play.pk3");
        var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var w = new StreamWriter(entry.Open());
                w.Write(text.Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            Add("zscript.txt", Script);
            Add("cvarinfo.txt", "server noarchive string wadsmith_from = \"\";\n");
            Add("mapinfo.txt", "GameInfo\n{\n\tAddEventHandlers = \"WadSmithPlayFrom\"\n}\n");
        }

        var data = bytes.ToArray();
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return path;
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
        return path;
    }
}

/// <summary>
/// The command line for a Play (§4.5). The engine is started in <see cref="EngineLogFile.Folder"/>, the log's folder, so the
/// plain name it is given for its log lands there.
/// </summary>
public static class PlayCommand
{
    public static (string Program, IReadOnlyList<string> Arguments) For(EngineInstall engine, PlayRequest r)
    {
        var args = new List<string>();
        if (engine.Kind == "flatpak")
        {
            // The sandbox sees only the folders it is given: the game's, the project's, and WadSmith's settings and log;
            // it works in the log's folder, as the engine does anywhere else.
            args.Add("run");
            foreach (var dir in new[] { r.Iwad, r.Config, r.Log, r.FromAddOn }.OfType<string>().Select(Path.GetDirectoryName).Concat(r.Files).OfType<string>().Distinct(StringComparer.Ordinal)) args.Add($"--filesystem={dir}");
            args.Add($"--cwd={EngineLogFile.Folder(r.Log)}");
            args.Add(engine.App ?? EngineLocator.GZDoomApp);
        }

        args.AddRange(["-iwad", r.Iwad]);
        foreach (var f in r.Files) args.AddRange(["-file", f]);
        if (r.From is { } from && r.FromAddOn is { } addOn)
        {
            args.AddRange(["-file", addOn, "+set", PlayFrom.Setting, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{from.X:0.##} {from.Y:0.##} {from.Angle:0.##}")]);
        }

        args.AddRange(["+map", r.Map, "-skill", r.Skill.ToString(System.Globalization.CultureInfo.InvariantCulture), "-config", r.Config, "+logfile", EngineLogFile.Argument(r.Log)]);
        return (engine.Program, args);
    }
}

/// <summary>
/// The engine's log, which WadSmith reads back after a Play (measured 2026-10-14). GZDoom 4 opens the file +logfile names
/// as it is; UZDoom 5 writes log-NAME.txt in its working folder, a dot or a colon in NAME made a dash, and a whole path
/// there fails ("Could not start log"). So the engine is started in the log's folder and given a plain name, and the log
/// is whichever file it wrote.
/// </summary>
public static class EngineLogFile
{
    /// <summary>What follows +logfile for <paramref name="log"/>: its name, without folder, extension, dot or colon.</summary>
    public static string Argument(string log) => Path.GetFileNameWithoutExtension(log).Replace('.', '-').Replace(':', '-');

    /// <summary>The folder the engine is started in, so the plain name lands beside <paramref name="log"/>.</summary>
    public static string Folder(string log) => Path.GetDirectoryName(Path.GetFullPath(log)) ?? Path.GetFullPath(".");

    /// <summary>The files an engine may write for <paramref name="log"/>: GZDoom's (the name as given), UZDoom's (log-NAME.txt), and the log itself.</summary>
    public static IEnumerable<string> Files(string log)
    {
        var folder = Folder(log);
        var name = Argument(log);
        return [Path.Combine(folder, name), Path.Combine(folder, $"log-{name}.txt"), Path.GetFullPath(log)];
    }

    /// <summary>The log the engine wrote, the newest of <see cref="Files"/>; null when it wrote none.</summary>
    public static string? Written(string log) => Files(log).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    /// <summary>Removes what an earlier run wrote, so the log read afterwards is this run's.</summary>
    public static void Clear(string log)
    {
        foreach (var file in Files(log).Where(File.Exists)) File.Delete(file);
    }
}

/// <summary>
/// The engine settings WadSmith plays with: its own file for each engine (UZDoom's and GZDoom's settings stay apart, so
/// neither reads what the other wrote), made once as a copy of the user's settings for that engine, else for the other
/// (the same kind of file), so their controls and screen carry over; the user's own files are only ever read (D19).
/// </summary>
public static class EngineConfig
{
    /// <summary>
    /// The settings file to pass with <c>-config</c> when <paramref name="engine"/> plays (GZDoom's when none is named),
    /// copying the user's the first time; a Windows engine may keep its settings beside itself.
    /// </summary>
    public static string Prepare(string dataFolder, Func<string, string?> env, EngineInstall? engine = null)
    {
        var folder = Path.Combine(dataFolder, "engine");
        Directory.CreateDirectory(folder);
        var family = engine?.Family ?? "gzdoom";
        var mine = Path.Combine(folder, family + ".ini");
        if (File.Exists(mine)) return mine;
        var engineFolders = OperatingSystem.IsWindows() ? EngineLocator.Folders(engine?.Choice, env) : [];
        var theirs = UserSettings(env, engineFolders).OrderBy(s => s.Family == family ? 0 : 1).Select(s => s.File).FirstOrDefault(File.Exists);
        if (theirs is not null) File.Copy(theirs, mine);
        return mine;
    }

    /// <summary>
    /// The user's own settings files, each with whose they are, in the order each engine reads them: UZDoom keeps its
    /// settings under XDG_CONFIG_HOME (its Flatpak, in the app's own config folder), GZDoom always under ~/.config (its
    /// Flatpak, under the app's folder as its home); on Windows, beside the engine or in Documents\My Games.
    /// </summary>
    public static IEnumerable<(string File, string Family)> UserSettings(Func<string, string?> env, IReadOnlyList<string> engineFolders)
    {
        var home = env("HOME") ?? "";
        var config = env("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathRooted(c) ? c : Path.Combine(home, ".config");
        yield return (Path.Combine(config, "uzdoom", "uzdoom.ini"), "uzdoom");
        yield return (Path.Combine(home, ".var/app", EngineLocator.UZDoomApp, "config/uzdoom/uzdoom.ini"), "uzdoom");
        yield return (Path.Combine(home, ".config/gzdoom/gzdoom.ini"), "gzdoom");
        yield return (Path.Combine(home, ".var/app", EngineLocator.GZDoomApp, ".config/gzdoom/gzdoom.ini"), "gzdoom");
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var (file, _) in Games.GameLocator.WindowsEngineSettings(env, engineFolders))
        {
            yield return (file, Path.GetFileName(file).StartsWith("uzdoom", StringComparison.OrdinalIgnoreCase) ? "uzdoom" : "gzdoom");
        }
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

        // The project's add-ons (a map pack it was started from, the user's own files and folders) load before the project, as in the editor.
        foreach (var addOn in project.Info.AddOns)
        {
            if (File.Exists(addOn) || Directory.Exists(addOn)) files.Add(Path.GetFullPath(addOn));
        }

        files.Add(Path.GetFullPath(project.Folder));
        return (new PlayRequest(iwad, files, slot, Math.Clamp(skill, 1, 5), config, log), null);
    }
}
