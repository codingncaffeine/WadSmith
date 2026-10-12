using System.Reflection;

namespace WadSmith.Core;

/// <summary>The files under the repository's <c>data/</c> folder, embedded in this assembly.</summary>
public static class DataFiles
{
    private static readonly Assembly Self = typeof(DataFiles).Assembly;

    /// <summary>Every embedded data path, such as <c>games/games.json</c>.</summary>
    public static IEnumerable<string> All => Self.GetManifestResourceNames().Where(n => n.StartsWith("data/", StringComparison.Ordinal)).Select(n => n["data/".Length..]);

    public static string ReadText(string path)
    {
        using var s = Self.GetManifestResourceStream("data/" + path) ?? throw new FileNotFoundException($"The data file {path} is not built into WadSmith.Core.", path);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
