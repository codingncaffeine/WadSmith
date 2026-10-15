using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using SkiaSharp;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.App.ViewModels;
using WadSmith.Editor.Model;

namespace WadSmith.App.Canvas;

/// <summary>What a left drag on the plan does.</summary>
internal enum EditorTool
{
    /// <summary>Click to select (Shift adds or removes).</summary>
    Select,

    /// <summary>Drag a rectangle to draw a room.</summary>
    Room,

    /// <summary>Click a wall between two rooms to make it a door.</summary>
    Door,

    /// <summary>Click a room to step on, then the spot the player arrives at.</summary>
    Teleport,

    /// <summary>Drag from the bottom of a staircase to its top.</summary>
    Stairs,

    /// <summary>Drag along a wall, or click it, for a window.</summary>
    Window,

    /// <summary>Click a door or a lift, then the wall where its switch goes.</summary>
    Switch,

    /// <summary>Click where the key for a locked door goes.</summary>
    Key,

    /// <summary>Click where the camera stands whose view a wall chosen in the Inspector shows (armed from there, not the tool column).</summary>
    Camera,

    /// <summary>Click in a room for a light.</summary>
    Light,

    /// <summary>Drag a line to measure it and see the floors and ceilings along it.</summary>
    Measure,
}

/// <summary>
/// The 2D map view. Wheel zooms at the pointer; middle or right drag, or Space with left drag, pans;
/// with the Select tool a click selects the thing, corner, wall or area under the pointer (Shift adds
/// or removes); with the Room tool a drag draws a room, snapped to corners and to the grid; Home fits
/// the map. The picture and the selection's outline are built on workers from the session's geometry
/// and drawn on the render thread, so the UI thread only takes input and updates the view.
/// </summary>
internal sealed class MapCanvas : Control
{
    public static readonly StyledProperty<EditorSession?> SessionProperty = AvaloniaProperty.Register<MapCanvas, EditorSession?>(nameof(Session));
    public static readonly StyledProperty<MapViewMode> ModeProperty = AvaloniaProperty.Register<MapCanvas, MapViewMode>(nameof(Mode));
    public static readonly StyledProperty<EditorTool> ToolProperty = AvaloniaProperty.Register<MapCanvas, EditorTool>(nameof(Tool), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<bool> ShowFitProperty = AvaloniaProperty.Register<MapCanvas, bool>(nameof(ShowFit), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<PickOnly> PicksProperty = AvaloniaProperty.Register<MapCanvas, PickOnly>(nameof(Picks));
    public static readonly StyledProperty<double> GridSizeProperty = AvaloniaProperty.Register<MapCanvas, double>(nameof(GridSize));
    public static readonly StyledProperty<bool> ClassicKeysProperty = AvaloniaProperty.Register<MapCanvas, bool>(nameof(ClassicKeys));

    private readonly LatestOnWorker<(EditorSession Session, CanvasSnapshot Picture)> _pictures;
    private readonly LatestOnWorker<(EditorSession Session, long Generation, SelectionOverlay Outline)> _outlines;
    private readonly LatestOnWorker<(EditorSession Session, long Gesture, MoveSet Set, string? Held)> _gathers;
    private readonly LatestOnWorker<(EditorSession Session, bool Add, IReadOnlyList<Element> Picked)> _boxes;
    private readonly LatestOnWorker<(EditorSession Session, FitMarks Marks)> _fits;

    /// <summary>The time moving pictures play to (the 3D view keeps its own, as GZDoom starts each level's at zero).</summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Whether an animation frame is asked for: while the picture holds a moving floor, each of the display's frames checks
    /// the moving floors and asks for a drawing only when one of them changes.
    /// </summary>
    private bool _framesAsked;
    private long _shownFrames;

    /// <summary>Whether the picture on show holds a moving floor (what a test reports when the plan does not play one).</summary>
    internal bool PlaysMovingFloors => _snapshot is { Animated: true };

    /// <summary>Where the player cannot pass (from a worker), the player's box at the pointer, and the pointer it follows.</summary>
    private FitMarks? _fitMarks;
    private (MapPoint At, WadSmith.Editor.Checks.GhostFit Fit)? _ghost;
    private Point? _ghostPointer;

    /// <summary>A box being drawn over empty space with the Select tool (screen points), and whether it adds to the selection.</summary>
    private Point? _boxFrom;
    private Point? _boxTo;
    private bool _boxAdd;
    private CanvasSnapshot? _snapshot;
    private MapLook _look = new(null);
    private SelectionOverlay? _selection;
    private SelectionOverlay? _hoverOutline;
    private long _outlineGeneration;
    private ThreeD.Camera3D? _camera;
    private double _cx;
    private double _cy;
    private double _zoom = 0.5;
    private bool _fitted;

    // A spot to show instead of the whole map when the plan first fits (a landmark, D28): where, and how much round it.
    private (MapPoint At, double Span)? _focus;
    private Point? _panFrom;
    private bool _spaceDown;
    private Element? _hover;
    private MapPoint? _drawFrom;
    private MapPoint? _drawTo;
    private MapPoint? _snap;
    private Point _drawPress;

    /// <summary>The corners of a room being drawn corner by corner (clicks), empty when none is.</summary>
    private readonly List<MapPoint> _corners = [];

    /// <summary>The map version a drawn room was added in; its rectangle stays on screen until the picture shows it.</summary>
    private long _drawnIn = -1;
    private (MapPoint From, MapPoint To) _justDrawn;

    /// <summary>A press with the Select tool: where, on what, and whether a click without a drag selects only that.</summary>
    private Point? _pressAt;
    private Element? _pressed;
    private bool _hoverSection;
    private bool _narrowOnClick;

    /// <summary>A drag moving the selection: its history gesture, what moves (once gathered), the grab point, the offset so far.</summary>
    private long _moveGesture;
    private MoveSet? _moveSet;
    private MoveEdit? _moveEdit;
    private MapPoint _grab;
    private MapPoint? _anchor;
    private (double X, double Y) _offset;
    private bool _free;
    private Point _lastPointer;
    private bool _endWhenGathered;
    private HashSet<Vertex> _moving = new(ReferenceEqualityComparer.Instance);
    private MapPoint? _mergeAt;

    /// <summary>
    /// Whether the move under way comes from the arrow keys: each press adds a grid step to the offset, and the move is let
    /// go (one undo step, joined to the map as a drag is) after a pause, a click or any other key.
    /// </summary>
    private bool _keyMove;
    private Avalonia.Threading.DispatcherTimer? _keyMoveEnd;

    /// <summary>How long the arrow keys may rest before their move is let go.</summary>
    private static readonly TimeSpan KeyMovePause = TimeSpan.FromMilliseconds(700);

    /// <summary>How far one press of an arrow key scrolls the plan, in screen pixels (Eureka's 120).</summary>
    private const double ArrowScroll = 120;

    /// <summary>Moves of up to this many corners and objects change the map as the pointer goes; bigger ones when let go.</summary>
    private const int LiveMoveLimit = MoveSet.LiveLimit;

    /// <summary>The handles round the selection: four corners and four sides resize it, the knob above turns it.</summary>
    private enum Grip
    {
        None,
        Turn,
        North,
        South,
        East,
        West,
        NorthEast,
        NorthWest,
        SouthEast,
        SouthWest,
    }

    /// <summary>The room the tools' column takes down the plan's left edge (pixels): a fitted map is centred to its right.</summary>
    private const double ToolsRoom = 100;

    /// <summary>How far outside the selection the handles sit (pixels), so they never cover its corners.</summary>
    private const double GripPad = 10;

    /// <summary>How far above the handles' box the turning knob sits (pixels).</summary>
    private const double KnobRise = 26;

    // A turn or resize under way (a gesture like a move): the handle held, the box it started from, where it was pressed.
    private Grip _grip;
    private (double MinX, double MinY, double MaxX, double MaxY) _gripBox;
    private MapPoint _gripFrom;
    private bool _fromMiddle;
    private Placement _placement = Placement.Identity;
    private TransformEdit? _transformEdit;
    private bool _overGrip;

    /// <summary>The smart guides shown while a room's corner is placed (map units).</summary>
    private IReadOnlyList<Guide> _guides = [];

    /// <summary>An object being turned by its facing knob, the gesture its turns merge into, and whether the pointer is over the knob.</summary>
    private Thing? _turning;
    private long _turnGesture;
    private bool _overKnob;

    // The line measured (and shown in the profile), and whether it is being dragged.
    private MapPoint? _measureFrom;
    private MapPoint? _measureTo;
    private bool _measuring;

    // A room drawn round (Ctrl while dragging), and with how many sides ([ and ] change them).
    private bool _round;
    private int _sides = Shapes.DefaultSides;

    // A window being dragged along a wall: the wall, and where the drag started and is now (distances along it).
    private Line? _windowWall;
    private double _windowFrom;
    private double _windowTo;

    static MapCanvas()
    {
        FocusableProperty.OverrideDefaultValue<MapCanvas>(true);
    }

    public MapCanvas()
    {
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearHover());
        _pictures = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || r.Picture.Mode != Mode || (_snapshot is not null && _snapshot.Mode == Mode && r.Picture.Version < _snapshot.Version)) return;
            _snapshot = r.Picture;
            PlayFrames();
            InvalidateVisual();
        }, "the map picture");
        _outlines = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || r.Generation != _outlineGeneration) return;
            _selection = r.Outline;
            InvalidateVisual();
        }, "the selection outline");
        _gathers = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || r.Gesture != _moveGesture) return;
            if (r.Held is { } held)
            {
                // Moving these would change a locked room: the drag stops before anything moves.
                CancelMove();
                r.Session.Tell(held == "a locked room" ? "That would change a locked room: unlock it first (with nothing selected, under Groups)." : $"That would change {held}, which is locked: unlock it first (with nothing selected, under Groups).");
                return;
            }

            _moveSet = r.Set;
            _moving = new HashSet<Vertex>(r.Set.Vertices, ReferenceEqualityComparer.Instance);

            // The point that snaps: the moving corner nearest the grab, else the nearest object.
            _anchor = Nearest(r.Set.Vertices.Select(v => new MapPoint(v.X, v.Y))) ?? Nearest(r.Set.Things.Select(t => new MapPoint(t.X, t.Y)));
            if (_grip != Grip.None) Shape(_lastPointer, _free, _fromMiddle);
            else if (_keyMove) ApplyMove();
            else Follow(_lastPointer, _free);
            if (_endWhenGathered) EndMove(keep: true);
        }, "what a drag moves");
        _boxes = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session)) return;
            // Locked rooms and objects stay out of a box selection.
            var picked = r.Picked.Where(e => !Grouping.Holds(r.Session.Geometry, e)).ToList();
            if (r.Add) r.Session.Selection.Set([.. r.Session.Selection.Items, .. picked]);
            else r.Session.Selection.Set([.. picked]);
        }, "a box selection");
        _fits = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || !ShowFit || (_fitMarks is { } had && had.Version > r.Marks.Version)) return;
            _fitMarks = r.Marks;
            InvalidateVisual();
        }, "where the player fits");
    }

    public EditorSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    /// <summary>How the map is painted: floors, ceilings, heights or lines only.</summary>
    public MapViewMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>What a left drag does: select or draw a room.</summary>
    public EditorTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <summary>The map version the plan's picture shows (-1 before the first), for instruments.</summary>
    internal long ShownVersion => _snapshot?.Version ?? -1;

    /// <summary>Whether the player's box follows the pointer and the places the player cannot pass are marked (G).</summary>
    public bool ShowFit
    {
        get => GetValue(ShowFitProperty);
        set => SetValue(ShowFitProperty, value);
    }


    /// <summary>What the Select tool picks: anything, or only corners, walls, rooms or objects.</summary>
    public PickOnly Picks
    {
        get => GetValue(PicksProperty);
        set => SetValue(PicksProperty, value);
    }

    /// <summary>The grid corners snap to, in map units; 0 follows the zoom (see <see cref="GridStep"/>).</summary>
    public double GridSize
    {
        get => GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    /// <summary>The grid corners snap to now.</summary>
    public double GridStepNow => GridStep();

    /// <summary>Whether the keys are Doom Builder's: the arrow keys then always scroll the plan, and Ctrl with them moves the selection.</summary>
    public bool ClassicKeys
    {
        get => GetValue(ClassicKeysProperty);
        set => SetValue(ClassicKeysProperty, value);
    }

    /// <summary>Where the middle of the plan is, in map units (for instruments).</summary>
    internal MapPoint Centre => new(_cx, _cy);

    /// <summary>Whether a drawing, a drag or a box is under way: the editor's keys wait until it ends.</summary>
    internal bool IsGesturing => _drawFrom is not null || _corners.Count > 0 || _boxFrom is not null || _moveGesture != 0 || _pressed is not null || _windowWall is not null;
    /// <summary>Raised as the player's box follows the pointer: whether the player fits there (null off the map or when hidden), and where.</summary>
    public event Action<WadSmith.Editor.Checks.GhostFit?, MapPoint>? GhostChanged;

    /// <summary>Raised when the element under the pointer changes (null over empty space).</summary>
    public event Action<Element?, MapPoint>? HoverChanged;

    /// <summary>Raised while a room is being drawn, with its size in map units (null when the drawing ends).</summary>
    public event Action<(double Width, double Height)?, double>? Drawing;

    /// <summary>Raised while something from a shelf is held over the plan, with what a drop there would do (null when it leaves).</summary>
    public event Action<string?>? DropHint;

    /// <summary>Raised while a room is drawn corner by corner: the corners so far and the length of the side to the pointer (null when it ends).</summary>
    public event Action<(int Corners, double Side)?>? CornersChanged;

    /// <summary>Raised while the selection is being moved, with what moves, how far and whether by the arrow keys (null when the move ends).</summary>
    public event Action<(int Corners, int Things, double Dx, double Dy, bool Keys)?>? Moving;

    /// <summary>Raised with a sentence while the selection is turned or resized with its handles (null when it ends).</summary>
    public event Action<string?>? Placing;

    /// <summary>Raised when a line has been measured: its two ends, for the elevation profile.</summary>
    public event Action<MapPoint, MapPoint>? Measured;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
        {
            RequestPicture();
            return;
        }

        if (change.Property == GridSizeProperty)
        {
            InvalidateVisual();
            return;
        }

        if (change.Property == ShowFitProperty)
        {
            _fitMarks = null;
            RequestFit();
            UpdateGhost(_ghostPointer);
            InvalidateVisual();
            return;
        }

        if (change.Property == ToolProperty)
        {
            CancelDrawing();
            EndKeyMove();
            CancelMove();
            if (change.GetNewValue<EditorTool>() != EditorTool.Teleport && Session is { } leaving) leaving.TeleportPad = null;
            _windowWall = null;
            if (change.GetNewValue<EditorTool>() != EditorTool.Switch && Session is { } unswitching) unswitching.SwitchTarget = null;
            if (change.GetNewValue<EditorTool>() != EditorTool.Key && Session is { } unkeyed) unkeyed.KeyToPlace = null;
            if (change.GetNewValue<EditorTool>() != EditorTool.Camera && Session is { } unscreened) unscreened.ScreenToFill = null;
            if (change.GetNewValue<EditorTool>() != EditorTool.Measure) (_measureFrom, _measureTo, _measuring) = (null, null, false);
            _snap = null;
            InvalidateVisual();
            return;
        }

        if (change.Property != SessionProperty) return;

        // A move of the arrow keys belongs to the map it began on: dropped here (focus left the plan first, which let it go).
        _keyMoveEnd?.Stop();
        if (_keyMove) (_keyMove, _moveGesture, _moveSet, _moveEdit) = (false, 0, null, null);
        if (change.OldValue is EditorSession old)
        {
            old.GeometryChanged -= OnGeometryChanged;
            old.Selection.Changed -= OnSelectionChanged;
            old.MirrorChanged -= InvalidateVisual;
            old.HiddenChanged -= RequestPicture;
            old.PropsChanged -= RequestPicture;
            old.TexturesChanged -= RequestPicture;
        }

        _snapshot = null;
        PlayFrames();
        _selection = null;
        _hover = null;
        _hoverOutline = null;
        _fitMarks = null;
        _fitted = false;
        _focus = null;
        if (change.NewValue is EditorSession s)
        {
            _look = new MapLook(s.Resources);
            s.GeometryChanged += OnGeometryChanged;
            s.Selection.Changed += OnSelectionChanged;
            s.MirrorChanged += InvalidateVisual;
            s.HiddenChanged += RequestPicture;
            s.PropsChanged += RequestPicture;
            s.TexturesChanged += RequestPicture;
            RequestPicture();
            RequestFit();
            OnSelectionChanged();
        }

        UpdateGhost(_ghostPointer);
        InvalidateVisual();
    }

    /// <summary>Asks for the display's next frame while the shown picture has a moving floor and the plan is on screen.</summary>
    private void PlayFrames()
    {
        if (_framesAsked || _snapshot is not { Animated: true } || TopLevel.GetTopLevel(this) is not { } top) return;
        _framesAsked = true;
        top.RequestAnimationFrame(_ =>
        {
            _framesAsked = false;
            if (_snapshot is not { Animated: true } shown || VisualRoot is null) return;
            if (shown.FramesAt(_clock.Elapsed.TotalSeconds) != _shownFrames) InvalidateVisual();
            PlayFrames();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PlayFrames();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Off screen, the next animation frame finds no visual root and asks for no more.
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The map changed and its new shapes are ready: rebuild the picture and outlines from them.</summary>
    private void OnGeometryChanged()
    {
        RequestPicture();
        RequestFit();
        UpdateGhost(_ghostPointer);
        OnSelectionChanged();
        if (_hover is not null && Session is { } s) _hoverOutline = SelectionOverlay.Take([_hover], s.Geometry, _look);
    }

    /// <summary>Finds where the player cannot pass, on a worker, when the marks are shown.</summary>
    private void RequestFit()
    {
        if (!ShowFit || Session is not { } session) return;
        var geometry = session.Geometry;
        _fits.Request(() => (session, FitMarks.Of(geometry)));
    }

    /// <summary>The player's box at the pointer (null when the pointer has left the plan): whether it fits, from the walls round it only.</summary>
    private void UpdateGhost(Point? pointer)
    {
        _ghostPointer = pointer;
        (MapPoint, WadSmith.Editor.Checks.GhostFit)? ghost = null;
        // Not while the pointer is busy dragging something (a move, a turn, a box).
        if (ShowFit && pointer is { } p && Session is { } session && _moveGesture == 0 && _turning is null && _boxFrom is null)
        {
            var at = ToMap(p);
            if (WadSmith.Editor.Checks.Fit.At(session.Geometry, at) is { Inside: true } fit) ghost = (at, fit);
        }

        if (ghost is null && _ghost is null) return;
        _ghost = ghost;
        GhostChanged?.Invoke(ghost?.Item2, ghost?.Item1 ?? default);
        InvalidateVisual();
    }

    private void RequestPicture()
    {
        if (Session is not { } session) return;
        var geometry = session.Geometry;
        var look = _look;
        var mode = Mode;
        var hidden = new HashSet<string>(session.Hidden);
        _pictures.Request(() =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var picture = CanvasSnapshot.Take(geometry, look, mode, hidden, WadSmith.Editor.Editing.Pictures.Animations(session.Project.CopiedNow()));
            FrameStats.Snapshot(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return (session, picture);
        });
    }

    /// <summary>The selection's outline, built on a worker (a selection can be the whole map).</summary>
    private void OnSelectionChanged()
    {
        if (Session is not { } session) return;
        var generation = ++_outlineGeneration;
        if (session.Selection.IsEmpty)
        {
            _selection = null;
            InvalidateVisual();
            return;
        }

        var items = session.Selection.Items.ToArray();
        var geometry = session.Geometry;
        var look = _look;
        _outlines.Request(() => (session, generation, SelectionOverlay.Take(items, geometry, look)));
        InvalidateVisual();
    }

    /// <summary>Brings elements into view: centred, zoomed to fit them with room around, never closer than 2 pixels a unit.</summary>
    public void FitTo(IReadOnlyList<Element> elements)
    {
        if (Session is not { } session || Bounds.Width < 1) return;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y) => (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
        foreach (var e in elements)
        {
            switch (e)
            {
                case Vertex v: Add(v.X, v.Y); break;
                case Thing t: Add(t.X, t.Y); break;
                case Line l: Add(l.Start.X, l.Start.Y); Add(l.End.X, l.End.Y); break;
                case Side { Line: { } l }: Add(l.Start.X, l.Start.Y); Add(l.End.X, l.End.Y); break;
                case Sector s when session.Geometry.WallBoundsAt(s.Slot) is { } b: Add(b.MinX, b.MinY); Add(b.MaxX, b.MaxY); break;
            }
        }

        if (x0 > x1) return;
        _zoom = Math.Min(2, Math.Min((Bounds.Width - ToolsRoom - 96) / Math.Max(x1 - x0, 64), (Bounds.Height - 96) / Math.Max(y1 - y0, 64)));
        (_cx, _cy) = ((x0 + x1) / 2 - ToolsRoom / 2 / _zoom, (y0 + y1) / 2);
        _fitted = true;
        InvalidateVisual();
    }

    /// <summary>Centres on <paramref name="at"/> with <paramref name="span"/> map units round it showing; waits for the first layout when the plan has none yet.</summary>
    public void FitAround(MapPoint at, double span)
    {
        _focus = (at, span);
        _fitted = false;
        InvalidateVisual();
    }

    /// <summary>Centres the map and zooms so all of it shows.</summary>
    public void FitMap()
    {
        _focus = null;
        ComputeFit();
        InvalidateVisual();
    }

    /// <summary>The fit itself, without asking for a redraw (Render calls it, and a render pass may not invalidate).</summary>
    private void ComputeFit()
    {
        if (Session is null || Bounds.Width < 1) return;
        if (_focus is { } f)
        {
            _zoom = Math.Min((Bounds.Width - ToolsRoom - 48) / f.Span, (Bounds.Height - 48) / f.Span);
            (_cx, _cy) = (f.At.X - ToolsRoom / 2 / _zoom, f.At.Y);
            _fitted = true;
            return;
        }

        var (minX, minY, maxX, maxY) = Session.Geometry.Extent();
        _zoom = Math.Min((Bounds.Width - ToolsRoom - 48) / Math.Max(maxX - minX, 64), (Bounds.Height - 48) / Math.Max(maxY - minY, 64));
        _cx = (minX + maxX) / 2 - ToolsRoom / 2 / _zoom;
        _cy = (minY + maxY) / 2;
        _fitted = true;
    }

    private MapPoint ToMap(Point p) => new(_cx + (p.X - Bounds.Width / 2) / _zoom, _cy - (p.Y - Bounds.Height / 2) / _zoom);

    /// <summary>Where a map point is on the control (for tools that drive it with pointer events).</summary>
    internal Point ToScreen(MapPoint m) => new((m.X - _cx) * _zoom + Bounds.Width / 2, Bounds.Height / 2 - (m.Y - _cy) * _zoom);

    public override void Render(DrawingContext context)
    {
        if (Session is null) return;
        if (!_fitted) ComputeFit();
        var hover = _hover is not null && !Session.Selection.Contains(_hover) && Tool != EditorTool.Room ? _hoverOutline : null;
        // A room just drawn stays drawn as a rectangle until the picture with it arrives from the worker.
        if (_drawnIn >= 0 && _snapshot is { } shown && shown.Version >= _drawnIn) _drawnIn = -1;
        var (from, to) = _drawFrom is not null || _drawnIn < 0 ? (_drawFrom, _drawTo) : (_justDrawn.From, (MapPoint?)_justDrawn.To);
        var round = Tool == EditorTool.Room && _round && _corners.Count == 0 && _drawFrom is { } r0 && _drawTo is { } r1 && r0.X != r1.X && r0.Y != r1.Y ? Shapes.Ellipse(r0, r1, _sides) : null;
        var preview = Tool is EditorTool.Room or EditorTool.Stairs || _drawnIn >= 0 ? new DrawPreview(_corners.Count > 0 ? null : from, _corners.Count > 0 ? null : to, Tool == EditorTool.Room ? _drawFrom is null || _corners.Count > 0 ? _snap : _drawTo : null, Tool == EditorTool.Room ? GridStep() : 0, _corners.Count > 0 ? [.. _corners] : null, round, Tool == EditorTool.Room ? _guides : null) : null;
        MovePreview? move = null;
        if (_moveGesture != 0)
        {
            preview = new DrawPreview(null, null, null, _free ? 0 : GridStep());
            if (_moveSet is { } set) move = new MovePreview(set, _offset.X, _offset.Y, _mergeAt, _grip != Grip.None ? _placement : null);
        }

        // A grid chosen by hand shows all the time, so what a corner will snap to is in sight.
        if (preview is null && GridSize > 0) preview = new DrawPreview(null, null, null, GridSize);
        var fit = ShowFit ? new FitOverlay(_fitMarks, _ghost?.At, _ghost?.Fit.Fits ?? false) : null;
        var seconds = _clock.Elapsed.TotalSeconds;
        _shownFrames = _snapshot?.FramesAt(seconds) ?? 0;
        context.Custom(new MapDrawOperation(new Rect(Bounds.Size), _snapshot, _selection, hover, new View(_cx, _cy, _zoom, Bounds.Width, Bounds.Height), _camera, preview, move, fit, Session.Mirror.IsOn ? Session.Mirror : null, seconds));
        // The selected object's facing knob, at the tip of its arrow.
        if (FacingKnob() is { } knob)
        {
            var accent = new SolidColorBrush(Color.FromRgb(0xE0, 0x58, 0x2A));
            var pen = new Pen(accent, 1.6);
            var facing = knob.Thing.Angle * Math.PI / 180;
            context.DrawLine(pen, new Point(knob.Centre.X + Math.Cos(facing) * knob.Ring, knob.Centre.Y - Math.Sin(facing) * knob.Ring), knob.Knob);
            context.DrawEllipse(accent, new Pen(Brushes.White, 1.2), knob.Knob, 5, 5);
        }

        if (_boxFrom is { } b0 && _boxTo is { } b1)
        {
            var box = new Rect(b0, b1).Normalize();
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x24, 0xE0, 0x58, 0x2A)), box);
            context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x58, 0x2A)), 1, new DashStyle([4, 3], 0)), box);
        }

        if (_drawFrom is { } a && _drawTo is { } b && (a.X != b.X || a.Y != b.Y)) DrawSize(context, a, b);
        DrawGrips(context);
        DrawWindowDrag(context);
        DrawMeasure(context);
    }

    /// <summary>The line measured: dashed between its two ends, its length beside it (screen pixels).</summary>
    private void DrawMeasure(DrawingContext context)
    {
        if (Tool != EditorTool.Measure || _measureFrom is not { } a || _measureTo is not { } b || a.DistanceTo(b) < 1) return;
        var brush = new SolidColorBrush(Color.FromRgb(0x7C, 0xD8, 0x6A));
        var (p, q) = (ToScreen(a), ToScreen(b));
        context.DrawLine(new Pen(brush, 2, new DashStyle([6, 4], 0)), p, q);
        context.DrawEllipse(brush, null, p, 4, 4);
        context.DrawEllipse(brush, null, q, 4, 4);
        var text = new FormattedText(a.DistanceTo(b).ToString("0", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);
        var at = new Point((p.X + q.X) / 2 + 8, (p.Y + q.Y) / 2 - text.Height - 4);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0xD0, 0x0A, 0x07, 0x08)), new Rect(at.X - 5, at.Y - 2, text.Width + 10, text.Height + 4), 4);
        context.DrawText(text, at);
    }

    /// <summary>The room's size beside it, in map units, so rooms can be drawn to measure.</summary>
    private void DrawSize(DrawingContext context, MapPoint a, MapPoint b)
    {
        var text = new FormattedText(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Abs(b.X - a.X):0} × {Math.Abs(b.Y - a.Y):0}"), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);
        var x = (Math.Max(a.X, b.X) - _cx) * _zoom + Bounds.Width / 2 + 8;
        var y = Bounds.Height / 2 - (Math.Max(a.Y, b.Y) - _cy) * _zoom - text.Height - 6;
        x = Math.Clamp(x, 4, Math.Max(4, Bounds.Width - text.Width - 12));
        y = Math.Clamp(y, 4, Math.Max(4, Bounds.Height - text.Height - 8));
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0xD0, 0x0A, 0x07, 0x08)), new Rect(x - 5, y - 2, text.Width + 10, text.Height + 4), 4);
        context.DrawText(text, new Point(x, y));
    }

    /// <summary>
    /// The grid a drawn corner snaps to: the one chosen by hand ('[' and ']'), else 64 units, halved while that still leaves 12 pixels between
    /// lines (down to 8), so a closer view draws finer rooms.
    /// </summary>
    private double GridStep()
    {
        if (GridSize > 0) return GridSize;
        var step = 64.0;
        while (step > 8 && step / 2 * _zoom >= 12) step /= 2;
        return step;
    }

    /// <summary>Where a corner drawn at this spot lands: on a corner within a few pixels, else on the grid.</summary>
    private MapPoint Snap(Point p)
    {
        var m = ToMap(p);
        if (Session?.Geometry.VertexNear(m, 8 / _zoom) is { } v) return new MapPoint(v.X, v.Y);
        var step = GridStep();
        return new MapPoint(Math.Round(m.X / step) * step, Math.Round(m.Y / step) * step);
    }

    /// <summary>
    /// Where a corner added to a wall at the pointer goes: the nearest point on the wall, on the grid
    /// along a straight up-down or across wall, never on either end.
    /// </summary>
    private MapPoint CornerOn(Line wall, Point p)
    {
        var m = ToMap(p);
        var (ax, ay, bx, by) = (wall.Start.X, wall.Start.Y, wall.End.X, wall.End.Y);
        var (dx, dy) = (bx - ax, by - ay);
        var t = Math.Clamp(((m.X - ax) * dx + (m.Y - ay) * dy) / Math.Max(dx * dx + dy * dy, 1e-9), 0, 1);
        var on = new MapPoint(ax + dx * t, ay + dy * t);
        var step = GridStep();
        MapPoint snapped = dx == 0 ? new(ax, Math.Round(on.Y / step) * step) : dy == 0 ? new(Math.Round(on.X / step) * step, ay) : new(Math.Round(on.X), Math.Round(on.Y));
        bool Between(MapPoint q) => dx == 0 || dy == 0
            ? Math.Min(ax, bx) <= q.X && q.X <= Math.Max(ax, bx) && Math.Min(ay, by) <= q.Y && q.Y <= Math.Max(ay, by) && !(q.X == ax && q.Y == ay) && !(q.X == bx && q.Y == by)
            : !(q.X == ax && q.Y == ay) && !(q.X == bx && q.Y == by);
        if (Between(snapped)) return snapped;
        return t is > 0 and < 1 ? on : new MapPoint((ax + bx) / 2, (ay + by) / 2);
    }

    /// <summary>Whether a point lies on a wall, between its ends or on one, within half a unit.</summary>
    private static bool OnWall(MapPoint q, Line wall)
    {
        var (ax, ay, dx, dy) = (wall.Start.X, wall.Start.Y, wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return false;
        var along = ((q.X - ax) * dx + (q.Y - ay) * dy) / length;
        var across = Math.Abs((q.X - ax) * dy - (q.Y - ay) * dx) / length;
        return across <= 0.5 && along >= -0.5 && along <= length + 0.5;
    }

    private MapPoint? Nearest(IEnumerable<MapPoint> points)
    {
        MapPoint? best = null;
        var bestD = double.MaxValue;
        foreach (var p in points)
        {
            var d = p.DistanceTo(_grab);
            if (d < bestD) (best, bestD) = (p, d);
        }

        return best;
    }

    /// <summary>
    /// The arrow keys on the plan (D65): with something selected they move it a grid step (Shift: one unit), all the presses
    /// of a run one undo step; with nothing selected they scroll the plan, as Ultimate Doom Builder's and Eureka's do. With
    /// Doom Builder's keys the arrows always scroll and Ctrl with them moves the selection (Ctrl moves it in both).
    /// Returns whether the key was an arrow the plan used.
    /// </summary>
    private bool ArrowKey(KeyEventArgs e)
    {
        var (x, y) = e.Key switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, 1), Key.Down => (0, -1), _ => (0, 0) };
        if ((x, y) == (0, 0) || (e.KeyModifiers & KeyModifiers.Alt) != 0) return false;
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var pressing = _drawFrom is not null || _boxFrom is not null || _pressed is not null || (_moveGesture != 0 && !_keyMove);
        if (Session is { Selection.IsEmpty: false } && (ctrl || !ClassicKeys) && !pressing && _corners.Count == 0 && _windowWall is null)
        {
            var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 1 : GridStep();
            if (!_keyMove)
            {
                _grip = Grip.None;
                BeginMove(_lastPointer, keys: true);
                _keyMove = true;
            }

            _offset = (_offset.X + x * step, _offset.Y + y * step);
            ApplyMove();
            _keyMoveEnd ??= new Avalonia.Threading.DispatcherTimer(KeyMovePause, Avalonia.Threading.DispatcherPriority.Input, (_, _) => EndKeyMove());
            _keyMoveEnd.Stop();
            _keyMoveEnd.Start();
            return true;
        }

        // Scrolling: also while a room is drawn corner by corner, to reach the next corner; never under a pressed button.
        if (ctrl || pressing) return false;
        EndKeyMove();
        _cx += x * ArrowScroll / _zoom;
        _cy += y * ArrowScroll / _zoom;
        InvalidateVisual();
        return true;
    }

    /// <summary>Lets go of a move made with the arrow keys: the map keeps it, joined as a drag's is.</summary>
    private void EndKeyMove()
    {
        _keyMoveEnd?.Stop();
        if (_keyMove) EndMove(keep: true);
    }

    /// <summary>Starts moving the selection: what moves is gathered on a worker (a selection can be the whole map).</summary>
    private void BeginMove(Point at, bool keys = false)
    {
        if (Session is not { } session) return;
        _moveGesture = History.NewGesture();
        _grab = ToMap(_pressAt ?? at);
        _moveSet = null;
        _moveEdit = null;
        _anchor = null;
        _offset = (0, 0);
        _narrowOnClick = false;
        _endWhenGathered = false;
        _lastPointer = at;
        var geometry = session.Geometry;
        var seeds = MoveSet.Seeds.Of(session.Selection.Items);
        var gesture = _moveGesture;
        _gathers.Request(() =>
        {
            var set = MoveSet.Gather(geometry, seeds);
            return (session, gesture, set, Grouping.HeldBy(geometry, set.Vertices));
        });
        if (!keys) Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>The offset for the pointer at <paramref name="p"/>: the anchor follows it, snapped to the grid unless moving freely.</summary>
    private void Follow(Point p, bool free)
    {
        _free = free;
        _lastPointer = p;
        var m = ToMap(p);
        var (dx, dy) = (m.X - _grab.X, m.Y - _grab.Y);
        _mergeAt = null;
        if (_anchor is { } a && !free)
        {
            // The grabbed corner snaps onto a corner that stays within a few pixels (and will become it), else to the grid.
            var r = 8 / _zoom;
            var (tx, ty) = (a.X + dx, a.Y + dy);
            Vertex? best = null;
            var bestD = r;
            foreach (var v in Session?.Geometry.VerticesIn(tx - r, ty - r, tx + r, ty + r) ?? [])
            {
                var d = Math.Sqrt((v.X - tx) * (v.X - tx) + (v.Y - ty) * (v.Y - ty));
                if (!_moving.Contains(v) && d <= bestD) (best, bestD) = (v, d);
            }

            if (best is not null)
            {
                (dx, dy) = (best.X - a.X, best.Y - a.Y);
                _mergeAt = new MapPoint(best.X, best.Y);
            }
            else
            {
                var step = GridStep();
                (dx, dy) = (Math.Round((a.X + dx) / step) * step - a.X, Math.Round((a.Y + dy) / step) * step - a.Y);
            }
        }

        _offset = (dx, dy);
        ApplyMove();
    }

    /// <summary>Moves the map to the offset now (small moves), and redraws the moving pieces.</summary>
    private void ApplyMove()
    {
        if (_moveGesture == 0 || Session is not { } session) return;
        if (_moveSet is { IsEmpty: false } set)
        {
            if (set.Count <= LiveMoveLimit && (Committed || Changed)) Commit(session, set);
            if (_grip == Grip.None) Moving?.Invoke((set.Vertices.Count, set.Things.Count, _offset.X, _offset.Y, _keyMove));
            else Placing?.Invoke(DescribePlacing());
        }

        InvalidateVisual();
    }

    /// <summary>Whether the gesture has put anything on the map yet.</summary>
    private bool Committed => _grip == Grip.None ? _moveEdit is not null : _transformEdit is not null;

    /// <summary>Whether the pointer has taken the selection anywhere yet.</summary>
    private bool Changed => _grip == Grip.None ? _offset != (0, 0) : _placement != Placement.Identity;

    private void Commit(EditorSession session, MoveSet set)
    {
        if (_grip != Grip.None)
        {
            var turn = _transformEdit is null ? new TransformEdit(set, _placement, Session is { } turning ? turning.FlatSize : null) : _transformEdit.To(_placement);
            _transformEdit ??= turn;
            session.History.Run(turn, _moveGesture);
            return;
        }

        var edit = _moveEdit is null ? new MoveEdit(set, _offset.X, _offset.Y, Session is { } moving ? moving.FlatSize : null) : _moveEdit.To(_offset.X, _offset.Y);
        _moveEdit ??= edit;
        session.History.Run(edit, _moveGesture);
    }

    /// <summary>Lets go of a move: the map keeps it (one undo step), or, with <paramref name="keep"/> false, everything goes back.</summary>
    private void EndMove(bool keep)
    {
        if (_moveGesture == 0 || Session is not { } session) return;

        // Let go before what moves was gathered (a very large selection): the move lands when it arrives.
        if (keep && _moveSet is null)
        {
            _endWhenGathered = true;
            return;
        }

        _endWhenGathered = false;
        if (keep && _moveSet is { IsEmpty: false } set && (Committed || Changed))
        {
            Commit(session, set);
            if (!Changed) session.History.Discard(_moveGesture);
            else if (set.Count <= LiveMoveLimit) session.FinishMove(set, _moveGesture);
        }
        else
        {
            session.History.Discard(_moveGesture);
        }

        _moveGesture = 0;
        _keyMove = false;
        _keyMoveEnd?.Stop();
        _moveSet = null;
        _moveEdit = null;
        _mergeAt = null;
        _moving = new HashSet<Vertex>(ReferenceEqualityComparer.Instance);
        Cursor = Cursor.Default;
        if (_grip != Grip.None) Placing?.Invoke(null);
        else Moving?.Invoke(null);
        _grip = Grip.None;
        _transformEdit = null;
        _placement = Placement.Identity;
        InvalidateVisual();
    }

    private void CancelMove() => EndMove(keep: false);

    /// <summary>
    /// How far along a wall the point under the pointer is, from the wall's start: on the grid along a straight
    /// up-down or across wall, in whole eighths along a slanted one, never off the wall.
    /// </summary>
    private double AlongWall(Line wall, Point p)
    {
        var m = ToMap(p);
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        var t = Math.Clamp(((m.X - wall.Start.X) * dx + (m.Y - wall.Start.Y) * dy) / length, 0, length);
        var step = GridStep();
        if (dy == 0) t = Math.Abs(Math.Round((wall.Start.X + Math.Sign(dx) * t) / step) * step - wall.Start.X);
        else if (dx == 0) t = Math.Abs(Math.Round((wall.Start.Y + Math.Sign(dy) * t) / step) * step - wall.Start.Y);
        else t = Math.Round(t / 8) * 8;
        return Math.Clamp(t, 0, length);
    }

    /// <summary>The status strip's sentence while a round room is dragged.</summary>
    private string DescribeRound()
    {
        var (w, h) = _drawFrom is { } a && _drawTo is { } b ? (Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)) : (0, 0);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Round room {w:0} × {h:0}, {_sides} sides · [ and ] for fewer or more sides; let go of Ctrl for a rectangle, Esc to stop");
    }

    /// <summary>The status strip's sentence while a window is dragged along a wall.</summary>
    private string DescribeWindow()
    {
        var width = Math.Abs(_windowTo - _windowFrom);
        return width < 1
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Let go for a window {WadSmith.Editor.Smart.WallWindows.DefaultWidth:0} wide here, or drag along the wall for another width · Esc to stop")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Window {width:0} wide · let go to make it, Esc to stop");
    }

    /// <summary>The status strip's sentence while a door is dragged along a wall.</summary>
    private string DescribeDoor()
    {
        var width = Math.Abs(_windowTo - _windowFrom);
        return width < 1
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Let go for a door filling this wall (a standard 64 in the middle of a wall longer than {WadSmith.Editor.Smart.Doors.MaxWidth:0}), or drag along the wall for another width · Esc to stop")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Door {width:0} wide · let go to make it, Esc to stop");
    }

    /// <summary>The stretch of wall a window is being dragged along (screen pixels).</summary>
    private void DrawWindowDrag(DrawingContext context)
    {
        if (_windowWall is not { } wall || Math.Abs(_windowTo - _windowFrom) < 1) return;
        var length = wall.Length;
        MapPoint At(double t) => new(wall.Start.X + (wall.End.X - wall.Start.X) * t / length, wall.Start.Y + (wall.End.Y - wall.Start.Y) * t / length);
        var pen = new Pen(new SolidColorBrush(Tool == EditorTool.Door ? Color.FromRgb(0xE0, 0x58, 0x2A) : Color.FromRgb(0x5A, 0xC8, 0xFA)), 6, lineCap: PenLineCap.Round);
        context.DrawLine(pen, ToScreen(At(_windowFrom)), ToScreen(At(_windowTo)));
    }

    /// <summary>The handles' places on the control, from the selection's box (empty when the selection has none).</summary>
    private List<(Grip Grip, Point At)> Grips()
    {
        var grips = new List<(Grip, Point)>();
        if (Tool != EditorTool.Select || _moveGesture != 0 || _selection is not { HasHandles: true, Box: { } b }) return grips;
        var (a, c) = (ToScreen(new MapPoint(b.MinX, b.MaxY)), ToScreen(new MapPoint(b.MaxX, b.MinY)));
        var (left, top, right, bottom) = (a.X - GripPad, a.Y - GripPad, c.X + GripPad, c.Y + GripPad);
        var (mx, my) = ((left + right) / 2, (top + bottom) / 2);
        grips.Add((Grip.Turn, new Point(mx, top - KnobRise)));
        grips.Add((Grip.NorthWest, new Point(left, top)));
        grips.Add((Grip.NorthEast, new Point(right, top)));
        grips.Add((Grip.SouthEast, new Point(right, bottom)));
        grips.Add((Grip.SouthWest, new Point(left, bottom)));

        // Side handles only where there is room for them between the corners.
        if (right - left >= 48)
        {
            grips.Add((Grip.North, new Point(mx, top)));
            grips.Add((Grip.South, new Point(mx, bottom)));
        }

        if (bottom - top >= 48)
        {
            grips.Add((Grip.East, new Point(right, my)));
            grips.Add((Grip.West, new Point(left, my)));
        }

        return grips;
    }

    /// <summary>The one object selected (Select tool) and the knob at the tip of its facing arrow, in pixels: dragging it turns the object.</summary>
    private (Thing Thing, Point Centre, Point Knob, double Ring)? FacingKnob()
    {
        if (Tool != EditorTool.Select || Session is not { } s || s.Selection.Count != 1 || s.Selection.Items[0] is not Thing { OnMap: true } t) return null;
        var centre = ToScreen(new MapPoint(t.X, t.Y));
        var ring = Math.Max(3, _look.Radius(t.Type) * _zoom);
        var a = t.Angle * Math.PI / 180;
        var reach = ring * 1.35 + 14;
        return (t, centre, new Point(centre.X + Math.Cos(a) * reach, centre.Y - Math.Sin(a) * reach), ring);
    }

    private bool OnKnob(Point p) => FacingKnob() is { } k && Math.Abs(p.X - k.Knob.X) <= 8 && Math.Abs(p.Y - k.Knob.Y) <= 8;

    private Grip GripAt(Point p)
    {
        foreach (var (grip, at) in Grips())
        {
            if (Math.Abs(p.X - at.X) <= 7 && Math.Abs(p.Y - at.Y) <= 7) return grip;
        }

        return Grip.None;
    }

    private static Cursor GripCursor(Grip grip) => new(grip switch
    {
        Grip.Turn => StandardCursorType.Hand,
        Grip.North => StandardCursorType.TopSide,
        Grip.South => StandardCursorType.BottomSide,
        Grip.East => StandardCursorType.RightSide,
        Grip.West => StandardCursorType.LeftSide,
        Grip.NorthEast => StandardCursorType.TopRightCorner,
        Grip.NorthWest => StandardCursorType.TopLeftCorner,
        Grip.SouthEast => StandardCursorType.BottomRightCorner,
        _ => StandardCursorType.BottomLeftCorner,
    });

    /// <summary>Starts turning or resizing the selection with a handle: what moves is gathered on a worker, as for a move.</summary>
    private void BeginShape(Grip grip, Point at)
    {
        if (Session is not { } session || _selection?.Box is not { } box) return;
        BeginMove(at);
        _grip = grip;
        _gripBox = box;
        _gripFrom = ToMap(at);
        _placement = Placement.Identity;
        _transformEdit = null;
        Cursor = GripCursor(grip);
    }

    /// <summary>
    /// The placement for the pointer at <paramref name="p"/>: the knob turns round the middle in steps of 15°;
    /// a corner or side handle resizes from the opposite one (from the middle with Ctrl), the handle landing on
    /// the grid, and mirrors when dragged past it. Shift turns and resizes freely.
    /// </summary>
    private void Shape(Point p, bool free, bool fromMiddle)
    {
        _free = free;
        _fromMiddle = fromMiddle;
        _lastPointer = p;
        var m = ToMap(p);
        var b = _gripBox;
        var middle = new MapPoint((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
        if (_grip == Grip.Turn)
        {
            var pivot = new MapPoint(Math.Round(middle.X), Math.Round(middle.Y));
            var degrees = (Math.Atan2(m.Y - pivot.Y, m.X - pivot.X) - Math.Atan2(_gripFrom.Y - pivot.Y, _gripFrom.X - pivot.X)) * 180 / Math.PI;
            degrees = Math.IEEERemainder(degrees, 360);
            if (!free) degrees = Math.Round(degrees / 15) * 15;
            _placement = Placement.Turn(degrees, pivot);
        }
        else
        {
            var (east, west, north, south) = (_grip is Grip.East or Grip.NorthEast or Grip.SouthEast, _grip is Grip.West or Grip.NorthWest or Grip.SouthWest, _grip is Grip.North or Grip.NorthEast or Grip.NorthWest, _grip is Grip.South or Grip.SouthEast or Grip.SouthWest);
            var held = new MapPoint(east ? b.MaxX : west ? b.MinX : middle.X, north ? b.MaxY : south ? b.MinY : middle.Y);
            var still = fromMiddle ? middle : new MapPoint(east ? b.MinX : west ? b.MaxX : middle.X, north ? b.MinY : south ? b.MaxY : middle.Y);

            // The handle sits off the box: keep the pointer's distance from the box's own corner or side.
            var target = new MapPoint(m.X - (_gripFrom.X - held.X), m.Y - (_gripFrom.Y - held.Y));
            if (!free)
            {
                var step = GridStep();
                target = new MapPoint(Math.Round(target.X / step) * step, Math.Round(target.Y / step) * step);
            }

            var sx = (east || west) && Math.Abs(held.X - still.X) > 1e-9 ? (target.X - still.X) / (held.X - still.X) : 1;
            var sy = (north || south) && Math.Abs(held.Y - still.Y) > 1e-9 ? (target.Y - still.Y) / (held.Y - still.Y) : 1;
            if (Math.Abs(sx) < 1e-3 || Math.Abs(sy) < 1e-3) return;
            _placement = Placement.Size(sx, sy, still);
        }

        ApplyMove();
    }

    /// <summary>The status strip's sentence for a turn or resize under way.</summary>
    private string DescribePlacing()
    {
        var p = _placement;
        var b = _gripBox;
        if (_grip == Grip.Turn)
        {
            var degrees = Math.Atan2(p.C, p.A) * 180 / Math.PI;
            var way = degrees > 0 ? "to the left" : degrees < 0 ? "to the right" : "";
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Turning {Math.Abs(degrees):0.#}° {way} · Shift turns freely, Esc puts it back");
        }

        var (w, h) = ((b.MaxX - b.MinX) * Math.Abs(p.A), (b.MaxY - b.MinY) * Math.Abs(p.D));
        var mirrored = p.A < 0 && p.D < 0 ? ", turned round" : p.A < 0 ? ", mirrored left to right" : p.D < 0 ? ", mirrored top to bottom" : "";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Resizing to {w:0.#} × {h:0.#} ({Math.Abs(p.A) * 100:0.#} % × {Math.Abs(p.D) * 100:0.#} %){mirrored} · Ctrl resizes from the middle, Shift freely, Esc puts it back");
    }

    /// <summary>The handles round the selection, or the box being turned or resized (screen pixels).</summary>
    private void DrawGrips(DrawingContext context)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0xE0, 0x58, 0x2A));
        var dashed = new Pen(accent, 1, new DashStyle([4, 3], 0));
        if (_grip != Grip.None && _moveGesture != 0)
        {
            // The box as it will land: turned or resized with the selection.
            var b = _gripBox;
            var corners = new[] { (b.MinX, b.MinY), (b.MaxX, b.MinY), (b.MaxX, b.MaxY), (b.MinX, b.MaxY) }.Select(c => ToScreen(_placement.Apply(c.Item1, c.Item2))).ToArray();
            var outline = new StreamGeometry();
            using (var g = outline.Open())
            {
                g.BeginFigure(corners[0], false);
                foreach (var c in corners.Skip(1)) g.LineTo(c);
                g.EndFigure(true);
            }

            context.DrawGeometry(null, dashed, outline);
            return;
        }

        var grips = Grips();
        if (grips.Count == 0) return;
        var (nw, se) = (grips.First(g => g.Grip == Grip.NorthWest).At, grips.First(g => g.Grip == Grip.SouthEast).At);
        context.DrawRectangle(dashed, new Rect(nw, se));
        var knob = grips[0].At;
        context.DrawLine(new Pen(accent, 1), new Point(knob.X, knob.Y + 5), new Point(knob.X, nw.Y));
        var face = new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x10));
        var edge = new Pen(accent, 1.5);
        foreach (var (grip, at) in grips)
        {
            if (grip == Grip.Turn) context.DrawEllipse(face, edge, at, 5, 5);
            else context.DrawRectangle(face, edge, new Rect(at.X - 4, at.Y - 4, 8, 8));
        }
    }

    private void CancelDrawing()
    {
        _guides = [];
        if (_drawFrom is null && _corners.Count == 0) return;
        _drawFrom = _drawTo = null;
        _corners.Clear();
        if (_round) Placing?.Invoke(null);
        _round = false;
        Drawing?.Invoke(null, GridStep());
        CornersChanged?.Invoke(null);
        InvalidateVisual();
    }

    /// <summary>Where the next corner of a shape goes: snapped as usual, and with Shift along a 45° step from the last corner.</summary>
    private MapPoint NextCorner(Point p, bool straight)
    {
        // Back on the first corner closes the shape.
        if (_corners.Count >= 3 && ToScreen(_corners[0]) is var first && Math.Abs(first.X - p.X) <= 8 && Math.Abs(first.Y - p.Y) <= 8)
        {
            _guides = [];
            return _corners[0];
        }

        return Guided(p, SnapCorner(p), _corners.Count > 0 ? _corners[^1] : null, straight);
    }

    /// <summary>
    /// Where a room's corner goes with the smart guides: lined up with corners nearby (the map's and the shape's own), a side at
    /// a whole 45° from the corner before, or at 15° steps with Shift; a corner on one of the map's corners or on a wall stays.
    /// </summary>
    private MapPoint Guided(Point p, MapPoint snapped, MapPoint? previous, bool fifteen)
    {
        var m = ToMap(p);
        var stays = Session?.Geometry.VertexNear(m, 8 / _zoom) is not null || snapped != Snap(p);
        var reach = Math.Min(2048, 400 / _zoom);
        var corners = new List<MapPoint>(_corners);
        if (Session is { } s) corners.AddRange(s.Geometry.VerticesIn(m.X - reach, m.Y - reach, m.X + reach, m.Y + reach).Select(v => new MapPoint(v.X, v.Y)));
        var (at, guides) = SmartGuides.Place(m, snapped, stays, corners, previous, 5 / _zoom, GridStep(), fifteen);
        _guides = guides;
        return at;
    }

    /// <summary>
    /// Finishes the corners clicked one by one: closed on the first corner, a room; finished open (Enter, a
    /// double-click), a line across a room splits it, else the corners close into a room.
    /// </summary>
    private void CloseCorners(bool open = false)
    {
        if (_corners.Count < (open ? 2 : 3) || Session is not { } session)
        {
            return;
        }

        MapPoint[] shape = [.. _corners];
        _corners.Clear();
        _guides = [];
        CornersChanged?.Invoke(null);
        if (open) session.FinishCorners(shape);
        else session.DrawRoom(shape);
        _drawnIn = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Where a corner clicked here goes: on a corner within a few pixels, else on the grid, unless a wall within a few
    /// pixels passes nearer the pointer than the grid point does (a slanted wall): then on the wall. When the grid point
    /// itself lies on that wall, its ends included, the corner goes there — onto the wall's own corner when it is one, so a
    /// click a few units short of a room's corner, zoomed in, does not leave a corner beside it (a user's map, 2026-10-12:
    /// a corner 3.9 units off another left a sliver of room between two rooms).
    /// </summary>
    internal MapPoint SnapCorner(Point p)
    {
        var grid = Snap(p);
        var m = ToMap(p);
        if (Session?.Geometry.VertexNear(m, 8 / _zoom) is not null || WallAt(p) is not { } wall) return grid;
        if (OnWall(grid, wall)) return Session?.Geometry.VertexNear(grid, 0.5) is { } corner ? new MapPoint(corner.X, corner.Y) : grid;
        var on = CornerOn(wall, p);
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var away = Math.Abs((m.X - wall.Start.X) * dy - (m.Y - wall.Start.Y) * dx) / Math.Max(Math.Sqrt(dx * dx + dy * dy), 1e-9);
        return away < m.DistanceTo(grid) ? on : grid;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var before = ToMap(p);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.18, e.Delta.Y), 0.005, 64);
        var after = ToMap(p);
        _cx += before.X - after.X;
        _cy += before.Y - after.Y;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>The choices a right-click on an element offers (a door's looks), set by the editor; nothing opens when it gives none.</summary>
    public static readonly StyledProperty<Func<Element, IReadOnlyList<MenuChoice>>?> ContextMenuForProperty = AvaloniaProperty.Register<MapCanvas, Func<Element, IReadOnlyList<MenuChoice>>?>(nameof(ContextMenuFor));

    public Func<Element, IReadOnlyList<MenuChoice>>? ContextMenuFor
    {
        get => GetValue(ContextMenuForProperty);
        set => SetValue(ContextMenuForProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        EndKeyMove();
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        if (_drawFrom is not null && (props.IsRightButtonPressed || props.IsMiddleButtonPressed))
        {
            // Another button while drawing lets go of the room.
            CancelDrawing();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        // A right-click on something with choices (a door's looks): it is selected, and the choices open at the pointer.
        if (props.IsRightButtonPressed && Session is not null && _corners.Count == 0 && HitTest(e.GetPosition(this)) is { } under && ContextMenuFor?.Invoke(under) is { Count: > 0 } choices)
        {
            Session.Selection.Pick(under, ToMap(e.GetPosition(this)));
            var flyout = new MenuFlyout { Placement = PlacementMode.Pointer };
            foreach (var c in choices)
            {
                if (c.Header == "-")
                {
                    flyout.Items.Add(new Separator());
                    continue;
                }

                var item = new MenuItem { Header = c.Header, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(c.Act) };
                ToolTip.SetTip(item, c.Tip);
                flyout.Items.Add(item);
            }

            flyout.ShowAt(this, true);
            e.Handled = true;
            return;
        }

        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed || (props.IsLeftButtonPressed && _spaceDown))
        {
            _panFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is { } teleporting && Tool == EditorTool.Teleport)
        {
            var at = ToMap(e.GetPosition(this));
            if (teleporting.TeleportPad is not { OnMap: true } pad)
            {
                teleporting.TeleportPad = teleporting.Geometry.SectorAt(at);
            }
            else
            {
                var step = GridStep();
                teleporting.TeleportPad = null;
                teleporting.MakeTeleporter(pad, new MapPoint(Math.Round(at.X / step) * step, Math.Round(at.Y / step) * step), 90);
                Tool = EditorTool.Select;
            }

            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is not null && Tool == EditorTool.Measure)
        {
            _measureFrom = _measureTo = Snap(e.GetPosition(this));
            _measuring = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is { } lighting && Tool == EditorTool.Light)
        {
            lighting.PlaceLight(Snap(e.GetPosition(this)));
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is { } filming && Tool == EditorTool.Camera)
        {
            filming.PlaceCamera(Snap(e.GetPosition(this)));
            Tool = EditorTool.Select;
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is { } keying && Tool == EditorTool.Key)
        {
            if (keying.KeyToPlace is { } colour)
            {
                keying.KeyToPlace = null;
                keying.PlaceKey(colour, Snap(e.GetPosition(this)));
            }

            Tool = EditorTool.Select;
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is { } switching && Tool == EditorTool.Switch)
        {
            var p = e.GetPosition(this);
            if (switching.SwitchTarget is not { OnMap: true } target)
            {
                switching.SwitchTarget = switching.Geometry.SectorAt(ToMap(p));
            }
            else if (WallAt(p) is { } wall)
            {
                switching.SwitchTarget = null;
                switching.MakeSwitch(target, wall, ToMap(p));
                Tool = EditorTool.Select;
            }

            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is not null && Tool is EditorTool.Window or EditorTool.Door)
        {
            if (WallAt(e.GetPosition(this)) is { } wall)
            {
                _windowWall = wall;
                _windowFrom = _windowTo = AlongWall(wall, e.GetPosition(this));
                _drawPress = e.GetPosition(this);
                e.Pointer.Capture(this);
                Placing?.Invoke(Tool == EditorTool.Door ? DescribeDoor() : DescribeWindow());
                InvalidateVisual();
            }
            else
            {
                // A click on nothing says so: a tool that does nothing in silence looks broken.
                Session.Tell(Tool == EditorTool.Door
                    ? "Click on a wall: a door goes in the wall two rooms share, the line where they meet."
                    : "Click on a wall: a window goes in the wall two rooms share, the line where they meet.");
            }

            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is not null && Tool == EditorTool.Stairs)
        {
            _drawPress = e.GetPosition(this);
            _drawFrom = _drawTo = Snap(_drawPress);
            Drawing?.Invoke((0, 0), GridStep());
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is not null && Tool == EditorTool.Room)
        {
            // A double-click finishes a shape drawn corner by corner (a line across a room splits it).
            if (e.ClickCount == 2 && _corners.Count >= 2)
            {
                CloseCorners(open: true);
                e.Handled = true;
                return;
            }

            _drawPress = e.GetPosition(this);
            if (_corners.Count == 0)
            {
                _drawFrom = _drawTo = Guided(_drawPress, Snap(_drawPress), null, false);
                _round = (e.KeyModifiers & KeyModifiers.Control) != 0;
                Drawing?.Invoke((0, 0), GridStep());
            }

            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && OnKnob(e.GetPosition(this)) && FacingKnob() is { } knob)
        {
            _turning = knob.Thing;
            _turnGesture = History.NewGesture();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is not null && GripAt(e.GetPosition(this)) is var grip and not Grip.None)
        {
            _pressAt = e.GetPosition(this);
            BeginShape(grip, e.GetPosition(this));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is not null)
        {
            var hit = HitTest(e.GetPosition(this));
            _pressAt = e.GetPosition(this);
            _pressed = null;
            _narrowOnClick = false;
            var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

            // A double-click on a wall adds a corner there, ready to drag.
            if (e.ClickCount == 2 && hit is Line wall && !shift)
            {
                Session.AddCorner(wall, CornerOn(wall, e.GetPosition(this)));
                e.Handled = true;
                return;
            }

            if (hit is null)
            {
                // Empty space: a drag draws a box that selects what is inside (Shift adds to the selection).
                if (!shift) Session.Selection.Clear();
                _boxFrom = _boxTo = e.GetPosition(this);
                _boxAdd = shift;
                e.Pointer.Capture(this);
            }
            else if (shift)
            {
                Session.Selection.Toggle(hit);
            }
            else
            {
                // Pressing on something selected keeps the selection, so all of it can be dragged; a click
                // without a drag then selects just that.
                if (Session.Selection.Contains(hit)) _narrowOnClick = Session.Selection.Count > 1;
                else Session.Selection.Pick(hit, ToMap(e.GetPosition(this)));
                _pressed = hit;
                e.Pointer.Capture(this);
            }

            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panFrom is { } from)
        {
            _cx -= (p.X - from.X) / _zoom;
            _cy += (p.Y - from.Y) / _zoom;
            _panFrom = p;
            InvalidateVisual();
            return;
        }

        UpdateGhost(p);

        // Turning an object by its facing knob: to the eight directions, or freely with Shift.
        if (_turning is { } turning && Session is { } turner)
        {
            var c = ToScreen(new MapPoint(turning.X, turning.Y));
            var freely = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            var step = freely ? 1 : 45;
            var degrees = Math.Atan2(c.Y - p.Y, p.X - c.X) * 180 / Math.PI;
            var angle = (((int)Math.Round(degrees / step) * step) % 360 + 360) % 360;
            if (angle != turning.Angle) turner.History.Run(new SetProperty<Thing, int>(turning, "Turn thing", t => t.Angle, (t, v) => t.Angle = v, angle), _turnGesture);
            Placing?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Facing {angle}°{(freely ? ", turning freely" : " · it snaps to the eight directions; Shift turns it freely")}"));
            InvalidateVisual();
            return;
        }

        if (_moveGesture == 0 && _boxFrom is null && _pressed is null)
        {
            var onKnob = OnKnob(p);
            if (onKnob != _overKnob)
            {
                _overKnob = onKnob;
                Cursor = onKnob ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                Placing?.Invoke(onKnob ? "Drag the knob to turn it to face another way." : null);
            }

            if (onKnob) return;
        }

        if (Tool == EditorTool.Measure && _measuring && _measureFrom is { } start)
        {
            _measureTo = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? ToMap(p) : Snap(p);
            var length = start.DistanceTo(_measureTo.Value);
            Placing?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{length:0} long ({length / 32:0.#} players across) · let go to see the floors and ceilings along it; Shift measures freely"));
            InvalidateVisual();
            return;
        }

        if (Tool is EditorTool.Window or EditorTool.Door && _windowWall is { } held)
        {
            _windowTo = AlongWall(held, p);
            Placing?.Invoke(Tool == EditorTool.Door ? DescribeDoor() : DescribeWindow());
            InvalidateVisual();
            return;
        }

        if (Tool == EditorTool.Stairs)
        {
            var at = Snap(p);
            if (_drawFrom is { } bottom)
            {
                _drawTo = at;
                Drawing?.Invoke((Math.Abs(at.X - bottom.X), Math.Abs(at.Y - bottom.Y)), GridStep());
            }

            _snap = at;
            InvalidateVisual();
            HoverChanged?.Invoke(null, at);
            return;
        }

        if (Tool == EditorTool.Room)
        {
            var straight = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            var at = _corners.Count > 0 ? NextCorner(p, straight) : Guided(p, Snap(p), null, false);
            if (_drawFrom is { } from2 && _corners.Count == 0)
            {
                _drawTo = at;
                _round = (e.KeyModifiers & KeyModifiers.Control) != 0;
                Drawing?.Invoke((Math.Abs(at.X - from2.X), Math.Abs(at.Y - from2.Y)), GridStep());
                if (_round) Placing?.Invoke(DescribeRound());
            }

            if (_corners.Count > 0) CornersChanged?.Invoke((_corners.Count, at.DistanceTo(_corners[^1])));
            _snap = at;
            InvalidateVisual();
            HoverChanged?.Invoke(null, at);
            return;
        }

        // A press on something, then a drag of a few pixels, moves the selection.
        var free = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (_moveGesture != 0)
        {
            if (_endWhenGathered) return;
            if (_grip != Grip.None) Shape(p, free, (e.KeyModifiers & KeyModifiers.Control) != 0);
            else Follow(p, free);
            return;
        }

        if (_boxFrom is not null)
        {
            _boxTo = p;
            InvalidateVisual();
            return;
        }

        if (_pressed is not null && _pressAt is { } pressed && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Math.Abs(p.X - pressed.X) + Math.Abs(p.Y - pressed.Y) > 4)
        {
            BeginMove(p);
            Follow(p, free);
            return;
        }

        if (GripAt(p) is var over and not Grip.None)
        {
            _overGrip = true;
            Cursor = GripCursor(over);
            HoverChanged?.Invoke(null, ToMap(p));
            return;
        }

        var hit = Tool is EditorTool.Door or EditorTool.Window || (Tool == EditorTool.Switch && Session?.SwitchTarget is not null) ? WallAt(p) : HitTest(p);
        if (!ReferenceEquals(hit, _hover) || _overGrip)
        {
            _overGrip = false;
            _hover = hit;
            _hoverOutline = hit is null || Session is null ? null : SelectionOverlay.Take([hit], Session.Geometry, _look);
            Cursor = hit is not null && Session?.Selection.Contains(hit) == true ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
            InvalidateVisual();
        }

        HoverChanged?.Invoke(hit, ToMap(p));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panFrom is not null)
        {
            _panFrom = null;
            e.Pointer.Capture(null);
            return;
        }

        if (_turning is not null)
        {
            _turning = null;
            e.Pointer.Capture(null);
            Placing?.Invoke(null);
            return;
        }

        if (_boxFrom is { } from && _boxTo is { } to && e.InitialPressMouseButton == MouseButton.Left)
        {
            _boxFrom = _boxTo = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
            if (Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) <= 4 || Session is not { } boxed) return;
            var (c0, c1) = (ToMap(from), ToMap(to));
            var geometry = boxed.Geometry;
            var add = _boxAdd;
            var only = Tool == EditorTool.Select ? Picks : PickOnly.Anything;
            _boxes.Request(() => (boxed, add, BoxSelection.Pick(geometry, Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y), Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y), only)));
            return;
        }

        if (_grip != Grip.None && _moveGesture != 0 && e.InitialPressMouseButton == MouseButton.Left)
        {
            _pressAt = null;
            e.Pointer.Capture(null);
            EndMove(keep: true);
            return;
        }

        if (_pressed is not null && e.InitialPressMouseButton == MouseButton.Left)
        {
            var clicked = _pressed;
            _pressed = null;
            _pressAt = null;
            e.Pointer.Capture(null);
            if (_moveGesture != 0) EndMove(keep: true);
            else if (_narrowOnClick && Session is { } s) s.Selection.Pick(clicked, ToMap(e.GetPosition(this)));
            _narrowOnClick = false;
            return;
        }

        if (Tool == EditorTool.Measure && _measuring && e.InitialPressMouseButton == MouseButton.Left)
        {
            e.Pointer.Capture(null);
            _measuring = false;
            Placing?.Invoke(null);
            if (_measureFrom is { } m0 && _measureTo is { } m1 && m0.DistanceTo(m1) >= 1) Measured?.Invoke(m0, m1);
            else (_measureFrom, _measureTo) = (null, null);
            InvalidateVisual();
            return;
        }

        if (Tool == EditorTool.Door && _windowWall is { } doorway && e.InitialPressMouseButton == MouseButton.Left && Session is { } hanging)
        {
            // A click makes a door of the wall (a long one gets a standard door in its middle); a drag, of the stretch dragged.
            e.Pointer.Capture(null);
            var let = e.GetPosition(this);
            var stretched = Math.Abs(let.X - _drawPress.X) + Math.Abs(let.Y - _drawPress.Y) > 4 && Math.Abs(_windowTo - _windowFrom) >= 8;
            var span = (Math.Min(_windowFrom, _windowTo), Math.Max(_windowFrom, _windowTo));
            _windowWall = null;
            Placing?.Invoke(null);
            InvalidateVisual();
            hanging.MakeDoor(doorway, stretched ? span : null);
            return;
        }

        if (Tool == EditorTool.Window && _windowWall is { } windowed && e.InitialPressMouseButton == MouseButton.Left && Session is { } glazing)
        {
            e.Pointer.Capture(null);
            var let = e.GetPosition(this);
            var (start, end) = Math.Abs(let.X - _drawPress.X) + Math.Abs(let.Y - _drawPress.Y) > 4 ? (_windowFrom, _windowTo) : (_windowFrom, _windowFrom);
            _windowWall = null;
            Placing?.Invoke(null);
            InvalidateVisual();
            glazing.MakeWindow(windowed, start, end);
            return;
        }

        if (Tool == EditorTool.Stairs && _drawFrom is { } low && _drawTo is { } high && e.InitialPressMouseButton == MouseButton.Left && Session is { } building)
        {
            e.Pointer.Capture(null);
            _drawFrom = _drawTo = null;
            Drawing?.Invoke(null, GridStep());
            InvalidateVisual();
            if (Math.Abs(high.X - low.X) >= 1 && Math.Abs(high.Y - low.Y) >= 1) building.MakeStairs(low, high);
            return;
        }

        if (Tool != EditorTool.Room || e.InitialPressMouseButton != MouseButton.Left || Session is not { } drawing) return;
        e.Pointer.Capture(null);
        var released = e.GetPosition(this);
        var dragged = Math.Abs(released.X - _drawPress.X) + Math.Abs(released.Y - _drawPress.Y) > 4;
        var (a0, b0) = (_drawFrom, _drawTo);
        _drawFrom = _drawTo = null;
        Drawing?.Invoke(null, GridStep());
        InvalidateVisual();

        // A click (no drag) puts a corner of a shape drawn corner by corner; the first corner again closes it.
        if (!dragged || _corners.Count > 0 || a0 is not { } a || b0 is not { } b || Math.Abs(b.X - a.X) < 1 || Math.Abs(b.Y - a.Y) < 1)
        {
            var corner = _corners.Count > 0 ? NextCorner(released, (e.KeyModifiers & KeyModifiers.Shift) != 0) : Guided(released, SnapCorner(released), null, false);
            if (_corners.Count >= 3 && corner.DistanceTo(_corners[0]) < 1e-6)
            {
                CloseCorners();
            }
            else if (_corners.Count == 0 || corner.DistanceTo(_corners[^1]) > 1e-6)
            {
                _corners.Add(corner);
                CornersChanged?.Invoke((_corners.Count, 0));
            }

            InvalidateVisual();
            return;
        }

        var session = drawing;
        MapPoint[] box = [new(a.X, a.Y), new(a.X, b.Y), new(b.X, b.Y), new(b.X, a.Y)];
        if (_round) Placing?.Invoke(null);
        session.DrawRoom(_round ? Shapes.Ellipse(a, b, _sides) : box);
        _round = false;
        _drawnIn = session.Map.Version;
        _justDrawn = (a, b);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        UpdateGhost(null);
        if (_hover is null) return;
        _hover = null;
        _hoverOutline = null;
        InvalidateVisual();
        HoverChanged?.Invoke(null, default);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ArrowKey(e))
        {
            e.Handled = true;
            return;
        }

        // Any other key (not a modifier on its own) lets go of the arrows' move first, so it acts on the map as moved.
        if (_keyMove && e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt)) EndKeyMove();

        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                e.Handled = true;
                break;
            // While a round room is dragged: fewer or more sides (Ctrl may still be held for the round shape).
            case Key.OemOpenBrackets when _drawFrom is not null && _round:
                _sides = Math.Max(Shapes.MinSides, _sides - 2);
                Placing?.Invoke(DescribeRound());
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.OemCloseBrackets when _drawFrom is not null && _round:
                _sides = Math.Min(Shapes.MaxSides, _sides + 2);
                Placing?.Invoke(DescribeRound());
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Escape when _drawFrom is not null || _corners.Count > 0:
                CancelDrawing();
                e.Handled = true;
                break;
            case Key.Enter when _corners.Count >= 2:
                CloseCorners(open: true);
                e.Handled = true;
                break;
            case Key.Back when _corners.Count > 0:
                _corners.RemoveAt(_corners.Count - 1);
                CornersChanged?.Invoke(_corners.Count == 0 ? null : (_corners.Count, 0));
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Escape when _boxFrom is not null:
                _boxFrom = _boxTo = null;
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Escape when _windowWall is not null:
                _windowWall = null;
                Placing?.Invoke(null);
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.Escape when _moveGesture != 0:
                CancelMove();
                _pressed = null;
                e.Handled = true;
                break;
            case Key.Escape when Tool != EditorTool.Select:
                Tool = EditorTool.Select;
                e.Handled = true;
                break;
            case Key.Escape:
                Session?.Selection.Clear();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                _zoom = Math.Min(_zoom * 1.4, 64);
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                _zoom = Math.Max(_zoom / 1.4, 0.005);
                InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceDown = false;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        EndKeyMove();
    }

    /// <summary>Marks where the 3D camera stands and looks (null hides the mark).</summary>
    public void ShowCamera(ThreeD.Camera3D? camera)
    {
        _camera = camera;
        InvalidateVisual();
    }

    /// <summary>Zooms about the centre (for tools that measure drawing).</summary>
    internal void ZoomBy(double factor)
    {
        _zoom *= factor;
        InvalidateVisual();
    }

    /// <summary>Moves the view by screen pixels (for tools that measure panning).</summary>
    internal void PanBy(double dx, double dy)
    {
        _cx -= dx / _zoom;
        _cy += dy / _zoom;
        InvalidateVisual();
    }

    /// <summary>A 3D model file dragged in from the file manager, its local path; null for anything else.</summary>
    internal static string? ModelFileIn(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null && ModelProps.IsModel(p));

    /// <summary>A picture or video file dragged in from the file manager, its local path; null for anything else.</summary>
    internal static string? PictureFileIn(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null && (Pictures.IsPicture(p) || WadSmith.Core.Graphics.VideoFrames.IsVideo(p)));

    /// <summary>A sound or music file dragged in from the file manager, its local path; null for anything else.</summary>
    internal static string? AudioFileIn(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null && SoundFiles.IsAudio(p));

    /// <summary>A shelf picture held over the plan: the wall or room under it lights up, and the status says what a drop does.</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Session is not null && ModelFileIn(e) is { } model)
        {
            e.DragEffects = DragDropEffects.Copy;
            DropHint?.Invoke($"Let go to bring in {System.IO.Path.GetFileName(model)} as a prop and place it here.");
            return;
        }

        if (Session is not null && PictureFileIn(e) is { } picture)
        {
            e.DragEffects = DragDropEffects.Copy;
            var at = e.GetPosition(this);
            DropHint?.Invoke(WallAt(at) is not null || Session.Geometry.SectorAt(ToMap(at)) is not null
                ? $"Let go to bring in {System.IO.Path.GetFileName(picture)} and put it {(WallAt(at) is not null ? "on this wall" : $"on this room's {(Mode == MapViewMode.Ceilings ? "ceiling" : "floor")}")}."
                : "Drop the picture on a wall or in a room.");
            return;
        }

        if (Session is not null && AudioFileIn(e) is { } audio)
        {
            e.DragEffects = DragDropEffects.Copy;
            DropHint?.Invoke($"Let go to place {System.IO.Path.GetFileName(audio)} here as a sound (music becomes the map's music).");
            return;
        }

        if (Session is null || e.DataTransfer.TryGetValue(ViewModels.ShelfDrop.Format) is not { } drop)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        var p = e.GetPosition(this);
        var key = drop.Texture is null && drop.Prefab is null && WadSmith.Editor.Smart.Keys.ColourOf(drop.Thing, Session.Engine) is not null;
        Element? target = key ? Session.DoorAt(ToMap(p), 14 / _zoom) : drop.Texture is null ? null : WallAt(p) is { } w ? w : Session.Geometry.SectorAt(ToMap(p));
        // A texture over a wall: the whole straight wall is outlined, or only that wall with Shift (a section of it).
        var section = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (!ReferenceEquals(target, _hover) || section != _hoverSection)
        {
            _hover = target;
            _hoverSection = section;
            Element[] outlined = target is Line hovered && drop.Texture is not null && !drop.Flat && !section
                ? [.. Session.StraightWall(EditorSession.FaceToward(hovered, ToMap(p))).Select(f => (Element)f.Line!)]
                : target is null ? [] : [target];
            _hoverOutline = outlined.Length == 0 ? null : SelectionOverlay.Take(outlined, Session.Geometry, _look);
            InvalidateVisual();
        }

        DropHint?.Invoke(drop.Prefab is not null
            ? $"Let go to put a copy of {drop.Label} here."
            : drop.Texture is null
            ? (key && target is Sector ? $"Let go to lock this door with {drop.Label}; the key itself then goes somewhere the player reaches first." : $"Let go to place {drop.Label} here.")
            : target switch
            {
                Line { Back: null } => section ? $"Let go to put {drop.Label} on a section of this wall as wide as the picture." : $"Let go to put {drop.Label} on this whole wall. Hold Shift for just a section as wide as the picture.",
                Line => section ? $"Let go to put {drop.Label} on a section of this wall as wide as the picture, on this side." : $"Let go to put {drop.Label} on the upper and lower parts of this whole wall, on this side. Hold Shift for just a section.",
                Sector => $"Let go to put {drop.Label} on this room's {(Mode == MapViewMode.Ceilings ? "ceiling" : "floor")}.",
                _ => "Drop it on a wall or in a room.",
            } + (drop.From is { } source && target is not null ? $" It is copied into this project from {source.Game.Title} first." : ""));
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropHint?.Invoke(null);
        ClearHover();
        if (Session is { } importing && ModelFileIn(e) is { } model)
        {
            var m = ToMap(e.GetPosition(this));
            var step = GridStep();
            importing.ImportModel(model, (e.KeyModifiers & KeyModifiers.Shift) != 0 ? m : new MapPoint(Math.Round(m.X / step) * step, Math.Round(m.Y / step) * step));
            Focus();
            e.Handled = true;
            return;
        }

        if (Session is { } painting && PictureFileIn(e) is { } picture)
        {
            var at = ToMap(e.GetPosition(this));
            var (reach, ceiling, section) = (7 / _zoom, Mode == MapViewMode.Ceilings, (e.KeyModifiers & KeyModifiers.Shift) != 0);
            painting.ImportPicture(picture, texture =>
            {
                if (painting.Paint(texture, at, reach, ceiling, section) is { } why) painting.Tell(why);
            });
            Focus();
            e.Handled = true;
            return;
        }

        if (Session is { } hearing && AudioFileIn(e) is { } audio)
        {
            var m = ToMap(e.GetPosition(this));
            var step = GridStep();
            hearing.ImportAudio(audio, (e.KeyModifiers & KeyModifiers.Shift) != 0 ? m : new MapPoint(Math.Round(m.X / step) * step, Math.Round(m.Y / step) * step));
            Focus();
            e.Handled = true;
            return;
        }

        if (Session is not { } session || e.DataTransfer.TryGetValue(ViewModels.ShelfDrop.Format) is not { } drop) return;
        var p = e.GetPosition(this);
        if (drop.Prefab is { } prefab)
        {
            session.PlacePrefab(prefab, ToMap(p));
        }
        else if (drop.Texture is { } borrowed && drop.From is { } from)
        {
            session.PaintBorrowed(from, borrowed, drop.Flat, ToMap(p), 7 / _zoom, Mode == MapViewMode.Ceilings, (e.KeyModifiers & KeyModifiers.Shift) != 0);
        }
        else if (drop.Texture is { } texture)
        {
            session.Paint(texture, ToMap(p), 7 / _zoom, Mode == MapViewMode.Ceilings, (e.KeyModifiers & KeyModifiers.Shift) != 0);
        }
        else
        {
            var m = ToMap(p);
            var step = GridStep();
            var snapped = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? m : new MapPoint(Math.Round(m.X / step) * step, Math.Round(m.Y / step) * step);
            if (drop.Library is { } item) session.PlaceLibraryObject(item, snapped);
            else if (drop.QuakeModel is { } quakeModel && drop.From is { } quake) session.PlaceQuakeModel(quake, quakeModel, snapped);
            else if (drop.From is { } other) session.PlaceBorrowedThing(other, drop.Thing, snapped);
            else session.DropThing(drop.Thing, m, snapped, 14 / _zoom);
        }

        Focus();
        e.Handled = true;
    }

    private void ClearHover()
    {
        if (_hover is null) return;
        _hover = null;
        _hoverOutline = null;
        InvalidateVisual();
    }

    /// <summary>The wall under the pointer, for tools that work on walls.</summary>
    /// <summary>The wall a door, window or switch click means: the nearest within 14 pixels (twice a pick's reach: a wall is a thin line and these tools want one).</summary>
    private Line? WallAt(Point p) => Session?.Geometry.LineNear(ToMap(p), 14 / _zoom);

    /// <summary>What a click at this spot picks: a thing, then a corner (zoomed in), then a wall, then the area.</summary>
    private Element? HitTest(Point p)
    {
        if (Session is null) return null;
        var geometry = Session.Geometry;
        var m = ToMap(p);
        var px = 7 / _zoom;

        // Locked rooms and objects (and the walls and corners they hold) are not picked: clicks go through to what is free.
        bool Free(Element e) => !Grouping.Holds(geometry, e) && !Grouping.Hides(geometry, e, Session.Hidden);
        // With the Select tool picking one kind only, the rest is passed over (corners then at any zoom).
        var only = Tool == EditorTool.Select ? Picks : PickOnly.Anything;
        if (only is PickOnly.Anything or PickOnly.Objects && geometry.ThingNear(m, Math.Max(16, px)) is { } t && Free(t)) return t;
        if ((only == PickOnly.Corners || (only == PickOnly.Anything && _zoom > 0.25)) && geometry.VertexNear(m, only == PickOnly.Corners ? Math.Max(px, 12 / _zoom) : px) is { } v && Free(v)) return v;
        if (only is PickOnly.Anything or PickOnly.Walls && geometry.LineNear(m, px) is { } l && Free(l)) return l;
        return only is PickOnly.Anything or PickOnly.Rooms && geometry.SectorAt(m) is { } s && Free(s) ? s : null;
    }

    internal readonly record struct View(double Cx, double Cy, double Zoom, double Width, double Height)
    {
        public SKMatrix Matrix => SKMatrix.CreateScaleTranslation((float)Zoom, (float)-Zoom, (float)(Width / 2 - Cx * Zoom), (float)(Height / 2 + Cy * Zoom));
    }
}
