using WadSmith.Core;

namespace WadSmith.Editor.App;

/// <summary>
/// Where WadSmith keeps its own files: the XDG base directories (on Windows, %APPDATA% and %LOCALAPPDATA%), or one
/// folder in portable mode (<c>--portable DIR</c>), which is also how every test and smoke run keeps away from the real profile.
/// </summary>
public sealed record AppPaths(string Config, string Data, string State, string Cache, string Projects, bool Portable)
{
    public string Logs => Path.Combine(State, "logs");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    /// <summary>The asset library: base games extracted to plain files (D22).</summary>
    public string Assets => Path.Combine(Data, "assets");

    public static AppPaths Resolve(string? portableRoot, Func<string, string?> environment) => Resolve(portableRoot, environment, OperatingSystem.IsWindows());

    /// <summary>As <see cref="Resolve(string?, Func{string, string?})"/>, with whether to lay the folders out the Windows way given (a test passes it).</summary>
    public static AppPaths Resolve(string? portableRoot, Func<string, string?> environment, bool windows)
    {
        var home = environment("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(portableRoot))
        {
            var root = Path.GetFullPath(portableRoot);
            return new AppPaths(Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "state"), Path.Combine(root, "cache"), Path.Combine(root, "projects"), Portable: true);
        }

        // Windows: settings roam with the user, the rest (base games taken out, the asset library, logs, caches) stays on this computer.
        if (windows)
        {
            var roaming = WindowsFolders.RoamingAppData(environment) ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            var local = Path.Combine(WindowsFolders.LocalAppData(environment) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "WadSmith");
            return new AppPaths(Path.Combine(roaming, "WadSmith"), Path.Combine(local, "Data"), Path.Combine(local, "State"), Path.Combine(local, "Cache"), Path.Combine(WindowsFolders.Documents, "WadSmith"), Portable: false);
        }

        // A relative XDG value is invalid and is ignored, as the specification says.
        string Base(string variable, string fallback) =>
            environment(variable) is { Length: > 0 } value && Path.IsPathRooted(value) ? value : Path.Combine(home, fallback);

        return new AppPaths(
            Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "wadsmith"),
            Path.Combine(Base("XDG_DATA_HOME", Path.Combine(".local", "share")), "wadsmith"),
            Path.Combine(Base("XDG_STATE_HOME", Path.Combine(".local", "state")), "wadsmith"),
            Path.Combine(Base("XDG_CACHE_HOME", ".cache"), "wadsmith"),
            Path.Combine(DocumentsFolder(home, environment), "WadSmith"),
            Portable: false);
    }

    /// <summary>The user's documents folder as the desktop names it (xdg-user-dirs), else ~/Documents.</summary>
    private static string DocumentsFolder(string home, Func<string, string?> environment)
    {
        var configHome = environment("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathRooted(c) ? c : Path.Combine(home, ".config");
        try
        {
            var dirs = Path.Combine(configHome, "user-dirs.dirs");
            if (File.Exists(dirs))
            {
                foreach (var line in File.ReadLines(dirs))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("XDG_DOCUMENTS_DIR=", StringComparison.Ordinal)) continue;
                    var value = t["XDG_DOCUMENTS_DIR=".Length..].Trim('"').Replace("$HOME", home, StringComparison.Ordinal);
                    if (Path.IsPathRooted(value) && value.TrimEnd('/', '\\') != home.TrimEnd('/', '\\')) return value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Path.Combine(home, "Documents");
    }

    /// <summary>Creates WadSmith's own folders (not the projects folder, which is made on first save).</summary>
    public void EnsureCreated()
    {
        foreach (var d in new[] { Config, Data, State, Cache, Logs }) Directory.CreateDirectory(d);
    }
}
