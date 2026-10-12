namespace WadSmith.Editor.App;

/// <summary>One named setting the Inspector offers beside a number (D27): what a player knows, in place of the units.</summary>
/// <param name="Name">The word on the button.</param>
/// <param name="Value">The number it sets.</param>
/// <param name="Words">What it means, for the tooltip.</param>
public sealed record Preset(string Name, double Value, string Words);

/// <summary>
/// Presets over numbers (D27): room heights and brightness by name. A room's height is from its floor to its ceiling;
/// the player is 56 tall, Doom's rooms run from 72 to a few hundred.
/// </summary>
public static class Presets
{
    public static readonly IReadOnlyList<Preset> RoomHeights =
    [
        new("Low", 96, "a low room or a corridor, under two players tall"),
        new("Normal", 128, "an ordinary room, two players and a bit"),
        new("Tall", 192, "a tall hall, room for a ledge or a window above"),
        new("Huge", 320, "a cavern or a courtyard, big monsters fit"),
    ];

    public static readonly IReadOnlyList<Preset> Brightness =
    [
        new("Dark", 96, "hard to see; for secrets and scares"),
        new("Dim", 144, "shadowy, as most of Doom's rooms"),
        new("Normal", 192, "well lit"),
        new("Bright", 255, "full light, as in the open"),
    ];

    /// <summary>The preset <paramref name="value"/> is exactly, or null.</summary>
    public static Preset? Named(IReadOnlyList<Preset> presets, double value) => presets.FirstOrDefault(p => p.Value == value);
}
