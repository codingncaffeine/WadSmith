using System.Globalization;
using System.Text.RegularExpressions;
using WadSmith.Core.Games;
using WadSmith.Editor.App;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.Editing;

/// <summary>What Find and replace looks for (Eureka's find/replace panel, Doom Builder's Find and Replace).</summary>
public enum FindKind
{
    /// <summary>A picture on any part of a wall's faces.</summary>
    WallPicture,

    /// <summary>A floor or ceiling picture.</summary>
    FloorPicture,

    /// <summary>Objects of a kind, by editor number or name.</summary>
    Object,

    /// <summary>Walls with an action, by number or name.</summary>
    WallAction,

    /// <summary>Rooms with an effect, by number or name.</summary>
    RoomEffect,

    /// <summary>A tag: the rooms that have it and the walls whose action names it.</summary>
    Tag,
}

/// <summary>Which difficulties an object must appear on to be found (any of them); all three by default.</summary>
[Flags]
public enum FindSkills
{
    None = 0,
    Easy = 1,
    Normal = 2,
    Hard = 4,
    All = Easy | Normal | Hard,
}

/// <summary>A search: what kind of thing, the text typed (a name, a number, or a name with * and ?), and where to look.</summary>
public sealed record FindQuery(FindKind Kind, string What, FindSkills Skills = FindSkills.All);

/// <summary>One thing found: the face, room, object or wall, and the part of it that matched, in words ("upper", "floor").</summary>
public sealed record Found(Element Element, string Where);

/// <summary>
/// Find and replace over the whole map: <see cref="Find"/> reads only a geometry's frame (call it on a worker; it can
/// read the whole map), <see cref="Replace"/> changes what was found through one edit (the session runs it as one undo
/// step). Pictures match by name, ignoring case, with * for any letters and ? for one; objects, actions and effects by
/// number or by a few letters of their name.
/// </summary>
public static class Finding
{
    /// <summary>Why the search cannot run as typed (nothing typed, or a word for a number); null when it can.</summary>
    public static string? WhyNot(FindQuery query, ThingCatalog? things)
    {
        var what = query.What.Trim();
        if (what.Length == 0) return "Type what to look for.";
        return query.Kind switch
        {
            FindKind.Tag when !int.TryParse(what, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag) || tag <= 0 => "A tag is a number above 0.",
            FindKind.Object when Numbers(query, things).Count == 0 => $"No object of the game is called {what}.",
            FindKind.WallAction when Numbers(query, things).Count == 0 => $"No action is called {what}.",
            FindKind.RoomEffect when Numbers(query, things).Count == 0 => $"No room effect is called {what}.",
            _ => null,
        };
    }

