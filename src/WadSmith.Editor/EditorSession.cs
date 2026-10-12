using WadSmith.Core;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor;

public enum SaveState
{
    Saved,
    Pending,
    Saving,
    Failed,
}

/// <summary>
/// One map open for editing: the project it belongs to, the map, its history and selection, its
/// shapes for hit testing, and continuous saving (D24). There is no Save step: a change reaches the
/// project folder about a second after editing pauses. Only the UI thread changes the map; everything
/// that reads all of it (saving, versions, outlines, hit-test grids) works on a worker from the map's
/// read-only <see cref="EditorMap.Frame"/>, so nothing that grows with the map runs on the UI thread.
/// </summary>
/// <summary>What a session needs from the application: how to reach the UI thread, the project's game, and a picture maker for versions.</summary>
/// <param name="OnUiThread">Runs an action on the UI thread (the app passes its dispatcher).</param>
/// <param name="Resources">The project's game opened for textures, sprites and object names, when found.</param>
/// <param name="Picture">Paints a map as PNG for the versions list (on a worker); null for the automap drawing.</param>
/// <param name="EngineData">The engine's gzdoom.pk3, whose MAPINFO gives the game's own settings for each map; null when the engine is not installed.</param>
public sealed record SessionHooks(Action<Action> OnUiThread, GameResources? Resources = null, Func<MapFrame, byte[]?>? Picture = null, string? EngineData = null);

/// <summary>
/// The game's own settings for a map's slot, as the engine reads them: shown beside the map's settings, so "the game's"
/// has a value. Read on a worker when a session opens.
/// </summary>
/// <param name="Title">The game's name for the slot ("Entryway"); null when it has none.</param>
/// <param name="Music">The music lump the game plays there (D_RUNNIN).</param>
/// <param name="Sky">The game's sky texture there (SKY1).</param>
/// <param name="Next">Where the game's exit leads (MAP02, or an ending such as EndGameC).</param>
/// <param name="Slots">Every slot the game defines, in its order.</param>
/// <param name="Switches">The behaviour switches a remix keeps from its original (shorttex, light …); none for a new map.</param>
/// <param name="Foreign">The project holds a zmapinfo.txt WadSmith did not write, which it leaves alone: the settings are set there.</param>
public sealed record MapDefaults(string? Title, string? Music, string? Sky, string? Next, IReadOnlyList<string> Slots, IReadOnlyList<string> Switches, bool Foreign);

