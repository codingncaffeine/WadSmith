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

    /// <summary>The first engine data file found in the usual install locations, or null.</summary>
    public static string? Find() => Candidates.FirstOrDefault(File.Exists);
}
