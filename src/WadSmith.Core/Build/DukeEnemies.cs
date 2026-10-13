using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WadSmith.Core.Build;

/// <summary>What a Duke frame shows in a Doom monster's life: standing, walking, attacking, hurt, dying, dead.</summary>
public enum DukeRole
{
    Stand,
    Walk,
    Attack,
    Pain,
    Dying,
    Dead,
}

/// <summary>One frame of a Duke enemy: its first tile, and how many views it has (5 seen from round about, or 1 the same from every side).</summary>
public sealed record DukeFrame(DukeRole Role, int Tile, int Views);

/// <summary>
/// A Duke Nukem 3D enemy (D48, D55): its name, its sprite's first tile, the Doom monster it behaves like by default, and
/// its frames by role, read from the game's own GAME.CON actions (<c>action NAME start frames views step delay</c>).
/// </summary>
public sealed record DukeEnemy(string Key, string Name, int BaseTile, string Behaviour, IReadOnlyList<DukeFrame> Frames)
{
    /// <summary>The frames of one role, in order.</summary>
    public IReadOnlyList<DukeFrame> Of(DukeRole role) => [.. Frames.Where(f => f.Role == role)];

    /// <summary>The tile it is pictured by: its first standing frame from the front.</summary>
    public int PictureTile => (Of(DukeRole.Stand).FirstOrDefault() ?? Of(DukeRole.Walk).FirstOrDefault() ?? Frames[0]).Tile;
}

/// <summary>
/// The enemies of Duke Nukem 3D, read from a group file's own DEFS.CON (their first tiles) and GAME.CON (their actions),
/// so any copy of the game, and the user's own changes to those files inside it, are read as they are.
/// </summary>
public static partial class DukeEnemies
{
    /// <summary>The enemies, the prefix of their actions' names, and the Doom monster each behaves like unless chosen otherwise.</summary>
    private static readonly (string Key, string Name, string Prefix, string Behaviour)[] Known =
    [
        ("LIZTROOP", "Assault Trooper", "ATROOP", "ZombieMan"),
        ("PIGCOP", "Pig Cop", "APIG", "ShotgunGuy"),
        ("LIZMAN", "Assault Enforcer", "ALIZ", "ChaingunGuy"),
        ("OCTABRAIN", "Octabrain", "AOCTA", "Cacodemon"),
        ("COMMANDER", "Assault Commander", "ACOMM", "Revenant"),
        ("BOSS1", "Battlelord", "ABOSS1", "Cyberdemon"),
    ];

    /// <summary>The Doom monsters an enemy can be given the behaviour of, as the Inspector offers them.</summary>
    public static readonly IReadOnlyList<(string Class, string Name)> Behaviours =
    [
        ("ZombieMan", "Zombieman"), ("ShotgunGuy", "Shotgun guy"), ("ChaingunGuy", "Chaingunner"), ("DoomImp", "Imp"),
        ("Demon", "Demon"), ("LostSoul", "Lost soul"), ("Cacodemon", "Cacodemon"), ("HellKnight", "Hell knight"),
        ("BaronOfHell", "Baron of Hell"), ("Arachnotron", "Arachnotron"), ("PainElemental", "Pain elemental"), ("Revenant", "Revenant"),
        ("Fatso", "Mancubus"), ("Archvile", "Arch-vile"), ("SpiderMastermind", "Spider Mastermind"), ("Cyberdemon", "Cyberdemon"),
    ];

