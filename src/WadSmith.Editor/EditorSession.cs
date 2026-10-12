using WadSmith.Core.Projects;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

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
    public Look Look => Core.Games.GameCatalog.Get(Project.Info.Game)?.Engine is { } engine ? Look.For(engine) : Look.For("doom");

    /// <summary>Raised with a sentence for the status strip when something the user did could not be done as asked.</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// Draws a shape as a room through the history (one undo step) and selects the areas it made. Works
    /// only near the shape, so it is quick on any map; if the map's shapes are still being rebuilt after
    /// the last change, the room is drawn the moment they are ready.
    /// </summary>
    public void DrawRoom(IReadOnlyList<MapPoint> shape)
    {
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
        if (crossings > 0) Notice?.Invoke($"This move left {crossings} wall{(crossings == 1 ? "" : "s")} crossing another, which the game draws wrongly. Move them apart, or press Ctrl+Z to take the move back.");
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
