using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
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
    private SceneHit? _hover;
    private Point _pressedAt;
    private double _dragged;
    private Renderer3D? _renderer;
    private WriteableBitmap? _shown;
    private string? _message = "Starting the 3D view…";
    private MapScene? _scene;
    private bool _framed;
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
        }, "the 3D scene");
        _tints = new(ViewModels.MainViewModel.PostToUi, t => _renderer?.Highlight(t), "the 3D selection");
        _hovers = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || !ReferenceEquals(r.Scene, _scene)) return;
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

    public CameraMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Raised when the camera moves: where it stands in Fly and Walk, null while orbiting (the plan shows no marker then).</summary>
    public event Action<Camera3D?>? CameraMoved;

    /// <summary>What letting go of a shelf drag would do here (null to clear), for the status bar.</summary>
    public event Action<string?>? DropHint;

    /// <summary>The surface under the pointer as last found (a moment behind the pointer), for instruments.</summary>
    internal SceneHit? Hovered => _hover;

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
        _renderer?.Hover(([], []));
    }

    private static string DropWords(ViewModels.ShelfDrop drop, SceneHit? hit)
    {
        if (drop.Prefab is not null) return "Prefabs go on the plan: drop it there.";
        if (drop.Texture is null) return hit is { Kind: HitKind.Floor } ? $"Let go to place {drop.Label} here." : "Drop the object on a floor.";
        if (hit is not { Kind: not HitKind.Thing } h) return "Drop it on a wall, a floor or a ceiling.";
        return $"Let go to put {drop.Label} on {h.Words}." + (drop.From is { } source ? $" It is copied into this project from {source.Game.Title} first." : "");
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

            if (drop.From is { } from) session.PaintBorrowedSurface(from, texture, drop.Flat, h.Target);
            else if (session.PaintSurface(h.Target, texture) is { } why) session.Tell(why);
            return;
        }

        if (hit is not { Kind: HitKind.Floor } floor)
        {
            session.Tell("Drop the object on a floor.");
            return;
        }

        const double step = 8;
        session.PlaceThing(drop.Thing, shift ? new MapPoint(floor.X, floor.Y) : new MapPoint(Math.Round(floor.X / step) * step, Math.Round(floor.Y / step) * step));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Session is null || e.DataTransfer.TryGetValue(ViewModels.ShelfDrop.Format) is not { } drop)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        RequestHover(e.GetPosition(this), drop);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Session is null || e.DataTransfer.TryGetValue(ViewModels.ShelfDrop.Format) is not { } drop) return;
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
        _scenes.Request(() => (session, MapScene.Build(geometry, name => resources?.Textures.GetWall(name) is { } w ? (w.Width, w.Height) : null, SpriteShapes.For(resources), hidden)));
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

    /// <summary>Turns to look at the whole map from above and to the south-west.</summary>
    public void Frame()
    {
        if (_scene is null) return;
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
        var forward = Axis(Key.W, Key.Up, Key.S, Key.Down);
        var sideways = Axis(Key.D, Key.Right, Key.A, Key.Left);
        var vertical = Mode == CameraMode.Fly ? Axis(Key.E, Key.PageUp, Key.Q, Key.PageDown) : 0;
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
        if (_dragFrom is null) ClearHover();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
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
        if (_dragFrom is not { } from)
        {
            RequestHover(e.GetPosition(this));
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
        if (e.Key == Key.Home)
        {
            Mode = CameraMode.Orbit;
            Frame();
            e.Handled = true;
            return;
        }

        if (Mode == CameraMode.Orbit) return;
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
