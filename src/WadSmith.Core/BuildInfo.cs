using System.Reflection;
using System.Runtime.InteropServices;

namespace WadSmith.Core;

/// <summary>Which build is running, for logs, bug reports and <c>--version</c>.</summary>
public static class BuildInfo
{
    /// <summary>The version with the source revision the SDK appends, e.g. <c>0.0.1+1a2b3c4…</c>.</summary>
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The version, the operating system and the runtime identifier on one line.</summary>
    public static string Stamp => $"{Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})";

    public const string Licence = """
        Copyright (C) 2026 WadSmith contributors
        License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>.
        This is free software: you are free to change and redistribute it.
        There is NO WARRANTY, to the extent permitted by law.
        """;
}
