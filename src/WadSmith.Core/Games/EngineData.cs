namespace WadSmith.Core.Games;

/// <summary>
/// Where an installed GZDoom keeps its own data file (gzdoom.pk3), which holds the translators and the object classes
/// WadSmith reads: without it the Objects shelf has no monsters or items.
/// </summary>
public static class EngineData
{
    /// <summary>The distributions' install locations: Arch's own folder, the games folder Debian and a build from source use, a package in /opt.</summary>
    private static readonly string[] Candidates =
    [
        "/usr/share/gzdoom/gzdoom.pk3",
        "/usr/share/games/gzdoom/gzdoom.pk3",
        "/usr/share/games/doom/gzdoom.pk3",
        "/usr/local/share/gzdoom/gzdoom.pk3",
        "/usr/local/share/games/doom/gzdoom.pk3",
        "/usr/lib/gzdoom/gzdoom.pk3",
        "/opt/gzdoom/gzdoom.pk3",
        "/usr/share/uzdoom/uzdoom.pk3",
        "/usr/share/games/uzdoom/uzdoom.pk3",
        "/usr/share/games/doom/uzdoom.pk3",
    ];

    private static readonly string[] Names = ["gzdoom.pk3", "uzdoom.pk3", "GZDOOM.PK3", "UZDOOM.PK3"];

    /// <summary>
    /// The first engine data file found: in the usual install locations, then in the Flatpak GZDoom's files (a system or
    /// a user install), then in <paramref name="alsoIn"/> (the user's game folders and the chosen engine's folder: a
    /// portable or Windows GZDoom keeps it beside its games), then on Windows beside the engines found there, or null.
    /// </summary>
    public static string? Find(IEnumerable<string>? alsoIn = null, Func<string, string?>? env = null) =>
        Find(alsoIn, env ?? Environment.GetEnvironmentVariable, Candidates);

    /// <summary>As <see cref="Find(IEnumerable{string}?, Func{string, string?}?)"/>, with the install locations given (a test passes none).</summary>
    public static string? Find(IEnumerable<string>? alsoIn, Func<string, string?> env, IReadOnlyList<string> installLocations) =>
        installLocations.FirstOrDefault(File.Exists)
        ?? FlatpakFolders(env).Concat(alsoIn ?? []).Concat(OperatingSystem.IsWindows() ? Engines.EngineLocator.Folders(null, env) : [])
            .Where(f => !string.IsNullOrEmpty(f)).SelectMany(f => Names.Select(n => Path.Combine(f, n))).FirstOrDefault(File.Exists);

    /// <summary>
    /// Where the Flatpak GZDoom (org.zdoom.GZDoom, a system or a user install) keeps its data: under the Flatpak's prefix,
    /// in the games folder GZDoom installs to on Linux (and its own folder, should the package move it).
    /// </summary>
    public static IEnumerable<string> FlatpakFolders(Func<string, string?> env)
    {
        var home = env("HOME") ?? "";
        var userData = env("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathRooted(d) ? d : Path.Combine(home, ".local", "share");
        foreach (var root in new[] { "/var/lib/flatpak", Path.Combine(userData, "flatpak") })
        {
            var files = Path.Combine(root, "app", "org.zdoom.GZDoom", "current", "active", "files");
            yield return Path.Combine(files, "share", "games", "doom");
            yield return Path.Combine(files, "share", "gzdoom");
        }
    }
}
