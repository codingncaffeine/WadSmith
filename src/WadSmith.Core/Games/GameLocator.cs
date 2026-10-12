using System.Text.RegularExpressions;
using WadSmith.Core.Containers;

namespace WadSmith.Core.Games;

/// <summary>A base game found on this computer.</summary>
public sealed record FoundGame(GameDefinition Game, string Path, long Size);

/// <summary>
/// Finds the base games installed on this computer, judging every candidate by its lumps: Steam and
/// GOG (Heroic, Lutris, Bottles) installs, the folders Linux packages and source ports use, GZDoom's
/// own folders, and the folders the user added.
/// </summary>
public static partial class GameLocator
{
    /// <summary>The folders worth looking in on this computer, existing ones only.</summary>
    public static IReadOnlyList<string> StandardFolders(Func<string, string?> environment)
    {
        var home = environment("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        var folders = new List<string>
        {
            "/usr/share/games/doom", "/usr/share/doom", "/usr/local/share/games/doom", "/usr/local/share/doom",
            "/usr/share/games/freedoom", "/usr/share/freedoom",
            Path.Combine(home, ".local/share/games/doom"),
            Path.Combine(home, ".config/gzdoom"),
            Path.Combine(home, ".var/app/org.zdoom.GZDoom/.config/gzdoom"),
            Path.Combine(home, ".config/uzdoom"),
            Path.Combine(home, "Games/Heroic"),
            Path.Combine(home, ".local/share/bottles/bottles"),
        };

        if (environment("DOOMWADDIR") is { Length: > 0 } waddir) folders.Add(waddir);
        if (environment("DOOMWADPATH") is { Length: > 0 } wadpath) folders.AddRange(wadpath.Split(':', StringSplitOptions.RemoveEmptyEntries));

        // Steam: every library folder's installed games.
        foreach (var steam in new[] { ".local/share/Steam", ".steam/steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            var vdf = Path.Combine(home, steam, "steamapps/libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match m in SteamLibraryPath().Matches(File.ReadAllText(vdf)))
                {
                    folders.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal), "steamapps/common"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return folders.Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every base game in the folders (searched <paramref name="depth"/> levels down), each file
    /// once. Files are judged by content, so a renamed or repackaged copy is still found.
    /// </summary>
    public static IReadOnlyList<FoundGame> Scan(IEnumerable<string> folders, int depth = 4, CancellationToken cancel = default)
    {
        var found = new List<FoundGame>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            foreach (var file in Candidates(folder, depth, cancel))
            {
                cancel.ThrowIfCancellationRequested();
                string real;
                try
                {
                    real = Path.GetFullPath(new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file);
                }
                catch (IOException)
                {
                    real = Path.GetFullPath(file);
                }

                if (!seen.Add(real)) continue;
                var names = WadDirectory.ReadNames(real);
                if (names is null) continue;
                var game = GameCatalog.Identify(names);
                if (game is not null) found.Add(new FoundGame(game, real, new FileInfo(real).Length));
            }
        }

        return found;
    }

    private static IEnumerable<string> Candidates(string folder, int depth, CancellationToken cancel)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.System };
        var queue = new Queue<(string Dir, int Depth)>([(folder, 0)]);
        while (queue.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (dir, d) = queue.Dequeue();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", options).Where(f => f.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".iwad", StringComparison.OrdinalIgnoreCase)).ToList();
                dirs = d < depth ? Directory.EnumerateDirectories(dir, "*", options).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files) yield return f;
            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "proc" or "sys" or "shadercache" or "compatdata" or "downloading" or "temp") continue;
                queue.Enqueue((sub, d + 1));
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex SteamLibraryPath();
}
