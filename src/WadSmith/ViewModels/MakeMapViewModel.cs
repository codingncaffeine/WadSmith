using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Generate;
using WadSmith.Editor.Geometry;

namespace WadSmith.App.ViewModels;

/// <summary>One of a row of segments (Small · Medium · Large · Huge): checked when it is the group's choice.</summary>
internal sealed partial class SegmentChoice(SegmentGroup group, int index, string label, string tooltip, bool first, bool last) : ObservableObject
{
    public string Label { get; } = label;

    public string Tooltip { get; } = tooltip;

    public bool IsFirst { get; } = first;

    public bool IsLast { get; } = last;

    public bool IsChecked
    {
        get => group.Index == index;
        set
        {
            if (value) group.Index = index;
            else OnPropertyChanged();
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>A row of segments, one of them chosen.</summary>
internal sealed partial class SegmentGroup : ObservableObject
{
    public SegmentGroup(IReadOnlyList<(string Label, string Tooltip)> items, int index)
    {
        _index = index;
        Items = [.. items.Select((x, i) => new SegmentChoice(this, i, x.Label, x.Tooltip, i == 0, i == items.Count - 1))];
    }

    public IReadOnlyList<SegmentChoice> Items { get; }

    public event Action? Changed;

    private int _index;

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            foreach (var i in Items) i.Refresh();
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }
}

/// <summary>A game the map may use things from: its textures and its monsters, each a check (D50 "Use things from").</summary>
internal sealed partial class SourceRow(MakeMapViewModel owner, FoundGame game, string title, bool isBase, bool doom2Only) : ObservableObject
{
    public FoundGame Game { get; } = game;

    public string Title { get; } = title;

    public string Family => Game.Game.Engine;

    public bool IsBase { get; } = isBase;

    /// <summary>The row of Doom II's monsters for a Doom (1) map: only what Doom lacks, and no textures.</summary>
    public bool Doom2Only { get; } = doom2Only;

    public bool CanTextures => !Doom2Only;

    /// <summary>Whether the row offers its music (the row of Doom II's monsters for a Doom map does not).</summary>
    public bool CanMusic => !Doom2Only;

    [ObservableProperty]
    private bool _textures = isBase;

    [ObservableProperty]
    private bool _monsters = isBase;

    /// <summary>What the row offers, in words: "12 monsters · 5 looks", or why it offers less.</summary>
    [ObservableProperty]
    private string _note = "";

    /// <summary>The monsters of this game that cannot be carried into the base game yet (their pictures share a name with its own).</summary>
    public IReadOnlySet<string> Refused { get; set; } = new HashSet<string>();

    /// <summary>The game opened for its pictures (read on a worker when the page opens); null until then or when it could not be read.</summary>
    public GameResources? Resources { get; set; }

    partial void OnTexturesChanged(bool value) => owner.Remake();

    partial void OnMonstersChanged(bool value) => owner.Remake();

    /// <summary>Whether the map may play one of this game's tracks.</summary>
    [ObservableProperty]
    private bool _music = isBase;

    partial void OnMusicChanged(bool value) => owner.Remake();

    /// <summary>Whether this game's weapons (and their ammunition) join the base game's along the way: another game's only.</summary>
    [ObservableProperty]
    private bool _weapons;

    /// <summary>Whether the row offers its weapons: another game of another family (the base game's are always there).</summary>
    public bool CanWeapons => !IsBase && !Doom2Only;

    partial void OnWeaponsChanged(bool value) => owner.Remake();

    /// <summary>The monsters the row gives when its Monsters box is checked.</summary>
    public IReadOnlyList<MonsterKind> MonsterKinds(GeneratorSettings s)
    {
        var palette = FamilyPalette.For(Family);
        if (palette is null) return [];
        if (Doom2Only) return [.. palette.Monsters.Where(m => m.Doom2 && !Refused.Contains(m.Class))];
        var doom2 = Game.Game.MapNames != "ExMy";
        return [.. palette.Monsters.Where(m => (doom2 || !m.Doom2) && !Refused.Contains(m.Class))];
    }

    /// <summary>The looks the row gives when its Textures box is checked.</summary>
    public IReadOnlyList<LookChoice> LookKinds() => CanTextures ? GeneratorSettings.OwnLooks(Family) : [];
}

/// <summary>
/// "Make me a map" (D50, §3.14): one page from a game's card. Which games to use things from, size, outdoors, challenge,
/// locked doors, bosses, the features and the seed; the plan redrawn on a worker after every change with its facts and
/// the editor's own verdicts under it; Make it puts a project in the made-maps folder and opens it.
/// </summary>
internal sealed partial class MakeMapViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly GameDefinition _game;
    private readonly LatestOnWorker<(MapPlan? Plan, string Facts, string Verdict, string? Error)> _preview;
    private bool _loading = true;

    /// <summary>The page for <paramref name="game"/>; with <paramref name="like"/> (a made map's choices) it starts from those, else from the last map made.</summary>
    public MakeMapViewModel(AppServices services, MainViewModel main, FoundGame game, string target, GeneratorSettings? like = null)
    {
        _services = services;
        _main = main;
        _game = game.Game;
        // A made map plays in GZDoom: it uses dynamic lights, GZDoom's damage fields and other games' objects. A card set to a
        // classic target is told so on the page.
        Target = Targets.GZDoom;
        TargetNote = Targets.IsClassic(target) ? $"Made maps play in GZDoom; {Targets.TitleOf(target)} maps come later." : "";
        GameTitle = game.Game.Title;
        Size = new SegmentGroup([.. Enum.GetValues<MapSize>().Select(z => (z.ToString(), $"{GeneratorSettings.RoomsFor(z)} rooms."))], 1);
        Style = new SegmentGroup(
        [
            ("Base", "Rooms and halls with square corners, as in a tech base."),
            ("Caves", "Rough rooms with their corners cut, low and dim."),
            ("Castle", "Big tall halls with rows of pillars."),
            ("Mixed", "Each part of the map, between locked doors, in a style of its own."),
        ], 0);
        Outdoors = new SegmentGroup([("None", "Every room has a ceiling."), ("Some", "About one room in five is open to the sky."), ("Lots", "Nearly half the rooms are open to the sky.")], 1);
        var keyNames = Editor.Smart.Keys.Of(_game.Engine).Select(k => k.Colour).ToList();
        var keys = new List<(string, string)> { ("None", "No locked doors: every door opens.") };
        for (var k = 1; k <= Math.Min(3, FamilyPalette.For(_game.Engine)?.Keys.Count ?? 0); k++) keys.Add((k.ToString(CultureInfo.InvariantCulture), $"{k} locked door{(k > 1 ? "s" : "")}, each with its key earlier on the way: {string.Join(", then ", keyNames.Take(k))}."));
        Keys = new SegmentGroup(keys, Math.Min(2, keys.Count - 1));
        Bosses = new SegmentGroup([("None", "No boss: the exit room ends the map."), ("1", "One boss in an arena before the exit."), ("2", "Two bosses in the arena."), ("3", "Three bosses in the arena.")], 1);
        Maps = new SegmentGroup([("One", "One map."), ("3", "An episode of three maps in a row, each harder than the last, the bosses in the last."), ("5", "An episode of five maps, a boss halfway and the rest in the last."), ("8", "An episode of eight maps: an evening's play.")], 0);
        foreach (var g in new[] { Size, Style, Outdoors, Keys, Bosses, Maps }) g.Changed += Remake;
        _seed = SeedWords.Shuffle(Random.Shared);
        _preview = new LatestOnWorker<(MapPlan?, string, string, string?)>(MainViewModel.PostToUi, Show, "make-me-a-map preview");
        BuildSources(game);
        if ((like ?? (services.Settings.Current.LastMadeMap is { } last ? SettingsJson.Read(last) : null)) is { } start) Apply(start);
        _loading = false;
        Remake();
        _ = ReadSourcesAsync();
    }

    public string GameTitle { get; }

    /// <summary>What the map plays in (D51): GZDoom.</summary>
    public string Target { get; }

    /// <summary>Why a classic target chosen on the card is not used, in words; empty otherwise.</summary>
    public string TargetNote { get; }

    public ObservableCollection<SourceRow> Sources { get; } = [];

    public SegmentGroup Size { get; }

    public SegmentGroup Style { get; }

    /// <summary>One map, or an episode of several.</summary>
    public SegmentGroup Maps { get; }

    private static readonly int[] MapCounts = [1, 3, 5, 8];

    public SegmentGroup Outdoors { get; }

    public SegmentGroup Keys { get; }

    public SegmentGroup Bosses { get; }

    /// <summary>Whether the base game has keys (Strife's doors open with quest items).</summary>
    public bool HasKeys => Keys.Items.Count > 1;

    [ObservableProperty]
    private double _challenge = 0.5;

    [ObservableProperty]
    private string _challengeLabel = "";

    [ObservableProperty]
    private bool _secrets = true;

    [ObservableProperty]
    private bool _stairs = true;

    [ObservableProperty]
    private bool _lifts = true;

    [ObservableProperty]
    private bool _teleporters = true;

    [ObservableProperty]
    private bool _darkPlaces = true;

    [ObservableProperty]
    private bool _ambushes = true;

    [ObservableProperty]
    private bool _decorations = true;

    [ObservableProperty]
    private bool _traps = true;

    [ObservableProperty]
    private bool _hazards = true;

    [ObservableProperty]
    private string _seed;

    /// <summary>The plan the preview draws; null while the first is made.</summary>
    [ObservableProperty]
    private MapPlan? _plan;

    /// <summary>"Misty Cavern · 14 rooms · 2 keys · 2 secrets · about 7 minutes".</summary>
    [ObservableProperty]
    private string _facts = "";

    /// <summary>The monsters on each difficulty, and the editor's own verdicts on the map made.</summary>
    [ObservableProperty]
    private string _verdict = "";

    /// <summary>What the locked doors and bosses chosen are, in words.</summary>
    [ObservableProperty]
    private string _keysLabel = "";

    [ObservableProperty]
    private string _bossesLabel = "";

    /// <summary>The track the seed picks from the games whose Music box is checked, in words.</summary>
    [ObservableProperty]
    private string _musicLabel = "";

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _status = "";

    /// <summary>Where Make it puts the map.</summary>
    public string SaveFolder => _services.MadeMapsFolder;

    public string SaveLabel => $"Saved in {SaveFolder}";

    partial void OnChallengeChanged(double value) => Remake();

    partial void OnSecretsChanged(bool value) => Remake();

    partial void OnStairsChanged(bool value) => Remake();

    partial void OnLiftsChanged(bool value) => Remake();

    partial void OnTeleportersChanged(bool value) => Remake();

    partial void OnDarkPlacesChanged(bool value) => Remake();

    partial void OnAmbushesChanged(bool value) => Remake();

    partial void OnDecorationsChanged(bool value) => Remake();

    partial void OnTrapsChanged(bool value) => Remake();

    partial void OnHazardsChanged(bool value) => Remake();

    partial void OnSeedChanged(string value) => Remake();

    /// <summary>The Doom family's games a map may borrow from, fullest first: the id games, then Freedoom.</summary>
    private static readonly string[] DoomOrder = ["doom2", "doom-ultimate", "doom", "plutonia", "tnt", "freedoom2", "freedoom1", "doom-shareware"];

    /// <summary>The rows: the base game first, Doom II's monsters for a Doom map, then one game of each other family the generator knows.</summary>
    private void BuildSources(FoundGame baseGame)
    {
        var found = (_services.Games ?? []).Where(g => g.Game.IsBaseGame && GeneratorSettings.CanMake(g.Game)).DistinctBy(g => g.Game.Id).ToList();
        Sources.Add(new SourceRow(this, baseGame, baseGame.Game.Title, true, false));
        if (baseGame.Game.Engine == "doom" && baseGame.Game.MapNames == "ExMy")
        {
            var doom2 = DoomOrder.Select(id => found.FirstOrDefault(f => f.Game.Id == id)).OfType<FoundGame>().FirstOrDefault(f => f.Game.MapNames != "ExMy");
            if (doom2 is not null) Sources.Add(new SourceRow(this, doom2, $"{doom2.Game.Title}'s monsters", false, true));
        }

        foreach (var family in FamilyPalette.Families.Where(f => f != baseGame.Game.Engine))
        {
            // The fullest game of the family: for Doom, the id games and Freedoom in that order (a total conversion such as Hacx
            // draws its own monsters under Doom's names, so it is not offered); otherwise games.json's order, most specific first,
            // a paid game before a free one.
            var best = family == "doom"
                ? DoomOrder.Select(id => found.FirstOrDefault(f => f.Game.Id == id)).OfType<FoundGame>().FirstOrDefault()
                : GameCatalog.All.Where(d => d.Engine == family).Select(d => found.FirstOrDefault(f => f.Game.Id == d.Id)).OfType<FoundGame>().OrderBy(f => f.Game.Free).FirstOrDefault();
            if (best is not null) Sources.Add(new SourceRow(this, best, best.Game.Title, false, false));
        }

        foreach (var row in Sources) row.Note = Offer(row);
    }

    /// <summary>Opens each row's game on a worker, so that what cannot be carried in is known, and says what each offers.</summary>
    private async Task ReadSourcesAsync()
    {
        var engine = _services.EngineData;
        var games = _services.FoundGames();
        foreach (var row in Sources.ToList())
        {
            var resources = await Task.Run(() =>
            {
                try
                {
                    return GameResources.Load(row.Game.Game.Id, null, games, engine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                {
                    Log.Warn($"{row.Game.Game.Title} could not be read for Make me a map: {ex.Message}");
                    return null;
                }
            }).ConfigureAwait(true);
            row.Resources = resources;
        }

        var baseResources = Sources[0].Resources;
        foreach (var row in Sources.Where(r => !r.IsBase && r.Resources is not null && baseResources is not null))
        {
            row.Refused = row.MonsterKinds(Settings()).Where(m => row.Resources!.Things.FindClass(m.Class) is not { } t || Editor.Editing.ActorBorrowing.WhyNot(baseResources!, row.Resources, t) is not null).Select(m => m.Class).ToHashSet();
        }

        foreach (var row in Sources) row.Note = Offer(row);
        Remake();
    }

    /// <summary>What a row offers, in words.</summary>
    private string Offer(SourceRow row)
    {
        var usable = row.MonsterKinds(Settings()).Count;
        var looks = row.LookKinds().Count;
        var parts = new List<string> { $"{usable} monster{(usable == 1 ? "" : "s")}" };
        if (row.Refused.Count > 0) parts[0] += $" (not yet: {string.Join(", ", row.Refused.Order(StringComparer.Ordinal).Select(Spaced))}, whose pictures share a name with {Sources[0].Title}'s)";
        if (row.CanTextures) parts.Add($"{looks} look{(looks == 1 ? "" : "s")}");
        if (row.CanWeapons && FamilyPalette.For(row.Family) is { } weapons) parts.Add($"{weapons.Weapons.Count} weapons");
        if (!row.IsBase) parts.Add("copied in when used");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Sets the page to earlier choices (a made map's, or the last map made): every control, and each game's boxes by whether
    /// those choices used its monsters and looks. The seed stays new: the same choices make another map.
    /// </summary>
    private void Apply(GeneratorSettings s)
    {
        Size.Index = (int)s.Size;
        Maps.Index = Math.Max(0, Array.IndexOf(MapCounts, s.Maps));
        Style.Index = (int)s.Style;
        Outdoors.Index = (int)s.Outdoors;
        Keys.Index = Math.Clamp(s.Keys, 0, Keys.Items.Count - 1);
        Bosses.Index = Math.Clamp(s.Bosses, 0, 3);
        Challenge = s.Challenge;
        (Secrets, Stairs, Lifts, Teleporters, DarkPlaces, Ambushes, Decorations, Traps) = (s.Secrets, s.Stairs, s.Lifts, s.Teleporters, s.DarkPlaces, s.Ambushes, s.Decorations, s.Traps);
        Hazards = s.Hazards;
        foreach (var row in Sources)
        {
            var mine = row.MonsterKinds(s);
            row.Monsters = s.Monsters.Count == 0 ? row.IsBase : s.Monsters.Any(m => mine.Contains(m));
            row.Textures = row.CanTextures && (s.Looks.Count == 0 ? row.IsBase : s.Looks.Any(l => l.Family == row.Family));
            row.Music = row.CanMusic && (s.MusicFrom.Count == 0 ? row.IsBase : s.MusicFrom.Contains(row.Game.Game.Id));
            row.Weapons = row.CanWeapons && s.WeaponsFrom.Contains(row.Family);
        }
    }

    /// <summary>The track the seed picks from the tracks of the games whose Music box is checked (those read so far).</summary>
    private (string Game, string Track)? PickMusic(GeneratorSettings s) => MadeMusic.Pick(s.Seed, MusicPool());

    /// <summary>The tracks of the games whose Music box is checked (those read so far), by game.</summary>
    private List<(string Game, string Track)> MusicPool() =>
        [.. Sources.Where(r => r.Music && r.CanMusic && r.Resources is not null).SelectMany(r => r.Resources!.Music.Select(t => (r.Game.Game.Id, t)))];

    /// <summary>The page's choices as the generator's settings.</summary>
    public GeneratorSettings Settings()
    {
        var s = GeneratorSettings.ForGame(_game) with
        {
            GZDoom = !Targets.IsClassic(Target),
            Size = (MapSize)Size.Index,
            Maps = MapCounts[Maps.Index],
            Style = (MapStyle)Style.Index,
            Outdoors = (OutdoorShare)Outdoors.Index,
            Challenge = Challenge,
            Keys = Keys.Index,
            Bosses = Bosses.Index,
            Secrets = Secrets,
            Stairs = Stairs,
            Lifts = Lifts,
            Teleporters = Teleporters,
            DarkPlaces = DarkPlaces,
            Ambushes = Ambushes,
            Decorations = Decorations,
            Traps = Traps,
            Hazards = Hazards,
            Seed = SeedWords.Tidy(Seed),
            MusicFrom = [.. Sources.Where(r => r.Music && r.CanMusic).Select(r => r.Game.Game.Id)],
            WeaponsFrom = [.. Sources.Where(r => r.Weapons && r.CanWeapons).Select(r => r.Family).Distinct()],
        };
        var monsters = Sources.Where(r => r.Monsters).SelectMany(r => r.MonsterKinds(s)).Distinct().ToList();
        var looks = Sources.Where(r => r.Textures).SelectMany(r => r.LookKinds()).Distinct().ToList();
        var onlyOwnMonsters = Sources.All(r => r.IsBase == r.Monsters);
        var onlyOwnLooks = Sources.All(r => r.IsBase == r.Textures || !r.CanTextures);
        return s with { Monsters = onlyOwnMonsters || monsters.Count == 0 ? [] : monsters, Looks = onlyOwnLooks || looks.Count == 0 ? [] : looks };
    }

    /// <summary>Plans the map again on a worker (the newest choices only) and redraws the preview.</summary>
    public void Remake()
    {
        if (_loading) return;
        var s = Settings();
        ChallengeLabel = ChallengeWords(s.Family, s.Challenge);
        KeysLabel = s.Keys == 0 ? "Every door opens." : $"{string.Join(", then ", Editor.Smart.Keys.Of(s.Family).Take(s.Keys).Select(k => k.Colour))}, each key before its door.";
        var bosses = (s.Monsters.Count > 0 ? s.Monsters : s.OwnMonsters()).Where(m => m.Boss).Select(m => Spaced(m.Class)).ToList();
        BossesLabel = s.Bosses == 0 ? "The exit room ends the map." : bosses.Count == 0 ? "The toughest monster stands in for a boss." : $"{string.Join(" and ", Enumerable.Range(0, s.Bosses).Select(i => bosses[i % bosses.Count]).GroupBy(b => b).Select(g => g.Count() > 1 ? $"{g.Count()} × {g.Key}" : g.Key))} in an arena before the exit.";
        var pick = PickMusic(s);
        MusicLabel = pick is { } p ? $"Music: {p.Track}, from {Sources.First(r => r.Game.Game.Id == p.Game).Game.Game.Title}." : Sources.Any(r => r.Music && r.Resources is null) ? "Music: reading your games…" : $"Music: the game's own for {s.Slot}.";
        var baseThings = Sources.FirstOrDefault()?.Resources?.Things;
        _preview.Request(() => Preview(s, baseThings));
    }

    private static (MapPlan?, string, string, string?) Preview(GeneratorSettings s, ThingCatalog? things)
    {
        try
        {
            // An episode's first map, as it will be made (its challenge, no bosses before the last map).
            var plan = Planner.Plan(Episodes.Of(s)[0]);
            var made = MapMaker.Build(plan, new Lent(m => m.Number, null, (_, number) => number));
            var g = MapGeometry.Build(made.Map.Frame);
            var walk = Progression.Walk(g, s.Family);
            var problems = Problems.Check(g, null, Look.For(s.Family), s.Family);
            var (easy, normal, hard) = plan.MonsterCounts;
            var names = SkillNames(s.Family);
            var keys = plan.Links.Count(l => l.Key is not null);
            var secrets = plan.Rooms.Count(r => r.Role == RoomRole.Secret);
            var traps = plan.Rooms.Count(r => r.Role == RoomRole.Closet);
            var episode = s.Maps > 1 ? $" · map 1 of {s.Maps}" : "";
            var facts = $"{SeedWords.Title(s.Seed)}{episode} · {plan.Rooms.Count - traps} rooms · {Count(keys, "key")} · {Count(secrets, "secret")}{(traps > 0 ? $" · {Count(traps, "trap")}" : "")} · about {Count(plan.Minutes, "minute")}";
            var verdict = new List<string> { $"{hard} monsters on {names[3]}, {normal} on {names[2]}, {easy} on {names[1]}." };
            if (s.Family == "doom" && s.Monsters.All(m => m.Family == "doom") && things is not null && Balance.Read(made.Map.Things, things, s.Family) is { } balance)
            {
                verdict.Add(balance.Single(r => r.Difficulty == "Hard").Sentence.Replace("Hard:", $"On {names[3]}:", StringComparison.Ordinal));
            }

            verdict.Add(walk.ExitReached && problems.Count == 0 && made.Failed.Count == 0
                ? "Checked: the exit can be reached, every key before its door, and Problems has nothing to say."
                : $"Not right yet: {(made.Failed.Count > 0 ? made.Failed[0].Said : problems.Count > 0 ? problems[0].Sentence : "the exit cannot be reached")}");
            return (plan, facts, string.Join("\n", verdict), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return (null, "", "", ex.Message);
        }
    }

    private void Show((MapPlan? Plan, string Facts, string Verdict, string? Error) result)
    {
        if (result.Error is { } error)
        {
            Status = error;
            return;
        }

        Plan = result.Plan;
        Facts = result.Facts;
        Verdict = result.Verdict;
        Status = "";
    }

    private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static string Spaced(string className) => string.Concat(className.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(className[i - 1]) ? $" {c}" : c.ToString()));

    /// <summary>The base game's own difficulty names, easiest first.</summary>
    public static IReadOnlyList<string> SkillNames(string family) => family switch
    {
        "heretic" => ["Thou needeth a wet-nurse", "Yellowbellies-r-us", "Bringest them oneth", "Thou art a smite-meister", "Black plague possesses thee"],
        "hexen" => ["Squire", "Knight", "Warrior", "Berserker", "Titan"],
        "strife" => ["Training", "Rookie", "Veteran", "Elite", "Bloodbath"],
        _ => ["I'm too young to die", "Hey, not too rough", "Hurt me plenty", "Ultra-Violence", "Nightmare!"],
    };

    /// <summary>The challenge in the game's words: its difficulty name at that point and what it means.</summary>
    public static string ChallengeWords(string family, double challenge)
    {
        var names = SkillNames(family);
        var at = Math.Clamp((int)Math.Floor(challenge * 5), 0, 4);
        var how = at switch
        {
            0 => "few monsters, and supplies to spare",
            1 => "a gentle fight with plenty to pick up",
            2 => "a fair fight",
            3 => "crowded rooms and tougher monsters, with little to waste",
            _ => "packed rooms and the toughest monsters early",
        };
        return $"{names[at]}: {how}";
    }

    [RelayCommand]
    private void Shuffle() => Seed = SeedWords.Shuffle(Random.Shared);

    /// <summary>Every choice at random, then a new seed.</summary>
    [RelayCommand]
    private void SurpriseMe()
    {
        _loading = true;
        var r = Random.Shared;
        Size.Index = r.Next(4);
        Style.Index = r.Next(4);
        Maps.Index = r.Next(2) == 0 ? 0 : r.Next(4);
        Outdoors.Index = r.Next(3);
        Keys.Index = r.Next(Keys.Items.Count);
        Bosses.Index = r.Next(4);
        Challenge = Math.Round(r.NextDouble(), 2);
        Secrets = r.Next(4) > 0;
        Stairs = r.Next(4) > 0;
        Lifts = r.Next(3) > 0;
        Teleporters = r.Next(2) > 0;
        DarkPlaces = r.Next(3) > 0;
        Ambushes = r.Next(2) > 0;
        Decorations = r.Next(5) > 0;
        Traps = r.Next(3) > 0;
        Hazards = r.Next(2) > 0;
        foreach (var row in Sources.Where(x => !x.IsBase))
        {
            row.Monsters = r.Next(2) > 0;
            row.Textures = row.CanTextures && r.Next(2) > 0;
            row.Music = row.CanMusic && r.Next(3) == 0;
            row.Weapons = row.CanWeapons && r.Next(3) == 0;
        }

        Seed = SeedWords.Shuffle(r);
        _loading = false;
        Remake();
    }

    [RelayCommand]
    private void Back() => _main.ShowWelcome();

    /// <summary>Chooses where made maps go (the same setting as in Preferences).</summary>
    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        if (_main.PickFolder is null) return;
        var folder = await _main.PickFolder("Choose the folder maps made for you go to").ConfigureAwait(true);
        if (folder is null) return;
        _services.Settings.Current.MadeMapsFolder = folder;
        _services.Settings.Save();
        OnPropertyChanged(nameof(SaveFolder));
        OnPropertyChanged(nameof(SaveLabel));
    }

    [RelayCommand]
    private Task MakeAsync() => Make(play: false);

    [RelayCommand]
    private Task MakeAndPlayAsync() => Make(play: true);

    /// <summary>
    /// Makes the map as a project in the made-maps folder, named after its seed: the plan, the other games' monsters and
    /// pictures it uses carried in, the map built and saved; then opens it in the editor (and plays it).
    /// </summary>
    private async Task Make(bool play)
    {
        if (Busy) return;
        Busy = true;
        Status = "Making the map…";
        var s = Settings();
        var rows = Sources.ToList();
        var tracks = MusicPool();
        GameResources? madeInto = null;
        try
        {
            var engine = _services.EngineData;
            var (session, notes) = await Task.Run(() =>
            {
                var folder = UniqueFolder(_services.MadeMapsFolder, SeedWords.Title(s.Seed));
                var said = new List<string>();
                GameResources? From(string family, bool doom2) => rows.FirstOrDefault(r => r.Family == family && (r.IsBase || r.Doom2Only == doom2) && !(r.IsBase && doom2))?.Resources
                    ?? rows.FirstOrDefault(r => r.Family == family && !r.IsBase)?.Resources;
                var (project, made) = MadeProjects.Create(folder, s, Target, (project, plan) =>
                {
                    // The project's game is opened once, for the first map; each map then carries in what it uses.
                    madeInto ??= _services.ResourcesFor(project);
                    if (madeInto is not { } into) return null;
                    var (lent, notes) = Editor.Generate.Sources.Carry(project, into, m => From(m.Family, m.Doom2 && m.Family == s.Family), l => From(l.Family, false), family => From(family, false), plan, engine);
                    said.AddRange(notes.Where(n => !said.Contains(n)));
                    return lent;
                });

                // Each map's music, picked by its own seed, once the maps are in the project.
                foreach (var map in made)
                {
                    if (MadeMusic.Pick(map.Plan.Settings.Seed, tracks) is not { } pick || madeInto is not { } inGame || rows.FirstOrDefault(r => r.Game.Game.Id == pick.Game)?.Resources is not { } from) continue;
                    try
                    {
                        MadeMusic.Use(project, inGame, from, pick.Track, map.Plan.Settings.Slot, engine);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                    {
                        said.Add($"The music {pick.Track} could not be copied: {ex.Message}");
                    }
                }

                return (EditorSession.Open(project, made[0].Plan.Settings.Slot, MainViewModel.HooksFor(_services.ResourcesFor(project), engine)), (IReadOnlyList<string>)said);
            }).ConfigureAwait(true);
            _services.Settings.Current.LastMadeMap = SettingsJson.Write(s);
            _services.Settings.Save();
            foreach (var n in notes) Log.Info($"Make me a map: {n}");
            var message = $"Your map, made from the seed {s.Seed}. Everything in it can be changed: drag a room, or press Tab and walk it.";
            if (notes.Count > 0) message += $" {notes[0]}";
            _main.OpenEditor(session, message);
            if (play && _main.Page is EditorViewModel editor) editor.PlayCommand.Execute(null);
        }
        catch (Exception ex) when (ex is CorruptDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Status = $"The map could not be made: {ex.Message}";
            Log.Warn(Status);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>A folder named <paramref name="name"/> in <paramref name="parent"/>, numbered when one of that name is there.</summary>
    internal static string UniqueFolder(string parent, string name)
    {
        Directory.CreateDirectory(parent);
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '-' : c));
        var folder = Path.Combine(parent, safe);
        for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(parent, $"{safe} {n}");
        return folder;
    }
}
