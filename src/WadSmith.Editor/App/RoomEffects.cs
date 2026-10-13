namespace WadSmith.Editor.App;

/// <summary>
/// What a room can do to the player or its light, by name (Eureka's sector-type browser): GZDoom's own numbers for
/// Doom's sector types and the common extras (gzdoom.pk3 <c>xlat/defines.i</c>). A classic target writes the classic
/// number back through the game's translation; one with no classic number is a Problem there ("not-classic").
/// </summary>
public static class RoomEffects
{
    /// <summary>The effects, in the order the Inspector offers them: none first, then light, then harm, then the rest.</summary>
    public static readonly IReadOnlyList<(int Number, string Name)> All =
    [
        (0, "None"),
        (65, "Light flickers"),
        (81, "Light flickers like fire"),
        (72, "Light glows up and down"),
        (66, "Light blinks fast"),
        (67, "Light blinks slowly"),
        (77, "Light blinks fast, in step with others"),
        (76, "Light blinks slowly, in step with others"),
        (71, "Hurts a little (5% a second, nukage)"),
        (69, "Hurts (10%, slime)"),
        (80, "Hurts a lot (20%, super slime)"),
        (68, "Hurts a lot and blinks (20%)"),
        (82, "Hurts, lava"),
        (83, "Hurts more, lava"),
        (84, "Hurts and pushes east, lava"),
        (75, "Hurts, and ends the map when health runs low"),
        (115, "Kills at once"),
        (196, "Heals"),
        (79, "Slippery (ice)"),
        (74, "Its door closes after 30 seconds"),
        (78, "Its door opens after 5 minutes"),
        (197, "Lightning flashes (outdoors)"),
    ];

    /// <summary>The part of a room's special that is its effect; the higher bits hold secret, friction and push flags.</summary>
    public const int Mask = 0xFF;

    /// <summary>The name of the effect a room's special holds; "Effect N" for one not in the list.</summary>
    public static string NameOf(int special)
    {
        var effect = special & Mask;
        foreach (var (number, name) in All)
        {
            if (number == effect) return name;
        }

        return $"Effect {effect}";
    }

    /// <summary>A room's special with its effect replaced and its flags kept.</summary>
    public static int With(int special, int effect) => (special & ~Mask) | (effect & Mask);
}
