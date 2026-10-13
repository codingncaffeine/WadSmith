using System.Globalization;
using System.Text;
using WadSmith.Core.Maps;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Generate;

/// <summary>
/// What a made map borrows, as it is in its project: the number each monster from another game was carried in as, the look
/// a room's choice has there (another game's pictures under the names they were copied in as), and the number another
/// game's weapon or ammunition (its family and own number) was carried in as. A null answer leaves the thing out.
/// </summary>
public sealed record Lent(Func<MonsterKind, int?>? Monsters = null, Func<LookChoice, Look>? Looks = null, Func<string, int, int?>? Items = null);

/// <summary>A made map: its plan, the map the editor's tools built from it, the script they ran, and any line that failed.</summary>
public sealed record MadeMap(MapPlan Plan, EditorMap Map, string Script, IReadOnlyList<ScriptStep> Failed);

/// <summary>
/// Turns a generator plan into an edit script (D50: every room, door, lock, lift, stair, secret, teleporter and object is made
/// by the editor's own tools, so each is the same parametric, recognised object the user's hand makes) and runs it on an
/// empty map. <c>wadsmith-cli generate</c> and the "Make me a map" page run the same code.
/// </summary>
public static class MapMaker
{
    /// <summary>The plan as an edit script, with what it borrows as <paramref name="lent"/> has it in the project.</summary>
    public static string Script(MapPlan plan, Lent? lent = null)
    {
        var numberOf = lent?.Monsters;
        var lookOf = lent?.Looks;
        var s = plan.Settings;
        var text = new StringBuilder();
        void Line(FormattableString line) => text.AppendLine(line.ToString(CultureInfo.InvariantCulture));
        var own = Look.For(s.Family);
        Look LookFor(PlannedRoom r) => r.Look is { } choice ? lookOf?.Invoke(choice) ?? choice.Look : own;
        void UseLook(Look l) => Line($"look custom {l.Wall} {l.Floor} {l.Ceiling}");
        void Heights(Box b, double floor, double ceiling, int light)
        {
            Line($"floor {b.CentreX} {b.CentreY} {floor}");
            Line($"ceiling {b.CentreX} {b.CentreY} {ceiling}");
            Line($"brightness {b.CentreX} {b.CentreY} {light}");
        }

        Line($"# {SeedWords.Title(s.Seed)}: made by WadSmith from the seed {SeedWords.Tidy(s.Seed)}");
        Line($"# Rooms");
        foreach (var room in plan.Rooms)
        {
            UseLook(LookFor(room));
            if (room.Cuts == default) Line($"room {room.Box.X0} {room.Box.Y0} {room.Box.X1} {room.Box.Y1}");
            else Line($"shape {string.Join(' ', room.Outline().Select(p => FormattableString.Invariant($"{p.X} {p.Y}")))}");

            // Heights are set at the cell's middle, which cut corners and pillars leave clear (a closet's own middle for a closet).
            var (hx, hy) = room.Role == RoomRole.Closet ? (room.Box.CentreX, room.Box.CentreY) : Planner.Middle(plan, room);
            Line($"floor {hx} {hy} {room.Floor}");
            Line($"ceiling {hx} {hy} {room.Ceiling}");
            Line($"brightness {hx} {hy} {room.Light}");
        }

        Line($"# Pillars: columns closed floor to ceiling");
        foreach (var room in plan.Rooms)
        {
            foreach (var p in room.Pillars)
            {
                Line($"room {p.X0} {p.Y0} {p.X1} {p.Y1}");
                Line($"ceiling {p.CentreX} {p.CentreY} {room.Floor}");
            }
        }

        Line($"# Corridors");
        foreach (var link in plan.Links)
        {
            var (a, b) = (plan.Rooms[link.A], plan.Rooms[link.B]);
            UseLook(LookFor(a.Zone <= b.Zone ? a : b));
            var light = Math.Min(a.Light, b.Light) is var l && l < 144 ? l : Math.Min(Math.Max(a.Light, b.Light), 192);
            var box = link.Box;
            switch (link.Kind)
            {
                case LinkKind.Stairs:
                {
                    var (low, high) = a.Floor <= b.Floor ? (a, b) : (b, a);
                    var lowFirst = link.AlongX ? Math.Abs(box.X0 - low.Box.X1) < 1 : Math.Abs(box.Y0 - low.Box.Y1) < 1;
                    var (from, to) = link.AlongX
                        ? lowFirst ? ((box.X0, box.Y0), (box.X1, box.Y1)) : ((box.X1, box.Y0), (box.X0, box.Y1))
                        : lowFirst ? ((box.X0, box.Y0), (box.X1, box.Y1)) : ((box.X0, box.Y1), (box.X1, box.Y0));
                    Line($"stairs {from.Item1} {from.Item2} {to.Item1} {to.Item2} {(link.AlongX ? "x" : "y")}");

                    // Each step's ceiling, found where the Stairs tool cuts them (its own count).
                    var length = link.AlongX ? box.Width : box.Height;
                    var total = high.Floor - low.Floor;
                    var count = Math.Min(Math.Max(1, (int)Math.Ceiling(Math.Abs(total) / Smart.Stairs.ComfortRise) - 1), Math.Max(1, (int)Math.Floor(length / Smart.Stairs.MinDepth)));
                    var depth = (double)length / count;
                    for (var i = 0; i < count; i++)
                    {
                        var along = (i + 0.5) * depth;
                        var (x, y) = link.AlongX ? (lowFirst ? box.X0 + along : box.X1 - along, box.CentreY) : (box.CentreX, lowFirst ? box.Y0 + along : box.Y1 - along);
                        Line($"ceiling {x} {y} {link.Ceiling}");
                        Line($"brightness {x} {y} {light}");
                    }

                    break;
                }

                case LinkKind.Lift:
                {
                    var (low, high) = a.Floor <= b.Floor ? (a, b) : (b, a);
                    var highFirst = link.AlongX ? Math.Abs(box.X0 - high.Box.X1) < 1 : Math.Abs(box.Y0 - high.Box.Y1) < 1;
                    Box pad, rest;
                    if (link.AlongX)
                    {
                        (pad, rest) = highFirst ? (box with { X1 = box.X0 + 128 }, box with { X0 = box.X0 + 128 }) : (box with { X0 = box.X1 - 128 }, box with { X1 = box.X1 - 128 });
                    }
                    else
                    {
                        (pad, rest) = highFirst ? (box with { Y1 = box.Y0 + 128 }, box with { Y0 = box.Y0 + 128 }) : (box with { Y0 = box.Y1 - 128 }, box with { Y1 = box.Y1 - 128 });
                    }

                    Line($"room {rest.X0} {rest.Y0} {rest.X1} {rest.Y1}");
                    Heights(rest, low.Floor, link.Ceiling, light);
                    Line($"room {pad.X0} {pad.Y0} {pad.X1} {pad.Y1}");
                    Heights(pad, high.Floor, link.Ceiling, light);
                    Line($"lift {pad.CentreX} {pad.CentreY}");
                    break;
                }

                default:
                    Line($"room {box.X0} {box.Y0} {box.X1} {box.Y1}");
                    Heights(box, link.Floor, link.Ceiling, light);
                    break;
            }
        }

        Line($"# Doors and locks");
        foreach (var link in plan.Links.Where(l => l.Kind is LinkKind.Door or LinkKind.Locked))
        {
            var room = plan.Rooms[link.DoorAt];
            var (mx, my) = Planner.Mouth(link, room);
            Line($"door {mx} {my}");
            if (link.Key is { } key)
            {
                var (ix, iy) = link.AlongX ? (Math.Abs(mx - room.Box.X1) < 1 ? mx + 8 : mx - 8, my) : (mx, Math.Abs(my - room.Box.Y1) < 1 ? my + 8 : my - 8);
                Line($"lock {ix} {iy} {key.Lock}");
            }
        }

        Line($"# Monster closets: a door, opened when the player walks into the room");
        foreach (var closet in plan.Rooms.Where(r => r.Role == RoomRole.Closet))
        {
            var (dx, dy) = closet.ClosetDoor;
            Line($"door {dx} {dy}");
            var b = closet.Box;
            var (ix, iy) = Math.Abs(dy - b.Y0) < 1 ? (dx, dy + 8) : Math.Abs(dy - b.Y1) < 1 ? (dx, dy - 8) : Math.Abs(dx - b.X0) < 1 ? (dx + 8, dy) : (dx - 8, dy);
            Line($"trigger {closet.Trigger.X} {closet.Trigger.Y} {ix} {iy}");
        }

        Line($"# Secrets, teleporters and the exit");
        foreach (var room in plan.Rooms.Where(r => r.Role == RoomRole.Secret))
        {
            Line($"secret {room.Box.CentreX} {room.Box.CentreY}");

            // Its door, flush with the room's wall, takes the wall's look from the room.
            if (plan.Links.FirstOrDefault(l => (l.A == room.Index || l.B == room.Index) && l.Kind == LinkKind.Door && l.DoorAt != room.Index) is { } link)
            {
                var near = plan.Rooms[link.DoorAt];
                var (mx, my) = Planner.Mouth(link, near);
                var (ix, iy) = link.AlongX ? (Math.Abs(mx - near.Box.X1) < 1 ? mx + 8 : mx - 8, my) : (mx, Math.Abs(my - near.Box.Y1) < 1 ? my + 8 : my - 8);
                Line($"hide {ix} {iy} {room.Box.CentreX} {room.Box.CentreY}");
            }
        }

        foreach (var t in plan.Teleports)
        {
            Line($"room {t.Pad.X0} {t.Pad.Y0} {t.Pad.X1} {t.Pad.Y1}");
            Line($"teleport {t.Pad.CentreX} {t.Pad.CentreY} {t.ToX} {t.ToY}");
        }

        Line($"exit {plan.Exit.X} {plan.Exit.Y}");

        Line($"# Objects");
        foreach (var t in plan.Things)
        {
            switch (t.Role)
            {
                case ThingRole.Start:
                    Line($"start {t.X} {t.Y} {t.Angle}");
                    break;
                case ThingRole.Light:
                    Line($"light {t.X} {t.Y}");
                    break;
                default:
                    // A monster from another game (or one this game lacks) is placed by the number it was carried in as; none, and it is left out.
                    var number = t.Monster is { } m ? numberOf?.Invoke(m) ?? (m.Family == s.Family && (s.Doom2Things || !m.Doom2) ? t.Number : null)
                        : t.Family is { } family && family != s.Family ? lent?.Items?.Invoke(family, t.Number) : t.Number;
                    if (number is not { } n) break;
                    var skill = t.FromSkill switch { 1 => " normal", 2 => " hard", _ => "" };
                    Line($"thing {n} {t.X} {t.Y} {t.Angle}{skill}{(t.Ambush ? " ambush" : "")}");
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>Plans and builds a map from <paramref name="settings"/> on an empty map in the slot the settings name.</summary>
    public static MadeMap Make(GeneratorSettings settings, Lent? lent = null) => Build(Planner.Plan(settings), lent);

    /// <summary>Builds the map a plan describes.</summary>
    public static MadeMap Build(MapPlan plan, Lent? lent = null)
    {
        var s = plan.Settings;
        var script = Script(plan, lent);
        var map = EditorMap.FromModel(new MapModel { Name = s.Slot, Namespace = "zdoom" });
        var steps = EditScript.Run(map, script, Look.For(s.Family), s.Family);
        return new MadeMap(plan, map, script, [.. steps.Where(x => !x.Done)]);
    }
}

/// <summary>Makes a generated map into a project of its own, as the page's "Make it" does.</summary>
public static class MadeProjects
{
    /// <summary>The file in the project that keeps the settings the map was made with, for "Make another like it".</summary>
    public const string SettingsFile = "generator.json";

    /// <summary>
    /// Creates the project in <paramref name="folder"/> for a made map, named after its seed, and saves the map in its slot with
    /// the settings beside it. <paramref name="prepare"/> runs on the new project before the map is built (to carry in other
    /// games' monsters, pictures, weapons and ammunition) and says how they are named there. Writes files: call it on a worker.
    /// </summary>
    public static (WadSmith.Core.Projects.Project Project, MadeMap Made) Create(string folder, MapPlan plan, string target = WadSmith.Core.Projects.Targets.GZDoom, Func<WadSmith.Core.Projects.Project, Lent?>? prepare = null)
    {
        var s = plan.Settings;
        var name = SeedWords.Title(s.Seed);
        var project = WadSmith.Core.Projects.Project.Create(folder, name, s.GameId, target);
        var made = MapMaker.Build(plan, prepare?.Invoke(project));
        project.SaveMap(made.Map.Frame.ToModel(), name, new WadSmith.Core.Projects.MapOrigin { Kind = "generated", Game = s.GameId, Map = SeedWords.Tidy(s.Seed) });
        project.WriteFile(SettingsFile, System.Text.Encoding.UTF8.GetBytes(SettingsJson.Write(s)));
        return (project, made);
    }
}

/// <summary>The settings as the project keeps them: the choices only (the monsters and looks by family and name, not their pictures).</summary>
public static class SettingsJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed record Kept(GeneratorSettings Settings, List<string> Monsters, List<string> Looks);

    public static string Write(GeneratorSettings s) => System.Text.Json.JsonSerializer.Serialize(
        new Kept(s with { Monsters = [], Looks = [] }, [.. s.Monsters.Select(m => $"{m.Family}:{m.Class}")], [.. s.Looks.Select(l => $"{l.Family}:{l.Name}")]), Options);

    /// <summary>Reads settings back; the monsters and looks are found again in the palettes and looks by family and name (those no longer there are left out).</summary>
    public static GeneratorSettings? Read(string json)
    {
        try
        {
            var kept = System.Text.Json.JsonSerializer.Deserialize<Kept>(json, Options);
            if (kept?.Settings is not { } s) return null;
            var monsters = kept.Monsters.Select(k => k.Split(':', 2)).Where(p => p.Length == 2).Select(p => FamilyPalette.For(p[0])?.Monsters.FirstOrDefault(m => m.Class == p[1])).OfType<MonsterKind>().ToList();
            var looks = kept.Looks.Select(k => k.Split(':', 2)).Where(p => p.Length == 2).Select(p => GeneratorSettings.OwnLooks(p[0]).FirstOrDefault(l => l.Name == p[1])).OfType<LookChoice>().ToList();
            return s with { Monsters = monsters, Looks = looks };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
