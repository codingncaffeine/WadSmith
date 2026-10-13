using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>How the 3D view's camera moves (D46).</summary>
internal enum CameraMode
{
    /// <summary>Turn around the map or the selection, like a model on a table.</summary>
    Orbit,

    /// <summary>Move freely through walls and floors.</summary>
    Fly,

    /// <summary>Walk as the player does: eye height, steps up to 24, stopped by solid walls.</summary>
    Walk,
}

/// <summary>
/// The 3D view (D46): the map from any angle. Orbit: drag to turn around it, right- or middle-drag to
/// move, scroll to come closer. Fly and Walk: drag to look, WASD or the arrows to move (Shift for
/// speed; Q and E down and up when flying). Home frames the whole map again. The scene is built on a
/// worker from the session's shapes and drawn on its own render thread; this control only shows
/// finished pictures and turns input into a camera.
/// </summary>
internal sealed class View3D : Control
{
    public static readonly StyledProperty<EditorSession?> SessionProperty = AvaloniaProperty.Register<View3D, EditorSession?>(nameof(Session));
    public static readonly StyledProperty<CameraMode> ModeProperty = AvaloniaProperty.Register<View3D, CameraMode>(nameof(Mode), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ClassicKeysProperty = AvaloniaProperty.Register<View3D, bool>(nameof(ClassicKeys));

    private readonly HashSet<Key> _keys = [];
    private readonly DispatcherTimer _mover;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _x;
    private double _y;
    private double _z;
    private double _lookYaw;
    private double _lookPitch;

    private readonly LatestOnWorker<(EditorSession Session, MapScene Scene)> _scenes;
    private readonly LatestOnWorker<(float[] Surfaces, Dictionary<int, float[]> Sprites)> _tints;
    private readonly LatestOnWorker<(EditorSession Session, Editor.Model.Element? Hit, bool Toggle)> _picks;
    private readonly LatestOnWorker<(EditorSession Session, MapScene Scene, SceneHit? Hit, ViewModels.ShelfDrop? Drop, bool Apply, bool Shift)> _hovers;
    private readonly LatestOnWorker<(EditorSession Session, MapScene Scene, double Yaw, double Pitch)> _looks;
    private SceneHit? _hover;
    private Point? _lastPointer;

    /// <summary>A height drag (Ctrl+left on a floor or ceiling): the surface, its gesture, how much is applied, where it began and a pixel's worth there.</summary>
    private (SurfaceTarget Target, long Gesture, double Applied, Point From, double UnitsPerPixel, string Name)? _heightDrag;

    /// <summary>A texture drag (Ctrl+left on a wall face): the face, its gesture, how much is applied, where it began and a pixel's worth there.</summary>
    private (Editor.Model.Side Face, long Gesture, double AppliedX, double AppliedY, Point From, double UnitsPerPixel)? _textureDrag;

    /// <summary>The face Ctrl+arrows are nudging and the gesture the nudges join.</summary>
    private (Editor.Model.Side Face, long Gesture)? _nudge;

    /// <summary>The surface Ctrl+scroll is stepping and the gesture its steps join (one undo step until the pointer moves to another surface).</summary>
    private (Editor.Model.Element Element, bool Ceiling, long Gesture)? _wheel;
    private Point _pressedAt;
    private double _dragged;
    private Renderer3D? _renderer;
    private WriteableBitmap? _shown;
    private string? _message = "Starting the 3D view…";
    private MapScene? _scene;
    private bool _framed;

    // A spot to look at instead of framing the whole map when the first scene arrives (a landmark, D28).
    private (MapPoint At, double Span)? _focus;
    private double _tx;
    private double _ty;
    private double _tz;
    private double _yaw = 50;
    private double _pitch = -40;
    private double _distance = 2048;
    private Point? _dragFrom;
    private bool _panning;

    public View3D()
    {
        ClipToBounds = true;
        Focusable = true;
        _mover = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Move());
        _scenes = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || _renderer is null) return;
            _scene = r.Scene;
            if (!_framed) Frame();
            _renderer.Show(r.Scene, r.Session.Resources);
            SendCamera();
            RequestTint();

