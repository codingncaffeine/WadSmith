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
        "strife" => new("F_CONCRP", "F_PAVE01", "BRKGRY01", 0, 128, 192, "DORMS01", "DORWL01", "DORTRK01", "SWEXIT01", "STAIR01", "SWKNOB01", "F_WATR01"),
        _ => new("FLOOR4_8", "CEIL3_5", "STARTAN3", 0, 128, 192),
    };

    /// <summary>
    /// The named looks of a game family (data/looks/looks.json), the family's default first: each a look for new rooms,
    /// its doors, tracks, switches, steps and water those of the default unless it names its own.
    /// </summary>
    public static IReadOnlyList<(string Name, Look Look)> Named(string engine)
    {
        var defaults = For(engine);
        if (!Table.Value.TryGetValue(engine, out var looks)) return [("Default", defaults)];
        return [.. looks.Select(l => (l.Name, defaults with
        {
            Wall = l.Wall,
            Floor = l.Floor,
            Ceiling = l.Ceiling,
            CeilingHeight = l.CeilingHeight ?? defaults.CeilingHeight,
            Light = l.Light ?? defaults.Light,
            Switch = l.Switch ?? defaults.Switch,
        }))];
    }

    /// <summary>The named looks of a family that the game has every texture of (all of them when the game is not known).</summary>
    public static IReadOnlyList<(string Name, Look Look)> Available(string engine, GameResources? game) =>
        game is null ? Named(engine) : [.. Named(engine).Where(n => n.Look.Textures().All(t => t.Flat ? game.Textures.FindFlat(t.Name) is not null : game.Textures.FindWall(t.Name) is not null))];

    /// <summary>The textures a look puts on the map: walls first, then the floor and ceiling pictures.</summary>
    public IEnumerable<(string Name, bool Flat)> Textures() =>
        [(Wall, false), (Door, false), (BigDoor, false), (Track, false), (ExitSwitch, false), (Step, false), (Switch, false), (Floor, true), (Ceiling, true), (Water, true)];

    private sealed record NamedLook(string Name, string Wall, string Floor, string Ceiling, double? CeilingHeight, int? Light, string? Switch);

    private static readonly Lazy<Dictionary<string, List<NamedLook>>> Table = new(() =>
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Core.DataFiles.ReadText("looks/looks.json"));
        var table = new Dictionary<string, List<NamedLook>>();
        foreach (var family in doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Array))
        {
            table[family.Name] = [.. family.Value.EnumerateArray().Select(l => new NamedLook(
                l.GetProperty("name").GetString()!, l.GetProperty("wall").GetString()!, l.GetProperty("floor").GetString()!, l.GetProperty("ceiling").GetString()!,
                l.TryGetProperty("ceilingHeight", out var h) ? h.GetDouble() : null, l.TryGetProperty("light", out var light) ? light.GetInt32() : null,
                l.TryGetProperty("switch", out var sw) ? sw.GetString() : null))];
        }

        return table;
    });
}
