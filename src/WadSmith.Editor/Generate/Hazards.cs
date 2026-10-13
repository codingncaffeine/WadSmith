namespace WadSmith.Editor.Generate;

/// <summary>
/// The pools a made map may sink in its floors, per game family: water, which does no harm, and the game's own hurting
/// floors with the damage its maps give them (Doom's nukage 5 and lava 10, Heretic's sludge 4 and lava 5, Hexen's lava 5).
/// </summary>
public static class Hazards
{
    /// <summary>The pools of a family: the floor picture and the damage it does every 32 tics.</summary>
    public static IReadOnlyList<(string Flat, int Damage)> Of(string family) => family switch
    {
        "heretic" => [("FLTWAWA1", 0), ("FLTSLUD1", 4), ("FLTLAVA1", 5)],
        "hexen" => [("X_005", 0), ("X_001", 5)],
        "strife" => [("F_WATR01", 0)],
        _ => [("FWATER1", 0), ("NUKAGE1", 5), ("LAVA1", 10), ("BLOOD1", 0)],
    };
}
