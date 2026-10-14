using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>
/// A room's atmosphere by name (§3.7): its brightness, the colour of its light (lightcolor) and its fog (fadecolor) set
/// together, as mappers pair them (Plain keeps the room's brightness). Colours are 0xRRGGBB; white light and no fog are written as no field at all.
/// </summary>
public sealed record Atmosphere(string Name, string Tip, int? Brightness, int LightColour, int FogColour);

public static class Atmospheres
{
    public const int White = 0xFFFFFF;
    public const int NoFog = 0;

    public static readonly IReadOnlyList<Atmosphere> All =
    [
        new("Plain", "Ordinary white light and no fog; the brightness is left as it is.", null, White, NoFog),
        new("Cave", "Dim, warm light with a dusty dark haze in the distance.", 112, 0xD8C8A8, 0x181410),
        new("Hell glow", "Red-orange light, as from fire below, fading into dark red.", 176, 0xFF8060, 0x300800),
        new("Toxic", "Sickly green light and a green haze, for slime pits and labs gone wrong.", 160, 0xA0FF90, 0x0C2808),
        new("Moonlit", "Cold blue light with a deep blue night in the distance.", 128, 0xA8C0FF, 0x000818),
        new("Underwater", "Blue-green light, the far end lost in murky water.", 128, 0x70C0E0, 0x002838),
        new("Misty", "Bright, soft light and a pale grey fog that swallows the distance.", 192, White, 0x707070),
        new("Haunted", "Dark violet light and a black-purple gloom.", 96, 0xC0A0FF, 0x100018),
    ];

    /// <summary>The atmosphere a room has now, or null when its light, colour and fog match none of them.</summary>
    public static Atmosphere? Of(Sector room) =>
        All.FirstOrDefault(a => (a.Brightness is null || a.Brightness == room.Light) && a.LightColour == Colour(room, "lightcolor", White) && a.FogColour == Colour(room, "fadecolor", NoFog));

    /// <summary>Gives <paramref name="room"/> the atmosphere: its brightness, light colour and fog, in the edit.</summary>
    public static void Apply(StructureEdit e, Sector room, Atmosphere a)
    {
        if (a.Brightness is { } light) e.Set(room, r => r.Light, (r, v) => r.Light = v, light);
        e.Field(room, "lightcolor", a.LightColour == White ? (UdmfValue?)null : UdmfValue.Int(a.LightColour));
        e.Field(room, "fadecolor", a.FogColour == NoFog ? (UdmfValue?)null : UdmfValue.Int(a.FogColour));
    }

    /// <summary>A colour field of the room as 0xRRGGBB, or <paramref name="otherwise"/> when it has none.</summary>
    public static int Colour(Sector room, string key, int otherwise) =>
        room.Extra.TryGet(key, out var v) && v.Kind == UdmfValueKind.Integer ? (int)Math.Clamp(v.AsInt(), 0, 0xFFFFFF) : otherwise;
}