            // The surface under the pointer may have moved (a height changed): find it again in the new scene.
            if (_lastPointer is { } p && _dragFrom is null) RequestHover(p);
        }, "the 3D scene");
        _tints = new(ViewModels.MainViewModel.PostToUi, t => _renderer?.Highlight(t), "the 3D selection");
        _looks = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || !ReferenceEquals(r.Scene, _scene)) return;
            (_yaw, _pitch) = (r.Yaw, r.Pitch);
            SendCamera();
        }, "a clear view of a landmark");
        _hovers = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || !ReferenceEquals(r.Scene, _scene)) return;
            if (r.Drop is null && !ReferenceEquals(_hover?.Element, r.Hit?.Element)) HoverChanged?.Invoke(r.Hit?.Element);
            _hover = r.Hit;
            _renderer?.Hover(r.Hit is { } hit ? r.Scene.Surface(hit) : ([], []));
            if (r.Drop is not { } drop) return;
            if (!r.Apply)
            {
                DropHint?.Invoke(DropWords(drop, r.Hit));
                return;
            }

            DropHint?.Invoke(null);
            _renderer?.Hover(([], []));
            Apply(r.Session, drop, r.Hit, r.Shift);
        }, "the surface under the pointer");
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            DropHint?.Invoke(null);
            ClearHover();
        });
        _picks = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session)) return;
            if (r.Hit is { } hit && Editor.Editing.Grouping.Holds(r.Session.Geometry, hit))
            {
                r.Session.Tell("That is locked: unlock it with nothing selected, under Groups, to select or change it.");
                return;
            }

            if (r.Hit is null)
            {
                if (!r.Toggle) r.Session.Selection.Clear();
            }
            else if (r.Toggle)
            {
                r.Session.Selection.Toggle(r.Hit);
            }
            else
            {
                r.Session.Selection.Set(r.Hit);
            }
        }, "a 3D click");
    }

    public EditorSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    /// <summary>Doom Builder's movement keys (E S D F, Page Up and Page Down) instead of W A S D, Q and E.</summary>
    public bool ClassicKeys
    {
        get => GetValue(ClassicKeysProperty);
        set => SetValue(ClassicKeysProperty, value);
    }

    // The keys that move the camera in Fly and Walk: forward, back, left, right, up, down (arrows and Page keys in both presets).
    private (Key Forward, Key Back, Key Left, Key Right, Key? Up, Key? Down) MoveKeys => ClassicKeys ? (Key.E, Key.D, Key.S, Key.F, null, null) : (Key.W, Key.S, Key.A, Key.D, Key.E, Key.Q);

    private bool Moves(Key key)
    {
        var m = MoveKeys;
        return key == m.Forward || key == m.Back || key == m.Left || key == m.Right || key == m.Up || key == m.Down
            || key is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.LeftShift or Key.RightShift;
    }

    public CameraMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Raised when the camera moves: where it stands in Fly and Walk, null while orbiting (the plan shows no marker then).</summary>
    public event Action<Camera3D?>? CameraMoved;

    /// <summary>What letting go of a shelf drag would do here (null to clear), for the status bar.</summary>
    public event Action<string?>? DropHint;

    /// <summary>Raised when the pointer comes over something new in the view (null: over nothing), as the plan's HoverChanged.</summary>
    public event Action<Element?>? HoverChanged;

    /// <summary>The surface under the pointer as last found (a moment behind the pointer), for instruments.</summary>
    internal SceneHit? Hovered => _hover;

    /// <summary>The camera as it stands now: the eye, which way it looks.</summary>
    internal Camera3D CurrentCamera => Current;

    /// <summary>The scene drawn now (null before the first), for instruments that draw it again elsewhere.</summary>
    internal MapScene? Scene => _scene;

    /// <summary>Raised when G is pressed in the view: the game's own view on or off (D41).</summary>
    public event Action? EngineViewToggled;

    /// <summary>Where a map point lands on this view, in its own coordinates; null when it is behind the camera.</summary>
    internal Point? ScreenPoint(double x, double y, double z)
    {
        var aspect = Bounds.Width / Math.Max(Bounds.Height, 1);
        var m = Current.ViewProjection(aspect, flipY: false);
        var cx = m[0] * x + m[4] * y + m[8] * z + m[12];
        var cy = m[1] * x + m[5] * y + m[9] * z + m[13];
        var cw = m[3] * x + m[7] * y + m[11] * z + m[15];
        if (cw <= 1e-6) return null;
        return new Point((cx / cw + 1) / 2 * Bounds.Width, (1 - cy / cw) / 2 * Bounds.Height);
    }

    /// <summary>Asks the worker what is under a point of the view; a shelf drag's payload rides along, applied on the answer when <paramref name="apply"/>.</summary>
    private void RequestHover(Point at, ViewModels.ShelfDrop? drop = null, bool apply = false, bool shift = false)
    {
        if (Session is not { } session || _scene is not { } scene) return;
        var aspect = Bounds.Width / Math.Max(Bounds.Height, 1);
        var across = 2 * at.X / Math.Max(Bounds.Width, 1) - 1;
        var up = 1 - 2 * at.Y / Math.Max(Bounds.Height, 1);
        var camera = Current;
        var (origin, direction) = camera.Ray(across, up, aspect);
        _hovers.Request(() => (session, scene, scene.Hit(origin, direction, camera.Yaw), drop, apply, shift));
    }

    private void ClearHover()
    {
        if (_hover is null) return;
        _hover = null;
        HoverChanged?.Invoke(null);
        _renderer?.Hover(([], []));
    }

    /// <summary>A floor or ceiling as the height notices name it: a platform's top and underside by those names.</summary>
    private static string SurfaceName(SceneHit hit) => hit.Platform ? (hit.Kind == HitKind.Floor ? "The platform's top" : "The platform's underside") : hit.Kind == HitKind.Ceiling ? "Ceiling" : "Floor";

    /// <summary>What is dragged over the view: a shelf picture, or a 3D model file from the file manager as a drop of its own.</summary>
    private static ViewModels.ShelfDrop? DropOf(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(ViewModels.ShelfDrop.Format) ?? (Canvas.MapCanvas.ModelFileIn(e) is { } model ? new ViewModels.ShelfDrop(null, 0, System.IO.Path.GetFileName(model), ModelFile: model) : null);

    private static string DropWords(ViewModels.ShelfDrop drop, SceneHit? hit)
    {
        if (drop.ModelFile is not null) return hit is { Kind: HitKind.Floor } ? $"Let go to bring in {drop.Label} as a prop and place it here." : "Drop the model on a floor.";
        if (drop.Prefab is not null) return "Prefabs go on the plan: drop it there.";
        if (drop.Texture is null) return hit is { Kind: HitKind.Floor } ? $"Let go to place {drop.Label} here." : "Drop the object on a floor.";
        if (hit is not { Kind: not HitKind.Thing } h) return "Drop it on a wall, a floor or a ceiling.";
        var wall = h.Kind == HitKind.Wall ? " (the whole wall; hold Shift for just a section as wide as the picture)" : "";
        return $"Let go to put {drop.Label} on {h.Words}{wall}." + (drop.From is { } source ? $" It is copied into this project from {source.Game.Title} first." : "");
    }

    /// <summary>A shelf drop on the surface found under it: the texture on that surface, or the object on that floor.</summary>
    private static void Apply(EditorSession session, ViewModels.ShelfDrop drop, SceneHit? hit, bool shift)
    {
        if (drop.Prefab is not null)
        {
            session.Tell("Prefabs go on the plan: drop it there.");
            return;
        }

        if (drop.Texture is { } texture)
        {
            if (hit is not { Kind: not HitKind.Thing } h)
            {
                session.Tell("Drop it on a wall, a floor or a ceiling.");
                return;
            }

            // Shift on a wall: a section as wide as the picture where it was let go, not the whole wall.
            WadSmith.Editor.Geometry.MapPoint? section = shift && h.Kind == HitKind.Wall ? new(h.X, h.Y) : null;
            if (drop.From is { } from) session.PaintBorrowedSurface(from, texture, drop.Flat, h.Target, section);
            else if (session.PaintSurface(h.Target, texture, section) is { } why) session.Tell(why);
            return;
        }

        if (hit is not { Kind: HitKind.Floor } floor)
        {
            session.Tell("Drop the object on a floor.");
            return;
        }

        const double step = 8;
        var at = shift ? new MapPoint(floor.X, floor.Y) : new MapPoint(Math.Round(floor.X / step) * step, Math.Round(floor.Y / step) * step);
        if (drop.ModelFile is { } model) session.ImportModel(model, at, floor.Platform ? floor.Z : null);
        else if (drop.From is { } other) session.PlaceBorrowedThing(other, drop.Thing, at, floor.Platform ? floor.Z : null);
        else session.PlaceThing(drop.Thing, at, floor.Platform ? floor.Z : null);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Session is null || DropOf(e) is not { } drop)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        RequestHover(e.GetPosition(this), drop);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Session is null || DropOf(e) is not { } drop) return;
        RequestHover(e.GetPosition(this), drop, apply: true, shift: (e.KeyModifiers & KeyModifiers.Shift) != 0);
        Focus();
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderer = new Renderer3D();
        _renderer.FrameReady += bitmap => ViewModels.MainViewModel.PostToUi(() =>
        {
            _shown = bitmap;
            _message = null;
            InvalidateVisual();
        });
        _renderer.Failed += message => ViewModels.MainViewModel.PostToUi(() =>
        {
            _message = message + " The map editor works as usual without it.";
            InvalidateVisual();
        });
        Retarget();
        RequestScene();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderer?.Dispose();
        _renderer = null;
        _shown = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            if (change.OldValue is EditorSession old)
            {
                old.GeometryChanged -= RequestScene;
                old.Selection.Changed -= RequestTint;
                old.HiddenChanged -= RequestScene;
            }

            if (change.NewValue is EditorSession s)
            {
                s.GeometryChanged += RequestScene;
                s.Selection.Changed += RequestTint;
                s.HiddenChanged += RequestScene;
            }
            _framed = false;
            _scene = null;
            RequestScene();
        }
        else if (change.Property == BoundsProperty)
        {
            Retarget();
        }
        else if (change.Property == IsVisibleProperty && IsVisible)
        {
            RequestScene();
        }
        else if (change.Property == ModeProperty)
        {
            Enter(change.GetNewValue<CameraMode>());
        }
    }

    private void RequestScene()
    {
        if (Session is not { } session || _renderer is null || !IsVisible) return;
        var geometry = session.Geometry;
        var resources = session.Resources;
        var hidden = new HashSet<string>(session.Hidden);
        _scenes.Request(() => (session, MapScene.Build(geometry, name => resources?.Textures.GetWall(name) is { } w ? (w.Width, w.Height) : null, SpriteShapes.For(resources), hidden, ModelShapes.For(session))));
    }

    private (GameResources? Resources, SpriteShape? Shape) _player;

    /// <summary>Stands the player's ghost where the pointer is on the plan (on the floor it would stand on), or takes it away.</summary>
    public void ShowGhost((MapPoint At, double Floor, bool Fits)? ghost)
    {
        if (_renderer is null) return;
        var resources = Session?.Resources;
        if (!ReferenceEquals(_player.Resources, resources) || (_player.Shape is null && resources is not null)) _player = (resources, SpriteShapes.For(resources)(1));
        if (ghost is not { } g || !IsVisible || _player.Shape is not { } shape)
        {
            _renderer.Ghost(null, false);
            return;
        }

        _renderer.Ghost(MapScene.SpriteQuad(g.At.X, g.At.Y, g.Floor, shape, 255), g.Fits);
    }

    /// <summary>The selection's triangles, gathered on a worker, to tint in the view.</summary>
    private void RequestTint()
    {
        if (Session is not { } session || _scene is not { } scene || _renderer is null) return;
        var selected = new HashSet<Editor.Model.Element>(session.Selection.Items, ReferenceEqualityComparer.Instance);
        _tints.Request(() => scene.Highlight(selected));
    }

    /// <summary>New bitmaps of the view's size in device pixels (the render thread draws into them).</summary>
    private void Retarget()
    {
        if (_renderer is null) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = (int)Math.Round(Bounds.Width * scale);
        var height = (int)Math.Round(Bounds.Height * scale);
        if (width < 2 || height < 2) return;
        var size = new PixelSize(width, height);
        var dpi = new Vector(96 * scale, 96 * scale);
        WriteableBitmap[] bitmaps = [new(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul), new(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul), new(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul)];
        _renderer.Target(bitmaps, width, height);
    }

    /// <summary>Whether a picture has been shown (for tools that wait for the first one).</summary>
    internal bool HasPicture => _shown is not null;

    /// <summary>The map version the 3D scene was built from (-1 before the first), for instruments.</summary>
    internal long SceneVersion => _scene?.Version ?? -1;

    /// <summary>Turns the orbit camera (for tools that measure drawing).</summary>
    internal void TurnBy(double degrees)
    {
        _yaw += degrees;
        SendCamera();
    }

    /// <summary>
    /// Where Play From Here starts the player, facing which way (the game's degrees): where the camera stands in Walk and Fly,
    /// and the point it turns round in Orbit, facing the way it looks.
    /// </summary>
    internal (double X, double Y, double Angle) PlaySpot() => Mode == CameraMode.Orbit ? (_tx, _ty, (_yaw % 360 + 360) % 360) : (_x, _y, (_lookYaw % 360 + 360) % 360);

    /// <summary>Turns the orbit camera round a spot at height <paramref name="z"/>, from far enough to see <paramref name="span"/> across it.</summary>
    internal void LookAt(MapPoint at, double z, double span)
    {
        (_tx, _ty, _tz) = (at.X, at.Y, z);
        _distance = Camera3D.FitDistance(span, Bounds.Width / Math.Max(Bounds.Height, 1));
        (_yaw, _pitch) = (50, -30);
        _framed = true;
        SendCamera();
    }

    /// <summary>Looks at <paramref name="at"/> from far enough to see <paramref name="span"/> round it, now or when the first scene arrives.</summary>
    internal void LookAround(MapPoint at, double span)
    {
        _focus = (at, span);
        if (_scene is not null) Frame();
    }

    /// <summary>
    /// Turns the orbit camera to an angle from which nothing stands between it and the spot it turns round, tried on a
    /// worker (each try is a ray through the whole scene): a few headings at three heights, steeper last; straight down
    /// when every one is blocked. A hit within 64 of the spot counts as clear, so a closed door shows its own face.
    /// </summary>
    private void RequestClearView()
    {
        if (Session is not { } session || _scene is not { } scene) return;
        var (tx, ty, tz, distance) = (_tx, _ty, _tz, _distance);
        _looks.Request(() =>
        {
            foreach (var pitch in (double[])[-30, -45, -60])
            {
                foreach (var yaw in (double[])[50, 140, 230, 320, 95, 185, 275, 5])
                {
                    var eye = Camera3D.Orbit(tx, ty, tz, yaw, pitch, distance);
                    var (dx, dy, dz) = (tx - eye.X, ty - eye.Y, tz - eye.Z);
                    var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (length < 1) return (session, scene, yaw, pitch);
                    var hit = scene.Hit((eye.X, eye.Y, eye.Z), (dx / length, dy / length, dz / length), yaw);
                    if (hit is null || hit.Value.Distance >= length - 64) return (session, scene, yaw, pitch);
                }
            }

            return (session, scene, 50.0, -85.0);
        });
    }

    /// <summary>Turns to look at the whole map from above and to the south-west.</summary>
    public void Frame()
    {
        if (_scene is null) return;
        if (_focus is { } f)
        {
            _focus = null;
            LookAt(f.At, (Session?.Geometry.AreaAt(f.At)?.FloorHeight ?? 0) + 48, f.Span);
            RequestClearView();
            return;
        }

        var (x0, y0, z0, x1, y1, z1) = _scene.Bounds;
        (_tx, _ty, _tz) = ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
        _distance = Camera3D.FitDistance(Math.Max(x1 - x0, y1 - y0), Bounds.Width / Math.Max(Bounds.Height, 1));
        (_yaw, _pitch) = (50, -48);
        _framed = true;
        SendCamera();
    }

    /// <summary>
    /// Switches how the camera moves. Fly starts where the orbit camera stands; Walk lands on the floor
    /// under the point the orbit turned around (or at the player start when that is over the void).
    /// </summary>
    private void Enter(CameraMode mode)
    {
        _keys.Clear();
        _mover.Stop();
        var orbit = Camera3D.Orbit(_tx, _ty, _tz, _yaw, _pitch, _distance);
        switch (mode)
        {
            case CameraMode.Fly:
                (_x, _y, _z, _lookYaw, _lookPitch) = (orbit.X, orbit.Y, orbit.Z, orbit.Yaw, orbit.Pitch);
                break;
            case CameraMode.Walk:
                var geometry = Session?.Geometry;
                var spot = new MapPoint(_tx, _ty);
                if (geometry?.AreaAt(spot) is null && _scene?.Things.FirstOrDefault(t => t.Type == 1) is { Element: not null } start)
                {
                    spot = new MapPoint(start.X, start.Y);
                    _lookYaw = start.Angle;
                }
                else
                {
                    _lookYaw = _yaw;
                }

                var floor = geometry?.AreaAt(spot)?.FloorHeight ?? _tz;
                (_x, _y, _z, _lookPitch) = (spot.X, spot.Y, floor + Walker.EyeHeight, 0);
                break;
        }

        SendCamera();
        Focus();
    }

    private Camera3D Current => Mode == CameraMode.Orbit ? Camera3D.Orbit(_tx, _ty, _tz, _yaw, _pitch, _distance) : new Camera3D(_x, _y, _z, _lookYaw, _lookPitch);

    private void SendCamera()
    {
        var camera = Current;
        _renderer?.Look(camera, inside: Mode != CameraMode.Orbit);
        CameraMoved?.Invoke(Mode == CameraMode.Orbit ? null : camera);
    }

    /// <summary>One step of movement while keys are held: smooth, at the same speed whatever the frame rate.</summary>
    private void Move()
    {
        var dt = Math.Min(_clock.Elapsed.TotalSeconds, 0.1);
        _clock.Restart();
        if (_keys.Count == 0 || Mode == CameraMode.Orbit)
        {
            _mover.Stop();
            return;
        }

        double Axis(Key plus, Key plus2, Key minus, Key minus2) => (_keys.Contains(plus) || _keys.Contains(plus2) ? 1 : 0) - (_keys.Contains(minus) || _keys.Contains(minus2) ? 1 : 0);
        var keys = MoveKeys;
        var forward = Axis(keys.Forward, Key.Up, keys.Back, Key.Down);
        var sideways = Axis(keys.Right, Key.Right, keys.Left, Key.Left);
        var vertical = Mode == CameraMode.Fly ? Axis(keys.Up ?? Key.PageUp, Key.PageUp, keys.Down ?? Key.PageDown, Key.PageDown) : 0;
        var fast = _keys.Contains(Key.LeftShift) || _keys.Contains(Key.RightShift);
        var speed = (Mode == CameraMode.Walk ? 320 : 768) * (fast ? 2.5 : 1) * dt;
        var yaw = _lookYaw * Math.PI / 180;
        var pitch = Mode == CameraMode.Fly ? _lookPitch * Math.PI / 180 : 0;
        var dx = (Math.Cos(yaw) * Math.Cos(pitch) * forward + Math.Sin(yaw) * sideways) * speed;
        var dy = (Math.Sin(yaw) * Math.Cos(pitch) * forward - Math.Cos(yaw) * sideways) * speed;
        var dz = (Math.Sin(pitch) * forward + vertical) * speed;
        if (Mode == CameraMode.Fly)
        {
            (_x, _y, _z) = (_x + dx, _y + dy, _z + dz);
        }
        else if (!TryWalk(_x + dx, _y + dy) && !TryWalk(_x + dx, _y))
        {
            // Blocked: slide along whichever direction is free.
            TryWalk(_x, _y + dy);
        }

        SendCamera();
    }

    /// <summary>Walks to a spot if the player could (<see cref="Walker"/>).</summary>
    private bool TryWalk(double x, double y)
    {
        if (Session?.Geometry is not { } geometry) return false;
        if (Walker.Step(geometry, new MapPoint(_x, _y), _z - Walker.EyeHeight, new MapPoint(x, y)) is not { } eye) return false;
        (_x, _y, _z) = (x, y, eye);
        return true;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x07, 0x08)), new Rect(Bounds.Size));
        if (_shown is { } bitmap) context.DrawImage(bitmap, new Rect(Bounds.Size));
        if (_message is null) return;
        var text = new FormattedText(_message, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 14, new SolidColorBrush(Color.FromRgb(0xB3, 0xA9, 0xA6)))
        {
            MaxTextWidth = Math.Max(100, Bounds.Width - 48),
            TextAlignment = TextAlignment.Center,
        };
        context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastPointer = null;
        _wheel = null;
        _nudge = null;
        if (_dragFrom is null && _heightDrag is null && _textureDrag is null) ClearHover();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pressed = e.GetCurrentPoint(this).Properties;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && pressed.IsLeftButtonPressed && Session is not null && _hover is { Kind: HitKind.Floor or HitKind.Ceiling } surface)
        {
            // Ctrl+drag on a floor or ceiling moves it (§3.4): a pixel is worth what the view shows at that distance.
            var unitsPerPixel = 2 * surface.Distance * Math.Tan(Current.Fov * Math.PI / 360) / Math.Max(Bounds.Height, 1) / Current.Stretch;
            _heightDrag = (surface.Target, Editor.Editing.History.NewGesture(), 0, e.GetPosition(this), unitsPerPixel, SurfaceName(surface));
            _wheel = null;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && pressed.IsLeftButtonPressed && Session is not null && _hover is { Kind: HitKind.Wall, Element: Editor.Model.Side face } wall)
        {
            // Ctrl+drag on a wall face slides its texture (§3.12): a pixel is worth what the view shows at that distance.
            var unitsPerPixel = 2 * wall.Distance * Math.Tan(Current.Fov * Math.PI / 360) / Math.Max(Bounds.Height, 1);
            _textureDrag = (face, Editor.Editing.History.NewGesture(), 0, 0, e.GetPosition(this), unitsPerPixel);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        ClearHover();
        var props = e.GetCurrentPoint(this).Properties;
        _dragFrom = e.GetPosition(this);
        _pressedAt = _dragFrom.Value;
        _dragged = 0;
        _panning = props.IsRightButtonPressed || props.IsMiddleButtonPressed;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var at = e.GetPosition(this);
        _lastPointer = at;
        if (_heightDrag is { } drag)
        {
            // The surface follows the pointer up and down, on the 8 grid (Shift: by 1), every step joining one undo step.
            var total = (drag.From.Y - at.Y) * drag.UnitsPerPixel;
            var snapped = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? Math.Round(total) : Math.Round(total / 8) * 8;
            if (snapped != drag.Applied && Session is { } session)
            {
                var now = session.RaiseSurface(drag.Target, snapped - drag.Applied, drag.Gesture);
                _heightDrag = drag with { Applied = snapped };
                if (now is { } h) session.Tell($"{drag.Name} at {h:0} · let go to keep it (Shift moves by 1).");
            }

            return;
        }

        if (_textureDrag is { } slide)
        {
            // The texture slides with the pointer, on the 8 grid (Shift: by 1): right moves it right, up moves it up.
            var (tx, ty) = ((at.X - slide.From.X) * slide.UnitsPerPixel, (at.Y - slide.From.Y) * slide.UnitsPerPixel);
            var fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            var (sx, sy) = fine ? (Math.Round(tx), Math.Round(ty)) : (Math.Round(tx / 8) * 8, Math.Round(ty / 8) * 8);
            if ((sx != slide.AppliedX || sy != slide.AppliedY) && Session is { } session)
            {
                var now = session.NudgeTexture(slide.Face, -(sx - slide.AppliedX), -(sy - slide.AppliedY), slide.Gesture);
                _textureDrag = slide with { AppliedX = sx, AppliedY = sy };
                if (now is { } o) session.Tell($"Texture shifted to {o.X:0} across, {o.Y:0} up · let go to keep it (Shift moves by 1).");
            }

            return;
        }

        if (_dragFrom is not { } from)
        {
            RequestHover(at);
            return;
        }

        var p = e.GetPosition(this);
        var dx = p.X - from.X;
        var dy = p.Y - from.Y;
        _dragFrom = p;
        _dragged += Math.Abs(dx) + Math.Abs(dy);
        if (Mode != CameraMode.Orbit)
        {
            _lookYaw -= dx * 0.25;
            _lookPitch = Math.Clamp(_lookPitch - dy * 0.25, -89, 89);
        }
        else if (_panning)
        {
            // Move the point turned around across the screen: right and up as the camera sees them.
            var scale = _distance / Math.Max(Bounds.Height, 1) * 1.4;
            var yaw = _yaw * Math.PI / 180;
            var pitch = _pitch * Math.PI / 180;
            _tx += (-Math.Sin(yaw) * -dx + Math.Cos(yaw) * Math.Sin(pitch) * -dy) * scale;
            _ty += (Math.Cos(yaw) * -dx + Math.Sin(yaw) * Math.Sin(pitch) * -dy) * scale;
            _tz += Math.Cos(pitch) * dy * scale;
        }
        else
        {
            _yaw -= dx * 0.4;
            _pitch = Math.Clamp(_pitch - dy * 0.3, -89, 89);
        }

        SendCamera();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_heightDrag is { } drag)
        {
            _heightDrag = null;
            e.Pointer.Capture(null);
            if (drag.Applied == 0 && Session is { } s) s.Tell($"Ctrl+drag moves {(drag.Name.StartsWith("The", StringComparison.Ordinal) ? "" : "a ")}{drag.Name.ToLowerInvariant()} up or down; Ctrl+scroll steps it by 8.");
            return;
        }

        if (_textureDrag is { } slide)
        {
            _textureDrag = null;
            e.Pointer.Capture(null);
            if (slide.AppliedX == 0 && slide.AppliedY == 0 && Session is { } s) s.Tell("Ctrl+drag slides a wall's texture; Ctrl+arrows nudge it by 8; Ctrl+A lines the textures up along the wall.");
            return;
        }

        var clicked = _dragFrom is not null && _dragged < 4 && e.InitialPressMouseButton == MouseButton.Left;
        _dragFrom = null;
        e.Pointer.Capture(null);
        if (!clicked || Session is not { } session || _scene is not { } scene) return;

        // A click (not a drag) selects what is under the pointer, in both views; Shift adds or removes.
        var aspect = Bounds.Width / Math.Max(Bounds.Height, 1);
        var across = 2 * _pressedAt.X / Math.Max(Bounds.Width, 1) - 1;
        var up = 1 - 2 * _pressedAt.Y / Math.Max(Bounds.Height, 1);
        var camera = Current;
        var (origin, direction) = camera.Ray(across, up, aspect);
        var toggle = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        _picks.Request(() => (session, scene.Pick(origin, direction, camera.Yaw), toggle));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            // Ctrl+scroll on a floor or ceiling steps it by 8 (Shift: by 1); the steps on one surface make one undo step.
            if (Session is { } session && _hover is { Kind: HitKind.Floor or HitKind.Ceiling } surface && e.Delta.Y != 0)
            {
                if (_wheel is not { } w || !ReferenceEquals(w.Element, surface.Element) || w.Ceiling != surface.Target.Ceiling) _wheel = (surface.Element, surface.Target.Ceiling, Editor.Editing.History.NewGesture());
                var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 1 : 8;
                if (session.RaiseSurface(surface.Target, Math.Sign(e.Delta.Y) * step, _wheel.Value.Gesture) is { } now) session.Tell($"{SurfaceName(surface)} at {now:0} · Ctrl+scroll moves it by 8, with Shift by 1; Ctrl+drag follows the pointer.");
            }

            e.Handled = true;
            return;
        }

        _wheel = null;
        if (Mode == CameraMode.Fly)
        {
            var (fx, fy, fz) = Current.Forward;
            (_x, _y, _z) = (_x + fx * e.Delta.Y * 96, _y + fy * e.Delta.Y * 96, _z + fz * e.Delta.Y * 96);
        }
        else if (Mode == CameraMode.Orbit)
        {
            _distance = Math.Clamp(_distance * Math.Pow(0.85, e.Delta.Y), 16, 200_000);
        }

        SendCamera();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && Session is { } session && _hover is { Kind: HitKind.Wall, Element: Editor.Model.Side face } wall)
        {
            // On the wall face under the pointer: Ctrl+arrows nudge its texture the way the arrow points (Shift: by 1); Ctrl+A lines the textures along the wall up with it.
            var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 1 : 8;
            var (dx, dy) = e.Key switch { Key.Left => (step, 0), Key.Right => (-step, 0), Key.Up => (0, step), Key.Down => (0, -step), _ => (0, 0) };
            if (dx != 0 || dy != 0)
            {
                if (_nudge is not { } n || !ReferenceEquals(n.Face, face)) _nudge = (face, Editor.Editing.History.NewGesture());
                if (session.NudgeTexture(face, dx, dy, _nudge.Value.Gesture) is { } now) session.Tell($"Texture shifted to {now.X:0} across, {now.Y:0} up · Ctrl+arrows move it by 8, with Shift by 1; Ctrl+A lines the walls along it up.");
                e.Handled = true;
                return;
            }

            if (e.Key == Key.A)
            {
                session.AlignTextures(face, wall.Part);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Home)
        {
            Mode = CameraMode.Orbit;
            Frame();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.None)
        {
            EngineViewToggled?.Invoke();
            e.Handled = true;
            return;
        }

        // Fly and Walk take only their movement keys; the rest (Tab, F9, the tools) go on to the editor's keymap.
        if (Mode == CameraMode.Orbit || !Moves(e.Key)) return;
        if (_keys.Add(e.Key) && !_mover.IsEnabled)
        {
            _clock.Restart();
            _mover.Start();
        }

        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _keys.Remove(e.Key);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _keys.Clear();
    }
}