public sealed class EditorSession : IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(900);

    private readonly Action<Action> _onUiThread;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly LatestOnWorker<MapGeometry> _shapes;
    private readonly LatestOnWorker<(long Version, MoveSet Set, Func<MoveSet, Placement?> Make)> _placings;
    private MapGeometry _geometry;
    private long _savedVersion;
    private Task _lastSave = Task.CompletedTask;
    private bool _disposed;

    private readonly Func<MapFrame, byte[]?>? _picture;
    private Action? _whenCaughtUp;

    private EditorSession(Project project, string slot, EditorMap map, MapGeometry geometry, SessionHooks hooks)
    {
        var onUiThread = hooks.OnUiThread;
        Project = project;
        Resources = hooks.Resources;
        Hooks = hooks;
        _picture = hooks.Picture;
        Slot = slot;
        Map = map;
        History = new History(map);
        _geometry = geometry;
        _onUiThread = onUiThread;
        _savedVersion = map.Version;
        _timer = new Timer(_ => _onUiThread(CaptureAndSave), null, Timeout.Infinite, Timeout.Infinite);
        _shapes = new LatestOnWorker<MapGeometry>(onUiThread, g =>
        {
            if (_disposed || g.Version <= _geometry.Version) return;
            _geometry = g;
            GeometryChanged?.Invoke();
            if (_whenCaughtUp is { } then && g.Version == Map.Version)
            {
                _whenCaughtUp = null;
                then();
            }
        }, "the map's shapes");
        _placings = new(onUiThread, r =>
        {
            if (_disposed) return;
            if (r.Version != Map.Version)
            {
                // The map changed while what moves was gathered: gather again from the selection as it is now.
                TransformSelection(r.Make);
                return;
            }

            if (r.Set.IsEmpty || r.Make(r.Set) is not { } placement || placement.Flattens || placement == Placement.Identity) return;
            var gesture = History.NewGesture();
            History.Run(new TransformEdit(r.Set, placement, FlatSize), gesture);
            if (r.Set.Count <= MoveSet.LiveLimit) FinishMove(r.Set, gesture);
        }, "what a turn moves");
        map.Changed += _ =>
        {
            State = SaveState.Pending;
            StateChanged?.Invoke(State);
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            _shapes.Request(() => MapGeometry.Build(Map.Frame));
        };
        MapEntry = project.Map(slot) ?? new ProjectMap { Slot = slot, File = $"maps/{slot}.wad" };
        LoadMapDefaults();
    }

    public Project Project { get; }

    // Map settings: the name, music, sky and next map of this map, kept in the project's record and written as its zmapinfo.txt.

    /// <summary>The project's entry for this map: its settings as they are now (UI thread).</summary>
    public ProjectMap MapEntry { get; private set; }

    /// <summary>The game's own settings for this slot; null until read (a moment after opening) or when the game is not installed.</summary>
    public MapDefaults? Defaults { get; private set; }

    /// <summary>Raised when the map's settings change, or when the game's defaults for the slot arrive.</summary>
    public event Action? MapSettingsChanged;

    private Task _lastSettings = Task.CompletedTask;

    /// <summary>
    /// Changes the map's settings: the record is updated at once and written on a worker along with the project's
    /// zmapinfo.txt (the slot and file never change). UI thread.
    /// </summary>
    public void ChangeMapSettings(Func<ProjectMap, ProjectMap> change)
    {
        if (History.ReadOnly)
        {
            Tell(ReadOnlyNotice);
            return;
        }

        var entry = change(MapEntry) with { Slot = MapEntry.Slot, File = MapEntry.File };
        if (entry == MapEntry) return;
        MapEntry = entry;
        MapSettingsChanged?.Invoke();
        _lastSettings = Task.Run(async () =>
        {
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                Project.UpdateMap(Slot, _ => entry);
                WriteMapInfo();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or KeyNotFoundException)
            {
                Log.Warn($"The map settings of {Slot} could not be saved: {ex.Message}");
                _onUiThread(() => Notice?.Invoke($"The map settings could not be saved: {ex.Message}"));
            }
            finally
            {
                _writer.Release();
            }
        });
    }

    /// <summary>Writes the project's zmapinfo.txt from its maps' settings on a worker (before Play, so the game sees them).</summary>
    public Task RefreshMapInfoAsync() => _lastSettings = Task.Run(async () =>
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            WriteMapInfo();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
        {
            Log.Warn($"The project's zmapinfo.txt could not be written: {ex.Message}");
        }
        finally
        {
            _writer.Release();
        }
    });

    /// <summary>The project's zmapinfo.txt from its record and the game's files (worker, under the writer).</summary>
    private void WriteMapInfo()
    {
        var game = Resources?.Game ?? Core.Games.GameCatalog.Get(Project.Info.Game);
        if (game is null) return;
        if (!ProjectMapInfo.Refresh(Project, game, Resources?.Files ?? [], Hooks.EngineData)) Log.Info($"{ProjectMapInfo.FileName} in {Project.Folder} is the project's own: left alone.");
    }

    /// <summary>Reads the game's own settings for the slot on a worker and writes the project's zmapinfo.txt once, so a project made before has one.</summary>
    private void LoadMapDefaults()
    {
        _ = Task.Run(async () =>
        {
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                var foreign = ProjectMapInfo.HasForeign(Project);
                var game = Resources?.Game ?? Core.Games.GameCatalog.Get(Project.Info.Game);
                MapDefaults defaults;
                if (game is null || Resources is null || Resources.Files.Count == 0 || Hooks.EngineData is not { } engine || !File.Exists(engine))
                {
                    defaults = new MapDefaults(null, null, null, null, [], [], foreign);
                }
                else
                {
                    var containers = Resources.Files.Select(Core.Containers.ResourceContainer.Open).ToList();
                    var definitions = Core.Games.MapInfo.For(game, containers, engine);
                    var d = definitions.For(Slot);
                    var titles = Core.Games.MapTitles.For(game, containers[^1], engine);
                    var switches = ProjectMapInfo.SwitchesFor(MapEntry, containers[0], engine, []);
                    var music = definitions.Resolve(d?.Text("music"));
                    if (music is not null && Resources.Music.FirstOrDefault(m => m.Equals(music, StringComparison.OrdinalIgnoreCase) || m.Equals("D_" + music, StringComparison.OrdinalIgnoreCase)) is { } lump) music = lump;
                    defaults = new MapDefaults(titles.GetValueOrDefault(Slot), music, d?.Text("sky1"), d?.Text("next"), definitions.Slots, switches, foreign);
                }

                if (!foreign) WriteMapInfo();
                _onUiThread(() =>
                {
                    if (_disposed) return;
                    Defaults = defaults;
                    MapSettingsChanged?.Invoke();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
            {
                Log.Warn($"The game's settings for {Slot} could not be read: {ex.Message}");
            }
            finally
            {
                _writer.Release();
            }
        });
    }

    /// <summary>The project's game, opened for its textures, sprites and object names; null when it was not found.</summary>
    public GameResources? Resources { get; }

    /// <summary>What the application gave this session (passed on when the map is reopened).</summary>
    public SessionHooks Hooks { get; }

    /// <summary>The copy kept when the map opened, being written by a worker (null when it matched the newest copy).</summary>
    public Task<MapVersion?> OpeningVersion { get; private set; } = Task.FromResult<MapVersion?>(null);

    public string Slot { get; }

    public EditorMap Map { get; }

    public History History { get; }

    public Selection Selection { get; } = new();

    public SaveState State { get; private set; } = SaveState.Saved;

    /// <summary>What new rooms look like in this project's game.</summary>
    public Look Look => _look ?? Look.For(Engine);

    private Look? _look;

    /// <summary>Makes <paramref name="look"/> what new rooms (and the doors, stairs and switches made in them) look like from now on.</summary>
    public void ChooseLook(Look look)
    {
        _look = look;
        LookChanged?.Invoke();
    }

    /// <summary>Raised when <see cref="Look"/> changes.</summary>
    public event Action? LookChanged;

    /// <summary>
    /// The look of a room: its floor and ceiling pictures, heights and light, and the texture most of its solid walls
    /// show; its doors, tracks and switches stay the game's.
    /// </summary>
    public Look LookOf(Sector room)
    {
        var walls = Map.Lines.Where(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, room) && l.Front.Middle is not ("-" or "")).Select(l => l.Front.Middle);
        var wall = walls.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? Look.Wall;
        return Look with { Floor = room.FloorTexture, Ceiling = room.CeilingTexture, Wall = wall, FloorHeight = room.FloorHeight, CeilingHeight = room.CeilingHeight, Light = room.Light };
    }

    /// <summary>Raised with a sentence for the status strip when something the user did could not be done as asked.</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// Draws a shape as a room through the history (one undo step) and selects the areas it made. Works
    /// only near the shape, so it is quick on any map; if the map's shapes are still being rebuilt after
    /// the last change, the room is drawn the moment they are ready.
    /// </summary>
    public void DrawRoom(IReadOnlyList<MapPoint> shape)
    {
        if (Mirror.IsOn)
        {
            DrawRooms([shape, .. Mirror.Shapes(shape)], History.NewGesture(), []);
            return;
        }

        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => DrawRoom(shape);
            return;
        }

        try
        {
            IReadOnlyList<Sector> made = [];
            var geometry = _geometry;
            var look = Look;
            History.Record("Draw a room", e => made = ShapeDrawing.Draw(e, geometry, shape, look));
            if (made.Count > 0) Selection.Set([.. made]);
            else Notice?.Invoke("That room is already there, exactly as drawn.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Drawing a room in {Slot} failed: {ex.Message}");
            Notice?.Invoke($"That room could not be drawn there. {ex.Message}");
        }
    }

    /// <summary>A flat's size in the game, for keeping a carried floor's shift within one repeat; null when unknown.</summary>
    public (int Width, int Height)? FlatSize(string name) => Resources?.Textures.GetFlat(name) is { } image ? (image.Width, image.Height) : null;

    /// <summary>
    /// Re-cuts a straight flight into <paramref name="count"/> steps from the floor below to the floor above: its steps are
    /// joined into one room, then cut again as the Stairs tool cuts, one undo step. Says why not when it cannot.
    /// </summary>
    public void ReStep(Sector step, int count)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => ReStep(step, count);
            return;
        }

        if (StairsFinder.Recognise(_geometry, step) is not { } flight || count == flight.Count || count < 1) return;
        if (StairsFinder.Run(_geometry, flight) is not { } run)
        {
            Notice?.Invoke("Only straight stairs whose steps fill one rectangle can be cut again; these steps have another shape.");
            return;
        }

        // Checked before anything changes: a refusal after the join would leave the steps joined.
        var length = run.AlongX ? Math.Abs(run.To.X - run.From.X) : Math.Abs(run.To.Y - run.From.Y);
        var rise = (flight.Above - flight.Below) / (count + 1);
        if (length / count < Stairs.MinDepth || Math.Abs(rise) > Stairs.MaxRise)
        {
            Notice?.Invoke(length / count < Stairs.MinDepth
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} steps would each be under {Stairs.MinDepth:0} deep; the most that fit is {(int)Math.Floor(length / Stairs.MinDepth)}.")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} steps would each rise {Math.Abs(rise):0}, more than the player can climb ({Stairs.MaxRise:0})."));
            return;
        }

        var gesture = History.NewGesture();
        var joinedIn = _geometry;
        History.Record("Change the steps", e => Rooms.Join(e, joinedIn, [.. flight.Steps]), gesture);
        void Cut()
        {
            if (_geometry.Version != Map.Version)
            {
                _whenCaughtUp = Cut;
                return;
            }

            (IReadOnlyList<Sector> Steps, string? Why) made = ([], null);
            var cutIn = _geometry;
            var look = Look;
            History.Record("Change the steps", e => made = Stairs.Make(e, cutIn, run.From, run.To, look, count, run.AlongX), gesture);
            if (made.Why is { } why) Notice?.Invoke(why);
            else Selection.Set([.. made.Steps]);
        }

        Cut();
    }

    /// <summary>What the engine said about the map the last time it was played (Phase 4), on the map's own elements.</summary>
    public IReadOnlyList<Checks.EngineFinding> LastPlay { get; private set; } = [];

    /// <summary>Raised when <see cref="LastPlay"/> changes.</summary>
    public event Action? LastPlayChanged;

    /// <summary>Reads a Play's log on a worker (<paramref name="played"/>: the map as it was saved for that Play).</summary>
    public void ReadPlayLog(string logPath, MapFrame played)
    {
        Task.Run(() =>
        {
            IReadOnlyList<Checks.EngineFinding> found = [];
            try
            {
                if (File.Exists(logPath)) found = Checks.EngineLog.Read(File.ReadAllText(logPath), played);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"The engine's log {logPath} could not be read: {ex.Message}");
            }

            _onUiThread(() =>
            {
                LastPlay = found;
                LastPlayChanged?.Invoke();
            });
        });
    }

    /// <summary>Says something in the status strip, for the views (a drag refused, …).</summary>
    public void Tell(string sentence) => Notice?.Invoke(sentence);

    /// <summary>What a refused change says on a map opened to learn from (D28).</summary>
    public const string ReadOnlyNotice = "This is the game's own map, open to learn from: nothing in it changes. Remix it to make your own copy.";

    /// <summary>Puts the selected rooms and objects in the group <paramref name="name"/>, one undo step.</summary>
    public void GroupSelection(string name)
    {
        var members = Grouping.MembersOf(Selection.Items);
        if (members.Count == 0 || string.IsNullOrWhiteSpace(name))
        {
            Notice?.Invoke(members.Count == 0 ? "Select rooms or things to group; walls and corners go with the rooms they belong to." : "Give the group a name.");
            return;
        }

        History.Record("Group", e => Grouping.Name(e, members, name));
        Notice?.Invoke($"{(members.Count == 1 ? "It is" : $"The {members.Count} are")} in the group {name.Trim()} now: with nothing selected, the Inspector lists it under Groups to select, lock or hide.");
    }

    /// <summary>Puts rooms and objects in the group <paramref name="name"/>, or out of any when it is empty; one undo step.</summary>
    public void NameGroup(IReadOnlyList<Element> members, string name)
    {
        var live = members.Where(m => m.OnMap).ToList();
        if (live.Count == 0) return;
        History.Record(string.IsNullOrWhiteSpace(name) ? "Ungroup" : "Group", e => Grouping.Name(e, live, name));
        if (!string.IsNullOrWhiteSpace(name)) Notice?.Invoke($"{(live.Count == 1 ? "It is" : $"The {live.Count} are")} in the group {name.Trim()}: with nothing selected, the Inspector lists it to select or lock.");
    }

    /// <summary>Locks rooms and objects, one undo step, and lets go of the selection.</summary>
    public void LockMembers(IReadOnlyList<Element> members)
    {
        History.Record("Lock", e => Grouping.Lock(e, members.Where(m => m.OnMap), true));
        Selection.Clear();
        Notice?.Invoke("Locked: it can no longer be selected or changed. Unlock it with nothing selected (Esc), under Groups, or Ctrl+Z.");
    }

    /// <summary>Takes a group's members out of it (and unlocks them), one undo step.</summary>
    public void Ungroup(MapGroup group) => History.Record("Ungroup", e => Grouping.Name(e, group.Members.Where(m => m.OnMap), null));

    /// <summary>Locks or unlocks a group, one undo step; locked members leave the selection.</summary>
    public void LockGroup(MapGroup group, bool locked)
    {
        History.Record(locked ? "Lock a group" : "Unlock a group", e => Grouping.Lock(e, group.Members.Where(m => m.OnMap), locked));
        if (locked) Selection.Set([.. Selection.Items.Where(e => !Grouping.Holds(_geometry, e))]);
    }

    /// <summary>The groups hidden on the plan and in 3D, this session only (never saved): for clarity while working elsewhere.</summary>
    public IReadOnlySet<string> Hidden => _hidden;

    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    /// <summary>Raised when <see cref="Hidden"/> changes.</summary>
    public event Action? HiddenChanged;

    /// <summary>Hides or shows a group; hidden members leave the selection.</summary>
    public void Hide(string group, bool hidden)
    {
        if (!(hidden ? _hidden.Add(group) : _hidden.Remove(group))) return;
        if (hidden) Selection.Set([.. Selection.Items.Where(e => !Grouping.Hides(_geometry, e, _hidden))]);
        HiddenChanged?.Invoke();
    }

    /// <summary>Selects a group's members, unless it is locked.</summary>
    public void SelectGroup(MapGroup group)
    {
        if (group.Locked)
        {
            Notice?.Invoke($"{group.Name} is locked: unlock it with nothing selected, under Groups, to select or change it.");
            return;
        }

        Selection.Set([.. group.Members.Where(m => m.OnMap)]);
    }

    /// <summary>
    /// Symmetry mode (§3.4): rooms drawn and objects placed are repeated about the middle as <see cref="Mirror"/> says; Off
    /// by default.
    /// </summary>
    public Mirror Mirror
    {
        get => _mirror;
        set
        {
            _mirror = value;
            MirrorChanged?.Invoke();
        }
    }

    private Mirror _mirror = Mirror.Off;

    /// <summary>Raised when <see cref="Mirror"/> changes.</summary>
    public event Action? MirrorChanged;

    /// <summary>
    /// Draws a room and its mirror images one after another, each once the shapes include the one before (so a copy that
    /// touches the original shares its walls), as one undo step; selects them all.
    /// </summary>
    private void DrawRooms(IReadOnlyList<IReadOnlyList<MapPoint>> shapes, long gesture, List<Sector> made)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => DrawRooms(shapes, gesture, made);
            return;
        }

        try
        {
            var geometry = _geometry;
            var look = Look;
            History.Record("Draw a room", e => made.AddRange(ShapeDrawing.Draw(e, geometry, shapes[0], look)), gesture);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Drawing a mirrored room in {Slot} failed: {ex.Message}");
            Notice?.Invoke($"A copy of that room could not be drawn there. {ex.Message}");
        }

        if (shapes.Count > 1)
        {
            DrawRooms([.. shapes.Skip(1)], gesture, made);
            return;
        }

        if (made.Any(s => s.OnMap)) Selection.Set([.. made.Where(s => s.OnMap)]);
    }

    /// <summary>
    /// Finishes corners clicked one by one without closing them on the first: a line from one wall of a room to
    /// another, inside it, splits the room (see <see cref="Splitting"/>); three or more corners otherwise close
    /// into a room as <see cref="DrawRoom"/> does. One undo step either way.
    /// </summary>
    public void FinishCorners(IReadOnlyList<MapPoint> corners)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => FinishCorners(corners);
            return;
        }

        if (corners.Count >= 2 && Splitting.Across(_geometry, corners) is not null)
        {
            (IReadOnlyList<Sector> Parts, string? Why) result = ([], null);
            var geometry = _geometry;
            History.Record("Split a room", e => result = Splitting.Split(e, geometry, corners));
            if (result.Parts.Count > 0) Selection.Set([.. result.Parts.Skip(1)]);
            else if (result.Why is { } why) Notice?.Invoke(why);
            return;
        }

        if (corners.Count >= 3) DrawRoom(corners);
        else Notice?.Invoke("Click three or more corners for a room, or draw a line from one wall of a room to another to split it.");
    }

    /// <summary>The game family of the project ("doom", "heretic", "hexen", "strife").</summary>
    public string Engine => Core.Games.GameCatalog.Get(Project.Info.Game)?.Engine ?? "doom";

    /// <summary>
    /// Puts a texture where it was dropped: on the face of the wall within <paramref name="radius"/> of
    /// the point, the face toward the point (the wall itself on a solid wall, its upper and lower parts
    /// between two rooms); else on the floor of the area there, or its ceiling when
    /// <paramref name="ceiling"/>. One undo step; selects what was painted. Returns a sentence when
    /// there was nothing there.
    /// </summary>
    /// <summary>
    /// Paints with a picture from another game the user owns (<paramref name="from"/>): copied into the project on a worker the
    /// first time (D43), then put where it was dropped as <see cref="Paint"/> does, saying what was copied.
    /// </summary>
    public void PaintBorrowed(GameResources from, string name, bool flat, MapPoint at, double radius, bool ceiling) => Borrow(from, name, flat, chosen => Paint(chosen, at, radius, ceiling));

    /// <summary>A picture from another game the user owns onto one surface of the 3D view (D43): copied into the project first, then put on.</summary>
    public void PaintBorrowedSurface(GameResources from, string name, bool flat, SurfaceTarget target) => Borrow(from, name, flat, chosen => PaintSurface(target, chosen));

    /// <summary>Copies a picture from another game into the project on a worker, then <paramref name="paint"/>s it (its name here) on the UI thread, saying what happened.</summary>
    private void Borrow(GameResources from, string name, bool flat, Func<string, string?> paint)
    {
        if (Resources is not { } into)
        {
            Notice?.Invoke("Pictures from your other games need this map's own game to be found first.");
            return;
        }

        Task.Run(() =>
        {
            string chosen;
            try
            {
                chosen = Borrowing.CopyPicture(Project, into, from, name, flat);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
            {
                Log.Warn($"Copying {name} from {from.Game.Title} failed: {ex.Message}");
                _onUiThread(() => Notice?.Invoke($"{name} could not be copied from {from.Game.Title}. {ex.Message}"));
                return;
            }

            _onUiThread(() =>
            {
                if (paint(chosen) is { } why)
                {
                    Notice?.Invoke(why);
                    return;
                }

                Notice?.Invoke(chosen == name.ToUpperInvariant()
                    ? $"{name} was copied from {from.Game.Title} into this project, so the map can use it."
                    : $"{name} was copied from {from.Game.Title} into this project as {chosen}, since this game already has a {name}.");
            });
        });
    }

    /// <summary>
    /// Puts a texture on one surface seen in the 3D view: a face's upper, middle or lower part, or an area's floor or
    /// ceiling (a sky ceiling becomes that texture), one undo step, and selects the face or area. Null when done, else why not.
    /// </summary>
    public string? PaintSurface(SurfaceTarget target, string texture)
    {
        if (Grouping.Holds(_geometry, target.Element)) return "That is locked: unlock it with nothing selected, under Groups, to change it.";
        switch (target.Element)
        {
            case Side { OnMap: true } face:
                History.Record("Put a texture on a wall", e =>
                {
                    switch (target.Part)
                    {
                        case WallPart.Top:
                            e.Set(face, s => s.Top, (s, v) => s.Top = v, texture);
                            break;
                        case WallPart.Bottom:
                            e.Set(face, s => s.Bottom, (s, v) => s.Bottom = v, texture);
                            break;
                        default:
                            e.Set(face, s => s.Middle, (s, v) => s.Middle = v, texture);
                            break;
                    }
                });
                Selection.Set(face);
                return null;
            case Sector { OnMap: true } area:
                if (target.Ceiling) History.Record("Put a texture on a ceiling", e => e.Set(area, s => s.CeilingTexture, (s, v) => s.CeilingTexture = v, texture));
                else History.Record("Put a texture on a floor", e => e.Set(area, s => s.FloorTexture, (s, v) => s.FloorTexture = v, texture));
                Selection.Set(area);
                return null;
            default:
                return "Drop it on a wall, a floor or a ceiling.";
        }
    }

    /// <summary>
    /// Moves a floor or ceiling seen in the 3D view by <paramref name="delta"/> (§3.4: Ctrl+scroll steps by 8, Ctrl+drag
    /// follows the pointer): the selected rooms go together when the surface is one of theirs; the steps of one
    /// <paramref name="gesture"/> make one undo step. Returns the surface's height now, or null when nothing moved.
    /// </summary>
    public double? RaiseSurface(SurfaceTarget target, double delta, long gesture)
    {
        if (target.Element is not Sector { OnMap: true } area || delta == 0) return null;
        if (Grouping.Holds(_geometry, area))
        {
            Notice?.Invoke("That room is locked: unlock it with nothing selected, under Groups, to change it.");
            return null;
        }

        var rooms = Selection.Items.OfType<Sector>().Where(r => r.OnMap).ToList();
        if (!rooms.Contains(area)) rooms = [area];
        var ceiling = target.Ceiling;
        History.Record(ceiling ? "Change ceiling height" : "Change floor height", e =>
        {
            foreach (var r in rooms)
            {
                if (ceiling) e.Set(r, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, r.CeilingHeight + delta);
                else e.Set(r, x => x.FloorHeight, (x, v) => x.FloorHeight = v, r.FloorHeight + delta);
            }
        }, gesture);
        return ceiling ? area.CeilingHeight : area.FloorHeight;
    }

    /// <summary>Lines the textures along a wall up with this face's (<see cref="Aligning.Align"/>), one undo step, and says how many.</summary>
    public void AlignTextures(Side face, WallPart part)
    {
        if (!face.OnMap || face.Line is null) return;
        if (Grouping.Holds(_geometry, face))
        {
            Notice?.Invoke("That wall is locked: unlock it with nothing selected, under Groups, to change it.");
            return;
        }

        var n = 0;
        History.Record("Line the textures up", e => n = Aligning.Align(e, _geometry, face, part, name => Resources?.Textures.FindWall(name)?.Width));
        Notice?.Invoke(n == 0 ? "No wall along this one carries the same texture, so there is nothing to line it up with." : $"The texture now runs on across {(n == 1 ? "the wall" : $"the {n} walls")} along this one.");
    }

    /// <summary>
    /// Slides a face's textures by <paramref name="dx"/> across and <paramref name="dy"/> up (the 3D view: Ctrl+arrows by
    /// 8, Ctrl+drag with the pointer); the steps of one <paramref name="gesture"/> make one undo step. Returns the offsets now.
    /// </summary>
    public (double X, double Y)? NudgeTexture(Side face, double dx, double dy, long gesture)
    {
        if (!face.OnMap || (dx == 0 && dy == 0)) return null;
        if (Grouping.Holds(_geometry, face))
        {
            Notice?.Invoke("That wall is locked: unlock it with nothing selected, under Groups, to change it.");
            return null;
        }

        History.Record("Shift the texture", e =>
        {
            if (dx != 0) e.Set(face, s => s.OffsetX, (s, v) => s.OffsetX = v, face.OffsetX + dx);
            if (dy != 0) e.Set(face, s => s.OffsetY, (s, v) => s.OffsetY = v, face.OffsetY + dy);
        }, gesture);
        return (face.OffsetX, face.OffsetY);
    }

    public string? Paint(string texture, MapPoint at, double radius, bool ceiling)
    {
        if (_geometry.LineNear(at, radius) is { OnMap: true } wall)
        {
            var cross = (wall.End.X - wall.Start.X) * (at.Y - wall.Start.Y) - (wall.End.Y - wall.Start.Y) * (at.X - wall.Start.X);
            var face = cross > 0 && wall.Back is { } back ? back : wall.Front;
            if (wall.Opposite(face) is null)
            {
                History.Record("Put a texture on a wall", e => e.Set(face, s => s.Middle, (s, v) => s.Middle = v, texture));
            }
            else
            {
                History.Record("Put a texture on a wall", e =>
                {
                    e.Set(face, s => s.Top, (s, v) => s.Top = v, texture);
                    e.Set(face, s => s.Bottom, (s, v) => s.Bottom = v, texture);
                });
            }

            Selection.Set(face);
            return null;
        }

        if (_geometry.SectorAt(at) is { OnMap: true } area)
        {
            if (ceiling) History.Record("Put a texture on a ceiling", e => e.Set(area, s => s.CeilingTexture, (s, v) => s.CeilingTexture = v, texture));
            else History.Record("Put a texture on a floor", e => e.Set(area, s => s.FloorTexture, (s, v) => s.FloorTexture = v, texture));
            Selection.Set(area);
            return null;
        }

        return "Drop it on a wall or in a room.";
    }

    /// <summary>
    /// Places an object of <paramref name="type"/> at a point, facing north, on every difficulty and in
    /// every game mode (and for every class in Hexen), one undo step, and selects it. With <paramref name="standAt"/> (the top
    /// of a platform in the 3D view) it stands that high, as a height above the floor (GZDoom puts it on the floor otherwise).
    /// </summary>
    public Thing PlaceThing(int type, MapPoint at, double? standAt = null)
    {
        var height = standAt is { } z && _geometry.AreaAt(at) is { } area ? Math.Max(0, Math.Round(z - SlopePlane.FloorAt(area, at))) : 0;
        Thing New(MapPoint p, int angle)
        {
            var t = new Thing { X = p.X, Y = p.Y, Type = type, Angle = angle, Height = height };
            foreach (var f in Smart.Things.Everywhere(Engine)) t.SetFlag(f, true);
            return t;
        }

        var thing = New(at, 90);

        // In symmetry mode, its copies stand at the images of the spot, facing as their image would.
        var images = Mirror.Images(at);
        var facings = Mirror.Facings(90);
        Thing[] copies = [.. images.Select((p, i) => New(p, facings[i])).Where(c => c.X != at.X || c.Y != at.Y).DistinctBy(c => (c.X, c.Y))];
        History.Record("Place a thing", e =>
        {
            e.Add(thing);
            foreach (var c in copies) e.Add(c);
        });
        Selection.Set([thing, .. copies]);
        return thing;
    }

    /// <summary>The room chosen as a teleport pad, waiting for where the player arrives (the Teleport tool's first click).</summary>
    public Sector? TeleportPad
    {
        get => _teleportPad;
        set
        {
            _teleportPad = value;
            TeleportPadChanged?.Invoke();
        }
    }

    private Sector? _teleportPad;

    /// <summary>Raised when <see cref="TeleportPad"/> changes.</summary>
    public event Action? TeleportPadChanged;

    /// <summary>
    /// Saves the selected rooms and things as a prefab of the project (what Copy would copy), named "Prefab N"; the file
    /// is written on a worker, and <see cref="PrefabsChanged"/> is raised once it is. Says what happened.
    /// </summary>
    public void SavePrefab()
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = SavePrefab;
            return;
        }

        var folder = Project.Folder;
        var text = Prefabs.Text(_geometry, Selection.Items, Map.Namespace, Look);
        if (text is null)
        {
            Notice?.Invoke("Select rooms or things to save as a prefab; walls and corners go with the rooms they belong to.");
            return;
        }

        Task.Run(() =>
        {
            var dir = Path.Combine(folder, Prefabs.FolderName);
            Directory.CreateDirectory(dir);
            var name = Enumerable.Range(1, 10000).Select(n => $"Prefab {n}").First(n => !File.Exists(Path.Combine(dir, n + Prefabs.Extension)));
            File.WriteAllText(Path.Combine(dir, name + Prefabs.Extension), text);
            _onUiThread(() =>
            {
                Notice?.Invoke($"Saved as {name}: it is under Objects, Prefabs, ready to drag onto any map of this project.");
                PrefabsChanged?.Invoke();
            });
        });
    }

    /// <summary>Raised on the UI thread when a prefab has been saved.</summary>
    public event Action? PrefabsChanged;

    /// <summary>
    /// Puts a copy of a prefab with its middle near <paramref name="at"/> (in whole 64-unit steps from where it was saved),
    /// joined to the map as a paste is, one undo step; the prefab is read on a worker.
    /// </summary>
    public void PlacePrefab(Prefab prefab, MapPoint at)
    {
        Task.Run(() =>
        {
            MapClip clip;
            try
            {
                clip = Prefabs.Load(prefab);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.CorruptDataException or FormatException)
            {
                Log.Warn($"Reading the prefab {prefab.Path} failed: {ex.Message}");
                _onUiThread(() => Notice?.Invoke($"The prefab {prefab.Name} could not be read. {ex.Message}"));
                return;
            }

            _onUiThread(() => Paste(clip, at));
        });
    }

    /// <summary>Joins the selected rooms into the first selected, as one undo step (see <see cref="Rooms.Join"/>), and selects it.</summary>
    public void JoinRooms()
    {
        var rooms = Selection.Items.OfType<Sector>().Where(s => s.OnMap).ToList();
        if (rooms.Count < 2)
        {
            Notice?.Invoke("Select two or more rooms to join (Shift adds to the selection).");
            return;
        }

        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = JoinRooms;
            return;
        }

        IReadOnlyList<string> said = [];
        var geometry = _geometry;
        History.Record("Join rooms", e => said = Rooms.Join(e, geometry, rooms));
        Selection.Set(rooms[0]);
        if (said.Count > 0) Notice?.Invoke(string.Join(" ", said));
    }

    /// <summary>What an island room becomes: a pillar (solid), a pool (lower, with water) or a platform (raised a step).</summary>
    public enum IslandKind
    {
        Pillar,
        Pool,
        Platform,
    }

    /// <summary>Makes a room inside another a pillar, a pool or a platform (see <see cref="Rooms"/>), as one undo step.</summary>
    public void MakeIsland(Sector room, IslandKind kind)
    {
        if (!room.OnMap) return;
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeIsland(room, kind);
            return;
        }

        var geometry = _geometry;
        if (Rooms.Island(geometry, room) is not { } outer)
        {
            Notice?.Invoke("Only a room drawn inside another, sharing its walls with that room alone, becomes a pillar, a pool or a platform.");
            return;
        }

        var look = Look;
        switch (kind)
        {
            case IslandKind.Pillar:
                History.Record("Make a pillar", e => Deleting.Delete(e, geometry, [room], look));
                Selection.Prune();
                break;
            case IslandKind.Pool:
                History.Record("Make a pool", e => Rooms.Pool(e, geometry, room, outer, look));
                break;
            default:
                History.Record("Make a platform", e => Rooms.Platform(e, geometry, room, outer, look));
                break;
        }
    }

    /// <summary>Places a new light at a point inside a room (see <see cref="Lights.New"/>), one undo step, and selects it; says why when the point is outside.</summary>
    public void PlaceLight(MapPoint at)
    {
        if (_geometry.SectorAt(at) is null)
        {
            Notice?.Invoke("A light goes inside a room.");
            return;
        }

        var light = Lights.New(at, Engine);
        History.Record("Place a light", e => e.Add(light));
        Selection.Set(light);
    }

    /// <summary>The colour of the key waiting to be placed (the click on the map that comes next), or null.</summary>
    public string? KeyToPlace
    {
        get => _keyToPlace;
        set
        {
            _keyToPlace = value;
            KeyToPlaceChanged?.Invoke();
        }
    }

    private string? _keyToPlace;

    /// <summary>Raised when <see cref="KeyToPlace"/> changes.</summary>
    public event Action? KeyToPlaceChanged;

    /// <summary>Places the keycard of a colour at a point inside a room (one undo step) and selects it; says why when the point is outside.</summary>
    public void PlaceKey(string colour, MapPoint at)
    {
        if (_geometry.SectorAt(at) is null)
        {
            Notice?.Invoke("A key goes inside a room, where the player can pick it up.");
            return;
        }

        PlaceThing(Keys.Doom.First(k => k.Colour == colour).Card, at);
    }

    /// <summary>The door or lift waiting for its switch (the click on a wall that comes next).</summary>
    public Sector? SwitchTarget
    {
        get => _switchTarget;
        set
        {
            _switchTarget = value;
            SwitchTargetChanged?.Invoke();
        }
    }

    private Sector? _switchTarget;

    /// <summary>Raised when <see cref="SwitchTarget"/> changes.</summary>
    public event Action? SwitchTargetChanged;

    /// <summary>Makes a switch on a wall that works a door or a lift (see <see cref="Switches.Make"/>), one undo step, and selects it; says why when it cannot.</summary>
    public void MakeSwitch(Sector target, Line wall, MapPoint at)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeSwitch(target, wall, at);
            return;
        }

        (Line? Switch, string? Why) made = (null, null);
        var geometry = _geometry;
        var look = Look;
        History.Record("Make a switch", e => made = Switches.Make(e, geometry, wall, at, target, look));
        if (made.Switch is { } done) Selection.Set(done);
        else if (made.Why is { } why) Notice?.Invoke(why);
    }

    /// <summary>Makes a room a teleport pad sending to a spot (see <see cref="Teleports.Make"/>), one undo step, and selects the arrival spot; says why when it cannot.</summary>
    public void MakeTeleporter(Sector pad, MapPoint at, int angle)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeTeleporter(pad, at, angle);
            return;
        }

        (Thing? Arrival, string? Why) made = (null, null);
        var geometry = _geometry;
        var engine = Engine;
        History.Record("Make a teleporter", e => made = Teleports.Make(e, geometry, pad, at, angle, engine));
        if (made.Arrival is { } arrival) Selection.Set(arrival);
        else if (made.Why is { } why) Notice?.Invoke(why);
    }

    /// <summary>Makes stairs in the rectangle dragged from <paramref name="from"/> (the bottom) to <paramref name="to"/> (see <see cref="Stairs.Make"/>), one undo step, and selects them.</summary>
    public void MakeStairs(MapPoint from, MapPoint to)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeStairs(from, to);
            return;
        }

        try
        {
            (IReadOnlyList<Sector> Steps, string? Why) made = ([], null);
            var geometry = _geometry;
            var look = Look;
            History.Record("Make stairs", e => made = Stairs.Make(e, geometry, from, to, look));
            if (made.Steps.Count > 0) Selection.Set([.. made.Steps]);
            else if (made.Why is { } why) Notice?.Invoke(why);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Making stairs in {Slot} failed: {ex.Message}");
            Notice?.Invoke($"Stairs could not be made there. {ex.Message}");
        }
    }

    /// <summary>Makes a room a lift (see <see cref="Lifts.Make"/>), one undo step; says why when it cannot.</summary>
    public void MakeLift(Sector area)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeLift(area);
            return;
        }

        string? why = null;
        var geometry = _geometry;
        History.Record("Make a lift", e => why = Lifts.Make(e, geometry, area));
        if (why is not null) Notice?.Invoke(why);
    }

    /// <summary>The room height a floating platform needs: the player (56) under it, the slab (16) and the player again on top.</summary>
    public const double PlatformRoomHeight = 2 * Walker.PlayerHeight + 16;

    /// <summary>
    /// Adds a platform to a room (GZDoom's 3D floor, see <see cref="Platforms.Add"/>), one undo step: a solid slab 16 thick
    /// the player walks under and stands on, or water from the floor up 32 the player swims in. Says why when it cannot.
    /// </summary>
    public void AddPlatform(Sector room, PlatformKind kind)
    {
        if (!room.OnMap) return;
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => AddPlatform(room, kind);
            return;
        }

        var geometry = _geometry;
        if (Grouping.Holds(geometry, room))
        {
            Notice?.Invoke("That room is locked: unlock it with nothing selected, under Groups, to change it.");
            return;
        }

        if (Platforms.ControlOf(geometry, room) is not null)
        {
            Notice?.Invoke("This room holds a platform for another room; select that room to add one there.");
            return;
        }

        var height = room.CeilingHeight - room.FloorHeight;
        double bottom, top;
        if (kind == PlatformKind.Water)
        {
            // Below the floor, so the water's underside never meets it; its surface 32 up (or under the ceiling in a low room).
            (bottom, top) = (room.FloorHeight - 8, room.FloorHeight + Math.Min(32, Math.Max(8, height - 8)));
        }
        else
        {
            if (height < PlatformRoomHeight)
            {
                Notice?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A floating platform needs a room at least {PlatformRoomHeight:0} tall: 56 under it for the player, 16 for the platform and 56 above it. This room is {height:0} tall; raise its ceiling first."));
                return;
            }

            bottom = room.FloorHeight + Math.Min(64, height - PlatformRoomHeight + Walker.PlayerHeight);
            top = bottom + 16;
        }

        var look = Look;
        var shared = room.Tag != 0 ? geometry.SectorsTagged(room.Tag).Count - 1 : 0;
        History.Record(kind == PlatformKind.Water ? "Fill the room with water" : "Add a platform", e => Platforms.Add(e, geometry, room, bottom, top, kind, look));
        Selection.Set(room);
        Notice?.Invoke((kind == PlatformKind.Water
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Water fills the room up to {top:0}; the player swims in it.")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A platform spans the room from {bottom:0} to {top:0}: the player walks under it and stands on it."))
            + " Its little control room sits outside the map, north-west, where the game reads it from."
            + (shared > 0 ? $" The room shares its tag with {shared} other room{(shared == 1 ? "" : "s")}, so {(shared == 1 ? "that one has" : "they have")} it too." : ""));
    }

    /// <summary>Takes a platform away (its control room goes), one undo step.</summary>
    public void RemovePlatform(Platform platform)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => RemovePlatform(platform);
            return;
        }

        var (geometry, look) = (_geometry, Look);
        History.Record(platform.Kind == PlatformKind.Water ? "Drain the water" : "Remove the platform", e => Platforms.Remove(e, geometry, platform, look));
        Selection.Prune();
    }

    /// <summary>Makes a platform solid, water or passable, one undo step.</summary>
    public void SetPlatformKind(Platform platform, PlatformKind kind)
    {
        if (platform.Kind == kind) return;
        var look = Look;
        History.Record("Change what the platform is", e => Platforms.SetKind(e, platform, kind, look));
    }

    /// <summary>Makes a door in a wall between two rooms (see <see cref="Doors.Make"/>), one undo step, and selects it; says why when it cannot.</summary>
    public void MakeDoor(Line wall, (double From, double To)? span = null)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeDoor(wall, span);
            return;
        }

        // A click on a wall longer than a door can be: a standard door in its middle.
        if (span is null && wall.Length > Doors.MaxWidth) span = Doors.Centred(wall);

        try
        {
            (Sector? Door, string? Why) result = (null, null);
            var geometry = _geometry;
            var look = Look;
            History.Record("Make a door", e => result = Doors.Make(e, geometry, wall, look, span));
            if (result.Door is { } door) Selection.Set(door);
            else if (result.Why is { } why) Notice?.Invoke(why);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Making a door in {Slot} failed: {ex.Message}");
            Notice?.Invoke($"A door could not be made there. {ex.Message}");
        }
    }

    /// <summary>
    /// Makes a window in a wall from <paramref name="from"/> to <paramref name="to"/> along it (equal: a window of
    /// the usual width round that point), as one undo step, and selects it; says why when it cannot be made.
    /// </summary>
    public void MakeWindow(Line wall, double from, double to)
    {
        if (!wall.OnMap) return;
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeWindow(wall, from, to);
            return;
        }

        try
        {
            (Sector? Window, string? Why) result = (null, null);
            var geometry = _geometry;
            var look = Look;
            History.Record("Make a window", e => result = WallWindows.Make(e, geometry, wall, from, to, look));
            if (result.Window is { } window) Selection.Set(window);
            else if (result.Why is { } why) Notice?.Invoke(why);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Making a window in {Slot} failed: {ex.Message}");
            Notice?.Invoke($"A window could not be made there. {ex.Message}");
        }
    }

    /// <summary>
    /// Makes an area a secret (it counts when the player steps in; its entrances are solid walls on the in-game
    /// map), or with <paramref name="secret"/> false an ordinary area again; one undo step.
    /// </summary>
    public void MakeSecret(Sector area, bool secret = true)
    {
        if (!area.OnMap) return;
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeSecret(area, secret);
            return;
        }

        var geometry = _geometry;
        History.Record(secret ? "Make a secret" : "Not a secret", e =>
        {
            if (secret) Secrets.Make(e, geometry, area);
            else Secrets.Clear(e, geometry, area);
        });
    }

    /// <summary>Makes the door into a secret look like the wall beside it from outside (one undo step).</summary>
    public void HideSecretDoor(Sector secret, Sector door)
    {
        if (!secret.OnMap || !door.OnMap) return;
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => HideSecretDoor(secret, door);
            return;
        }

        var geometry = _geometry;
        if (Doors.Recognise(geometry, door) is not { } found) return;
        History.Record("Hide the secret door", e => Secrets.HideDoor(e, geometry, found, secret));
    }

    /// <summary>Copies the selected rooms (with their walls and things) and things; says what was copied, or what can be.</summary>
    public bool CopySelection()
    {
        var clip = Copying.Copy(_geometry, Selection.Items);
        if (clip.IsEmpty)
        {
            Notice?.Invoke("Select rooms or things to copy; walls and corners are copied with the rooms they belong to.");
            return false;
        }

        clip.From = Resources;
        Copying.Clipboard = clip;
        Notice?.Invoke($"Copied {clip.Describe()}. Ctrl+V puts a copy where the pointer is.");
        return true;
    }

    /// <summary>Copies the selection, then deletes it (one undo step for the delete).</summary>
    public void Cut()
    {
        if (CopySelection()) DeleteSelection();
    }

    /// <summary>
    /// Pastes what was copied with its middle near <paramref name="at"/> (in whole 64-unit steps from where it was
    /// copied), joins it to the map, one undo step, and selects it; says so when walls were left crossing.
    /// </summary>
    public void Paste(MapPoint at)
    {
        if (Copying.Clipboard is not { } clip)
        {
            Notice?.Invoke("Nothing is copied yet: select rooms or things and press Ctrl+C.");
            return;
        }

        Paste(clip, at);
    }

    /// <summary>
    /// Pastes <paramref name="clip"/> as <see cref="Paste(MapPoint)"/> pastes what was copied. A copy from another project or
    /// game first has its pictures carried into this project on a worker (D43); its objects that mean something else in this
    /// game (Heretic's 66 is a Gargoyle, Doom's a Revenant) are left out, and the paste says so.
    /// </summary>
    public void Paste(MapClip clip, MapPoint at)
    {
        if (clip.From is { } from && Resources is { } here && !ReferenceEquals(from, here))
        {
            var pictures = clip.Pictures();
            Task.Run(() =>
            {
                (Dictionary<string, string> Renamed, int Copied) carried;
                try
                {
                    carried = Borrowing.Carry(Project, here, from, pictures);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
                {
                    Log.Warn($"Carrying the copy's pictures from {from.Game.Title} failed: {ex.Message}");
                    _onUiThread(() => Notice?.Invoke($"The copy's pictures could not be brought over from {from.Game.Title}. {ex.Message}"));
                    return;
                }

                bool Means(Thing t) => from.Game.Engine == here.Game.Engine || (here.Things.Find(t.Type) is { } ours && from.Things.Find(t.Type) is { } theirs && ours.ClassName == theirs.ClassName);
                var left = clip.Things.Count(t => !Means(t));
                _onUiThread(() =>
                {
                    PasteNow(clip, at, carried.Renamed, Means);
                    var said = new List<string>();
                    if (carried.Copied > 0) said.Add($"{(carried.Copied == 1 ? "A picture" : $"{carried.Copied} pictures")} the copy uses {(carried.Copied == 1 ? "was" : "were")} copied into this project from {from.Game.Title}{(carried.Renamed.Count > 0 ? $" ({carried.Renamed.Count} under a new name, as this game has its own)" : "")}.");
                    if (left > 0) said.Add($"{(left == 1 ? "An object was" : $"{left} objects were")} left out: {(left == 1 ? "its number means" : "their numbers mean")} something else in {here.Game.Title}.");
                    if (said.Count > 0) Notice?.Invoke(string.Join(" ", said));
                });
            });
            return;
        }

        PasteNow(clip, at, null, null);
    }

    private void PasteNow(MapClip clip, MapPoint at, IReadOnlyDictionary<string, string>? rename, Func<Thing, bool>? keep)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => PasteNow(clip, at, rename, keep);
            return;
        }

        (IReadOnlyList<Element> Pasted, int Crossings) result = ([], 0);
        var geometry = _geometry;
        var look = Look;
        var centre = Copying.PlaceNear(clip, at);
        History.Record("Paste", e => result = Copying.Paste(e, geometry, clip, centre, look, rename, keep));
        if (result.Pasted.Count > 0) Selection.Set([.. result.Pasted]);
        if (result.Crossings > 0) Notice?.Invoke($"The copy's walls cross {result.Crossings} wall{(result.Crossings == 1 ? "" : "s")} already there, which the game draws wrongly. Drag it clear, or press Ctrl+Z.");
    }

    /// <summary>Copies the selection and pastes it beside itself, to the east, with a gap.</summary>
    public void Duplicate()
    {
        if (!CopySelection() || Copying.Clipboard is not { } clip) return;
        Paste(new MapPoint(clip.Origin.X + Math.Ceiling(clip.Size.Width / 64) * 64 + 64, clip.Origin.Y));
    }

    /// <summary>
    /// Rounds off a corner where two walls meet into a curve of <paramref name="sides"/> walls starting <paramref name="reach"/>
    /// along each (<see cref="Rounding"/>), one undo step; says why not when it cannot. Waits for the map's shapes.
    /// </summary>
    public void RoundCorner(Vertex corner, double reach, int sides)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => RoundCorner(corner, reach, sides);
            return;
        }

        if (!corner.OnMap) return;
        string? why = null;
        var geometry = _geometry;
        History.Record("Round a corner", e => why = Rounding.Round(e, geometry, corner, reach, sides));
        if (why is not null)
        {
            Notice?.Invoke(why);
            return;
        }

        Selection.Clear();
        Notice?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The corner is a curve of {sides} walls now, starting {reach:0} along each wall. Ctrl+Z puts the corner back."));
    }

    /// <summary>Adds a corner to a wall at a point on it, splitting the wall in two, and selects the corner (to drag it).</summary>
    public void AddCorner(Line wall, MapPoint at)
    {
        if (!wall.OnMap) return;
        var corner = new Vertex { X = at.X, Y = at.Y };
        History.Record("Add a corner", e => Walls.Split(e, wall, e.Add(corner)));
        Selection.Set(corner);
    }

    /// <summary>
    /// Deletes the selection so the map stays whole (see <see cref="Deleting"/>), as one undo step, and
    /// says what stayed and why. Waits for the map's shapes like <see cref="DrawRoom"/>.
    /// </summary>
    public void DeleteSelection()
    {
        if (Selection.IsEmpty) return;
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = DeleteSelection;
            return;
        }

        var items = Selection.Items.ToList();
        IReadOnlyList<string> said = [];
        try
        {
            var geometry = _geometry;
            var look = Look;
            History.Record("Delete", e => said = Deleting.Delete(e, geometry, items, look));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Deleting in {Slot} failed: {ex.Message}");
            said = [$"That could not be deleted. {ex.Message}"];
        }

        Selection.Prune();
        if (said.Count > 0) Notice?.Invoke(string.Join(" ", said));
    }

    /// <summary>
    /// Turns, resizes or mirrors the selection as one undo step: what moves is gathered on a worker (the
    /// selection can be the whole map), placed as <paramref name="make"/> says (it is given what moves, to turn
    /// round its middle), joined to the map, and stays selected.
    /// </summary>
    public void TransformSelection(Func<MoveSet, Placement?> make)
    {
        if (Selection.IsEmpty) return;
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => TransformSelection(make);
            return;
        }

        var geometry = _geometry;
        var seeds = MoveSet.Seeds.Of(Selection.Items);
        var version = Map.Version;
        _placings.Request(() => (version, MoveSet.Gather(geometry, seeds), make));
    }

    /// <summary>Turns the selection by <paramref name="degrees"/> counterclockwise round its middle (kept to whole units, so a quarter turn keeps corners on the grid).</summary>
    public void TurnSelection(double degrees) => TransformSelection(set => Placement.Turn(degrees, new MapPoint(Math.Round(set.Middle.X), Math.Round(set.Middle.Y))));

    /// <summary>Mirrors the selection in place: left to right, or top to bottom.</summary>
    public void FlipSelection(bool leftToRight) => TransformSelection(set => Placement.Size(leftToRight ? -1 : 1, leftToRight ? 1 : -1, set.Middle));

    /// <summary>Resizes the selection round its middle by <paramref name="factor"/> both ways.</summary>
    public void ResizeSelection(double factor) => TransformSelection(set => Placement.Size(factor, factor, set.Middle));

    /// <summary>
    /// Ends a drag that moved <paramref name="set"/>: joins what landed on the rest of the map (corners
    /// merged, walls split and shared), in the same undo step as the move, and says so when walls were
    /// left crossing.
    /// </summary>
    public void FinishMove(MoveSet set, long gesture)
    {
        var crossings = 0;
        try
        {
            History.Record("Join", e => crossings = Joining.AfterMove(e, set, Look), gesture);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Joining a move in {Slot} failed: {ex.Message}");
        }

        Selection.Prune();
        if (crossings > 0) Notice?.Invoke($"This left {crossings} wall{(crossings == 1 ? "" : "s")} crossing another, which the game draws wrongly. Move them apart, or press Ctrl+Z to take it back.");
    }

    /// <summary>The last save's failure, in a sentence.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event Action<SaveState>? StateChanged;

    /// <summary>
    /// The map's outlines and hit-test grids, built on a worker after each change; it can trail the map
    /// by one build (milliseconds on an ordinary map) and says which version it shows.
    /// </summary>
    public MapGeometry Geometry => _geometry;

    /// <summary>Raised on the UI thread when <see cref="Geometry"/> is replaced by a newer one.</summary>
    public event Action? GeometryChanged;

    /// <summary>
    /// The gaps and low openings the map started with, which the passage checks leave to their author: none for a map made in
    /// WadSmith, pending while <see cref="UseOriginal"/> reads a remix's original.
    /// </summary>
    public Checks.FitBaseline FitBaseline { get; private set; } = Checks.FitBaseline.None;

    /// <summary>Raised on the UI thread when <see cref="FitBaseline"/> is known.</summary>
    public event Action? FitBaselineChanged;

    /// <summary>Where the map came from (a remix's game and map), from the project's record.</summary>
    public MapOrigin? Origin => Project.Info.Maps.FirstOrDefault(m => string.Equals(m.Slot, Slot, StringComparison.OrdinalIgnoreCase))?.Origin;

    /// <summary>Reads the map as it started (<paramref name="original"/>, on a worker) for <see cref="FitBaseline"/>.</summary>
    public void UseOriginal(Func<MapModel> original)
    {
        FitBaseline = Checks.FitBaseline.Pending;
        Task.Run(() =>
        {
            var baseline = Checks.FitBaseline.None;
            try
            {
                baseline = Checks.FitBaseline.Of(MapGeometry.Build(EditorMap.FromModel(original()).Frame));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException)
            {
                Log.Warn($"The map {Slot} started as could not be read again, so its passages are all checked: {ex.Message}");
            }

            _onUiThread(() =>
            {
                FitBaseline = baseline;
                FitBaselineChanged?.Invoke();
            });
        });
    }

    /// <param name="project">The project.</param>
    /// <param name="slot">The map to open.</param>
    /// <param name="onUiThread">Runs an action on the UI thread (the app passes its dispatcher).</param>
    /// <remarks>Reads and indexes the whole map: call it on a worker, then hand the session to the UI thread.</remarks>
    public static EditorSession Open(Project project, string slot, Action<Action> onUiThread) => Open(project, slot, new SessionHooks(onUiThread));

    /// <param name="project">The project.</param>
    /// <param name="slot">The map to open.</param>
    /// <param name="hooks">How to reach the UI thread, the game, and the version picture maker.</param>
    /// <param name="reason">Why the copy kept at opening was kept, for the versions list.</param>
    /// <remarks>Reads and indexes the whole map: call it on a worker, then hand the session to the UI thread.</remarks>
    public static EditorSession Open(Project project, string slot, SessionHooks hooks, string reason = "Opened")
    {
        var map = EditorMap.FromModel(project.LoadMap(slot));
        var session = new EditorSession(project, slot, map, MapGeometry.Build(map.Frame), hooks);
        session.OpeningVersion = session.SaveVersion(reason);
        return session;
    }

    /// <summary>Waits for every pending change to reach the disk (on close, before Play).</summary>
    public Task FlushAsync()
    {
        if (Map.Version != _savedVersion) CaptureAndSave();
        return Task.WhenAll(_lastSave, _lastSettings);
    }

    /// <summary>
    /// Keeps a copy of the map as it is now, with a picture, in the project's versions (D24). The copy
    /// is written by a worker; the task gives the version kept, or null when the newest copy already
    /// held this map (or the copy could not be written, which is logged).
    /// </summary>
    public Task<MapVersion?> SaveVersion(string reason)
    {
        var frame = Map.Frame;
        var now = DateTimeOffset.Now;
        return Task.Run(async () =>
        {
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                byte[]? picture = null;
                try
                {
                    picture = _picture?.Invoke(frame);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    Log.Warn($"The picture for a version of {Slot} could not be made: {ex.Message}");
                }

                return MapVersions.Write(Project, Slot, frame.ToModel(), reason, now, picture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"A version of {Slot} could not be kept: {ex.Message}");
                return null;
            }
            finally
            {
                _writer.Release();
            }
        });
    }

    /// <summary>
    /// Makes a kept copy the project's map again and opens it in a new session; the caller has kept the
    /// map as it was (as a version) and closed this session first. Reads and writes files: call it on a worker.
    /// </summary>
    public static EditorSession Restore(Project project, string slot, MapVersion version, SessionHooks hooks)
    {
        var model = MapVersions.Load(version);
        model.Name = slot;
        project.SaveMap(model);
        return Open(project, slot, hooks, $"Back to the version from {Describe(version.When)}");
    }

    /// <summary>A moment as a kept version's reason names it, readable on any later day: "11 Oct, 20:50".</summary>
    public static string Describe(DateTimeOffset when) => when.LocalDateTime.ToString("d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    private void CaptureAndSave()
    {
        if (_disposed || Map.Version == _savedVersion) return;
        var frame = Map.Frame;
        var version = frame.Version;
        State = SaveState.Saving;
        StateChanged?.Invoke(State);
        _lastSave = Task.Run(async () =>
        {
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                Project.SaveMap(frame.ToModel());
                _onUiThread(() =>
                {
                    _savedVersion = Math.Max(_savedVersion, version);
                    LastError = null;
                    State = Map.Version == _savedVersion ? SaveState.Saved : SaveState.Pending;
                    StateChanged?.Invoke(State);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Saving {Slot} failed", ex);
                _onUiThread(() =>
                {
                    LastError = $"Your changes could not be saved: {ex.Message}";
                    State = SaveState.Failed;
                    StateChanged?.Invoke(State);
                });
            }
            finally
            {
                _writer.Release();
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
