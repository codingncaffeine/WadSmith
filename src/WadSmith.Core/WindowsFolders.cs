using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WadSmith.Core;

/// <summary>
/// Where Windows keeps things: the folders its variables name (read through the environment given, so a test can move
/// them; null when a variable is not set to a full path), and the registry values Steam and GOG Galaxy leave for their installs.
/// </summary>
public static class WindowsFolders
{
    /// <summary>Paths on Windows are one path whatever their letters' case.</summary>
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>%APPDATA%: settings that travel with the user.</summary>
    public static string? RoamingAppData(Func<string, string?> env) => Rooted(env("APPDATA"));

    /// <summary>%LOCALAPPDATA%: data that stays on this computer.</summary>
    public static string? LocalAppData(Func<string, string?> env) => Rooted(env("LOCALAPPDATA"));

    public static string? UserProfile(Func<string, string?> env) => Rooted(env("USERPROFILE"));

    /// <summary>Program Files and Program Files (x86), the ones set.</summary>
    public static IEnumerable<string> ProgramFiles(Func<string, string?> env) =>
        new[] { env("ProgramFiles"), env("ProgramFiles(x86)") }.Select(Rooted).OfType<string>().Distinct(PathComparer);

    /// <summary>Documents as Explorer shows it: OneDrive may have moved it, and no variable says where.</summary>
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>Where Steam is installed, as it registered itself, or null.</summary>
    public static string? SteamFolder() => OperatingSystem.IsWindows() ? RegistryValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath") : null;

    /// <summary>The folder of every game GOG Galaxy installed, as it registered them (GZDoom looks in the same places).</summary>
    public static IReadOnlyList<string> GogGameFolders() => OperatingSystem.IsWindows() ? RegistryValuesBelow(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\GOG.com\Games", "path") : [];

    [SupportedOSPlatform("windows")]
    private static string? RegistryValue(RegistryHive hive, RegistryView view, string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var sub = root.OpenSubKey(key);
            return sub?.GetValue(name) as string is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The string value <paramref name="name"/> of every key under <paramref name="key"/>, the ones set.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> RegistryValuesBelow(RegistryHive hive, RegistryView view, string key, string name)
    {
        var values = new List<string>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var parent = root.OpenSubKey(key);
            foreach (var child in parent?.GetSubKeyNames() ?? [])
            {
                using var sub = parent!.OpenSubKey(child);
                if (sub?.GetValue(name) is string { Length: > 0 } value) values.Add(value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return values;
    }

    private static string? Rooted(string? path) => path is { Length: > 0 } && Path.IsPathRooted(path) ? path : null;
}