    /// <summary>Everything in the map the search finds, in the map's order; empty when it cannot run (see <see cref="WhyNot"/>).</summary>
    public static IReadOnlyList<Found> Find(MapGeometry geometry, FindQuery query, ThingCatalog? things)
    {
        if (WhyNot(query, things) is not null) return [];
        var f = geometry.Frame;
        var found = new List<Found>();
        switch (query.Kind)
        {
            case FindKind.WallPicture:
            {
                var match = Pattern(query.What);
                foreach (var l in f.Lines)
                {
                    if (l.Element is not { OnMap: true }) continue;
                    foreach (var side in new[] { l.Front, l.Back })
                    {
                        if (side < 0 || f.Sides[side].Element is not { } face) continue;
                        var s = f.Sides[side];
                        var parts = new List<string>();
                        if (s.Top != "-" && match(s.Top)) parts.Add("upper");
                        if (s.Middle != "-" && match(s.Middle)) parts.Add("middle");
                        if (s.Bottom != "-" && match(s.Bottom)) parts.Add("lower");
                        if (parts.Count > 0) found.Add(new Found(face, string.Join(" and ", parts)));
                    }
                }

                break;
            }

            case FindKind.FloorPicture:
            {
                var match = Pattern(query.What);
                foreach (var s in f.Sectors)
                {
                    if (s.Element is not { OnMap: true } room) continue;
                    var (floor, ceiling) = (match(s.FloorTexture), match(s.CeilingTexture));
                    if (floor || ceiling) found.Add(new Found(room, floor && ceiling ? "floor and ceiling" : floor ? "floor" : "ceiling"));
                }

                break;
            }

            case FindKind.Object:
            {
                var numbers = Numbers(query, things);
                foreach (var t in f.Things)
                {
                    if (t.Element is not { OnMap: true } thing || !numbers.Contains(t.Type) || !OnSkills(t.Extra, query.Skills)) continue;
                    found.Add(new Found(thing, things?.Find(t.Type)?.Name ?? $"number {t.Type}"));
                }

                break;
            }

            case FindKind.WallAction:
            {
                var numbers = Numbers(query, things);
                foreach (var l in f.Lines)
                {
                    if (l.Element is { OnMap: true } wall && l.Special != 0 && numbers.Contains(l.Special)) found.Add(new Found(wall, ActionCatalog.Get(l.Special)?.Name ?? $"action {l.Special}"));
                }

                break;
            }

            case FindKind.RoomEffect:
            {
                var numbers = Numbers(query, things);
                foreach (var s in f.Sectors)
                {
                    if (s.Element is { OnMap: true } room && (s.Special & RoomEffects.Mask) != 0 && numbers.Contains(s.Special & RoomEffects.Mask)) found.Add(new Found(room, RoomEffects.NameOf(s.Special)));
                }

                break;
            }

            case FindKind.Tag:
            {
                var tag = int.Parse(query.What.Trim(), CultureInfo.InvariantCulture);
                foreach (var s in f.Sectors)
                {
                    if (s.Element is { OnMap: true } room && s.Tag == tag) found.Add(new Found(room, "the room's tag"));
                }

                foreach (var l in f.Lines)
                {
                    if (l.Element is not { OnMap: true } wall) continue;
                    if (l.Special != 0 && Switches.NamesAreas(l.Special) && l.Args[0] == tag) found.Add(new Found(wall, "the tag its action works on"));
                    else if (l.LineId == tag) found.Add(new Found(wall, "the wall's own id"));
                }

                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Why <paramref name="with"/> cannot replace what this search finds (nothing typed, a picture the game does not have,
    /// a name that is not one object or action); null when it can. <paramref name="hasPicture"/> says whether the game
    /// has a picture of a name (null: not checked).
    /// </summary>
    public static string? WhyNotReplace(FindQuery query, string with, ThingCatalog? things, Func<string, bool>? hasPicture)
    {
        var text = with.Trim();
        if (text.Length == 0) return "Type what to put in its place.";
        return query.Kind switch
        {
            FindKind.WallPicture or FindKind.FloorPicture when text.Contains('*') || text.Contains('?') => "The new picture is one name, without * or ?.",
            FindKind.WallPicture or FindKind.FloorPicture when hasPicture is not null && !hasPicture(text) => $"The game has no picture called {text}.",
            FindKind.Object when One(FindKind.Object, text, things) is null => $"{text} is not one object of the game: type its number or its whole name.",
            FindKind.WallAction when One(FindKind.WallAction, text, things) is null => $"{text} is not one action: type its number or its whole name.",
            FindKind.RoomEffect when One(FindKind.RoomEffect, text, things) is null => $"{text} is not one room effect: type its number or its whole name.",
            FindKind.Tag when !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag) || tag <= 0 => "The new tag is a number above 0.",
            _ => null,
        };
    }

    /// <summary>
    /// Puts <paramref name="with"/> in place of what <paramref name="query"/> matched in each of <paramref name="found"/>,
    /// through <paramref name="e"/>; only the matching parts change (a face's matching parts, a room's matching floor or
    /// ceiling, a wall's action and nothing else of it). Returns how many elements changed. Check <see cref="WhyNotReplace"/> first.
    /// </summary>
    public static int Replace(StructureEdit e, FindQuery query, IEnumerable<Found> found, string with, ThingCatalog? things)
    {
        var text = with.Trim();
        var match = query.Kind is FindKind.WallPicture or FindKind.FloorPicture ? Pattern(query.What) : _ => false;
        var numbers = query.Kind is FindKind.Object or FindKind.WallAction or FindKind.RoomEffect ? Numbers(query, things) : [];
        var count = 0;
        foreach (var (element, _) in found)
        {
            if (!element.OnMap) continue;
            var changed = true;
            switch (query.Kind, element)
            {
                case (FindKind.WallPicture, Side s):
                    changed = false;
                    if (s.Top != "-" && match(s.Top)) { e.Set(s, x => x.Top, (x, v) => x.Top = v, text); changed = true; }
                    if (s.Middle != "-" && match(s.Middle)) { e.Set(s, x => x.Middle, (x, v) => x.Middle = v, text); changed = true; }
                    if (s.Bottom != "-" && match(s.Bottom)) { e.Set(s, x => x.Bottom, (x, v) => x.Bottom = v, text); changed = true; }
                    break;
                case (FindKind.FloorPicture, Sector room):
                    changed = false;
                    if (match(room.FloorTexture)) { e.Set(room, x => x.FloorTexture, (x, v) => x.FloorTexture = v, text); changed = true; }
                    if (match(room.CeilingTexture)) { e.Set(room, x => x.CeilingTexture, (x, v) => x.CeilingTexture = v, text); changed = true; }
                    break;
                case (FindKind.Object, Thing t) when numbers.Contains(t.Type):
                    e.Set(t, x => x.Type, (x, v) => x.Type = v, One(FindKind.Object, text, things)!.Value);
                    break;
                case (FindKind.WallAction, Line l) when numbers.Contains(l.Special):
                    e.Set(l, x => x.Special, (x, v) => x.Special = v, One(FindKind.WallAction, text, things)!.Value);
                    break;
                case (FindKind.RoomEffect, Sector room) when numbers.Contains(room.Special & RoomEffects.Mask):
                    e.Set(room, x => x.Special, (x, v) => x.Special = v, RoomEffects.With(room.Special, One(FindKind.RoomEffect, text, things)!.Value));
                    break;
                case (FindKind.Tag, _):
                    changed = Retag(e, element, int.Parse(query.What.Trim(), CultureInfo.InvariantCulture), int.Parse(text, CultureInfo.InvariantCulture));
                    break;
                default:
                    changed = false;
                    break;
            }

            if (changed) count++;
        }

        return count;
    }

    private static bool Retag(StructureEdit e, Element element, int from, int to)
    {
        switch (element)
        {
            case Sector room when room.Tag == from:
                e.Set(room, x => x.Tag, (x, v) => x.Tag = v, to);
                return true;
            case Line wall when wall.Special != 0 && Switches.NamesAreas(wall.Special) && wall.Args[0] == from:
                e.Set(wall, x => x.Args[0], (x, v) => x.Args[0] = v, to);
                return true;
            case Line wall when wall.LineId == from:
                e.Set(wall, x => x.LineId, (x, v) => x.LineId = v, to);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A picture name as typed, as a test: whole name, any case, * any letters, ? one letter.</summary>
    public static Func<string, bool> Pattern(string typed)
    {
        var text = typed.Trim();
        if (!text.Contains('*') && !text.Contains('?')) return name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase);
        var regex = new Regex("^" + Regex.Escape(text).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch;
    }

    /// <summary>The numbers a typed object, action or effect stands for: a number as it is, else every one whose name holds the letters.</summary>
    private static HashSet<int> Numbers(FindQuery query, ThingCatalog? things)
    {
        var text = query.What.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return [n];
        bool Has(string name) => name.Contains(text, StringComparison.OrdinalIgnoreCase);
        return query.Kind switch
        {
            FindKind.Object => [.. (things?.All ?? []).Where(t => Has(t.Name) || string.Equals(t.ClassName, text, StringComparison.OrdinalIgnoreCase)).Select(t => t.Number)],
            FindKind.WallAction => [.. ActionCatalog.All.Where(a => Has(a.Name)).Select(a => a.Number)],
            FindKind.RoomEffect => [.. RoomEffects.All.Where(x => x.Number != 0 && Has(x.Name)).Select(x => x.Number)],
            _ => [],
        };
    }

    /// <summary>The one number typed text names for a replacement: a number, or a whole name (an object's name or class, an action's, an effect's).</summary>
    private static int? One(FindKind kind, string text, ThingCatalog? things)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n >= 0 ? n : null;
        var names = kind switch
        {
            FindKind.Object => (things?.All ?? []).Where(t => string.Equals(t.Name, text, StringComparison.OrdinalIgnoreCase) || string.Equals(t.ClassName, text, StringComparison.OrdinalIgnoreCase)).Select(t => t.Number),
            FindKind.WallAction => ActionCatalog.All.Where(a => string.Equals(a.Name, text, StringComparison.OrdinalIgnoreCase)).Select(a => a.Number),
            FindKind.RoomEffect => RoomEffects.All.Where(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase)).Select(x => x.Number),
            _ => [],
        };
        var list = names.Distinct().ToList();
        return list.Count == 1 ? list[0] : null;
    }

    // An object found by difficulty: on any of the chosen ones (Easy is skill 1 or 2, Hard 4 or 5, as the Inspector shows them).
    private static bool OnSkills(KeyValuePair<string, Core.Maps.Udmf.UdmfValue>[] extra, FindSkills skills)
    {
        if (skills == FindSkills.All) return true;
        bool On(params string[] flags) => flags.Any(flag => Checks.Problems.Flag(extra, flag));
        return (skills.HasFlag(FindSkills.Easy) && On("skill1", "skill2")) || (skills.HasFlag(FindSkills.Normal) && On("skill3")) || (skills.HasFlag(FindSkills.Hard) && On("skill4", "skill5"));
    }
}
