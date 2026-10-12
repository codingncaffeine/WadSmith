namespace WadSmith.Editor;

/// <summary>
/// What new rooms look like in a game: their floor, ceiling and wall textures, heights and light.
/// One per game family until the Looks shelf lets the user pick and make others.
/// </summary>
public sealed record Look(string Floor, string Ceiling, string Wall, double FloorHeight, double CeilingHeight, int Light)
{
    /// <summary>Each game family's classic look.</summary>
    public static Look For(string engine) => engine switch
    {
        "heretic" => new("FLOOR03", "FLAT520", "GRSTNPB", 0, 128, 192),
        "hexen" => new("F_011", "F_032", "FOREST01", 0, 128, 192),
        "strife" => new("F_FLOOR1", "F_CEIL1", "BRKGRY01", 0, 128, 192),
        _ => new("FLOOR4_8", "CEIL3_5", "STARTAN3", 0, 128, 192),
    };
}
