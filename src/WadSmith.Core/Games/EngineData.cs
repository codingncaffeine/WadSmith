namespace WadSmith.Core.Games;

/// <summary>Where an installed GZDoom keeps its own data file (gzdoom.pk3), which holds the translators WadSmith reads.</summary>
public static class EngineData
{
    private static readonly string[] Candidates =
    [
        "/usr/share/gzdoom/gzdoom.pk3",
        "/usr/share/games/gzdoom/gzdoom.pk3",
        "/usr/local/share/gzdoom/gzdoom.pk3",
        "/usr/lib/gzdoom/gzdoom.pk3",
        "/opt/gzdoom/gzdoom.pk3",
        "/usr/share/uzdoom/uzdoom.pk3",
        "/usr/share/games/uzdoom/uzdoom.pk3",
    ];

    private static readonly string[] Names = ["gzdoom.pk3", "uzdoom.pk3", "GZDOOM.PK3", "UZDOOM.PK3"];

    /// <summary>
    /// The first engine data file found in the usual install locations, then in <paramref name="alsoIn"/> (the user's
    /// game folders and the chosen engine's folder: a portable or Windows GZDoom keeps it beside its games), or null.
    /// </summary>
    public static string? Find(IEnumerable<string>? alsoIn = null) =>
        Candidates.FirstOrDefault(File.Exists)
        ?? (alsoIn ?? []).Where(f => !string.IsNullOrEmpty(f)).SelectMany(f => Names.Select(n => Path.Combine(f, n))).FirstOrDefault(File.Exists);
}
