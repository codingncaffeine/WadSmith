using WadSmith.Core;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
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
public sealed record SessionHooks(Action<Action> OnUiThread, GameResources? Resources = null, Func<MapFrame, byte[]?>? Picture = null);

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
            History.Run(new TransformEdit(r.Set, placement), gesture);
            if (r.Set.Count <= MoveSet.LiveLimit) FinishMove(r.Set, gesture);
        }, "what a turn moves");
        map.Changed += _ =>
        {
            State = SaveState.Pending;
            StateChanged?.Invoke(State);
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            _shapes.Request(() => MapGeometry.Build(Map.Frame));
        };
    }

    public Project Project { get; }

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
    public void PaintBorrowed(GameResources from, string name, bool flat, MapPoint at, double radius, bool ceiling)
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
                if (Paint(chosen, at, radius, ceiling) is { } why)
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
    /// every game mode (and for every class in Hexen), one undo step, and selects it.
    /// </summary>
    public Thing PlaceThing(int type, MapPoint at)
    {
        Thing New(MapPoint p, int angle)
        {
            var t = new Thing { X = p.X, Y = p.Y, Type = type, Angle = angle };
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

    /// <summary>Makes a door in a wall between two rooms (see <see cref="Doors.Make"/>), one undo step, and selects it; says why when it cannot.</summary>
    public void MakeDoor(Line wall)
    {
        if (_geometry.Version != Map.Version)
        {
            _whenCaughtUp = () => MakeDoor(wall);
            return;
        }

        try
        {
            (Sector? Door, string? Why) result = (null, null);
            var geometry = _geometry;
            var look = Look;
            History.Record("Make a door", e => result = Doors.Make(e, geometry, wall, look));
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
        return _lastSave;
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
