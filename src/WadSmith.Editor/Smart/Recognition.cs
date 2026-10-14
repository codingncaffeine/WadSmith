using WadSmith.Core.Maps;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Smart;

/// <summary>Everything the editor recognises in a map (D21), by kind: area places for doors, lifts, windows and secrets, wall places for switches, teleporters and exits.</summary>
public sealed record Recognised(IReadOnlyDictionary<string, IReadOnlySet<int>> ByKind);

/// <summary>
/// Recognition over a whole map, and its control: what the original Doom games mark as each kind by their own
/// (vanilla) line and area types, so recognition can be measured (precision and recall) on any Doom-format map.
/// </summary>
public static class Recognition
{
    public static readonly string[] Kinds = ["doors", "lifts", "windows", "secrets", "switches", "teleporters", "exits"];

    /// <summary>Doom's doors opened from their own walls (DR, D1, keyed, blazing) and by tag (W1/WR/S1/SR/GR open and raise).</summary>
    private static readonly HashSet<int> LocalDoors = [1, 26, 27, 28, 31, 32, 33, 34, 117, 118];
    private static readonly HashSet<int> RemoteDoors = [2, 4, 29, 46, 61, 63, 86, 90, 99, 103, 105, 106, 108, 109, 111, 112, 114, 115, 133, 134, 135, 136, 137];

    /// <summary>
    /// Strife's doors (GZDoom's xlat/strife.txt): Doom's, its locked doors for its many keys, and its sliding doors (144 opened
    /// from their own walls, 207 by tag).
    /// </summary>
    private static readonly HashSet<int> StrifeLocalDoors = [.. LocalDoors, 144, 156, 157, 158, 159, 160, 161, 165, 166, 169, 170, 190, 205, 217, 221, 224, 225];
    private static readonly HashSet<int> StrifeRemoteDoors = [.. RemoteDoors, 151, 152, 153, 162, 163, 164, 167, 168, 171, 172, 173, 176, 191, 192, 207, 223];

    /// <summary>Doom's lift actions (W1, S1, SR, WR, blazing).</summary>
    private static readonly HashSet<int> Lifts = [10, 21, 62, 88, 120, 121, 122, 123];

    /// <summary>Doom's switches and buttons that work on tagged areas (S1 and SR, vanilla P_UseSpecialLine), not the exits.</summary>
    private static readonly HashSet<int> Switches = [7, 9, 14, 15, 18, 20, 21, 23, 29, 41, 49, 50, 55, 71, 101, 102, 103, 111, 112, 113, 122, 127, 131, 133, 135, 137, 140, 42, 43, 45, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 114, 115, 116, 123, 132, 99, 134, 136, 138, 139];

    private static readonly HashSet<int> Teleports = [39, 97, 125, 126, 174, 195, 207, 208, 209, 210];
    private static readonly HashSet<int> Exits = [11, 51, 52, 124, 197, 198];

    /// <summary>What the editor recognises in the map.</summary>
    public static Recognised Find(EditorMap map, MapGeometry geometry)
    {
        var areas = map.Sectors.Where(s => s.OnMap).ToList();
        var walls = map.Lines.Where(l => l.OnMap).ToList();
        return new Recognised(new Dictionary<string, IReadOnlySet<int>>
        {
            ["doors"] = areas.Where(s => Doors.Recognise(geometry, s) is not null).Select(s => s.Slot).ToHashSet(),
            ["lifts"] = areas.Where(s => Smart.Lifts.Recognise(geometry, s) is not null).Select(s => s.Slot).ToHashSet(),
            ["windows"] = areas.Where(s => WallWindows.Recognise(geometry, s) is not null).Select(s => s.Slot).ToHashSet(),
            ["secrets"] = areas.Where(s => Secrets.Recognise(geometry, s) is not null).Select(s => s.Slot).ToHashSet(),
            ["switches"] = walls.Where(l => Smart.Switches.Recognise(geometry, l) is not null).Select(l => l.Slot).ToHashSet(),
            ["teleporters"] = walls.Where(l => Smart.Teleports.IsTeleport(l)).Select(l => l.Slot).ToHashSet(),
            ["exits"] = walls.Where(l => Smart.Exits.IsExit(l)).Select(l => l.Slot).ToHashSet(),
        });
    }

    /// <summary>What a Doom-format map as the game shipped it marks as each kind (windows have no mark, so none are listed); Strife's doors by Strife's own numbers.</summary>
    public static Recognised Expected(MapModel classic)
    {
        var doors = new HashSet<int>();
        var lifts = new HashSet<int>();
        void Tagged(MapLine l, HashSet<int> into)
        {
            var tag = Math.Max(l.Id, 0);
            if (tag == 0 && l.Back >= 0) into.Add(classic.Sides[l.Back].Sector);
            for (var s = 0; tag != 0 && s < classic.Sectors.Count; s++)
            {
                if (classic.Sectors[s].Id == tag) into.Add(s);
            }
        }

        var (localDoors, remoteDoors) = classic.Namespace == "strife" ? (StrifeLocalDoors, StrifeRemoteDoors) : (LocalDoors, RemoteDoors);
        foreach (var l in classic.Lines)
        {
            if (localDoors.Contains(l.Special) && l.Back >= 0) doors.Add(classic.Sides[l.Back].Sector);
            if (remoteDoors.Contains(l.Special)) Tagged(l, doors);
            if (Lifts.Contains(l.Special)) Tagged(l, lifts);
        }

        IReadOnlySet<int> LinesWith(HashSet<int> types, bool tagged = false) => Enumerable.Range(0, classic.Lines.Count).Where(i => types.Contains(classic.Lines[i].Special) && (!tagged || classic.Lines[i].Id > 0)).ToHashSet();
        return new Recognised(new Dictionary<string, IReadOnlySet<int>>
        {
            ["doors"] = doors,
            ["lifts"] = lifts,
            ["windows"] = new HashSet<int>(),
            ["secrets"] = Enumerable.Range(0, classic.Sectors.Count).Where(i => classic.Sectors[i].Special == 9 || (classic.Sectors[i].Special & 128) != 0).ToHashSet(),
            ["switches"] = LinesWith(Switches, tagged: true),
            ["teleporters"] = LinesWith(Teleports),
            ["exits"] = LinesWith(Exits),
        });
    }
}