    /// <summary>The enemies a group file holds, with their frames; none when it has no DEFS.CON or GAME.CON.</summary>
    public static IReadOnlyList<DukeEnemy> Read(GrpArchive grp)
    {
        if (grp.Read("DEFS.CON") is not { } defsBytes || grp.Read("GAME.CON") is not { } gameBytes) return [];
        var defines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Define().Matches(Uncommented(Encoding.Latin1.GetString(defsBytes))))
        {
            defines[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        var actions = new Dictionary<string, (int Start, int Frames, int Views, int Step)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Action().Matches(Uncommented(Encoding.Latin1.GetString(gameBytes))))
        {
            int Arg(int i, int otherwise) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : otherwise;
            actions.TryAdd(m.Groups[1].Value, (Arg(2, 0), Arg(3, 1), Arg(4, 1), Arg(5, 1)));
        }

        var enemies = new List<DukeEnemy>();
        foreach (var (key, name, prefix, behaviour) in Known)
        {
            if (!defines.TryGetValue(key, out var baseTile)) continue;
            var frames = new List<DukeFrame>();
            foreach (var role in Enum.GetValues<DukeRole>())
            {
                var chosen = actions.Where(a => a.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && RoleOf(a.Key[prefix.Length..]) == role).OrderBy(a => a.Key.Length).Select(a => a.Value).FirstOrDefault();
                if (chosen == default) continue;
                var views = chosen.Views is 5 or 7 or 8 ? 5 : 1;
                var step = chosen.Step == 0 ? 1 : Math.Abs(chosen.Step);
                for (var f = 0; f < Math.Clamp(chosen.Frames, 1, 8); f++) frames.Add(new DukeFrame(role, baseTile + chosen.Start + f * views * step, views));
            }

            if (frames.Count == 0) continue;

            // A role the enemy has no action for borrows the nearest one it has: a still walk frame for standing, its first
            // dying frame for pain, its last for lying dead.
            if (!frames.Any(f => f.Role == DukeRole.Walk) && frames.FirstOrDefault(f => f.Role == DukeRole.Stand) is { } stand) frames.Add(stand with { Role = DukeRole.Walk });
            if (!frames.Any(f => f.Role == DukeRole.Stand) && frames.FirstOrDefault(f => f.Role == DukeRole.Walk) is { } walk) frames.Add(walk with { Role = DukeRole.Stand });
            if (!frames.Any(f => f.Role == DukeRole.Attack) && frames.FirstOrDefault(f => f.Role == DukeRole.Stand) is { } aim) frames.Add(aim with { Role = DukeRole.Attack });
            if (!frames.Any(f => f.Role == DukeRole.Pain) && frames.FirstOrDefault(f => f.Role == DukeRole.Dying) is { } hurt) frames.Add(hurt with { Role = DukeRole.Pain });
            if (!frames.Any(f => f.Role == DukeRole.Dead) && frames.LastOrDefault(f => f.Role == DukeRole.Dying) is { } last) frames.Add(last with { Role = DukeRole.Dead });
            enemies.Add(new DukeEnemy(key, name, baseTile, behaviour, frames));
        }

        return enemies;
    }

    /// <summary>The role an action's name says, after the enemy's prefix ("WALKING", "SHOOT", "DYING"…); null for the rest (frozen, shrunk, ducking).</summary>
    public static DukeRole? RoleOf(string suffix) => suffix.ToUpperInvariant() switch
    {
        "STAND" or "BREETH" or "THINK" or "CRUZING" => DukeRole.Stand,
        "WALK" or "WALKING" or "RUN" or "RUNNING" or "GET" => DukeRole.Walk,
        "SHOOT" or "SHOOTING" or "ATACK" or "ATTACK" => DukeRole.Attack,
        "HIT" or "FLINTCH" or "PAIN" => DukeRole.Pain,
        "DYING" => DukeRole.Dying,
        "DEAD" or "LYINGDEAD" => DukeRole.Dead,
        _ => null,
    };

    private static string Uncommented(string text) => LineComment().Replace(BlockComment().Replace(text, " "), "");

    [GeneratedRegex(@"(?im)^\s*define\s+(\w+)\s+(-?\d+)")]
    private static partial Regex Define();

    [GeneratedRegex(@"(?im)^\s*action\s+(\w+)(?:[ \t]+(-?\d+))?(?:[ \t]+(-?\d+))?(?:[ \t]+(-?\d+))?(?:[ \t]+(-?\d+))?")]
    private static partial Regex Action();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
