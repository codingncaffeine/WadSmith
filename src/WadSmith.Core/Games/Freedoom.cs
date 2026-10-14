using System.IO.Compression;
using System.Security.Cryptography;

namespace WadSmith.Core.Games;

/// <summary>
/// Freedoom, the free game that plays like Doom (Phase 1 has Doom's four episodes, Phase 2 Doom II's 32 maps), offered
/// when no game is found (§3.2) so a map can be made and played at once. One release, pinned by its checksum as the
/// tests' copy is (<c>tools/fetch-test-data.sh</c>); a download that does not match is refused.
/// </summary>
public static class Freedoom
{
    public const string Version = "0.13.0";

    public static readonly Uri Url = new("https://github.com/freedoom/freedoom/releases/download/v0.13.0/freedoom-0.13.0.zip");

    /// <summary>The SHA-256 of the release's zip, lower-case hex.</summary>
    public const string Sha256 = "3f9b264f3e3ce503b4fb7f6bdcb1f419d93c7b546f4df3e874dd878db9688f59";

    /// <summary>The game files taken from the release.</summary>
    public static readonly IReadOnlyList<string> Files = ["freedoom1.wad", "freedoom2.wad"];

    /// <summary>
    /// Fetches the release into <paramref name="folder"/> (a part file beside the games, gone when done), checks it and
    /// unpacks the two games. <paramref name="progress"/> gets the bytes so far and the total when the server says it.
    /// </summary>
    public static Task InstallAsync(string folder, HttpClient http, IProgress<(long Done, long? Total)>? progress, CancellationToken cancel) =>
        FreeGames.InstallAsync(FreeGames.Freedoom, folder, http, progress, cancel);

    /// <summary>
    /// Checks <paramref name="zip"/> against <paramref name="sha256"/> and unpacks the game files into <paramref name="folder"/>;
    /// a zip that does not match, or lacks a game file, is refused (<see cref="InvalidDataException"/>) with nothing written.
    /// </summary>
    public static void Unpack(string zip, string folder, string sha256) => Unpack(zip, folder, sha256, Files, $"Freedoom {Version}");

    /// <summary>As <see cref="Unpack(string, string, string)"/> for any release: <paramref name="files"/> taken out, <paramref name="name"/> in its words.</summary>
    public static void Unpack(string zip, string folder, string sha256, IReadOnlyList<string> files, string name)
    {
        string hash;
        using (var f = File.OpenRead(zip)) hash = Convert.ToHexStringLower(SHA256.HashData(f));
        if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The download is not {name} as published (its checksum starts {hash[..12]}, not {sha256[..12]}), so it was not unpacked.");

        using var archive = ZipFile.OpenRead(zip);
        var entries = new List<(string Name, ZipArchiveEntry Entry)>();
        foreach (var file in files)
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(file, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"The download holds no {file}.");
            entries.Add((file, entry));
        }

        Directory.CreateDirectory(folder);
        foreach (var (file, entry) in entries)
        {
            var target = Path.Combine(folder, file);
            entry.ExtractToFile(target + ".part", overwrite: true);
            File.Move(target + ".part", target, overwrite: true);
        }
    }
}
