namespace WadSmith.Tests;

/// <summary>
/// Locates real game data for the tests that need it. The folders come from the
/// WADSMITH_TEST_GAMES environment variable (separated by the path separator). CI points it at
/// Freedoom; a developer adds their own copies of the games. A test whose file is absent is skipped,
/// never passed.
/// </summary>
internal static class GameFiles
{
    public const string Variable = "WADSMITH_TEST_GAMES";

    private static readonly Lazy<Dictionary<string, string>> Index = new(Build);

    /// <summary>The path of a file with this name (any case) in the test folders, or null.</summary>
    public static string? Find(string fileName) => Index.Value.GetValueOrDefault(fileName.ToUpperInvariant());

    /// <summary>The path of the file, or a skip of the calling test when it is not available.</summary>
    public static string Require(string fileName)
    {
        var path = Find(fileName);
        if (path is null) Assert.Skip($"{fileName} is not in {Variable}.");
        return path;
    }

    /// <summary>Every copy of a file name across the test folders (and their sub-folders, three deep).</summary>
    public static IReadOnlyList<string> FindAll(string fileName) => AllCopies.Value.GetValueOrDefault(fileName.ToUpperInvariant()) ?? [];

    private static readonly Lazy<Dictionary<string, List<string>>> AllCopies = new(() =>
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var raw = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var folder in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
            {
                var key = Path.GetFileName(file).ToUpperInvariant();
                if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                list.Add(file);
            }
        }

        return map;
    });

    /// <summary>Every WAD found in the test folders.</summary>
    public static IReadOnlyList<string> AllWads => Index.Value.Values.Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var folder in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                map.TryAdd(Path.GetFileName(file).ToUpperInvariant(), file);
            }
        }

        return map;
    }
}

/// <summary>A test's own empty folder under the system temp directory, removed afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wadsmith-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
