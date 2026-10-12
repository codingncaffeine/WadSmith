using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WadSmith.Editor;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>
/// The 3D view (D46): the map from any angle. Drag to turn around it, right- or middle-drag to move,
/// scroll to come closer; Home frames the whole map again. The scene is built on a worker from the
/// session's shapes and drawn on its own render thread; this control only shows finished pictures.
/// </summary>
internal sealed class View3D : Control
{
    public static readonly StyledProperty<EditorSession?> SessionProperty = AvaloniaProperty.Register<View3D, EditorSession?>(nameof(Session));

    private readonly LatestOnWorker<(EditorSession Session, MapScene Scene)> _scenes;
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
        _scenes = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || _renderer is null) return;
            _scene = r.Scene;
            if (!_framed) Frame();
            _renderer.Show(r.Scene, r.Session.Resources);
            SendCamera();
        }, "the 3D scene");
    }

    public EditorSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
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
            if (change.OldValue is EditorSession old) old.GeometryChanged -= RequestScene;
            if (change.NewValue is EditorSession s) s.GeometryChanged += RequestScene;
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
    }

    private void RequestScene()
    {
        if (Session is not { } session || _renderer is null || !IsVisible) return;
        var geometry = session.Geometry;
        var resources = session.Resources;
        _scenes.Request(() => (session, MapScene.Build(geometry, name => resources?.Textures.GetWall(name) is { } w ? (w.Width, w.Height) : null, SpriteShapes.For(resources))));
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

    private void SendCamera() => _renderer?.Look(Camera3D.Orbit(_tx, _ty, _tz, _yaw, _pitch, _distance), inside: false);

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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        _dragFrom = e.GetPosition(this);
        _panning = props.IsRightButtonPressed || props.IsMiddleButtonPressed;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var p = e.GetPosition(this);
        var dx = p.X - from.X;
        var dy = p.Y - from.Y;
        _dragFrom = p;
        if (_panning)
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
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * Math.Pow(0.85, e.Delta.Y), 16, 200_000);
        SendCamera();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Home) return;
        Frame();
        e.Handled = true;
    }
}
