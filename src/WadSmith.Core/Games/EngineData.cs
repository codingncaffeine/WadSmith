using WadSmith.Core.Engines;

namespace WadSmith.Core.Games;

/// <summary>
/// Where an installed engine keeps its own data file (uzdoom.pk3, gzdoom.pk3), which holds the translators and the object
/// classes WadSmith reads: without it the Objects shelf has no monsters or items.
/// </summary>
public static class EngineData
{
    /// <summary>
    /// The distributions' install locations, UZDoom's first (D64): its own install folder (CMake's default, where the AUR's
    /// uzdoom-bin puts it too), the AUR's uzdoom, a build from source; then GZDoom's: Arch's folder, the games folder Debian
    /// and a build from source use, the official .deb's folder in /opt.
    /// </summary>
    private static readonly string[] Candidates =
    [
        "/usr/share/games/uzdoom/uzdoom.pk3",
        "/usr/share/uzdoom/uzdoom.pk3",
        "/usr/local/share/games/uzdoom/uzdoom.pk3",
        "/usr/share/games/doom/uzdoom.pk3",
        "/opt/uzdoom/uzdoom.pk3",
        "/usr/share/gzdoom/gzdoom.pk3",
        "/usr/share/games/gzdoom/gzdoom.pk3",
        "/usr/share/games/doom/gzdoom.pk3",
        "/usr/local/share/gzdoom/gzdoom.pk3",
        "/usr/local/share/games/doom/gzdoom.pk3",
        "/usr/lib/gzdoom/gzdoom.pk3",
        "/opt/gzdoom/gzdoom.pk3",
    ];

    private static readonly string[] Names = ["uzdoom.pk3", "gzdoom.pk3", "UZDOOM.PK3", "GZDOOM.PK3"];

    /// <summary>How long an AppImage's runtime may take to hand its data file over.</summary>
    private static readonly TimeSpan Unpacking = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The data file of the engines installed (the first that has one) or, failing that, the first found in the usual
    /// install locations, the Flatpaks' files, <paramref name="alsoIn"/> (the user's game folders and the chosen engine's
    /// folder: a portable or Windows engine keeps it beside its games) and on Windows beside the engines found there.
    /// </summary>
    public static string? Find(IEnumerable<string>? alsoIn = null, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return For(EngineLocator.Find(null, env), alsoIn, env, Candidates, null);
    }

    /// <summary>As <see cref="Find(IEnumerable{string}?, Func{string, string?}?)"/> with no engine found, the install locations given (a test passes none).</summary>
    public static string? Find(IEnumerable<string>? alsoIn, Func<string, string?> env, IReadOnlyList<string> installLocations) =>
        For([], alsoIn, env, installLocations, null);

    /// <summary>
    /// The data file to read: that of <paramref name="engines"/> in order (the one that plays first, so the shelves show
    /// what it knows; an AppImage's is taken out into <paramref name="extractTo"/>, none when that is null), then as
    /// <see cref="Find(IEnumerable{string}?, Func{string, string?}?)"/> looks. Runs an AppImage's runtime the first time: call it off the UI thread.
    /// </summary>
    public static string? For(IReadOnlyList<EngineInstall> engines, IEnumerable<string>? alsoIn, Func<string, string?> env, string? extractTo) =>
        For(engines, alsoIn, env, Candidates, extractTo);

    private static string? For(IReadOnlyList<EngineInstall> engines, IEnumerable<string>? alsoIn, Func<string, string?> env, IReadOnlyList<string> installLocations, string? extractTo)
    {
        foreach (var engine in engines)
        {
            if (Own(engine, env, extractTo) is { } own) return own;
        }

        return installLocations.FirstOrDefault(File.Exists)
            ?? FlatpakFolders(env).Concat(alsoIn ?? []).Concat(OperatingSystem.IsWindows() ? EngineLocator.Folders(null, env) : [])
                .Where(f => !string.IsNullOrEmpty(f)).SelectMany(f => Names.Select(n => Path.Combine(f, n))).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// An engine's own data file: in its Flatpak's files, inside its AppImage (taken out into <paramref name="extractTo"/>),
    /// beside it (Windows, a portable build, a build from source), or in the share folder of the package it came with
    /// (/usr/bin/uzdoom keeps it in /usr/share/games/uzdoom); the link to the engine followed too.
    /// </summary>
    private static string? Own(EngineInstall engine, Func<string, string?> env, string? extractTo)
    {
        string[] families = engine.Family == "uzdoom" ? ["uzdoom"] : engine.Name.StartsWith("GZDoom", StringComparison.Ordinal) ? ["gzdoom"] : ["gzdoom", "uzdoom"];
        if (engine.Kind == "appimage") return extractTo is null ? null : AppImage.DataFile(engine.Program, families[0], extractTo, Unpacking);
        var places = new List<string>();
        if (engine.Kind == "flatpak" && engine.App is { } app)
        {
            foreach (var root in EngineLocator.FlatpakRoots(env))
            {
                var share = Path.Combine(root, "app", app, "current", "active", "files", "share");
                foreach (var f in families) places.AddRange([Path.Combine(share, "games", f, f + ".pk3"), Path.Combine(share, "games", "doom", f + ".pk3"), Path.Combine(share, f, f + ".pk3")]);
            }
        }
        else
        {
            foreach (var program in new[] { engine.Program, RealPath(engine.Program) }.Distinct(StringComparer.Ordinal))
            {
                if (Path.GetDirectoryName(program) is not { } folder) continue;
                var prefix = Path.GetFileName(folder) is "bin" or "games" ? Path.GetDirectoryName(folder) : null;
                foreach (var f in families)
                {
                    places.Add(Path.Combine(folder, f + ".pk3"));
                    if (prefix is not null) places.AddRange([Path.Combine(prefix, "share", "games", f, f + ".pk3"), Path.Combine(prefix, "share", f, f + ".pk3"), Path.Combine(prefix, "share", "games", "doom", f + ".pk3")]);
                }
            }
        }

        return places.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Where the Flatpaks keep their data (a user or a system install), UZDoom's first: under each Flatpak's prefix, in the
    /// games folder the engine installs to on Linux (share/games/uzdoom for UZDoom, share/games/doom for GZDoom), and its
    /// own folder, should the package move it.
    /// </summary>
    public static IEnumerable<string> FlatpakFolders(Func<string, string?> env)
    {
        foreach (var (app, family) in new[] { (EngineLocator.UZDoomApp, "uzdoom"), (EngineLocator.GZDoomApp, "gzdoom") })
        {
            foreach (var root in EngineLocator.FlatpakRoots(env))
            {
                var share = Path.Combine(root, "app", app, "current", "active", "files", "share");
                yield return Path.Combine(share, "games", family);
                yield return Path.Combine(share, "games", "doom");
                yield return Path.Combine(share, family);
            }
        }
    }

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
