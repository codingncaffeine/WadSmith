namespace WadSmith.Editor;

/// <summary>
/// What new rooms and doors look like in a game: their floor, ceiling and wall textures, heights and
/// light, the textures of doors (up to 64 wide and wider), door tracks, the exit switch, stair risers, other switches (one matching the walls) and water for pools. One per game family
/// until the Looks shelf lets the user pick and make others.
/// </summary>
public sealed record Look(string Floor, string Ceiling, string Wall, double FloorHeight, double CeilingHeight, int Light, string Door = "DOOR3", string BigDoor = "BIGDOOR2", string Track = "DOORTRAK", string ExitSwitch = "SW1EXIT", string Step = "STEP6", string Switch = "SW1STRTN", string Water = "FWATER1")
{
    /// <summary>Each game family's classic look.</summary>
    public static Look For(string engine) => engine switch
    {
        "heretic" => new("FLOOR03", "FLAT520", "GRSTNPB", 0, 128, 192, "DOORWOOD", "DOORWOOD", "GRSTNPB", "SW1OFF", "GRSTNPB", "SW2OFF", "FLTWAWA1"),
        "hexen" => new("F_011", "F_032", "FOREST01", 0, 128, 192, "D_WD01", "D_WD07", "D_ENDBR", "SW_1_UP", "FOREST01", "SW_2_UP", "X_005"),
        "strife" => new("F_FLOOR1", "F_CEIL1", "BRKGRY01", 0, 128, 192, "DORMS01", "DORWL01", "DORTRK01", "SWEXIT01", "STAIR01", "SWKNOB01", "F_WATR01"),
        _ => new("FLOOR4_8", "CEIL3_5", "STARTAN3", 0, 128, 192),
    };
}
